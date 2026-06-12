<#
  Builds EnchorCrowdRequests and packages a shareable installer into dist\EnchorCrowdRequests-Installer.zip,
  containing EnchorCrowdRequests.dll, install.ps1 (auto-detects Clone Hero), and a short README.
#>
param([string]$CloneHeroDir = "")
$ErrorActionPreference = "Stop"

# Build first (reuses build.ps1's detection/build, installs to plugins too).
& (Join-Path $PSScriptRoot "build.ps1") -CloneHeroDir $CloneHeroDir

$dll = Join-Path $PSScriptRoot "src\bin\Release\EnchorCrowdRequests.dll"
if (-not (Test-Path $dll)) { throw "Build output missing: $dll" }

$dist = Join-Path $PSScriptRoot "dist"
$stage = Join-Path $dist "EnchorCrowdRequests"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item $dll $stage -Force
Copy-Item (Join-Path $PSScriptRoot "install.ps1") $stage -Force

@"
Enchor - Crowd Requests - enchor.us song browser for Clone Hero
=================================================
1. Make sure BepInEx 6 (IL2CPP, win-x64) is installed in your Clone Hero folder and you've run the game once.
2. Right-click install.ps1 -> Run with PowerShell  (or copy EnchorCrowdRequests.dll into <Clone Hero>\BepInEx\plugins).
3. Launch Clone Hero and press F9.
"@ | Set-Content (Join-Path $stage "READ ME FIRST.txt") -Encoding utf8

$zip = Join-Path $dist "EnchorCrowdRequests-Installer.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
Write-Host "Packaged: $zip"
