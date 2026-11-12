# Food Court solution format

20th Century Food Court stores each solution as a binary `.solution` file. `fctext` converts them to and from a text format.

**Warning:** The game reads every solution at startup. A malformed `.solution` file can stop the game from starting. Back up solution files before editing them.

## fctext

`fctext.exe` is at `tools\fctext\bin\Release\net472\fctext.exe` in the release zip and in a source build.

| Command | Effect |
|---|---|
| `fctext <file.solution or folder>...` | Prints solutions as text. |
| `fctext <file.solution> -o <file.txt>` | Writes one solution to a UTF-8 text file. |
| `fctext --check <file.solution or folder>...` | Checks that each file is fully understood: rewriting it gives identical bytes, it passes validation, a solved file's saved cost matches the catalogue, and text conversion round-trips. |
| `fctext --import <text file> <new.solution>` | Writes a new solution from text, after validation. The result is unsolved with a cost of 0. |
| `fctext --check-placement <solution, text file or folder>...` | Applies the game's placement rules: floor and rack fit, overlaps, required rows and rotations, and what a pump may aim at. |

## Binary layout

Little-endian. A `string` is an `int` byte length followed by UTF-8 bytes.

| Field | Type |
|---|---|
| Format version | `int`, 1010 to 1013 |
| Level id | `int` |
| Name | `string` |
| Solved | `bool` |
| Ticks | `int` |
| Cost | `int` |
| Module count | `int` |
| Modules | module records |
| Wire count | `int` |
| Wires | 4 `int`s each: module A, socket A, module B, socket B |

### Module record

| Field | Type |
|---|---|
| Type | `int` |
| Removable | `bool`, false for fixed level parts |
| Rack X, Y | 2 `int`s |
| Floor X, Y | 2 `int`s |
| Type-specific data | see below |
| Rotation | `byte` |

Type-specific data:

| Type | Data |
|---|---|
| `CounterSmall` | 2 `int`s |
| `CounterLarge` | 4 `int`s |
| `Painter` | 2 `int`s |
| `SolidInput*`, `LiquidInput*`, `FreezerInput*`, `ToppingInput*`, `EnroberInput` | 1 `int` |
| `Sequencer` | 48 `bool`s: 12 rows of 4 channels |
| `Animatronic` | 1 `bool` |
| Others | none |

Older versions have an extra `int` in some records:

| Version | Types | Position |
|---|---|---|
| Before 1011 | `Sequencer`, `CounterSmall`, `CounterLarge` | Before the data |
| Before 1012 | `Microwave`, `Grill`, `Fryer` | Before the data |
| Before 1013 | `LiquidInput*` | After the data |

## Text format

Header lines, then one line per module, then one line per wire. Blank lines and lines starting with `#` are ignored.

```text
level WineBar
name My Solution
version 1013
solved ticks=8 cost=61

m0 PrimaryInputForWineBar fixed rack=0,2 floor=2,6 rot=3
m11 CounterSmall rack=8,2 floor=0,0 values=1,-1
m12 Sequencer rack=9,1 floor=0,0 grid=#.../.#../..../..../..../..../..../..../..../..../..../....

m0.START -> m6.INPUT_A
```

### Header

| Line | Value |
|---|---|
| `level <name>` | Level's internal name. |
| `name <name>` | Solution name. |
| `version <n>` | Format version. Defaults to 1013. |
| `solved ticks=<n> cost=<n>` or `unsolved` | Solved state. |

### Modules

```text
m<n> <type> [fixed] rack=<x>,<y> floor=<x>,<y> [rot=<n>] [values=<v>,...] [grid=<rows>] [flags=<digits>]
```

| Field | Value |
|---|---|
| `m<n>` | Module index in the file. |
| `fixed` | Not removable. |
| `rot` | Rotation. Omitted when 0. |
| `values` | The module's `int`s, comma-separated. |
| `grid` | Sequencer rows separated by `/`, one character per channel: `#` on, `.` off. |
| `flags` | The module's `bool`s, one `0` or `1` each, with no separators. |

### Wires

```text
m<n>.<socket> -> m<n>.<socket>
```

The output socket comes first. A socket is its name, such as `INPUT_A`, or `#` and its index, such as `#1`.

## Sockets

* Socket names are the game's labels in uppercase, with other characters replaced by `_`.
* Unlabelled sockets are `IN1`, `IN2` and so on.
* A scanner's first socket is `START`.

## Module catalogue

`tools\fctext\ModuleCatalog.cs` lists the 80 module types, generated from the game's module definitions. 28 of them have been checked against real solution files.
