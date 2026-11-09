# Checks fctext's text import. Needs no game install. Exit code 0 means every check passed.
$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot
$root = Join-Path $env:TEMP "lastcallbbs-fctext-tests"
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null

& dotnet build (Join-Path $repo "tools\fctext\fctext.csproj") -c Release -v q -o "$root\bin" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not build fctext" }
$fctext = Join-Path $root "bin\fctext.exe"

$valid = @"
# A comment
level WineBar
name Test Solution
version 1013
solved ticks=8 cost=61

m0 PrimaryInputForWineBar fixed rack=0,2 floor=2,6 rot=3
m1 Output fixed rack=0,0 floor=2,0 rot=3
m2 CounterSmall rack=8,2 values=1,-1
m3 MixerSimple rack=2,2

m0.START -> m3.INPUT_A
m3.OUTPUT_A -> m2.TRIGGER_A
"@

# Each case replaces one piece of the valid text and must be refused with this message.
$bad = @(
    @{ Name = "unknown level"; From = "level WineBar"; To = "level Nowhere"; Expect = "unknown level" }
    @{ Name = "missing level"; From = "level WineBar"; To = ""; Expect = "missing level" }
    @{ Name = "empty name"; From = "name Test Solution"; To = "name "; Expect = "name is empty" }
    @{ Name = "version too new"; From = "version 1013"; To = "version 1014"; Expect = "isn't one the game accepts" }
    @{ Name = "version too old"; From = "version 1013"; To = "version 1009"; Expect = "isn't one the game accepts" }
    @{ Name = "unknown module"; From = "m3 MixerSimple"; To = "m3 Blender"; Expect = "unknown module type" }
    @{ Name = "modules out of order"; From = "m3 MixerSimple"; To = "m7 MixerSimple"; Expect = "numbered in order" }
    @{ Name = "unknown field"; From = "rack=2,2"; To = "rack=2,2 colour=1"; Expect = "unknown field" }
    @{ Name = "bad position"; From = "rack=2,2"; To = "rack=2"; Expect = "expected x,y" }
    @{ Name = "bad rotation"; From = "floor=2,0 rot=3"; To = "floor=2,0 rot=4"; Expect = "rotation must be" }
    @{ Name = "wrong value count"; From = "values=1,-1"; To = "values=1"; Expect = "expected 2 values" }
    @{ Name = "counter out of range"; From = "values=1,-1"; To = "values=10,-1"; Expect = "must be from -9 to 9" }
    @{ Name = "missing output"; From = "m1 Output fixed"; To = "m1 Output"; Expect = "one fixed Output" }
    @{ Name = "wrong primary input"; From = "m0 PrimaryInputForWineBar"; To = "m0 PrimaryInputForCocktails"; Expect = "one fixed PrimaryInputForWineBar" }
    @{ Name = "unknown socket"; From = "m3.OUTPUT_A"; To = "m3.OUTPUT_Z"; Expect = "has no socket" }
    @{ Name = "socket out of range"; From = "m3.OUTPUT_A"; To = "m3.#99"; Expect = "doesn't exist" }
    @{ Name = "undefined module in wire"; From = "m3.OUTPUT_A"; To = "m9.OUTPUT_A"; Expect = "isn't defined" }
    @{ Name = "input to input"; From = "m3.OUTPUT_A"; To = "m3.INPUT_B"; Expect = "output to an input" }
    @{ Name = "socket used twice"; From = "m3.OUTPUT_A -> m2.TRIGGER_A"; To = "m3.OUTPUT_A -> m2.TRIGGER_A`nm3.OUTPUT_A -> m2.TRIGGER_B"; Expect = "already has a wire" }
    @{ Name = "unreadable line"; From = "m3.OUTPUT_A -> m2.TRIGGER_A"; To = "connect things"; Expect = "can't read" }
)

$failed = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  pass  $what" }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failed++ }
}

function Run-Import([string]$text, [string]$output) {
    $textFile = Join-Path $root "input.txt"
    [IO.File]::WriteAllText($textFile, $text)
    $errorFile = Join-Path $root "stderr.txt"
    $process = Start-Process -FilePath $fctext -ArgumentList @("--import", "`"$textFile`"", "`"$output`"") -NoNewWindow -Wait -PassThru -RedirectStandardError $errorFile -RedirectStandardOutput (Join-Path $root "stdout.txt")
    return @{ Code = $process.ExitCode; Error = (Get-Content $errorFile -Raw) }
}

$good = Join-Path $root "good.solution"
$result = Run-Import $valid $good
Check ($result.Code -eq 0 -and (Test-Path $good)) "valid text imports"

& $fctext --check $good | Out-Null
Check ($LASTEXITCODE -eq 0) "imported file passes --check"

$text = (& $fctext $good) -join "`n"
Check ($text -match "(?m)^unsolved$") "imports are always unsolved, whatever the text claims"
Check ($text -match "m0\.START -> m3\.INPUT_A") "wires survive"
Check ($text -match "values=1,-1") "values survive"

$exported = Join-Path $root "exported.txt"
& $fctext $good -o $exported | Out-Null
$exportCode = $LASTEXITCODE
$exportedBytes = [IO.File]::ReadAllBytes($exported)
Check ($exportCode -eq 0 -and $exportedBytes -contains 10 -and $exportedBytes -notcontains 13) "-o writes LF line endings"

$before = (Get-FileHash $good).Hash
$result = Run-Import $valid $good
Check ($result.Code -ne 0 -and (Get-FileHash $good).Hash -eq $before) "won't overwrite an existing file"

$wrongName = Join-Path $root "out.txt"
$result = Run-Import $valid $wrongName
Check ($result.Code -ne 0 -and -not (Test-Path $wrongName)) "output must end in .solution"

$grid = $valid -replace "m3 MixerSimple rack=2,2", "m3 MixerSimple rack=2,2`nm4 Sequencer rack=5,1 grid=#.../.#../..#./...#/..../..../..../..../..../..../..../...."
$sequencer = Join-Path $root "grid.solution"
$result = Run-Import $grid $sequencer
Check ($result.Code -eq 0 -and ((& $fctext $sequencer) -join "`n") -match "grid=#\.\.\./\.#\.\./\.\.#\./\.\.\.#/") "sequencer grid survives"

$index = 0
foreach ($case in $bad) {
    $index++
    $changed = $valid.Replace($case.From, $case.To)
    if ($changed -eq $valid) { throw "Test case didn't change the text: $($case.Name)" }
    $output = Join-Path $root "bad$index.solution"
    $result = Run-Import $changed $output
    Check ($result.Code -ne 0 -and $result.Error -match [regex]::Escape($case.Expect) -and -not (Test-Path $output)) "refuses: $($case.Name)"
}

Write-Host ""
if ($failed -eq 0) { Write-Host "All checks passed." -ForegroundColor Green; exit 0 }
Write-Host "$failed check(s) failed." -ForegroundColor Red
exit 1
