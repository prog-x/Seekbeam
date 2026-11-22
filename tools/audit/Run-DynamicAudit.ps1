<#
.SYNOPSIS
  Dynamic, independent verification that the real Seekbeam.exe never modifies indexed files (FILE_IMMUTABILITY_POLICY §7).
.DESCRIPTION
  1. Builds the audit dataset (all formats, malformed files, archives, links, ACL-denied dir, ADS, long paths...).
  2. Takes a reference snapshot (SHA-256, size, attributes, times, SDDL, ADS, reparse targets).
  3. Starts Seekbeam.FsAudit (independent process, ReadDirectoryChangesW with every notification filter).
  4. Runs the real Seekbeam.exe on an isolated profile (--data): initial indexing, forced kill mid-way (crash),
     restart and recovery, full content indexing, then graceful exit (--exit).
  5. Stops FsAudit, takes a second snapshot, compares, writes a Markdown report to artifacts/audit.
  No administrator rights required. Exit code 0 = no modification observed.
#>
param(
  [string]$Work = (Join-Path $env:TEMP ("seekbeam-dynamic-audit-" + (Get-Date -Format "yyyyMMdd-HHmmss"))),
  [string]$Configuration = "Release",
  [switch]$NoBuild,
  [int]$TimeoutSeconds = 300,
  [int]$BulkEntries = 30000,
  [int]$KillAfterSeconds = 2
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if (-not $NoBuild) { dotnet build "$repo\Seekbeam.sln" -c $Configuration | Out-Null }
$app = "$repo\src\Seekbeam.App\bin\$Configuration\net10.0-windows\Seekbeam.exe"
$gen = "$repo\tools\Seekbeam.DatasetGen\bin\$Configuration\net10.0-windows\Seekbeam.DatasetGen.exe"
$fsa = "$repo\tools\Seekbeam.FsAudit\bin\$Configuration\net10.0-windows\Seekbeam.FsAudit.exe"
foreach ($f in $app, $gen, $fsa) { if (-not (Test-Path $f)) { throw "Missing $f (build first)" } }

New-Item -ItemType Directory -Force $Work | Out-Null
$src = Join-Path $Work "source"; $data = Join-Path $Work "seekbeam-data"
& $gen audit $src $BulkEntries | Out-Host
& $gen snapshot $src "$Work\before.json" | Out-Host

# Isolated Seekbeam profile pointing at the dataset.
New-Item -ItemType Directory -Force $data | Out-Null
$cfg = @{ General = @{ FirstRunCompleted = $true; StartWithWindows = $false }; Roots = @(@{ Path = $src; FollowLinks = $true; IncludeHidden = $true; Excludes = @() });
          GlobalExcludes = @(); Resources = @{ Mode = "Performance" }; Discovery = @{ Enabled = $false } } | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $data "config.json"), $cfg)

$stop = Join-Path $Work "stop.flag"; $report = Join-Path $Work "fsaudit.txt"
$audit = Start-Process $fsa -ArgumentList "`"$src`" `"$report`" `"$stop`"" -PassThru -WindowStyle Hidden
while (-not (Test-Path "$report.ready")) { Start-Sleep -Milliseconds 100 }

function Wait-Log([string]$pattern, [int]$timeout) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $timeout) {
    $logs = Get-ChildItem (Join-Path $data "logs") -Filter *.log -ErrorAction SilentlyContinue
    if ($logs -and (Select-String -Path $logs.FullName -Pattern $pattern -Quiet)) { return $true }
    Start-Sleep -Milliseconds 500
  }
  return $false
}

$t0 = Get-Date
$p1 = Start-Process $app -ArgumentList "--background --data `"$data`"" -PassThru
Start-Sleep -Seconds $KillAfterSeconds
Stop-Process -Id $p1.Id -Force                                   # forced stop (crash) during indexing
Get-Process Seekbeam.ExtractorHost -ErrorAction SilentlyContinue | Stop-Process -Force
# Keep the pre-crash log for the report, then only consider events of the recovered run.
Get-ChildItem (Join-Path $data "logs") -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object { Move-Item $_.FullName (Join-Path $Work ("precrash-" + $_.Name)) -Force }
$p2 = Start-Process $app -ArgumentList "--background --data `"$data`"" -PassThru
$okNames = Wait-Log "Name queue drained" $TimeoutSeconds
$okContent = Wait-Log "Content queue drained" $TimeoutSeconds   # the recovered run must finish the content work interrupted by the kill
Start-Process $app -ArgumentList "--exit --data `"$data`"" -Wait
$p2.WaitForExit(30000) | Out-Null
$graceful = Wait-Log "Stopped cleanly" 10
$t1 = Get-Date

New-Item -ItemType File $stop | Out-Null
$audit.WaitForExit(30000) | Out-Null
& $gen snapshot $src "$Work\after.json" | Out-Host
$cmp = & $gen compare "$Work\before.json" "$Work\after.json"
$cmpExit = $LASTEXITCODE
$fsLines = Get-Content $report
$modify = ($fsLines | Select-String "^MODIFY events").ToString()
$access = ($fsLines | Select-String "^ACCESS events").ToString()
$log = @(Get-Content (Get-ChildItem $Work -Filter "precrash-*.log").FullName) + @(Get-Content (Get-ChildItem (Join-Path $data "logs") -Filter *.log).FullName)

$md = @"
# Seekbeam — dynamic immutability audit

- Date: $(Get-Date -Format u)
- Machine: $env:COMPUTERNAME (non-elevated: $(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)))
- Binary: $app
- Scenario: initial indexing → forced kill after $KillAfterSeconds s (dataset: $BulkEntries bulk entries + audit set) → restart & recovery → full name + content indexing → graceful exit (--exit)
- Duration: $([int]($t1 - $t0).TotalSeconds) s; names drained: $okNames; content drained: $okContent; graceful exit: $graceful
- Crash detected at restart: $([bool]($log | Select-String "ended abnormally"))

## Independent kernel notifications (Seekbeam.FsAudit)

- $modify
- $access

## Snapshot comparison (SHA-256, size, attributes, times, SDDL, ADS, reparse targets)

``````
$($cmp -join "`n")
``````

## Seekbeam log (excerpt)

``````
$(($log | Select-String "drained|abnormally|Stopped cleanly|Extractor host started|Added root|Error" | Select-Object -First 25) -join "`n")
``````
"@
$outDir = Join-Path $repo "artifacts\audit"; New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir ("dynamic-audit-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".md")
Set-Content -Path $out -Value $md -Encoding utf8
Copy-Item $report (Join-Path $outDir "fsaudit-last.txt") -Force
& $gen audit-cleanup $src | Out-Null
Write-Host $md
$pass = ($cmpExit -eq 0) -and ($modify -match ": 0$") -and $okNames -and $okContent
Write-Host ("RESULT: " + $(if ($pass) { "PASS - no modification of source files observed" } else { "FAIL" }))
exit $(if ($pass) { 0 } else { 1 })
