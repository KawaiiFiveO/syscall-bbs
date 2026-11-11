# Food Court Solution Format

This document describes the format of `.solution` files for 20th Century Food Court. These files define how a level is solved.

**Warning:** A malformed `.solution` file can prevent the game from starting. Always back up your solution files before editing them.

## Tooling

The `fctext` tool can be used to convert between binary `.solution` files and text format.

*   **Read/Write:** `fctext <file.solution>` (prints text) or `fctext <file.solution> -o <file.txt>` (writes UTF-8 text).
*   **Verify:** `fctext --check <file.solution>` verifies that rewriting a file reproduces identical bytes, it passes validation, a solved file's saved cost matches the catalogue, and it converts to text and back to the same solution.
*   **Import:** `fctext --import <text file> <new.solution>` writes a new solution from text. Imports are always written as unsolved with a cost of 0.
*   **Placement:** `fctext --check-placement <solution, text file or folder>...` applies the game's placement rules, which the simulator doesn't check: floor and rack fit, overlaps, required rows and rotations, and what a pump may aim at.

The `fctext` executable can be found in the release zip at:
`tools\fctext\bin\Release\net472\fctext.exe`

## Binary Layout

Files are little-endian.

| Field | Type | Description |
|---|---|---|
| Format version | `int` | Current is 1013 (min 1010). |
| Level ID | `int` | |
| Name | `string` | UTF-8, preceded by its byte length as an `int`. |
| Solved status | `bool` | |
| Ticks | `int` | |
| Cost | `int` | |
| Module count | `int` | |
| Modules | `record[]`| See below. |
| Wire count | `int` | |
| Wires | `record[]`| 4-int records (module A, socket A, module B, socket B). |

### Module Record

| Field | Type | Description |
|---|---|---|
| Type | `int` | |
| Removable | `bool` | False for fixed level parts. |
| Rack X, Y | `int` | |
| Floor X, Y | `int` | |
| Type-specific data | | |
| Rotation | `byte` | |

#### Type-specific data (Examples)
*   `CounterSmall/Large`: 2 or 4 `int`s.
*   `Sequencer`: 48 `bool`s.
*   `Painter`: 2 `int`s.
*   `Animatronic`: 1 `bool`.
*   `SolidInput*`, `LiquidInput*`, `FreezerInput*`, `ToppingInput*`, `EnroberInput`: 1 `int`.
*   Others: none.

## Text Format

A text solution file consists of header lines, followed by one line per module, and one line per wire, separated by blank lines.

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

### Header Lines
*   `level <name>`: Level name.
*   `name <name>`: Solution name.
*   `version <version>`: Format version (default 1013).
*   `solved ticks=<n> cost=<n>`: Unsolved solutions use the `unsolved` keyword instead of `solved`.

### Module Lines
`m<n> <type> [fixed] rack=<x>,<y> floor=<x>,<y> [rot=<n>] [values=<v1>,<v2>...] [grid=<pattern>] [flags=<digits>]`

*   `m<n>`: Index of the module in the file.
*   `fixed`: Present if the module is not removable.
*   `rot`: Omitted if 0.
*   `values`: Comma-separated integers.
*   `grid`: For sequencers, a pattern using `#` (on) and `.` (off).
*   `flags`: One `0` or `1` per flag, with no separators.
*   Sockets can be named (e.g., `INPUT_A`) or referenced by index (e.g., `#1`).

### Wire Lines
`m<n>.<socket> -> m<n>.<socket>`

The output module and socket should be listed first.

## Module Catalogue

There are 80 known module types. The `ModuleCatalog.cs` file contains the definitions.
*   **Note**: 52 of these types come from reading the game's code and haven't been exercised by real solution files; the other 28 have.

## Socket Naming
Socket names are uppercase with other characters as `_`. Unlabelled sockets are named `IN1`, `IN2`, etc. The first socket on a scanner is always `START`.
