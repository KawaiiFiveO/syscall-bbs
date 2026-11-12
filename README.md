# SysCall BBS

SysCall BBS is a mod loader for Last Call BBS (Zachtronics) on Windows.

GOG and Steam builds are supported. SysCall does not modify game files.

## Requirements

* Release zip: Windows, .NET Framework 4.7.2 or later.
* Building from source: .NET SDK and the .NET Framework 4.7.2 Developer Pack.

## Installation

1. Extract the release zip.
2. Run `Install.cmd`. It finds the game folder from the registry and asks if more than one copy is installed.
3. Start the game with `Last Call BBS (Modded).exe` in the game folder.

`Install.cmd` accepts these options:

| Option | Effect |
|---|---|
| `-GameDir <dir>` | Installs to `<dir>` instead of the detected game folder. |
| `-WithHookLogger` | Also installs `HookLogger`, which prints every hook as it fires. |
| `-WithFoodCourtTools` | Also installs `FoodCourtTools`, which runs Food Court solutions on demand. |

Reinstalling keeps existing `LastCallBBS.Launcher.ini` and `config.ini` files. The installer runs `Last Call BBS (Modded).exe --verify` when it finishes.

### Uninstall

Delete these from the game folder:

* `Last Call BBS (Modded).exe`
* `Last Call BBS (Modded).exe.config`
* `0Harmony.dll`
* `Mono.Cecil.dll`
* `LastCallBBS.ModAPI.dll`
* `LastCallBBS.Launcher.ini`
* `LastCallBBS.Launcher.log`
* `mods\`

## Mods

To install a mod, copy its folder into `mods\` in the game folder. Each mod's settings are in `mods\<Name>\config.ini`.

**Warning:** Mods run with the same permissions as the game. Only install mods from sources you trust.

### Scoped mods

A mod's `mod.ini` can limit it to parts of the game. The loader blocks a scoped mod whose code reaches outside its scope. To allow a blocked mod, add this line to `LastCallBBS.Launcher.ini`, where `<id>` is the mod's id:

```ini
AllowUnscoped.<id> = true
```

### SkipLoading

`SkipLoading` is installed by default. Settings in `mods\SkipLoading\config.ini`:

| Setting | Default | Effect |
|---|---|---|
| `InstantDownloads` | `true` | BBS downloads finish immediately. |
| `NoDownloadCooldown` | `true` | Removes the 15-minute download quota. |
| `SkipTwelveHourWait` | `false` | Skips the 12-hour wait after the last download. This triggers the story's BBS shutdown. |

## Configuration

`LastCallBBS.Launcher.ini` in the game folder:

| Setting | Default | Effect |
|---|---|---|
| `Console` | `true` | Shows the log in a console window while the game runs. |
| `Achievements` | `true` | Steam only. Enables achievements. |
| `RichPresence` | `true` | Steam only. Enables rich presence. |
| `LocalScoreList` | `true` | Steam only. Shows your own scores in the Food Court results list. |
| `SaveFolder` | blank | Steam only. Folder in `Documents\My Games\Last Call BBS` to use for modded runs. `NonSteamUser` uses the GOG saves. Blank uses the normal Steam save. |
| `GameDirectory` | blank | Folder containing `Last Call BBS.exe`. Blank means the loader's own folder. |

## Steam

* Steam leaderboards are always blocked in modded runs.
* Starting the game through Steam is unaffected.

## Documentation

* [Making mods](docs/making-mods.md)
* [Food Court tools](docs/food-court-tools.md)
* [Food Court solution format](docs/food-court-format.md)

## Build

* Build, install and verify: `launcher\install.ps1 [-GameDir <dir>] [-WithHookLogger] [-WithFoodCourtTools]`
* Release zip: `launcher\package.ps1`, written to `dist\`
* Tests: `tests\run-tests.ps1`

## License

See [LICENSE.txt](LICENSE.txt).
