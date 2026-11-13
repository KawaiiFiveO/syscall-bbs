# Decompiles the game into readable C#: ilspycmd, then strtool --rewrite, which replaces every
# encrypted string with its text, applies the names in strtool\names.tsv and gives every other
# obfuscated name a short alias (listed in <Out>\aliases.tsv).
#
#   tools\decompile.ps1 -Out <new folder> [-Game <game folder or exe>] [-Comments] [-NoAliases] [-IlSpyCmd <path>]
#
# Without -Game it uses the installed copy found in the registry, if there's exactly one.
# -Comments keeps each decoder call and adds its text as a comment after it.
# -NoAliases leaves obfuscated names as they are.
# The output is the game's code: keep it out of the repo.
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Game,
    [switch]$Comments,
    [switch]$NoAliases,
    [string]$IlSpyCmd
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\launcher\FindGame.ps1")

if (-not $Game) {
    $dirs = @(Find-GameDirs)
    if ($dirs.Count -eq 0) { throw "Last Call BBS wasn't found. Pass -Game <game folder or exe>." }
    if ($dirs.Count -gt 1) { throw "Last Call BBS is installed in more than one place. Pass -Game with one of:`n  $($dirs -join "`n  ")" }
    $Game = $dirs[0]
}
if (Test-Path -LiteralPath $Game -PathType Container) { $Game = Join-Path $Game "Last Call BBS.exe" }
if (-not (Test-Path -LiteralPath $Game -PathType Leaf)) { throw "Not found: $Game" }
$Game = (Resolve-Path -LiteralPath $Game).Path

if ((Test-Path -LiteralPath $Out) -and (Get-ChildItem -LiteralPath $Out -Force | Select-Object -First 1)) {
    throw "$Out isn't empty. Pick a new folder."
}

if (-not $IlSpyCmd) {
    $command = Get-Command ilspycmd -ErrorAction SilentlyContinue
    if ($command) { $IlSpyCmd = $command.Source }
    else {
        $IlSpyCmd = @("ilspycmd.exe", "ilspycmd.cmd") | ForEach-Object { Join-Path $env:USERPROFILE ".dotnet\tools\$_" } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $IlSpyCmd -or -not (Test-Path -LiteralPath $IlSpyCmd)) {
    throw "ilspycmd wasn't found. Install it with 'dotnet tool install -g ilspycmd', or pass -IlSpyCmd <path>."
}

# Native tools report on stderr; failures are caught by exit code instead.
$ErrorActionPreference = "Continue"
$strtoolDir = Join-Path $PSScriptRoot "strtool"
dotnet build $strtoolDir -c Release -nologo -v q
if ($LASTEXITCODE) { throw "strtool didn't build." }
$strtool = Join-Path $strtoolDir "bin\Release\net472\strtool.exe"

Write-Host "Decompiling $Game"
& $IlSpyCmd -p -o $Out $Game
if ($LASTEXITCODE) { throw "ilspycmd failed (exit code $LASTEXITCODE)." }

$strtoolArgs = @($Game, "--rewrite", $Out, "--names", (Join-Path $strtoolDir "names.tsv"))
if ($Comments) { $strtoolArgs += "--comments" }
if ($NoAliases) { $strtoolArgs += "--no-aliases" }
& $strtool @strtoolArgs
if ($LASTEXITCODE) { throw "strtool --rewrite failed (exit code $LASTEXITCODE)." }
Write-Host "Done: $((Resolve-Path -LiteralPath $Out).Path)"
