$ErrorActionPreference = "Stop"
$version = "1.3.1"
$repoRoot = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repoRoot "REPO_Active\REPO_Active.csproj"
$desktop = [Environment]::GetFolderPath("Desktop")
$outZip = Join-Path $desktop ("REPO_Active_v{0}_r2modman.zip" -f $version)
$staging = Join-Path $repoRoot "dist\REPO_Active_Package"
if(Test-Path $staging){ Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Write-Host "== Build =="
dotnet build $proj -c Release
$dll = Join-Path $repoRoot "REPO_Active\bin\Release\netstandard2.1\REPO_Active.dll"
if(!(Test-Path $dll)){ throw "DLL not found: $dll" }
$manifest = Join-Path $repoRoot "manifest.json"
if(!(Test-Path $manifest)){ throw "manifest.json missing at repo root." }
Copy-Item $manifest (Join-Path $staging "manifest.json") -Force
$readme = Join-Path $repoRoot "README.md"
if(Test-Path $readme){ Copy-Item $readme (Join-Path $staging "README.md") -Force }
$icon = Join-Path $repoRoot "icon.png"
if(Test-Path $icon){ Copy-Item $icon (Join-Path $staging "icon.png") -Force }
$pluginDir = Join-Path $staging "BepInEx\plugins\REPO_Active"
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item $dll (Join-Path $pluginDir "REPO_Active.dll") -Force
Write-Host "== Pack ZIP via tar.exe =="
if(Test-Path $outZip){ Remove-Item $outZip -Force }
Push-Location $staging
tar.exe -a -c -f $outZip *
Pop-Location
Write-Host "DONE => $outZip"
