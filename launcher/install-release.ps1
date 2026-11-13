# Installs a prebuilt release into the game folder. The game's own files are never modified,
# and existing settings are kept.
#
# Paths dropped on Install.cmd arrive as arguments: the game's folder, Last Call BBS.exe or a
# shortcut to it picks the game, and a folder with a mod.ini is offered in the mod list.
[CmdletBinding(PositionalBinding = $false)]
param(
    # The folder containing the game. If omitted, it's found from the registry or asked for.
    [string]$GameDir = "",
    # Mods to install without asking: "all", "none", or folder names separated by commas.
    [string[]]$Mods = @(),
    # Removes the launcher and mods\ (to the Recycle Bin) without asking.
    [switch]$Uninstall,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Paths = @()
)
$ErrorActionPreference = "Stop"
# Show only the message: this runs from Install.cmd, for players.
trap {
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

. (Join-Path $PSScriptRoot "InstallMenu.ps1")
. (Join-Path $PSScriptRoot "FindGame.ps1")

$dropped = Split-Dropped $Paths
if ($GameDir) {
    $found = Resolve-GamePath $GameDir
    if (-not $found) { throw "Last Call BBS.exe not found in $GameDir" }
    $GameDir = $found
}
elseif ($dropped.GameDir) { $GameDir = $dropped.GameDir }
else { $GameDir = Select-GameDir }
Write-Host "Game folder: $GameDir"
Assert-CanInstall $GameDir

# On an existing installation, ask whether to update or uninstall, unless the arguments already say.
if ($Uninstall -or ((Test-Installed $GameDir) -and -not $Mods -and -not $dropped.Mods -and (Select-Action) -eq "uninstall")) {
    Uninstall-SysCall $GameDir (-not $Uninstall)
    return
}

$entries = @()
$entries += Get-ChildItem (Join-Path $PSScriptRoot "mods") -Directory | ForEach-Object { Get-ModEntry $_.FullName $true }
$entries += Get-ChildItem (Join-Path $PSScriptRoot "optional-mods") -Directory | ForEach-Object { Get-ModEntry $_.FullName $false }
$entries += $dropped.Mods | ForEach-Object { Get-ModEntry $_ $true }
$picked = @(Select-Mods $entries $GameDir ($Mods -join ","))

function Copy-New([string]$source, [string]$target) {
    if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $source $target }
}

foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot "files") -File) {
    if ($file.Extension -eq ".ini") { Copy-New $file.FullName (Join-Path $GameDir $file.Name) }
    else { Copy-Item -LiteralPath $file.FullName $GameDir -Force }
}
foreach ($mod in $picked) {
    Copy-ModFolder $mod.Path $GameDir
    Write-Host "Installed $($mod.Title)"
}
Show-Skipped $entries $picked $GameDir

Write-Host "Checking the installation..."
$process = Start-Process -FilePath (Join-Path $GameDir "Last Call BBS (Modded).exe") -ArgumentList "--verify" -Wait -PassThru
if ($process.ExitCode -ne 0) { Write-Warning "Verification reported problems. See LastCallBBS.Launcher.log in $GameDir." }
else { Write-Host "Done. Start the game with 'Last Call BBS (Modded).exe' in $GameDir" }
