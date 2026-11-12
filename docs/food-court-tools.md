# Food Court tools

`FoodCourtTools` is a mod that runs 20th Century Food Court solutions in the game on request and reports the result. It also applies solution edits to the running game.

## Installation

Install with `Install.cmd -WithFoodCourtTools`, or `launcher\install.ps1 -WithFoodCourtTools` from source.

The game must be running through `Last Call BBS (Modded).exe` for every tool on this page.

## Running a solution

```powershell
tools\fcrun.ps1 <file.solution or text file> [-TimeoutSeconds 30]
```

* A text file is converted with `fctext --import` first. See [Food Court solution format](food-court-format.md).
* Prints `status=… ticks=… cost=… orders=…`, then the path of the JSON result, then the waveform files if any.

| Exit code | Meaning |
|---|---|
| `0` | Status is `ok`. |
| `1` | Any other status, a timeout or an error. |
| `2` | The text file failed to import. |

Without `fcrun.ps1`: copy a `.solution` file into `mods\FoodCourtTools\requests\`. The result is written to `mods\FoodCourtTools\results\<name>.json`.

### Result

| Field | Value |
|---|---|
| `file` | Request file name. |
| `level` | Level's internal name, such as `WineBar`. |
| `name` | Solution name. |
| `status` | `ok`, `error` (the game reported an error), `timeout` or `failed` (the run couldn't start). |
| `error` | The game's message, or `null`. |
| `ticks` | Score: the slowest order's tick count minus one. `null` unless `ok`. |
| `cost` | Cost computed from the module catalogue. |
| `savedSolved`, `savedTicks`, `savedCost` | Values stored in the solution file. |
| `orderTicks` | Tick count of each completed order. |
| `orders` | One array per order, with one entry per tick. |

Each tick entry:

| Field | Value |
|---|---|
| `tick` | Tick number. Restarts for each order. |
| `signals` | Sockets that are on, such as `m3.INPUT_A`. |
| `products` | Each product's move as `[fromX, fromY, toX, toY]`. |

A `failed` result has only `file`, `status` and `error`.

## Editing a solution as text

```powershell
tools\fcedit.ps1 wine-oclock-2            # solution file name or .solution path
tools\fcedit.ps1 my-build.txt             # text file
tools\fcedit.ps1 my-build.txt -Once       # check and apply once, then exit
```

* A solution is exported to `mods\FoodCourtTools\text\` and opened in an editor. A text file is opened as is.
* Each save is checked. Problems are printed with line numbers and nothing is applied. A valid file replaces the solution in the game.
* If no solution has the text file's name, a new solution is created.
* If the puzzle is running, the edit waits until it stops.
* Each edit is one undo step. **Ctrl+Z** in the puzzle screen reverts it.
* `-Editor <command>` sets the editor. The default is `$env:EDITOR`, then VS Code, then Notepad. `-NoEditor` opens none.
* With `-Once`, exit code 0 means the edit was applied or is waiting for the puzzle screen.

`fcedit.ps1` is only in the source repository.

## Level documents

On every game start, each level's requirements are written to `mods\FoodCourtTools\levels\<Level>.md`. They do not contain solutions.

## Waveforms

Each order of a run is written as a VCD file, `results\<name>.order<N>.vcd`, viewable in GTKWave or another VCD viewer.

* Each module is a scope. Each output socket is a 1-bit wire.
* Tick 0 holds the scanner's opening pulse.

To turn waveforms off, create `mods\FoodCourtTools\config.ini` containing:

```ini
Waveforms = false
```

## Limitations

* Results don't include the recipe state of products.
* Runs don't check whether parts fit on the floor or rack. Use `fctext --check-placement`.
