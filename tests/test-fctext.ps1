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

# Placement. Each case replaces one line of a legal layout and must be reported.
$placed = $valid.Replace("m3 MixerSimple rack=2,2", "m3 MixerSimple rack=2,2`nm4 Stacker rack=5,0 floor=2,1 rot=3`nm5 Conveyor rack=0,0 floor=2,2 rot=3`nm6 LiquidInputSingle rack=6,0 floor=3,2 rot=2 values=0")
$placementCases = @(
    @{ Name = "pump aimed at a stacker"; From = "floor=3,2 rot=2"; To = "floor=3,1 rot=2"; Expect = "can only aim" }
    @{ Name = "pump aimed off the floor"; From = "floor=3,2 rot=2"; To = "floor=0,2 rot=2"; Expect = "aims off the floor" }
    @{ Name = "output too far right"; From = "floor=2,0 rot=3"; To = "floor=5,0 rot=3"; Expect = "doesn't fit on the" }
    @{ Name = "output on the wrong row"; From = "floor=2,0 rot=3"; To = "floor=2,1 rot=3"; Expect = "must be on row" }
    @{ Name = "scanner on the wrong row"; From = "floor=2,6 rot=3"; To = "floor=2,5 rot=3"; Expect = "must be on row" }
    @{ Name = "scanner rotated"; From = "floor=2,6 rot=3"; To = "floor=2,6 rot=1"; Expect = "can't use rot" }
    @{ Name = "floor overlap"; From = "m5 Conveyor rack=0,0 floor=2,2"; To = "m5 Conveyor rack=0,0 floor=2,1"; Expect = "overlap on the floor" }
    @{ Name = "second output tile overlap"; From = "m5 Conveyor rack=0,0 floor=2,2"; To = "m5 Conveyor rack=0,0 floor=3,0"; Expect = "overlap on the floor" }
    @{ Name = "rack overlap with a wide part"; From = "m3 MixerSimple rack=2,2"; To = "m3 MixerSimple rack=1,2"; Expect = "overlap on the rack" }
    @{ Name = "off the rack"; From = "m4 Stacker rack=5,0"; To = "m4 Stacker rack=11,0"; Expect = "doesn't fit on the" }
)

function Run-Placement([string]$text) {
    $textFile = Join-Path $root "placement.txt"
    [IO.File]::WriteAllText($textFile, $text)
    $output = (& $fctext --check-placement $textFile) -join "`n"
    return @{ Code = $LASTEXITCODE; Output = $output }
}

$result = Run-Placement $placed
Check ($result.Code -eq 0) "legal layout passes placement"
$result = Run-Placement ($placed.Replace("floor=3,2 rot=2", "floor=2,3 rot=3"))
Check ($result.Code -eq 0) "pump aimed at a conveyor passes placement"
foreach ($case in $placementCases) {
    $changed = $placed.Replace($case.From, $case.To)
    if ($changed -eq $placed) { throw "Test case didn't change the text: $($case.Name)" }
    $result = Run-Placement $changed
    Check ($result.Code -ne 0 -and $result.Output -match [regex]::Escape($case.Expect)) "placement: $($case.Name)"
}

# Netlists (specs/food-court-netlist.md).
$netlist = @"
// A comment
level WineBar;
name "Netlist Test";

fixed PrimaryInputForWineBar scan @(2,6) down;
fixed Output out @(2,0) down;
Conveyor @(2,5) down;
Sensor eye @(2,4) down;
CounterSmall #(1, -1) ctr (.TA(scan.RED | eye), .TB(scan.WHITE));
assign go = ctr.ZERO | seq.B;
Sequencer seq (.START(scan.START & eye), .STOP(go)) {
    A = [0, 3];
    D = [11];
}
"@

function Run-Netlist([string]$text, [string[]]$extra = @()) {
    $file = Join-Path $root "test.fcn"
    [IO.File]::WriteAllText($file, $text)
    $output = Join-Path $root "netlist.solution"
    if (Test-Path $output) { Remove-Item $output }
    $errorFile = Join-Path $root "stderr.txt"
    $arguments = @("--compile", "`"$file`"", "`"$output`"") + $extra
    $process = Start-Process -FilePath $fctext -ArgumentList $arguments -NoNewWindow -Wait -PassThru -RedirectStandardError $errorFile -RedirectStandardOutput (Join-Path $root "stdout.txt")
    return @{ Code = $process.ExitCode; Error = (Get-Content $errorFile -Raw); Output = (Get-Content (Join-Path $root "stdout.txt") -Raw); File = $output }
}

$result = Run-Netlist $netlist
Check ($result.Code -eq 0 -and (Test-Path $result.File)) "netlist compiles"
$text = (& $fctext $result.File) -join "`n"
Check ($text -match "Sequencer rack=\d+,\d+ floor=0,0 grid=#\.\.\./\.\.\.\./\.\.\.\./#\.\.\./(\.\.\.\./){7}\.\.\.#") "sequencer steps become the grid"
Check ($text -match "CounterSmall rack=\d+,\d+ floor=0,0 values=1,-1") "counter values survive"
# Two ORs, one AND, and one buffer for eye.
Check (([regex]::Matches($text, "MixerSimple")).Count -eq 3 -and ([regex]::Matches($text, "MixerComplex")).Count -eq 1) "expressions become mixers"
Check ($result.Output -match "added   eye: 2 uses, 1 output: 1 buffer") "fan-out adds a buffer and reports it"
Check ($result.Output -match "logic   \`$\d+k") "report shows the logic cost"
& $fctext --check $result.File | Out-Null
Check ($LASTEXITCODE -eq 0) "compiled netlist passes --check, including the netlist round trip"

$result = Run-Netlist $netlist @("--strict")
Check ($result.Code -ne 0 -and $result.Error -match "eye has 2 uses but 1 output" -and -not (Test-Path $result.File)) "--strict refuses to add a buffer"

$wide = $netlist.Replace("assign go = ctr.ZERO | seq.B;", "assign go = ctr.ZERO | seq.B | seq.C | scan.RED | scan.WHITE;").Replace(".TA(scan.RED | eye), .TB(scan.WHITE)", ".TA(eye)")
$result = Run-Netlist $wide
Check ($result.Code -eq 0 -and $result.Output -match "split an OR of 5 terms") "an OR of 5 terms is split"
$result = Run-Netlist $wide @("--strict")
Check ($result.Code -ne 0 -and $result.Error -match "--strict doesn't split it") "--strict refuses to split"

$netlistCases = @(
    @{ Name = "unknown name"; From = "seq.B;"; To = "nothing.B;"; Expect = "unknown name" }
    @{ Name = "unknown output"; From = "ctr.ZERO"; To = "ctr.NEGATIVE"; Expect = "has no output" }
    @{ Name = "unknown input"; From = ".TB(scan.WHITE)"; To = ".TX(scan.WHITE)"; Expect = "has no input" }
    @{ Name = "input twice"; From = ".TB(scan.WHITE)"; To = ".TB(scan.WHITE), .TB(scan.RED)"; Expect = "connected twice" }
    @{ Name = "several outputs, none named"; From = "seq.B;"; To = "seq;"; Expect = "several outputs" }
    @{ Name = "floor block without position"; From = "Sensor eye @(2,4) down"; To = "Sensor eye"; Expect = "goes on the floor" }
    @{ Name = "rack block with position"; From = "CounterSmall #(1, -1) ctr"; To = "CounterSmall #(1, -1) ctr @(0,0) up"; Expect = "rack only" }
    @{ Name = "wrong value count"; From = "#(1, -1)"; To = "#(1)"; Expect = "takes 2 values" }
    @{ Name = "bad facing"; From = "@(2,4) down"; To = "@(2,4) south"; Expect = "isn't a facing" }
    @{ Name = "step out of range"; From = "D = [11];"; To = "D = [12];"; Expect = "out of range" }
    @{ Name = "bad channel"; From = "D = [11];"; To = "E = [11];"; Expect = "isn't a channel" }
    @{ Name = "NOT"; From = "scan.START & eye"; To = "scan.START & ~eye"; Expect = "no NOT" }
    @{ Name = "duplicate name"; From = "assign go ="; To = "assign eye ="; Expect = "already defined" }
    @{ Name = "missing level"; From = "level WineBar;"; To = ""; Expect = "missing" }
    @{ Name = "unknown level"; From = "level WineBar;"; To = "level Nowhere;"; Expect = "unknown level" }
    @{ Name = "placement problem"; From = "Sensor eye @(2,4) down"; To = "Sensor eye @(2,5) down"; Expect = "overlap on the floor" }
)
foreach ($case in $netlistCases) {
    $changed = $netlist.Replace($case.From, $case.To)
    if ($changed -eq $netlist) { throw "Test case didn't change the text: $($case.Name)" }
    $result = Run-Netlist $changed
    Check ($result.Code -ne 0 -and $result.Error -match [regex]::Escape($case.Expect) -and -not (Test-Path $result.File)) "netlist refuses: $($case.Name)"
}

# Functions.
$functions = $netlist.Replace("assign go = ctr.ZERO | seq.B;", @"
assign go = ctr.ZERO | seq.B | never(eye) | since(scan.RED) | latch(scan.RED, scan.WHITE);
assign later = delay(scan.START, 3) | hold(scan.START, 2) | delay(scan.START, 3);
assign count = exactly(scan.RED, 2) | balanced(scan.RED, scan.WHITE) | from_tick1();
assign always = from_tick1();
Sequencer tail2 (.START(always)) { B = [1]; }
Sequencer tail (.START(later), .STOP(count)) { A = [0]; }
"@)
$result = Run-Netlist $functions
Check ($result.Code -eq 0) "functions compile"
Check ($result.Output -match "never\(eye\): CounterSmall \(\+\`$3k\)" -and $result.Output -match "since\(scan\.RED\): CounterLarge \(\+\`$5k\)") "counter functions are reported with their cost"
Check ($result.Output -match "hold\(scan\.START, 2\): shares the sequencer from line \d+, channel B") "delay and hold share a sequencer"
Check ($result.Output -match "delay\(scan\.START, 3\): same block as line") "an identical call is one block"
Check ($result.Output -match "from_tick1\(\): CounterSmall \(\+\`$3k\)" -and $result.Output -match "from_tick1\(\): same block as line") "from_tick1() is one shared counter"
$text = (& $fctext $result.File) -join "`n"
Check ($text -match "CounterSmall rack=\d+,\d+ floor=0,0 values=1,-2") "exactly(x, 2) counts down from START"
Check ($text -match "grid=\.#\.\./\.#\.\./#\.\.\./") "delay(x, 3) and hold(x, 2) fill their channels"
& $fctext --check $result.File | Out-Null
Check ($LASTEXITCODE -eq 0) "a netlist with functions passes --check"

$functionCases = @(
    @{ Name = "unknown function"; From = "never(eye)"; To = "always(eye)"; Expect = "unknown function" }
    @{ Name = "wrong argument count"; From = "never(eye)"; To = "never(eye, eye)"; Expect = "takes 1 argument" }
    @{ Name = "number out of range"; From = "delay(scan.START, 3) |"; To = "delay(scan.START, 13) |"; Expect = "from 1 to 12" }
    @{ Name = "missing number"; From = "exactly(scan.RED, 2)"; To = "exactly(scan.RED, scan.WHITE)"; Expect = "must be a number" }
    @{ Name = "number as a signal"; From = "never(eye)"; To = "never(3)"; Expect = "isn't a signal" }
)
foreach ($case in $functionCases) {
    $changed = $functions.Replace($case.From, $case.To)
    if ($changed -eq $functions) { throw "Test case didn't change the text: $($case.Name)" }
    $result = Run-Netlist $changed
    Check ($result.Code -ne 0 -and $result.Error -match [regex]::Escape($case.Expect)) "functions refuse: $($case.Name)"
}
$result = Run-Netlist ($netlist.Replace("assign go = ctr.ZERO | seq.B;", "assign go = ctr.ZERO | seq.B;`nassign loop = delay(loop | scan.RED, 2);`nSequencer t2 (.START(loop)) { A = [0]; }"))
Check ($result.Code -ne 0 -and $result.Error -match "feeds back into its own function") "functions refuse: feedback into its own argument"

# count() and at_tick().
$counts = $netlist.Replace("assign go = ctr.ZERO | seq.B;", @"
assign go = ctr.ZERO | seq.B | count(2*scan.RED + 3*scan.WHITE - eye) == 0 | count(2*scan.RED + 3*scan.WHITE - eye) > 0;
assign small = count(scan.RED - scan.WHITE) == 0;
assign flipped = count(scan.WHITE - scan.RED) == 0;
assign some = count(4 - eye) >= 0;
assign at = at_tick(3) | delay(scan.START, 3) | at_tick(0);
Sequencer t3 (.START(small | flipped | some), .STOP(at)) { A = [0]; }
"@)
$result = Run-Netlist $counts
Check ($result.Code -eq 0) "count() compiles"
Check ($result.Output -match "count\(2\*scan\.RED \+ 3\*scan\.WHITE - eye\) == 0: CounterLarge" -and $result.Output -match "> 0: shares the counter from line") "== and > on one sum share a large counter"
Check ($result.Output -match "count\(scan\.RED - scan\.WHITE\) == 0: CounterSmall" -and $result.Output -match "count\(scan\.WHITE - scan\.RED\) == 0: shares the counter") "== shares with the negated sum on a small counter"
Check ($result.Output -match "at_tick\(0\): scan\.START itself, no block" -and $result.Output -match "delay\(scan\.START, 3\): shares the sequencer") "at_tick(0) is START and at_tick(3) shares with delay"
$text = (& $fctext $result.File) -join "`n"
Check ($text -match "CounterLarge rack=\d+,\d+ floor=0,0 values=2,3,-1,0") "count() weights become deltas, unused triggers 0"
Check ($text -match "CounterLarge rack=\d+,\d+ floor=0,0 values=-1,4,0,0") "a constant becomes a weight on START"
$upgrade = $netlist.Replace("assign go = ctr.ZERO | seq.B;", "assign go = ctr.ZERO | seq.B | count(scan.RED - eye) == 0 | count(scan.RED - eye) > 0;")
$result = Run-Netlist $upgrade
Check ($result.Code -eq 0 -and $result.Output -match "count\(scan\.RED - eye\) == 0: CounterLarge" -and $result.Output -notmatch "CounterSmall x2") "a shared small counter becomes large when > needs POS"
& $fctext --check $result.File | Out-Null
Check ($LASTEXITCODE -eq 0) "a netlist with count() passes --check"

$countCases = @(
    @{ Name = "not equal"; From = "count(4 - eye) >= 0"; To = "count(4 - eye) != 0"; Expect = "needs a NOT" }
    @{ Name = "no comparison"; From = "count(4 - eye) >= 0"; To = "count(4 - eye)"; Expect = "needs a comparison" }
    @{ Name = "too many signals"; From = "count(4 - eye) >= 0"; To = "count(4 - eye + scan.RED + scan.WHITE + ctr.ZERO) >= 0"; Expect = "at most 4 different signals" }
    @{ Name = "weight too big"; From = "count(4 - eye) >= 0"; To = "count(100*eye) >= 0"; Expect = "at most 99" }
    @{ Name = "number times number"; From = "count(4 - eye) >= 0"; To = "count(4*5 - eye) >= 0"; Expect = "not two numbers" }
)
foreach ($case in $countCases) {
    $changed = $counts.Replace($case.From, $case.To)
    if ($changed -eq $counts) { throw "Test case didn't change the text: $($case.Name)" }
    $result = Run-Netlist $changed
    Check ($result.Code -ne 0 -and $result.Error -match [regex]::Escape($case.Expect)) "count() refuses: $($case.Name)"
}

# Optimiser.
$opt = $netlist.Replace("assign go = ctr.ZERO | seq.B;", @"
assign go = ctr.ZERO | seq.B | inner;
assign inner = scan.RED | scan.WHITE;
assign twin1 = eye | scan.RED;
assign twin2 = eye | scan.RED;
assign unused = scan.RED | scan.WHITE | eye;
Sequencer t4 (.START(twin1), .STOP(twin2)) { A = [0]; }
"@)
function Total([string]$output) { if ($output -match "total   \`$(\d+)k") { [int]$Matches[1] } else { -1 } }
$result = Run-Netlist $opt
Check ($result.Code -eq 0) "optimised netlist compiles"
Check ($result.Output -match "opt     merged inner into go: used only there") "a single-use mixer is merged into its user"
# twin1, twin2 and the counter's (scan.RED | eye) all have the same inputs.
Check ($result.Output -match "opt     merged twin1 into expression at line \d+: same inputs" -and $result.Output -match "opt     merged twin2 into expression at line \d+: same inputs") "mixers with the same inputs are merged, whatever their order"
Check ($result.Output -match "opt     removed unused: nothing uses it") "an unused mixer is removed"
$optimisedTotal = Total $result.Output
$saved = if ($result.Output -match "opt     saved \`$(\d+)k in total") { [int]$Matches[1] } else { -1 }
& $fctext --check $result.File | Out-Null
Check ($LASTEXITCODE -eq 0) "an optimised build passes --check"
$result = Run-Netlist $opt @("-O0")
Check ($result.Code -eq 0 -and $result.Output -notmatch "opt     " -and $saved -gt 0 -and (Total $result.Output) -eq $optimisedTotal + $saved) "-O0 builds what's written, and the saving matches the report"
$flat = @"
level WineBar;
name "Strict";
fixed PrimaryInputForWineBar scan @(2,6) down;
fixed Output out @(2,0) down;
assign inner = scan.RED | scan.WHITE;
assign go = inner | scan.START;
Sequencer s1 (.START(go)) { A = [0]; }
"@
$result = Run-Netlist $flat
Check ($result.Code -eq 0 -and $result.Output -match "merged inner into go") "a netlist without fan-out is optimised normally"
$result = Run-Netlist $flat @("--strict")
Check ($result.Code -eq 0 -and $result.Output -notmatch "opt     " -and $result.Output -match "MixerSimple x2") "--strict doesn't optimise"

$pulse = $netlist.Replace("assign go = ctr.ZERO | seq.B;", "assign go = ctr.ZERO | seq.B;`nassign p1 = at_tick(1);`nSequencer t5 (.START(p1)) { A = [0]; }")
$result = Run-Netlist $pulse
Check ($result.Code -eq 0 -and $result.Output -match "at_tick\(1\) at line \d+: a CounterSmall fed by its own ZERO" -and $result.Output -match "func    line \d+: at_tick\(1\): built as a counter") "at_tick(1) alone becomes a self-fed counter"
$result = Run-Netlist $pulse @("-O0")
Check ($result.Code -eq 0 -and $result.Output -match "at_tick\(1\): Sequencer") "-O0 keeps at_tick(1) as a sequencer channel"
$late = $netlist.Replace("assign go = ctr.ZERO | seq.B;", "assign go = ctr.ZERO | seq.B;`nassign p20 = at_tick(20);`nSequencer t6 (.START(p20)) { A = [0]; }")
$result = Run-Netlist $late @("-O0")
Check ($result.Code -eq 0 -and $result.Output -match "at_tick\(20\): CounterLarge counting from_tick1\(\)") "at_tick past 12 is a counter, even with -O0"
$text = (& $fctext $result.File) -join "`n"
Check ($text -match "CounterLarge rack=\d+,\d+ floor=0,0 values=1,-19,0,0") "at_tick(20) counts up from -19"

# --to-netlist writes counters as functions and count() where that compiles back to the same block.
$idiom = $netlist.Replace("assign go = ctr.ZERO | seq.B;", @"
assign go = ctr.ZERO | seq.B | from_tick1() | never(eye) | count(3*scan.RED - eye) > 0;
"@)
$result = Run-Netlist $idiom
$back = Join-Path $root "idiom.fcn"
& $fctext --to-netlist $result.File -o $back | Out-Null
$written = Get-Content $back -Raw
# The counters' inputs come from fan-out buffers, so their names vary.
Check ($written -match "= from_tick1\(\);" -and $written -match "= never\(\w+\);") "--to-netlist writes from_tick1() and never()"
Check ($written -match "= count\(3\*\w+ - \w+\) > 0;") "--to-netlist writes count()"
Check ($written -match "= balanced\(") "--to-netlist writes balanced() for a (1, -1) counter"
& $fctext --check $result.File | Out-Null
Check ($LASTEXITCODE -eq 0) "idioms pass the round trip"

# Dead-code warnings.
$dead = $netlist.Replace("assign go = ctr.ZERO | seq.B;", @"
assign go = ctr.ZERO | seq.B | seq.C;
Sequencer loop (.START(scan.START | loop.A)) { A = [2]; B = [0]; }
Sequencer last (.START(scan.WHITE | last.A)) { A = [11]; B = [0]; }
Sequencer idle (.STOP(scan.RED)) { A = [0]; }
CounterSmall #(1, 0) lonely (.TA(scan.WHITE));
CounterSmall #(0, -1) zero (.TA(eye), .TB(scan.RED));
Sequencer sink (.START(loop.B | last.B | idle.A | zero)) { A = [0]; }
"@)
$result = Run-Netlist $dead
Check ($result.Code -eq 0) "dead code only warns"
Check ($result.Error -match "warning: line \d+: loop\.A only feeds loop's own START, at step 2, while it's running") "a sequencer feeding its own START is reported"
Check ($result.Error -notmatch "last\.A only feeds") "a self-START at step 11 isn't reported, because it can restart"
Check ($result.Error -match "its input seq\.C is never on \(channel C has no steps\)") "an empty channel in use is reported"
Check ($result.Error -match "idle has no START, so it never runs") "a sequencer without START is reported"
Check ($result.Error -match "lonely drives nothing \(\`$3k\)") "a counter that drives nothing is reported"
Check ($result.Error -match "zero\.TA has delta 0, so its input eye\.SENSE does nothing") "a trigger with delta 0 is reported"

$round = Join-Path $root "round.fcn"
& $fctext --to-netlist $good -o $round | Out-Null
$back = Join-Path $root "round.solution"
& $fctext --compile $round $back --strict | Out-Null
Check ($LASTEXITCODE -eq 0 -and (Test-Path $back)) "--to-netlist output compiles with --strict"

Write-Host ""
if ($failed -eq 0) { Write-Host "All checks passed." -ForegroundColor Green; exit 0 }
Write-Host "$failed check(s) failed." -ForegroundColor Red
exit 1
