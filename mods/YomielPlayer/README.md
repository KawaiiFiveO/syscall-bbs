# Yomiel for Last Call BBS

A desktop program that plays Z-machine interactive fiction (`.z5`, `.z8`), built on [Yomiel](https://github.com/KawaiiFiveO/yomiel).

## Playing

1. Put story files in `mods/YomielPlayer/stories/`. Free stories are on [IFDB](https://ifdb.org/) and the [IF Archive](https://ifarchive.org/).
2. Open Yomiel on the desktop.

Closing the window keeps the story's place.

| Key | Effect |
|---|---|
| F2 | Start the selected story over. |
| Page Up, Page Down | Scroll back. |

## Menu

| Item | Effect |
|---|---|
| SPEED | Emulates a machine of 200,000, 50,000 or 15,000 Z-machine instructions per second. The story pauses where it does a lot of work. |
| TEXT | A line at a time at 30 or 10 lines per second, or a character at a time at 9600, 2400 or 1200 baud. |
| This Story Only | The story's speed and text settings apply to it alone. |
| SOUND | Disk Sounds: the game's disk noises on load, save and restore. Beeps: the story's beeps. |

The Mono font is ASCII only.

## Files

`Documents/My Games/Last Call BBS/<user>/mods/onehalf.yomielplayer/` holds `preferences.ini` (menu choices) and a folder per story with its saves, `SCRIPT` transcripts, `RECORDING` files, `commands.txt` for `REPLAY`, and its own `preferences.ini`.

Window size, story folder, scrollback and `[MORE]` prompts are set in `mods/YomielPlayer/config.ini`.
