# Last Call LDR

Loads mods into [Last Call BBS](https://www.zachtronics.com/last-call-bbs/) on Windows. Works with the GOG and Steam builds. The game's own files are never changed.

## Install

Requires the .NET SDK and the .NET Framework 4.7.2 Developer Pack. Close the game, then run:

```powershell
launcher\install.ps1
```

The game is found from the registry. Use `-GameDir <folder>` to choose it yourself.

Start `Last Call BBS (Modded).exe` in the game folder. On Steam, Steam must be running. Starting the game normally runs it without mods.

| Option | Adds |
|---|---|
| `-WithHookLogger` | `HookLogger`, which prints every hook as it fires |
| `-WithFoodCourtTools` | `FoodCourtTools`, which runs Food Court solutions on demand |

## Settings

In `LastCallBBS.Launcher.ini`, next to the game:

| Setting | Default | Effect |
|---|---|---|
| `Console` | `true` | Shows the log in a console window |
| `Achievements` | `true` | Steam only. `false` blocks achievements |
| `RichPresence` | `true` | Steam only. `false` blocks Rich Presence |
| `LocalScoreList` | `true` | Steam only. Shows your own scores in the Food Court results list |
| `SaveFolder` | blank | Steam only. Folder under `Documents\My Games\Last Call BBS` for modded runs |
| `AllowUnscoped.<id>` | `false` | Lets a mod that fails its scope check load |
| `GameDirectory` | blank | Game folder, if the launcher isn't in it |

The log is also written to `LastCallBBS.Launcher.log`.

## Steam

Modded runs never use Steam leaderboards. If that can't be guaranteed, the game won't start. Starting the game normally isn't affected.

## Bundled mods

`SkipLoading` removes the BBS download timers. Its settings are in `mods\SkipLoading\config.ini`.

## Making mods

See [docs/making-mods.md](docs/making-mods.md) and [the example](mods/FoodCourtStats).

## Development

```powershell
tests\run-tests.ps1
```

Checks the loader against each installed copy of the game, using temporary copies of its exe.

`tools\strtool` decodes the game's encrypted strings. `tools\fctext` converts Food Court `.solution` files to and from text.
