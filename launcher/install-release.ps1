# Installs a prebuilt release into the game folder. The game's own files are never modified,
# and existing settings are kept.
param(
    [string]$GameDir = "",
    [switch]$WithHookLogger,
    [switch]$WithFoodCourtTools
)
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "FindGame.ps1")
if (-not $GameDir) { $GameDir = Select-GameDir }
if (-not (Test-Path -LiteralPath (Join-Path $GameDir "Last Call BBS.exe"))) { throw "Last Call BBS.exe not found in $GameDir" }

function Copy-New([string]$source, [string]$target) {
    if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $source $target }
}

function Install-Mod([string]$source) {
    $target = Join-Path $GameDir "mods\$(Split-Path $source -Leaf)"
    New-Item -ItemType Directory -Force $target | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $source -File) {
        if ($file.Name -eq "config.ini") { Copy-New $file.FullName (Join-Path $target $file.Name) }
        else { Copy-Item -LiteralPath $file.FullName $target -Force }
    }
}

foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot "files") -File) {
    if ($file.Extension -eq ".ini") { Copy-New $file.FullName (Join-Path $GameDir $file.Name) }
    else { Copy-Item -LiteralPath $file.FullName $GameDir -Force }
}
Get-ChildItem (Join-Path $PSScriptRoot "mods") -Directory | ForEach-Object { Install-Mod $_.FullName }
if ($WithHookLogger) { Install-Mod (Join-Path $PSScriptRoot "optional-mods\HookLogger") }
if ($WithFoodCourtTools) { Install-Mod (Join-Path $PSScriptRoot "optional-mods\FoodCourtTools") }

$process = Start-Process -FilePath (Join-Path $GameDir "Last Call BBS (Modded).exe") -ArgumentList "--verify" -Wait -PassThru
if ($process.ExitCode -ne 0) { Write-Warning "Verification reported problems. See LastCallBBS.Launcher.log in $GameDir." }
else { Write-Host "Installed. Launch 'Last Call BBS (Modded).exe' in $GameDir" }
