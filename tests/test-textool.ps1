# Checks textool on made-up images, then confirms every .tex in each installed game build unpacks
# and packs back unchanged. Game folders are only read. Exit code 0 means every check passed.
param(
    # Defaults to every installed copy found in the registry. Pass "none" to skip the game files.
    [string[]]$GameDirs = @()
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repo = Split-Path $PSScriptRoot
. (Join-Path $repo "launcher\FindGame.ps1")
$root = Join-Path $env:TEMP "lastcallbbs-textool-tests"
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null

& dotnet build (Join-Path $repo "tools\textool\textool.csproj") -c Release -v q -o "$root\bin" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not build textool" }
$textool = Join-Path $root "bin\textool.exe"

$failed = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  pass  $what" }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failed++ }
}

function Run([string[]]$arguments) {
    $quoted = $arguments | ForEach-Object { "`"$_`"" }
    $process = Start-Process -FilePath $textool -ArgumentList $quoted -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput "$root\stdout.txt" -RedirectStandardError "$root\stderr.txt"
    return @{ Code = $process.ExitCode; Output = (Get-Content "$root\stdout.txt" -Raw) + (Get-Content "$root\stderr.txt" -Raw) }
}

# Writes a PNG from "a,r,g,b" pixels, separated by spaces, in rows separated by "|".
function New-Png([string]$path, [string]$pixels) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $rows = @($pixels.Split("|") | ForEach-Object { , @($_.Trim().Split(" ")) })
    $bitmap = New-Object Drawing.Bitmap $rows[0].Count, $rows.Count
    for ($y = 0; $y -lt $rows.Count; $y++) {
        for ($x = 0; $x -lt $rows[$y].Count; $x++) {
            $c = $rows[$y][$x].Split(",") | ForEach-Object { [int]$_ }
            $bitmap.SetPixel($x, $y, [Drawing.Color]::FromArgb($c[0], $c[1], $c[2], $c[3]))
        }
    }
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Write-Lf([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n")) }

$src = Join-Path $root "src"
New-Png "$src\art\icon.png" "255,200,10,30 128,255,255,255 0,0,0,0 | 64,90,180,20 255,0,0,0 200,1,2,3"
Write-Lf "$src\art\icon.png.cfg" "# a comment`ncrop-offset: 4,-2`ncrop-size: 10,12`n"
New-Png "$src\art\anim.array\000.png" "255,1,2,3 255,4,5,6"
New-Png "$src\art\anim.array\001.png" "255,7,8,9 | 255,10,11,12"
New-Png "$src\art\glyph.png" "255,0,0,0 255,128,128,128 255,255,255,255"
Write-Lf "$src\art\glyph.png.cfg" "format: r`n"
New-Png "$src\art\glow.png" "255,40,80,120 255,0,0,0"
Write-Lf "$src\art\glow.png.cfg" "blend: additive`n"

$out = Join-Path $root "out"
$result = Run @("pack", $src, $out)
Check ($result.Code -eq 0 -and (Test-Path "$out\art\icon.tex") -and (Test-Path "$out\art\anim.array.tex")) "pack writes a folder of PNGs and .array folders"

$result = Run @("info", "$out\art\icon.tex")
Check ($result.Output -match "0: 3x2 rgba, crop 0,0,3,2, offset 4,-2, crop size 10,12") "settings reach the header"
$result = Run @("info", "$out\art\anim.array.tex")
Check ($result.Output -match "2 frames" -and $result.Output -match "1: 1x2 rgba") ".array folders become one file with a frame per PNG"
$result = Run @("info", "$out\art\glyph.tex")
Check ($result.Output -match "0: 3x1 r,") "format: r writes a one-channel frame"

$result = Run @("check", $out)
Check ($result.Code -eq 0 -and $result.Output -match "4 files, 5 frames: all unchanged") "packed files pass check"

$back = Join-Path $root "back"
$result = Run @("unpack", $out, $back)
Check ($result.Code -eq 0 -and (Test-Path "$back\art\anim.array\001.png") -and (Test-Path "$back\art\icon.png")) "unpack mirrors the folders"
Check ((Get-Content "$back\art\icon.png.cfg" -Raw) -eq "crop-offset: 4,-2`ncrop-size: 10,12`n") "unpack writes the settings"
Check ((Get-Content "$back\art\glow.png.cfg" -Raw) -eq "blend: additive`n") "unpack finds additive frames"
Check (-not (Test-Path "$back\art\anim.array\000.png.cfg")) "no settings file when every setting is the default"

$again = Join-Path $root "again"
$result = Run @("pack", $back, $again)
$same = $true
foreach ($name in "icon.tex", "anim.array.tex", "glyph.tex", "glow.tex") {
    if ((Get-FileHash "$out\art\$name").Hash -ne (Get-FileHash "$again\art\$name").Hash) { $same = $false }
}
Check ($result.Code -eq 0 -and $same) "unpacked files pack to identical bytes"

$result = Run @("pack", "$src\art\anim.array", "$root\single")
Check ($result.Code -eq 0 -and (Test-Path "$root\single\anim.array.tex")) "pack takes one .array folder"
$result = Run @("pack", "$src\art\icon.png", "$root\single")
Check ($result.Code -eq 0 -and (Test-Path "$root\single\icon.tex")) "pack takes one PNG"

# Each case gives the icon a bad settings file, which pack must refuse.
$bad = @(
    @{ Name = "unknown key"; Text = "colour: red"; Expect = "unknown key colour" }
    @{ Name = "not a number"; Text = "crop-offset: 4,x"; Expect = "x isn't a number" }
    @{ Name = "wrong count"; Text = "crop-size: 4"; Expect = "expected 2 numbers" }
    @{ Name = "key twice"; Text = "colorblind-mode: 1`ncolorblind-mode: 2"; Expect = "given twice" }
    @{ Name = "crop outside the image"; Text = "crop: 1,0,3,2"; Expect = "isn't inside the 3x2 image" }
    @{ Name = "additive one-channel"; Text = "format: r`nblend: additive"; Expect = "can't be used with format: r" }
    @{ Name = "no colon"; Text = "blend additive"; Expect = "expected key: value" }
)
foreach ($case in $bad) {
    Write-Lf "$src\art\icon.png.cfg" $case.Text
    $result = Run @("pack", "$src\art\icon.png", "$root\bad")
    Check ($result.Code -ne 0 -and $result.Output -match [regex]::Escape($case.Expect) -and -not (Test-Path "$root\bad\icon.tex")) "refuses: $($case.Name)"
}

$game = Join-Path $root "fakegame"
New-Item -ItemType Directory -Force $game | Out-Null
Write-Lf "$game\Last Call BBS.exe" ""
$result = Run @("pack", "$src\art\glow.png", "$game\Content\Packed\textures")
Check ($result.Code -ne 0 -and -not (Test-Path "$game\Content")) "won't write into a game's own folders"
$result = Run @("pack", "$src\art\glow.png", "$game\mods\Test\Content\Packed\textures")
Check ($result.Code -eq 0 -and (Test-Path "$game\mods\Test\Content\Packed\textures\glow.tex")) "writes into a game's mods folder"

if ($GameDirs -notcontains "none") {
    if (-not $GameDirs) { $GameDirs = @(Find-GameDirs) }
    if (-not $GameDirs) { Write-Host "  skip  no installed game found for the game-file check" }
    foreach ($dir in $GameDirs) {
        $result = Run @("check", (Join-Path $dir "Content\Packed"))
        Check ($result.Code -eq 0) "every .tex in $dir unpacks and packs back unchanged ($($result.Output.Trim().Split("`n")[-1].Trim()))"
    }
}

if ($failed) { Write-Host "$failed failed" -ForegroundColor Red; exit 1 }
Write-Host "All passed"
