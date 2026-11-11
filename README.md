# Last Call LDR

A mod loader for Last Call BBS (Windows, GOG and Steam). It does not modify game files.

## Requirements

*   **Release zip**: Windows, .NET Framework 4.7.2 or later.
*   **From source**: .NET SDK and .NET Framework 4.7.2 Developer Pack.

## Installation

Extract the release zip and run `Install.cmd`.

You can provide the following options to `Install.cmd`:

*   `-WithHookLogger` : Installs `HookLogger` (prints every hook as it fires).
*   `-WithFoodCourtTools` : Installs `FoodCourtTools` (runs Food Court solutions on demand).

## Uninstalling

To uninstall, remove the following files and folders from your game directory:

*   `Last Call BBS (Modded).exe`
*   `Last Call BBS (Modded).exe.config`
*   `0Harmony.dll`
*   `Mono.Cecil.dll`
*   `LastCallBBS.ModAPI.dll`
*   `LastCallBBS.Launcher.ini`
*   `mods\` folder

Mod settings (if any) are stored in `mods\<Name>\config.ini` and will be removed along with the `mods` folder.

## Installing Mods

1.  Download a mod and extract it.
2.  Drop the mod's folder into the `mods\` directory in your game folder.

**Warning:** Only install mods from trusted sources.

### Scoped Mods
Some mods are restricted to specific parts of the game. If a mod is blocked, you can allow it by adding `AllowUnscoped.<id> = true` to `LastCallBBS.Launcher.ini` (replace `<id>` with the mod's ID).

### SkipLoading settings
The `SkipLoading` mod provides the following settings in `mods\SkipLoading\config.ini`:

| Setting | Default | Description |
|---|---|---|
| `InstantDownloads` | `true` | Finishes BBS downloads immediately. |
| `NoDownloadCooldown` | `true` | Removes the 15-minute download quota. |
| `SkipTwelveHourWait` | `false` | Skips the 12-hour wait after all downloads. |

## Steam

*   Modded runs block Steam leaderboards.
*   You can specify a custom save folder in `LastCallBBS.Launcher.ini` using the `SaveFolder` setting.

## Troubleshooting

*   **Logs**: The log is written to `LastCallBBS.Launcher.log` in the game folder.
*   **Startup Warning**: If you see a startup warning, verify your installation by running `Last Call BBS (Modded).exe --verify`.
*   **Updates**: After a game update, run the `--verify` command to ensure compatibility.

## Documentation

*   [Making Mods](docs/making-mods.md)
*   [Food Court Tools](docs/food-court-tools.md)
*   [Food Court Format](docs/food-court-format.md)

## Development

*   Tests: `tests\run-tests.ps1`
*   Build release: `launcher\package.ps1`
*   `strtool` is only available in the source repository.
