# Save file

Last Call BBS keeps progress for the desktop and every program in one text file, `save.dat`.

**Warning:** The game won't start if `save.dat` doesn't end with `<END OF FILE>`. Edit it only while the game is closed: the running game keeps its own copy and writes it back.

## Location

`Documents/My Games/Last Call BBS/<user>/save.dat`, where `<user>` is `NonSteamUser` on GOG and the SteamID64 on Steam.

The game writes a backup to `<user>/backup/save-<ticks>.dat` at most once a minute, and keeps the newest 10.

## Format

```
Burbank.Launched = True
Milwaukee.BestScores.1 = 8,65
StoryStarted = True


Do not remove this end-of-file indicator:
<END OF FILE>
```

- One `Key = Value` line per key, sorted by key. Lines end in CRLF.
- Each line is split at its first `=`, and both sides are trimmed.
- Missing keys take their default: `False`, `0`, or none.

| Value type | Form |
|---|---|
| Flag | `True` or `False` |
| Number | Decimal integer |
| Time | `DateTime` ticks, local time |
| Data | zlib-compressed, then Base64 |

## Keys

`<n>` is a number the game assigns: the level, puzzle, or item. Food Court levels start at 1 (2Twelve).

### General

| Key | Type | Meaning |
|---|---|---|
| `UniquePlayerID` | Text | 18 random characters, created on first use. Used as the ticket ID in crash reports. |
| `StoryStarted` | Flag | Story progress. |
| `StoryFinished` | Flag | Story progress. |

### Terminal

| Key | Type | Meaning |
|---|---|---|
| `Terminal.Downloaded.<n>` | Flag | Program `<n>` is downloaded. See the table below. |
| `Terminal.PerformanceQuery` | Time | Last download finished. The download quota ends 15 minutes later. |
| `Terminal.PerformanceOffset` | Time | End of the 12-hour wait. Set when the last program finishes downloading. |
| `Terminal.LastCallAccessed` | Flag | Connected to Last Call BBS. |
| `Terminal.LastCallShutDown` | Flag | Story progress. |
| `Terminal.WorldTimeAccessed` | Flag | Story progress. |
| `Terminal.EquityFirstAccessed` | Flag | Story progress. |
| `Terminal.Custom.<hash>` | Data | Text a custom server stored with `saveData`. `<hash>` is the .NET Framework `String.GetHashCode` of the script's text, so editing a script loses its data. |

| `<n>` | Program |
|---|---|
| 0 | Dungeons & Diagrams |
| 1 | 20th Century Food Court |
| 2 | Kabufuda Solitaire |
| 3 | HACK\*MATCH |
| 4 | Steed Force Hobby Studio |
| 5 | X'BPGH: The Forbidden Path |
| 6 | ChipWizard Professional |

### Programs

| Key | Type | Meaning |
|---|---|---|
| `Tokyo.PuzzleSolved.<n>` | Flag | Dungeons & Diagrams: puzzle solved. |
| `Tokyo.Solution.<n>` | Data | Dungeons & Diagrams: board, one byte per cell. |
| `Tokyo.BonusPuzzle` | Number | Dungeons & Diagrams: seed of the current bonus puzzle, shown as 8 digits. Default 23044101. |
| `Tokyo.BonusCount` | Number | Dungeons & Diagrams: bonus puzzles explored. |
| `Milwaukee.BestScores.<n>` | `time,cost` | Food Court: best time and best cost, each kept separately. |
| `Milwaukee.LastSolution.<n>` | Text | Food Court: file name of the last opened solution, without `.solution`. |
| `Milwaukee.EmailRead.<id>` | Flag | Food Court: email `<id>` read. |
| `Nagoya.WinCount` | Number | Kabufuda Solitaire: wins. |
| `Nagoya.HighestDifficulty` | Number | Kabufuda Solitaire: highest difficulty won. Default -1. |
| `Burbank.HighScore` | Number | HACK\*MATCH: high score. |
| `Burbank.HighScoreEndless` | Number | HACK\*MATCH: high score in endless mode. |
| `Burbank.Launched` | Flag | HACK\*MATCH: opened at least once. |
| `Burbank.Completed` | Flag | HACK\*MATCH: story progress. |
| `Sydney.Launched` | Flag | Steed Force Hobby Studio: opened at least once. |
| `Sydney.MechAssembled.<n>` | Flag | Steed Force Hobby Studio: model `<n>` assembled. |
| `Toronto.PuzzleSolved.<n>` | Flag | X'BPGH: puzzle solved. |
| `Toronto.Solution.<n>.<slot>` | Data | X'BPGH: solution in a slot. |
| `Toronto.LastSolution.<n>` | Number | X'BPGH: last used slot. |
| `Toronto.StoryWatched.<n>` | Flag | X'BPGH: story progress. |
| `Toronto.BonusCampaignSeen` | Flag | X'BPGH: story progress. |
| `Volgograd.Solution.<n>.<slot>` | Data | ChipWizard Professional: solution in a slot. |
| `Volgograd.LastSolution.<n>` | Number | ChipWizard Professional: last used slot. |
| `Volgograd.LastScore.<n>` | Number | ChipWizard Professional: score of the last run. Present once the puzzle is solved. |
| `Volgograd.BestScore.<n>` | Number | ChipWizard Professional: best score. |
| `Hikone.WinCount` | Number | Solitaire (desktop): wins. |
