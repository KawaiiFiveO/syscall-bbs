# Builds the launcher and the chosen mods and copies them into the game folder.
# The game's own files are never modified. Your settings are kept if they already exist.
# Paths given as arguments work as with Install.cmd: a game folder, Last Call BBS.exe, or a mod folder.
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

# The mods offered, in menu order. True means picked by default.
$bundled = [ordered]@{ SkipLoading = $true; HookLogger = $false; FoodCourtTools = $false; YomielPlayer = $false }
$entries = @()
foreach ($name in $bundled.Keys) { $entries += Get-ModEntry (Join-Path $PSScriptRoot "..\mods\$name") $bundled[$name] }
$entries += $dropped.Mods | ForEach-Object { Get-ModEntry $_ $true }
$picked = @(Select-Mods $entries $GameDir ($Mods -join ","))
$pickedNames = @($picked | ForEach-Object Folder)

if ("YomielPlayer" -in $pickedNames -and -not (Test-Path (Join-Path $PSScriptRoot "..\yomiel\Yomiel\Yomiel.csproj"))) {
    throw "YomielPlayer needs the yomiel submodule, which isn't checked out. Run: git submodule update --init"
}

dotnet build "$PSScriptRoot\LastCallBBS.Launcher.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed: launcher" }

$out = Join-Path $PSScriptRoot "bin\Release"
foreach ($f in @("Last Call BBS (Modded).exe", "Last Call BBS (Modded).exe.config", "0Harmony.dll", "Mono.Cecil.dll", "LastCallBBS.ModAPI.dll")) {
    Copy-Item (Join-Path $out $f) $GameDir -Force
}

foreach ($mod in $picked) {
    if (-not $bundled.Contains($mod.Folder)) {
        # A dropped mod folder, already built.
        Copy-ModFolder $mod.Path $GameDir
        Write-Host "Installed $($mod.Title)"
        continue
    }
    $name = $mod.Folder
    $source = $mod.Path
    dotnet build "$source\$name.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $name" }
    $target = Join-Path $GameDir "mods\$name"
    New-Item -ItemType Directory -Force $target | Out-Null
    # The mod's DLLs, without the ones the launcher ships. Existing settings are kept.
    Get-ChildItem (Join-Path $source "bin\Release") -Filter *.dll |
        Where-Object { $_.Name -notin "0Harmony.dll", "LastCallBBS.ModAPI.dll" } | Copy-Item -Destination $target -Force
    Get-ChildItem $source -File | Where-Object { $_.Extension -in ".png", ".md" -or $_.Name -eq "mod.ini" } | Copy-Item -Destination $target -Force
    # SkipLoading's config.ini is written below, carrying over older settings.
    $config = Join-Path $source "config.ini"
    if ($name -ne "SkipLoading" -and (Test-Path $config) -and -not (Test-Path (Join-Path $target "config.ini"))) { Copy-Item $config $target }
    Write-Host "Installed $($mod.Title)"
}
Show-Skipped $entries $picked $GameDir

# These settings used to live in the launcher's ini. Carry the values over once, then remove them there.
$ini = Join-Path $GameDir "LastCallBBS.Launcher.ini"
$modSource = Join-Path $PSScriptRoot "..\mods\SkipLoading"
$config = Join-Path $GameDir "mods\SkipLoading\config.ini"
$moved = @("InstantDownloads", "NoDownloadCooldown", "SkipTwelveHourWait")
$old = if (Test-Path $ini) { @(Get-Content $ini) } else { @() }
if ("SkipLoading" -in $pickedNames -and -not (Test-Path $config)) {
    $lines = @(Get-Content (Join-Path $modSource "config.ini"))
    foreach ($key in $moved) {
        $found = $old | Where-Object { $_ -match "^\s*$key\s*=" } | Select-Object -Last 1
        if ($found) { $lines = $lines | ForEach-Object { if ($_ -match "^\s*$key\s*=") { $found.Trim() } else { $_ } } }
    }
    Set-Content -Path $config -Value $lines -Encoding UTF8
}
if ((Test-Path $config) -and ($old | Where-Object { $_ -match "^\s*($($moved -join '|'))\s*=" })) {
    $keep = [System.Collections.ArrayList]@($old)
    for ($i = $keep.Count - 1; $i -ge 0; $i--) {
        if ($keep[$i] -notmatch "^\s*($($moved -join '|'))\s*=") { continue }
        # Remove the key, the comment block above it and one blank line.
        $first = $i
        while ($first -gt 0 -and $keep[$first - 1] -match "^\s*#" -and $keep[$first - 1] -notmatch "launcher settings|Values are") { $first-- }
        $end = $i
        if ($end + 1 -lt $keep.Count -and $keep[$end + 1] -match "^\s*$") { $end++ }
        $keep.RemoveRange($first, $end - $first + 1)
        $i = $first
    }
    Set-Content -Path $ini -Value $keep -Encoding UTF8
}
if (-not (Test-Path $ini)) {
    Copy-Item (Join-Path $PSScriptRoot "LastCallBBS.Launcher.ini") $ini
}

& (Join-Path $GameDir "Last Call BBS (Modded).exe") --verify | Out-Null
$exitCode = $LASTEXITCODE
Get-Content (Join-Path $GameDir "LastCallBBS.Launcher.log")
if ($exitCode -ne 0) { Write-Warning "Verification reported problems (see log above)." }
else { Write-Host "Installed. Launch 'Last Call BBS (Modded).exe' in $GameDir" }
