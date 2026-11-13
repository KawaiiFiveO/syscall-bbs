# Builds a release zip that installs without the .NET SDK. Output goes to dist\.
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot
if (-not (Test-Path (Join-Path $repo "yomiel\Yomiel\Yomiel.csproj"))) {
    throw "The yomiel submodule isn't checked out. Run: git submodule update --init"
}
$name = "SysCallBBS-$Version"
$dist = Join-Path $repo "dist"
$stage = Join-Path $dist $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

function Build([string]$project) {
    dotnet build $project -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

function Add-Mod([string]$mod, [string]$folder) {
    $source = Join-Path $repo "mods\$mod"
    Build "$source\$mod.csproj"
    $target = Join-Path $stage "$folder\$mod"
    New-Item -ItemType Directory -Force $target | Out-Null
    # The mod's DLLs, without the ones the launcher ships, plus its ini files, icons and readme.
    Get-ChildItem "$source\bin\Release" -Filter *.dll |
        Where-Object { $_.Name -notin "0Harmony.dll", "LastCallBBS.ModAPI.dll" } | Copy-Item -Destination $target
    Get-ChildItem $source -File | Where-Object { $_.Extension -in ".ini", ".png", ".md" } | Copy-Item -Destination $target
}

Build "$PSScriptRoot\LastCallBBS.Launcher.csproj"
$files = Join-Path $stage "files"
New-Item -ItemType Directory -Force $files | Out-Null
$out = Join-Path $PSScriptRoot "bin\Release"
foreach ($f in @("Last Call BBS (Modded).exe", "Last Call BBS (Modded).exe.config", "0Harmony.dll", "Mono.Cecil.dll", "LastCallBBS.ModAPI.dll")) {
    Copy-Item (Join-Path $out $f) $files
}
Copy-Item "$PSScriptRoot\LastCallBBS.Launcher.ini" $files

Add-Mod "SkipLoading" "mods"
Add-Mod "HookLogger" "optional-mods"
Add-Mod "FoodCourtTools" "optional-mods"
Add-Mod "YomielPlayer" "optional-mods"

Build "$repo\tools\fctext\fctext.csproj"
$fctext = Join-Path $stage "tools\fctext\bin\Release\net472"
New-Item -ItemType Directory -Force $fctext | Out-Null
Copy-Item "$repo\tools\fctext\bin\Release\net472\fctext.exe", "$repo\tools\fctext\bin\Release\net472\fctext.exe.config" $fctext
Copy-Item "$repo\tools\fcrun.ps1" (Join-Path $stage "tools")

Copy-Item "$PSScriptRoot\install-release.ps1", "$PSScriptRoot\InstallMenu.ps1", "$PSScriptRoot\FindGame.ps1", "$repo\README.md" $stage
New-Item -ItemType Directory -Force (Join-Path $stage "docs") | Out-Null
Copy-Item "$repo\docs\making-mods.md", "$repo\docs\food-court-format.md", "$repo\docs\food-court-netlist.md" (Join-Path $stage "docs")
# Install.cmd: double-click it, or drop the game's folder (or mod folders) on it.
$cmd = @(
    "@echo off",
    "powershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0install-release.ps1`" %*",
    "echo.",
    "pause"
)
Set-Content (Join-Path $stage "Install.cmd") ($cmd -join "`r`n") -Encoding Ascii

$harmonyLicense = Join-Path $env:USERPROFILE ".nuget\packages\lib.harmony\2.3.3\LICENSE"
$notices = @("Harmony 2.3.3", "", (Get-Content $harmonyLicense -Raw), "", "Mono.Cecil 0.11.5", "", (Get-Content "$PSScriptRoot\third-party\Mono.Cecil-LICENSE.txt" -Raw))
Set-Content (Join-Path $stage "THIRD-PARTY-NOTICES.txt") $notices -Encoding UTF8

# Releases never contain story files.
$stories = @(Get-ChildItem $stage -Recurse -File -Include *.z1, *.z2, *.z3, *.z4, *.z5, *.z6, *.z7, *.z8, *.zblorb, *.zlb)
if ($stories) { throw "Story files found in the release: $($stories.FullName -join ', ')" }

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Write-Host "Created $zip"
