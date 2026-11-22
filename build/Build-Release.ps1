<#
.SYNOPSIS  Builds, tests and packages Seekbeam: self-contained ReadyToRun publish, per-user MSI (WiX v6), portable zip, SHA-256 checksums.
           The version comes from <Version> in Directory.Build.props (single source of truth).
.EXAMPLE   pwsh build/Build-Release.ps1            # full pipeline
           pwsh build/Build-Release.ps1 -SkipTests
#>
param([switch]$SkipTests, [string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$version = ([xml](Get-Content (Join-Path $repo "Directory.Build.props") -Raw)).Project.PropertyGroup.Version
if (-not $version) { throw "Version not found in Directory.Build.props" }
$out = Join-Path $repo "artifacts"; $pub = Join-Path $out "publish\Seekbeam"; $dist = Join-Path $out "installer"
Remove-Item -Recurse -Force $pub, $dist -ErrorAction SilentlyContinue
dotnet build "$repo\Seekbeam.sln" -c Release
if ($LASTEXITCODE) { throw "build failed" }
if (-not $SkipTests) {
  # Tests use the Debug binaries (the integrity tests start the real Seekbeam.exe from bin\Debug).
  dotnet build "$repo\Seekbeam.sln" -c Debug
  if ($LASTEXITCODE) { throw "debug build failed" }
  dotnet test "$repo\Seekbeam.sln" -c Debug --no-build
  if ($LASTEXITCODE) { throw "tests failed" }
}
$common = @("-c", "Release", "-r", $Runtime, "--self-contained", "true", "-p:PublishReadyToRun=true", "-p:DebugType=none", "-o", $pub)
dotnet publish "$repo\src\Seekbeam.ExtractorHost\Seekbeam.ExtractorHost.csproj" @common
if ($LASTEXITCODE) { throw "publish host failed" }
dotnet publish "$repo\src\Seekbeam.App\Seekbeam.App.csproj" @common
if ($LASTEXITCODE) { throw "publish app failed" }
Remove-Item "$pub\*.pdb" -ErrorAction SilentlyContinue
# Licenses travel with the binaries (MIT for Seekbeam, notices for the redistributed components).
Copy-Item "$repo\LICENSE", "$repo\THIRD-PARTY-NOTICES.md" $pub
dotnet build "$repo\installer\Seekbeam.Installer.wixproj" -c Release -p:PublishDir="$pub\" -o $dist
if ($LASTEXITCODE) { throw "msi failed" }
Remove-Item "$dist\*.wixpdb" -ErrorAction SilentlyContinue
# Portable edition: same files + portable.txt (data stored next to the executable, no automatic startup).
# Only the extractor sandbox profile is created outside it (Windows-managed, removed by --purge-data, TD-17).
$portable = Join-Path $out "portable\Seekbeam"
Remove-Item -Recurse -Force (Join-Path $out "portable") -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $portable | Out-Null
Copy-Item "$pub\*" $portable -Recurse
Set-Content "$portable\portable.txt" "Seekbeam portable: data is stored in the Data folder next to Seekbeam.exe."
Compress-Archive -Path "$portable\*" -DestinationPath "$dist\Seekbeam-$version-$($Runtime -replace '^win-','')-portable.zip" -Force
# Checksums published next to the files (sha256sum format).
Get-ChildItem $dist -File | Where-Object Name -ne "SHA256SUMS.txt" | ForEach-Object {
  "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content (Join-Path $dist "SHA256SUMS.txt") -Encoding ascii
Get-ChildItem $dist | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}}
