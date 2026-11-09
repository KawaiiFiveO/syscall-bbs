# Making mods

A mod is a folder in `mods\` in the game folder:

```
mods\MyMod\
  MyMod.dll
  mod.ini
  config.ini    (optional)
```

## Project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="LastCallBBS.ModAPI">
      <HintPath>PATH\TO\GAME\LastCallBBS.ModAPI.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>PATH\TO\GAME\0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

## Code

Each mod has exactly one `IMod` class:

```csharp
using LastCallBBS.ModAPI;

public sealed class MyMod : IMod
{
    public void OnLoad(ModContext context)
    {
        context.Hooks.FoodCourt.PuzzleOpened.Subscribe(level => context.Log.Info("Opened " + level));
    }
}
```

An exception from `OnLoad` disables the mod. `context.ReportProblem(message)` reports a partial failure and keeps it loaded.

## mod.ini

```ini
id = author.mymod
name = My Mod
version = 1.0.0
scope = foodcourt
```

| Key | |
|---|---|
| `id` | Required. Lowercase letters, digits, `.`, `_`, `-` |
| `scope` | Required. See below |
| `name`, `version` | Optional |
| `dll` | Optional. Needed if the folder has several DLLs and none matches the folder name |

## Scopes and hooks

`scope` is `full`, or a comma-separated list of the scopes a mod uses.

| Scope | Hook | Passes |
|---|---|---|
| `game` | `Game.Update` | |
| `terminal` | `Terminal.DownloadStarted`, `DownloadCompleted` | program name |
| | `Terminal.Connected` | |
| `desktop` | `Desktop.WindowOpened` | window type name |
| `story` | `Story.EntryUnlocked` | `StoryEntry` |
| `foodcourt` | `FoodCourt.PuzzleOpened` | level name |
| | `FoodCourt.TickCompleted` | `FoodCourtTick` |

Handlers run on the game's main thread. A handler that throws 3 times is removed. Food Court data is a read-only copy, and tick numbers restart with each order.

A scoped mod is blocked if any DLL in its folder references the game or Harmony, uses reflection, or calls native code.

## Full access

`scope = full` adds `context.Game` and `context.Harmony`. Scrambled names change between game builds, so find patch targets with `CodeShape`, which requires exactly one match:

```csharp
var window = CodeShape.RequireType(context.Game, "Terminal.MainWindow");
var target = CodeShape.SingleMethod(CodeShape.DeclaredMethods(window), il => /* match IL */ true, "what this is");
```

See `mods\SkipLoading` for real patches.

## Settings and logging

```csharp
bool verbose = context.Config.GetBool("Verbose");   // also GetInt, GetString
context.Log.Info("message");                        // also Warn, Error
```

`Config` reads `config.ini` in the mod's folder.

## Checking a mod

```powershell
& ".\Last Call BBS (Modded).exe" --verify
```

Run it in the game folder. It loads every mod and attaches every hook without starting the game, then writes `LastCallBBS.Launcher.log`. A non-zero exit code means something failed.
