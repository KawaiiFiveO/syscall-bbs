# Builds the launcher and bundled mods and copies them into the game folder.
# The game's own files are never modified. Your settings are kept if they already exist.
param(
    # The folder containing the game. If omitted, it's found from the registry or asked for.
    [string]$GameDir = "",
    # Also installs HookLogger, which prints every hook as it fires.
    [switch]$WithHookLogger
)
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "FindGame.ps1")
if (-not $GameDir) { $GameDir = Select-GameDir }

if (-not (Test-Path (Join-Path $GameDir "Last Call BBS.exe"))) {
    throw "Last Call BBS.exe not found in $GameDir"
}

$modSource = Join-Path $PSScriptRoot "..\mods\SkipLoading"
foreach ($project in @("$PSScriptRoot\LastCallBBS.Launcher.csproj", "$modSource\SkipLoading.csproj")) {
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

$out = Join-Path $PSScriptRoot "bin\Release"
foreach ($f in @("Last Call BBS (Modded).exe", "Last Call BBS (Modded).exe.config", "0Harmony.dll", "Mono.Cecil.dll", "LastCallBBS.ModAPI.dll")) {
    Copy-Item (Join-Path $out $f) $GameDir -Force
}

$modDir = Join-Path $GameDir "mods\SkipLoading"
New-Item -ItemType Directory -Force $modDir | Out-Null
Copy-Item (Join-Path $modSource "bin\Release\SkipLoading.dll") $modDir -Force
Copy-Item (Join-Path $modSource "mod.ini") $modDir -Force

if ($WithHookLogger) {
    $source = Join-Path $PSScriptRoot "..\mods\HookLogger"
    dotnet build "$source\HookLogger.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed: HookLogger" }
    $target = Join-Path $GameDir "mods\HookLogger"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $source "bin\Release\HookLogger.dll") $target -Force
    Copy-Item (Join-Path $source "mod.ini") $target -Force
}

# These settings used to live in the launcher's ini. Carry the values over once, then remove them there.
$ini = Join-Path $GameDir "LastCallBBS.Launcher.ini"
$config = Join-Path $modDir "config.ini"
$moved = @("InstantDownloads", "NoDownloadCooldown", "SkipTwelveHourWait")
$old = if (Test-Path $ini) { @(Get-Content $ini) } else { @() }
if (-not (Test-Path $config)) {
    $lines = @(Get-Content (Join-Path $modSource "config.ini"))
    foreach ($key in $moved) {
        $found = $old | Where-Object { $_ -match "^\s*$key\s*=" } | Select-Object -Last 1
        if ($found) { $lines = $lines | ForEach-Object { if ($_ -match "^\s*$key\s*=") { $found.Trim() } else { $_ } } }
    }
    Set-Content -Path $config -Value $lines -Encoding UTF8
}
if ($old | Where-Object { $_ -match "^\s*($($moved -join '|'))\s*=" }) {
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
