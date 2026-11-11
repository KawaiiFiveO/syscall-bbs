# Food Court Tools

Food Court Tools is a set of utilities for analyzing and solving 20th Century Food Court levels. It requires the game to be running.

Install with `install.ps1 -WithFoodCourtTools`.

## Running a Build

The `fcrun.ps1` script submits a solution to the running game and returns the result.

```powershell
tools\fcrun.ps1 <file.solution or text file> [-TimeoutSeconds 30]
```

*   **Input**: A `.solution` file or a text file. Invalid text files exit with code 2.
*   **Output**: The script prints `status ticks cost orders` and the path to the result.
*   **Exit Codes**:
    *   `0`: Success (`ok`).
    *   `1`: Failure.
    *   `2`: Invalid input file.

Results are stored in `results\<name>.json`. You can also place a `.solution` file in the `requests\` folder for automatic submission.

### Result Fields

Results are provided in JSON format.

| Field | Description |
|---|---|
| `file` | The name of the solution file. |
| `level` | The level name. |
| `name` | The solution name. |
| `status` | `ok`, `error` (game error), `timeout`, or `failed`. |
| `error` | Message from the game (null if `ok`). |
| `ticks` | The slowest order's tick count less one (matches game score). Null unless `ok`. |
| `cost` | Total cost from the catalogue. |
| `orderTicks` | Each completed order's tick count. The score is the largest of these less one. |
| `orders` | One array per order run, with one entry per tick. |

#### `orders` details
Each tick entry contains:
*   `tick`: The tick number, which restarts with each order.
*   `signals`: The sockets that are on during that tick (e.g., `m3.INPUT_A`).
*   `products`: Each product's move that tick (as `[fromX,fromY,toX,toY]`).

## Editing a Solution as Text

`fcedit.ps1` opens a saved solution as text. Each time you save the file, it's checked, and if it's valid it replaces the solution in the running game.

```powershell
tools\fcedit.ps1 wine-oclock-2             # a solution's file name, or its .solution path
tools\fcedit.ps1 my-build.txt              # a text file; makes a new solution if none has its name
tools\fcedit.ps1 my-build.txt -Once        # check and apply once, then exit
```

*   Problems are printed with their line numbers, and nothing reaches the game.
*   If the puzzle is open, the edit waits until the solution isn't running.
*   Each edit is one undo step: **Ctrl+Z** in the game reverts it.
*   `-Editor <command>` picks the editor. The default is `$env:EDITOR`, then VS Code, then Notepad.

## Level Documents

The tool automatically generates level requirement documents in `mods\FoodCourtTools\levels\<Level>.md`. These are rewritten on every game start.

**Note:** These documents describe the requirements (orders, blocks, etc.) for a level; they do not contain solutions.

## Waveforms

If `Waveforms` is enabled in `mods\FoodCourtTools\config.ini` (default), each order generates a VCD file for waveform analysis.

*   **File**: `results\<name>.order<N>.vcd`
*   **Viewing**: Use a tool like **GTKWave**.
*   **Content**: Each module is represented as a scope, and each output socket is a 1-bit wire. Tick 0 includes the scanner's opening pulse from `PreviousSignals`.

## Limits

*   The game must be running.
*   The trace does not include the recipe state of products.
*   The trace does not check if parts fit on the floor or rack.
