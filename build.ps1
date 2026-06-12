<#
  Builds EnchorCrowdRequests.dll and installs it into Clone Hero's BepInEx\plugins folder.
  The Clone Hero install is auto-detected (Player.log -> Steam -> common locations).

    .\build.ps1
    .\build.ps1 -CloneHeroDir "D:\Games\Clone Hero"
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

if (-not (Test-CH $CloneHeroDir)) { Write-Host "Auto-detecting Clone Hero..."; $CloneHeroDir = Find-CH }
if (-not (Test-CH $CloneHeroDir)) { throw "Could not find Clone Hero. Pass -CloneHeroDir `"<folder with Clone Hero.exe>`"." }
Write-Host "Clone Hero: $CloneHeroDir"

$interop = Join-Path $CloneHeroDir "BepInEx\interop"
if (-not (Test-Path (Join-Path $interop "UnityEngine.UI.dll"))) {
    throw "BepInEx interop not found at:`n  $interop`nInstall BepInEx 6 (IL2CPP, x64) and run the game once, then re-run."
}

$localDotnet = Join-Path (Split-Path $PSScriptRoot -Parent) "dotnet\dotnet.exe"
if (Test-Path $localDotnet) { $dotnet = $localDotnet; $env:DOTNET_ROOT = Split-Path $localDotnet -Parent }
elseif (Get-Command dotnet -EA SilentlyContinue) { $dotnet = "dotnet" }
else { throw "No .NET SDK found (expected $localDotnet or 'dotnet' on PATH)." }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"

$proj = Join-Path $PSScriptRoot "src\EnchorCrowdRequests.csproj"
Write-Host "Building..."
& $dotnet build $proj -c Release -p:CloneHeroDir="$CloneHeroDir"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dll = Join-Path $PSScriptRoot "src\bin\Release\EnchorCrowdRequests.dll"
$plugins = Join-Path $CloneHeroDir "BepInEx\plugins"
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
try { Copy-Item $dll $plugins -Force -EA Stop; Write-Host "Installed: $plugins\EnchorCrowdRequests.dll"; Write-Host "Launch Clone Hero and press F9." }
catch { Write-Warning "Couldn't copy to $plugins ($($_.Exception.Message)). DLL is at: $dll" }
