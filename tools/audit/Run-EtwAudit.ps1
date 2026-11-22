<#
.SYNOPSIS
  Process-attributed dynamic audit with ETW (requires an elevated PowerShell). Complements Run-DynamicAudit.ps1, which
  observes the effects without attribution and needs no privileges.
.DESCRIPTION
  Records kernel File I/O events (Windows Performance Recorder, built into Windows 10/11: wpr.exe, profile "FileIO")
  while the real Seekbeam.exe indexes the audit dataset, converts the trace with tracerpt (built-in) and lists every file
  operation issued by Seekbeam.exe / Seekbeam.ExtractorHost.exe under the dataset: creates with a write/delete disposition,
  writes, set-information (rename, delete, attributes, times) and security changes. Expected result: none.
  Not executed in the development session (non-elevated). See docs/FILE_IMMUTABILITY_AUDIT.md §6.
#>
param(
  [string]$Work = (Join-Path $env:TEMP ("seekbeam-etw-audit-" + (Get-Date -Format "yyyyMMdd-HHmmss"))),
  [string]$Configuration = "Release",
  [int]$IndexSeconds = 90
)
$ErrorActionPreference = "Stop"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw "Run this script from an elevated PowerShell (ETW kernel file I/O tracing requires administrator rights)."
}
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$app = "$repo\src\Seekbeam.App\bin\$Configuration\net10.0-windows\Seekbeam.exe"
$gen = "$repo\tools\Seekbeam.DatasetGen\bin\$Configuration\net10.0-windows\Seekbeam.DatasetGen.exe"
New-Item -ItemType Directory -Force $Work | Out-Null
$src = Join-Path $Work "source"; $data = Join-Path $Work "seekbeam-data"
& $gen audit $src 20000 | Out-Host
New-Item -ItemType Directory -Force $data | Out-Null
$cfg = @{ General = @{ FirstRunCompleted = $true; StartWithWindows = $false }; Roots = @(@{ Path = $src; FollowLinks = $true; IncludeHidden = $true; Excludes = @() }); GlobalExcludes = @(); Resources = @{ Mode = "Performance" } } | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $data "config.json"), $cfg)

$etl = Join-Path $Work "fileio.etl"
wpr.exe -start FileIO -filemode | Out-Host
try {
  $p = Start-Process $app -ArgumentList "--background --data `"$data`"" -PassThru
  Start-Sleep -Seconds $IndexSeconds
  Start-Process $app -ArgumentList "--exit --data `"$data`"" -Wait
  $p.WaitForExit(30000) | Out-Null
}
finally { wpr.exe -stop $etl | Out-Host }

$csv = Join-Path $Work "fileio.csv"
tracerpt.exe $etl -o $csv -of CSV -y | Out-Null
$rows = Import-Csv $csv -ErrorAction SilentlyContinue
$srcPrefix = $src.ToLowerInvariant()
# Write-type operations in the kernel FileIo provider.
$writeOps = "FileIo/Write", "FileIo/SetInfo", "FileIo/Delete", "FileIo/Rename", "FileIo/FSControl"
$suspect = $rows | Where-Object {
  ($_."Image Name" -match "Seekbeam") -and ($writeOps -contains $_."Event Name") -and (($_ | Out-String).ToLowerInvariant().Contains($srcPrefix))
}
$creates = $rows | Where-Object { ($_."Image Name" -match "Seekbeam") -and ($_."Event Name" -eq "FileIo/Create") -and (($_ | Out-String).ToLowerInvariant().Contains($srcPrefix)) }
$report = @"
# Seekbeam — ETW (process-attributed) immutability audit
- Date: $(Get-Date -Format u)
- Trace: $etl
- FileIo/Create events by Seekbeam on the dataset: $($creates.Count) (expected: reads only — inspect CreateOptions / disposition)
- Write/SetInfo/Delete/Rename/FSControl events by Seekbeam on the dataset: $($suspect.Count) (expected: 0)
"@
$out = Join-Path $repo ("artifacts\audit\etw-audit-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".md")
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
Set-Content $out $report -Encoding utf8
$suspect | Select-Object -First 50 | Format-Table -AutoSize | Out-String | Add-Content $out
Write-Host $report
& $gen audit-cleanup $src | Out-Null
exit $(if ($suspect.Count -eq 0) { 0 } else { 1 })
