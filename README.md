# SysCall BBS

SysCall BBS is a mod loader for Last Call BBS (Zachtronics) on Windows.

GOG and Steam builds are supported. SysCall does not modify game files.

## Requirements

* Release zip: Windows, .NET Framework 4.7.2 or later.
* Building from source: .NET SDK and the .NET Framework 4.7.2 Developer Pack.

## Installation

1. Extract the release zip.
2. Run `Install.cmd`. It finds the game folder from the registry.
3. Start the game with `Last Call BBS (Modded).exe` in the game folder.

`Install.cmd` accepts these options:

| Option | Effect |
|---|---|
| `-GameDir <dir>` | Installs to `<dir>` instead of the detected game folder. |
| `-WithHookLogger` | Also installs `HookLogger`, which prints every hook as it fires. |
| `-WithFoodCourtTools` | Also installs `FoodCourtTools`, which runs Food Court solutions on demand. |
| `-WithYomiel` | Also installs `Yomiel`, a desktop program that plays Z-machine story files. |

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

### Yomiel

`Yomiel` (`-WithYomiel`) adds a desktop program that plays Z-machine story files: Infocom-format `.z5` and `.z8` games. Story files are not included. Put them in `mods\YomielPlayer\stories\`.

| Key | Effect |
|---|---|
| Enter | In the list: play the story, or continue where it was left. |
| F2 | In the list: start the story over. |
| F5 | In the list: read the story folder again. |
| Up, Down | In a story: previous and next command, unless the story uses them. |
| Page Up, Page Down | In a story: scroll back through earlier text. |

Closing the window keeps the story's place for next time. The window can be minimized.

The window's menu:

| Item | Effect |
|---|---|
| Refresh List | Reads the story folder again. In a story, Story List keeps the story's place and returns to the list. |
| Open Story Folder | Opens the story folder in Explorer. |
| FONT | The font. See below. |
| SPEED | Full, or an emulated machine of 200,000, 50,000 or 15,000 Z-machine instructions per second. Text appears only as fast as that machine would have produced it, so the story pauses where it does a lot of work, and saving and restoring take a moment. "Please wait..." shows during pauses over a second. |
| TEXT | Instant; a line at a time at 30 or 10 lines per second, as on an IBM PC; or a character at a time at 9600, 2400, 1200 or 300 baud, as over a modem. Lines appear already wrapped. |
| This Story Only | In a story: its speed and text settings apply to it alone, so one story can run at 1200 baud while others don't. |
| SOUND | Disk Sounds: the game's disk noises when a story loads (short, medium or long, by file size) and on save and restore. Beeps: the story's high beep (the organizer's beep) and low beep (Food Court's error buzz). Both on by default. |
| About Yomiel | Version, folders and keys. |

| Font | Look |
|---|---|
| Regular | The desktop font's thinner face. The default. |
| Bold | The desktop font as the terminal draws it. |
| Mono | The game's monospaced font. ASCII only: accented letters lose their accents. |
| CGA | The game's 8x8 IBM PC font, with accented letters and box drawing. |

Menu choices are kept in `preferences.ini`, and a story's own speed and text settings in `preferences.ini` in its folder. Saves, transcripts (`SCRIPT`) and recordings (`RECORDING`) are kept in `Documents\My Games\Last Call BBS\<user>\mods\onehalf.yomielplayer\<story file>\`. `REPLAY` reads `commands.txt` from the same folder.

Settings in `mods\YomielPlayer\config.ini`:

| Setting | Default | Effect |
|---|---|---|
| `Columns`, `Rows` | `96`, `30` | Window size in characters. At most 106 x 33. |
| `StoryFolder` | `stories` | Folder with story files. A relative path is inside the mod's folder. |
| `Scrollback` | `1000` | Lines kept for scrolling back. |
| `MorePrompts` | `true` | Pauses with `[MORE]` when more than a screenful of text arrives at once. |

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

* Build, install and verify: `launcher\install.ps1 [-GameDir <dir>] [-WithHookLogger] [-WithFoodCourtTools] [-WithYomiel]`
* Yomiel, the Z-machine core: `yomiel\` (see [yomiel/README.md](yomiel/README.md))
* Release zip: `launcher\package.ps1`, written to `dist\`
* Tests: `tests\run-tests.ps1`

## License

See [LICENSE.txt](LICENSE.txt).
