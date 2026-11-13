# Food Court netlist language

Text language for 20th Century Food Court solutions, similar to Verilog netlists. Define blocks and write logic as expressions; the rack is laid out automatically. Netlist files use the extension `.fcn`.

## Commands

| Command | Effect |
|---|---|
| `fctext --compile <file.fcn> <new.solution>` | Writes a new, unsolved solution. |
| `fctext --compile ... --strict` | Any change the compiler would make is treated as an error: buffers, split expressions, optimizations. The solution can only contain the written blocks. |
| `fctext --compile ... -O0` | No optimization. Buffers and splits are still added. |
| `fctext --to-netlist <file.solution or text file> [-o <file.fcn>]` | Converts a solution to a netlist. |
| `fctext --check <file.solution or folder>...` | Also checks that each solution converts to a netlist and back unchanged. |

To edit a netlist while the game is running, use `fcedit.ps1` (see [Food Court Tools](../mods/FoodCourtTools/README.md)).

## Example

The Walrus - 10 ticks, $75k, 37 sum of times, 27 wires:

```verilog
level Cocktails;
name "WR SOLUTION 75K";

fixed PrimaryInputForCocktails scan @(1,6) down;
fixed Output out @(1,0) down;
Conveyor @(1,5) down;
Conveyor @(1,4) down;
Conveyor @(1,3) down;

fixed SolidInputSingle   #(1) cups    @(2,3) down  (.IN1(scan.START));
fixed FreezerInputSingle #(0) freezer @(0,3) right (.IN1(add_ice));
fixed LiquidInputDouble  #(0) spirits @(0,2) right (.IN2(serve.A));
fixed LiquidInputDouble  #(1) mixers  @(3,2) left  (.IN1(cola.C), .IN2(serve.D));

Sorter  cup_sorter  @(2,2) down (.RIGHT(advance));
Sorter  tray_sorter @(1,2) down (.LEFT(shuttle.A), .THRU(advance));
Stacker stack       @(1,1) down (.EJECT(serve.B));

assign start_serve = shuttle.D | scan.W_SOUR | scan.HIGHBALL;
assign advance = scan.WHISKY | start_serve | cola.D | shuttle.C;
assign add_ice = tray_sorter.SENSE & (cola.B | serve.C);

Sequencer shuttle (.START(advance)) {
    A = [2];
    B = [4, 6, 7];
    C = [4];
    D = [1, 3, 5];
}

Sequencer serve (.START(start_serve), .STOP(shuttle.B)) {
    A = [2, 4];
    B = [0, 6];
    C = [3];
    D = [3];
}

Sequencer cola (.START(scan.COLA | scan.HIGHBALL), .STOP(start_serve)) {
    B = [0, 1, 2, 4, 7];
    C = [0, 1, 2, 3, 4];
    D = [4, 5, 7, 8, 9];
}
```

## File structure

* A file starts with `level <name>;`, the level's internal name, and `name "<text>";`, the solution's name. Both are required.
* Statements end with `;`, except after a sequencer's step list.
* Comments are `//` at the end of the line, or `/* ... */`.
* Names are letters, digits and `_`, and don't start with a digit. `level`, `name`, `fixed`, `assign` and `wire` are reserved.

## Blocks

```
[fixed] <Type> [#(<int>, ...)] [<name>] [@(<x>,<y>) <facing>] [(.<PORT>(<expr>), ...)] [<steps>] ;
```

| Part | Meaning |
|---|---|
| `fixed` | A part of the level that can't be removed. |
| `<Type>` | The module type, such as `Sorter` or `CounterSmall`. See the module catalogue in [Food Court solution format](food-court-format.md). |
| `#(...)` | The module's settings: a dispenser's item, a counter's deltas (2 for small counter, 4 for large), a painter's 2 values, an animatronic's flag (`#(0)` or `#(1)`). |
| `<name>` | Needed to use the block's outputs. |
| `@(x,y) facing` | Floor position. `facing` is `right`, `up`, `left` or `down`. |
| `.PORT(expr)` | What drives the input `PORT`. Each input can be driven once. |
| `<steps>` | Sequencers only. See below. |

Mixers, counters and sequencers go on the rack and take no position.

### Sequencers

```verilog
Sequencer a (.START(go), .STOP(halt)) {
    A = [2];
    B = [4, 6, 7];
}
```

Each output, `A` to `D`, lists the steps it is ON, from step 0 (the tick after START) to step 11. Outputs left out are never ON. `{ }` is an empty sequencer.

### Socket names

Sockets use the catalogue names or short forms:

| Short | Catalogue | Used on |
|---|---|---|
| `A` to `D` | `OUTPUT_A` to `OUTPUT_D`, `INPUT_A` to `INPUT_D` | Sequencers, mixers |
| `ZERO`, `POS` | `ZERO_FLAG`, `POSITIVE_FLAG` | Counters |
| `EN` | `ENABLE` | `MixerComplex` |
| `TA` to `TD` | `TRIGGER_A` to `TRIGGER_D` | Counters |

## Signals and expressions

* `<name>.<SOCKET>` is a block's output, such as `scan.WHISKY`. A block with one output can be written as just `<name>`.
* `a | b` is OR and `a & b` is AND. There is no NOT; use a counter.
* `assign <net> = <expr>;` to name an expression. Assigning a single signal is free.
* `wire <net>, ...;` declares names before they are assigned. Optional.
* Expressions can be used in `assign` and in `.PORT(...)`.

### Mixers

* An OR becomes a `MixerSimple`. An AND becomes a `MixerComplex`, with one side as ENABLE and the other side's OR terms as its inputs.
* Mixers can also be written as blocks, such as `MixerSimple hb (.INPUT_A(scan.HIGHBALL));`. The block's name is its output.
* A simple mixer has 4 inputs and 4 outputs, a complex mixer 3 and 3. Any other output can drive one input.

## Counts

```verilog
assign done = count(3*scan.3_PCS + 6*scan.6_PCS + 10*scan.10_PCS - maker.SENSE) == 0;
assign more = count(3*scan.3_PCS + 6*scan.6_PCS + 10*scan.10_PCS - maker.SENSE) > 0;
assign last = count(4 - micro.SENSE) == 0;
```

`count(<sum>) <comparison> <number>` creates a counter. The sum adds up how many earlier ticks each signal was on, times its weight. Terms are `3*a`, `a`, `-b` or a number, joined by `+` and `-`. A number counts from tick 1.

| Comparison | Uses |
|---|---|
| `== n` | ZERO |
| `> n` | POS |
| `< n` | POS, with weights negated |
| `>= n`, `<= n` | ZERO or POS + one mixer |

* A sum with at most 2 signals, weights up to ±9 and only `==` comparisons gets a `CounterSmall` ($3k). Otherwise it gets a `CounterLarge` ($5k): at most 4 signals, weights up to ±99.
* Comparisons of the same sum share a counter.
* A counter is never on at T0, and its value stops at ±99.

## Functions

Shorthands for common patterns. Each builds one block and can be used in any expression. Counters and sequencers reset at the start of each order.

| Function | On when | Builds | Cost |
|---|---|---|---|
| `at_tick(k)` | Tick k only (0 to 100) | Nothing for T0; otherwise a sequencer channel or a counter, whichever is cheapest | Free to $5k |
| `from_tick1()` | Every tick from T1 | `CounterSmall` | $3k |
| `never(x)` | x hasn't fired in an earlier tick | `CounterSmall` | $3k |
| `since(x)` | x has fired in an earlier tick | `CounterLarge` | $5k |
| `exactly(x, n)` | x fired exactly n times in earlier ticks (n 1 to 9) | `CounterSmall` | $3k |
| `balanced(x, y)` | x and y fired equally often in earlier ticks | `CounterSmall` | $3k |
| `latch(set, reset)` | set fired more often than reset in earlier ticks | `CounterLarge` | $5k |
| `delay(x, n)` | n ticks after x (n 1 to 12) | Sequencer | $5k |
| `hold(x, n)` | The n ticks after x (n 1 to 12) | Sequencer | $5k |

* Counter functions are never on at T0, and see x one tick late: if x fires on tick t, `never(x)` is off from tick t+1.
* A sequencer ignores START while it runs, so `delay` and `hold` miss an x that comes before the previous one is finished.
* `delay` and `hold` with the same x share one sequencer, up to 4. The same call written twice builds one block.
* A function can't feed back into its own argument. Write the counter or sequencer as a block instead.
* `from_tick1() | scan.START` is on every tick.

## Compiling

The compiler:

1. Splits ORs of more than 4 terms and chains ANDs.
2. optimizes (unless `-O0` or `--strict`).
3. Adds buffers: a signal used more often than its output allows gets `MixerSimple` buffers, $1k each.
4. Lays out the rack, then checks the solution and placement rules.

Mixers have no delay, so timing remains unchanged.

Errors: unknown names, sockets or types, an input driven twice, a loop made only of mixers, anything the game wouldn't accept.

### Optimization

Repeated until nothing changes:

* Repeated inputs of a mixer are dropped.
* A simple mixer with one input is replaced by that input.
* A mixer that can never be on is removed.
* Mixers with the same inputs are merged.
* A simple mixer used only as an OR input of another mixer is merged into it, if the inputs fit.

Then unused mixers are removed. `at_tick(k)` and `delay(scan.START, k)` are each built as a sequencer channel or a counter, whichever makes the build cheapest. Other counters and sequencers are kept as written.

### Warnings

The compiler warns about dead logic.

| Warning | Meaning |
|---|---|
| `a.A only feeds a's own START, at steps 3, 4, while it's running` | A running sequencer ignores START |
| `m.B: its input seq.C is never on (channel C has no steps)` | Input is driven by something that is never on |
| `seq has no START, so it never runs` | |
| `ctr drives nothing ($3k)` | |
| `ctr.TA has delta 0, so its input x does nothing` | |

### Report

Printed after each compile:

```
compiled the-walrus.fcn: 20 modules, 28 wires
  floor   $55k   Stacker 20, Sorter x2 20, Conveyor x3 15
  logic   $20k   Sequencer x3 15, MixerSimple x4 4, MixerComplex 1
  total   $75k
  added   scan.HIGHBALL: 2 uses, 1 output: 1 buffer (+$1k)
  opt     merged sour into go: used only there (-$1k)
  opt     saved $1k in total
```

### Rack layout

Rack blocks are placed in source order, 2-wide blocks first, each in the first free slot: rows 0 to 2, columns 0 to 10. More than 33 slots is an error.

## Converting back

`--to-netlist` names blocks by role (`scan`, `out`, `stack`) or by kind and number (`solid1`, `pump1`, `scanner1`). Sequencers are written as step lists, mixers as `assign` statements, and counters as a function or `count()` where that compiles back to the same counter, otherwise as blocks.

`--to-netlist` followed by `--compile --strict` gives the same solution, aside from rack positions and which input of a mixer or counter each wire uses.
