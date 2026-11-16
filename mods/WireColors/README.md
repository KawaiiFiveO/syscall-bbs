# Wire Colors

A mod that colors 20th Century Food Court wires. Colors are saved in the solution file.

To install, select Wire Colors in `Install.cmd`. Backup your `.solution` files before using.

## Coloring wires

Point at a wire while the solution is stopped:

| Key | Effect |
|---|---|
| Middle-click | Next color. |
| 1 to 8 | Set a color. |
| 0 | Back to orange. |

Number keys on the keypad do the same.

| Key | Color |
|---|---|
| 1 | Red |
| 2 | Yellow |
| 3 | Green |
| 4 | Cyan |
| 5 | Blue |
| 6 | Purple |
| 7 | Pink |
| 8 | White |

Each change is one undo step. A wire keeps color when its ends are moved. Plugs stay orange.

## Wire count

To show the number of wires on the Cost display, press **C** on the puzzle screen. Press **C** again to cycle back. Recorded GIFs still show cost.

## Solution files

Colors travel with the solution file, including copies made with Copy Solution. Without the mod, orange wires are shown; saving there removes the colors.

`fctext` shows and keeps colors; see [Food Court solution format](../../docs/food-court-format.md#wire-colors).

## Exporting without colors

To submit a solution to a leaderboard, export it without colors: press **F5** on the puzzle screen. The file is written to `My Games/Last Call BBS/<user>/mods/onehalf.wirecolors/export/<solution>.solution`, replacing an earlier export of the same solution.

`fctext --vanilla <file.solution> <new.solution>` does the same for a saved file.
