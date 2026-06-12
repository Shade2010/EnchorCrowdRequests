<#
  Installs EnchorCrowdRequests.dll into Clone Hero's BepInEx\plugins folder.
  Auto-detects the install (Player.log -> Steam libraries -> common locations).
  Run this from the unzipped installer folder (it expects EnchorCrowdRequests.dll next to it).

    Right-click -> Run with PowerShell, or:  .\install.ps1
    .\install.ps1 -CloneHeroDir "D:\Games\Clone Hero"
#>
param([string]$CloneHeroDir = "")
$ErrorActionPreference = "Stop"

function Test-CH([string]$d) { if ([string]::IsNullOrWhiteSpace($d)) { return $false }; Test-Path (Join-Path $d "Clone Hero.exe") }
function Find-CH {
    $log = Join-Path $env:USERPROFILE "AppData\LocalLow\srylain Inc_\Clone Hero\Player.log"
    if (Test-Path $log) {
        $m = Select-String -Path $log -Pattern "([A-Za-z]:[\\/].*?)[\\/]Clone Hero_Data" -EA SilentlyContinue | Select-Object -First 1
        if ($m) { $p = ($m.Matches[0].Groups[1].Value) -replace '/', '\'; if (Test-CH $p) { return $p } }
    }
    $vdf = "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) { foreach ($l in Get-Content $vdf) { if ($l -match '"path"\s+"(.+?)"') { $c = Join-Path ($Matches[1] -replace '\\\\', '\') "steamapps\common\Clone Hero"; if (Test-CH $c) { return $c } } } }
    foreach ($c in @((Join-Path $env:ProgramFiles "Clone Hero"), (Join-Path ${env:ProgramFiles(x86)} "Clone Hero"), (Join-Path $env:LOCALAPPDATA "Programs\Clone Hero"), "C:\Games\Clone Hero")) { if (Test-CH $c) { return $c } }
    return $null
}

$dll = Join-Path $PSScriptRoot "EnchorCrowdRequests.dll"
if (-not (Test-Path $dll)) { throw "EnchorCrowdRequests.dll not found next to this script. Keep them together." }

if (-not (Test-CH $CloneHeroDir)) { Write-Host "Locating Clone Hero..."; $CloneHeroDir = Find-CH }
if (-not (Test-CH $CloneHeroDir)) { throw "Could not find Clone Hero. Re-run as:  .\install.ps1 -CloneHeroDir `"<folder with Clone Hero.exe>`"" }
Write-Host "Clone Hero: $CloneHeroDir"

$bep = Join-Path $CloneHeroDir "BepInEx"
if (-not (Test-Path $bep)) {
    Write-Warning "BepInEx isn't installed yet. Install BepInEx 6 (IL2CPP, win-x64) and run the game once, then re-run this."
    return
}
$plugins = Join-Path $bep "plugins"
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Copy-Item $dll $plugins -Force
Write-Host "Installed: $plugins\EnchorCrowdRequests.dll"
Write-Host "Done. Launch Clone Hero and press F9 to open the song browser."
