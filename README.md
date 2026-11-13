# SysCall BBS

SysCall BBS is a mod loader for Last Call BBS (Zachtronics) on Windows.

GOG and Steam builds are supported. SysCall does not modify game files.

## Requirements

* Release zip: Windows, .NET Framework 4.7.2 or later.
* Building from source: .NET SDK and the .NET Framework 4.7.2 Developer Pack.

## Installation

1. Extract the release zip.
2. Run `Install.cmd`, or drop the game's folder on it.
3. Choose the mods to install.
4. Start the game with `Last Call BBS (Modded).exe` in the game folder.

Dropping a mod's folder on `Install.cmd` adds it to the list. Reinstalling keeps existing `.ini` files.

| Option | Effect |
|---|---|
| `-GameDir <dir>` | Installs to `<dir>`. |
| `-Mods <names>` | Installs these mods without asking: folder names separated by commas, `all` or `none`. |
| `-Uninstall` | Uninstalls without asking. |

### Uninstall

Run `Install.cmd` and choose Uninstall. SysCall will be removed; saves are kept.

To uninstall by hand, delete these from the game folder:

* `Last Call BBS (Modded).exe`
* `Last Call BBS (Modded).exe.config`
* `0Harmony.dll`
* `Mono.Cecil.dll`
* `LastCallBBS.ModAPI.dll`
* `LastCallBBS.Launcher.ini`
* `LastCallBBS.Launcher.log`
* `mods\`

## Mods

To install a mod, copy its folder into `mods\` in the game folder. Each mod's settings are in `mods\<Name>\config.ini`, and its documentation in `mods\<Name>\README.md`.

**Warning:** Mods run with the same permissions as the game. Only install mods from sources you trust.

### Scoped mods

A mod's `mod.ini` can limit it to parts of the game. The loader blocks a scoped mod whose code reaches outside its scope. To allow a blocked mod, add this line to `LastCallBBS.Launcher.ini`, where `<id>` is the mod's id:

```ini
AllowUnscoped.<id> = true
```

### SkipLoading

`SkipLoading` is installed by default. It allows downloads to finish instantly and removes the 15-minute quota. Settings are in `mods\SkipLoading\config.ini`.

### Yomiel

A desktop program that plays Z-machine interactive fiction. See [mods/YomielPlayer/README.md](mods/YomielPlayer/README.md).

### FoodCourtTools

Runs 20th Century Food Court solutions on request, edits them as text in the running game, and adds multiple playback speeds. See [mods/FoodCourtTools/README.md](mods/FoodCourtTools/README.md).

## Configuration

Edit `LastCallBBS.Launcher.ini` in the game folder.

## Steam

* Steam leaderboards are always blocked in modded runs.
* Starting the game through Steam is unaffected (launches vanilla).

## Documentation

* [Making mods](docs/making-mods.md)
* [Food Court solution format](docs/food-court-format.md)
* [Food Court netlist language](docs/food-court-netlist.md)

## Build

* Build, install and verify: `launcher\install.ps1 [-GameDir <dir>] [-Mods <names>]`
* Yomiel, the Z-machine core, is the `yomiel\` submodule: `git submodule update --init` (see [yomiel/docs/library.md](yomiel/docs/library.md))
* Releases: pushing a `v*` tag runs `.github/workflows/release.yml`, which attaches the zip to a draft release
* Release zip: `launcher\package.ps1`, written to `dist\`
* Tests: `tests\run-tests.ps1`

## License

SysCall BBS is licensed under the [0BSD License](https://xn--gba.st/kawaiifiveo).
