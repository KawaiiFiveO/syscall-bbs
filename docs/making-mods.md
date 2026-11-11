# Making Mods

This guide is for developers wanting to create mods.

## Project Setup

1.  Create a new C# Class Library project targeting **.NET Framework 4.7.2**, with `<LangVersion>latest</LangVersion>` (the sample below needs C# 10 or later).
2.  Reference `LastCallBBS.ModAPI.dll` (available in the game folder or from the `modapi/` project).
3.  Mods with `scope = full` also need `0Harmony`. If you build from source, the `modapi/` project provides it.

To ensure your mod is safe for `scoped` usage, ensure your project does not reference `0Harmony` or other protected libraries.

### Building

Compile your project using MSBuild or Visual Studio. Place your DLL in its own `mods\<Name>\` folder in your game folder, next to a `mod.ini` that sets at least `id` and `scope`.

## Implementing a Mod

Your mod must implement the `IMod` interface in `LastCallBBS.ModAPI`.

```csharp
using LastCallBBS.ModAPI;

namespace MyMod;

public sealed class MyMod : IMod
{
    private ModContext _context;

    public void OnLoad(ModContext context)
    {
        _context = context;
        // Hook or logic here
    }
}
```

### Scoped Mods

Scoped mods are restricted to specific game subsystems. To create a scoped mod, set the `scope` field in your `mod.ini`.

Common scopes:
*   `game`
*   `terminal`
*   `desktop`
*   `story`
*   `foodcourt`
*   `content`
*   `programs`

A mod is blocked during loading if its DLL:
*   References the game assembly directly.
*   References `0Harmony`, `Mono.Cecil`, `Steamworks.NET`, or `MonoMod*`.
*   Uses reflection (e.g., `System.Reflection`).
*   Uses dynamic lookups (e.g., `System.Type.GetType`).
*   Calls native code via P/Invoke.

**Exceptions**: Reflection types ending in `Attribute` and certain standard `MemberInfo` calls are allowed.

### Hooks

Hooks allow you to react to game events. Hooks are categorized in `ModHooks`.

| Category | Hooks | Data Type |
|---|---|---|
| `Game` | `Update` | (none) |
| `Terminal` | `DownloadStarted` | `string` |
| | `DownloadCompleted` | `string` |
| | `Connected` | (none) |
| `Desktop` | `WindowOpened` | `string` |
| `Story` | `EntryUnlocked` | `StoryEntry` |
| `FoodCourt` | `PuzzleOpened` | `string` |
| | `TickCompleted` | `FoodCourtTick` |

**Note**: If a game update changes a hook's signature, the subscription will throw a `ShapeMismatchException`, and the mod will fail to load.

#### `StoryEntry`
```csharp
public sealed class StoryEntry {
    public int Index { get; }
    public string Title { get; }
}
```

#### `FoodCourtTick`
```csharp
public sealed class FoodCourtTick {
    public int Tick { get; }
    public IReadOnlyList<string> Modules { get; }
    public IReadOnlyList<FoodCourtSignal> Signals { get; }
    public IReadOnlyList<FoodCourtSignal> PreviousSignals { get; }
    public IReadOnlyList<FoodCourtProduct> Products { get; }
}
```

### Mod Configuration

Use `ModConfig` to read settings from a `config.ini` file.

```csharp
var mySetting = _context.Config.GetBool("MySetting", true);
var myInt = _context.Config.GetInt("MyCount", 10);
var myString = _context.Config.GetString("MyName", "Default");
```

### Textures

`context.Textures.Load("icon.png")` returns a `ModTexture` with `Width` and `Height`.

* The path is relative to the mod's folder.
* Throws if the file is missing, outside the mod's folder, or not a PNG.
* Loading the same file twice returns the same object.

`GameTexture` (`scope = full`) is the game's `Texture` object, with premultiplied alpha.

* Created on first read.
* Read it only on the game's main thread after startup, for example in a hook handler. Not in `OnLoad`.

### Desktop Programs

A program is a text window opened from a desktop icon: a grid of characters with keyboard input. Needs `scope = programs` (or `full`). `mods/Typewriter` is a complete example.

```csharp
public void OnLoad(ModContext context) =>
    context.Programs.Add("Notes", context.Textures.Load("icon.png"), new NotesProgram());
```

* The icon is drawn at its own size, centred over its label like the game's icons, which are about 64×80 and at most 100×100.
* Icons fill a second column next to the game's, top to bottom, in load order.
* One window per program. Double-clicking the icon of an open program doesn't open a second window.

`IDesktopProgram` members, all called on the game's main thread:

| Member | When |
|---|---|
| `Title` | Every frame |
| `Columns`, `Rows` | When the window opens. At most 106×34. |
| `Opened(window)` | Window opened, with a blank screen |
| `KeyPressed(window, key)` | Each key press or auto-repeat, top window only |
| `Update(window, seconds)` | Every frame while shown, after that frame's keys. The screen is drawn after it. |
| `Closed(window)` | Window closed |

An exception from any member stops the program until its window is reopened. The window shows the error, and the details go to the log.

`window.Screen` is a `TextScreen`:

* `Put`, `Write` (one row, no wrapping), `Fill`, `Clear`, `Scroll(top, bottom, lines)`. Row 0 is the top. Writes outside the grid are clipped.
* `Foreground` and `Background` are the colours for writes that don't name one. `Background` is also the window's background.
* `CursorColumn`, `CursorRow`, `CursorVisible`: a blinking block cursor.
* Colours are `TextColor.FromRgb(0xRRGGBB)`.
* Cells are 9×15 pixels in the desktop font.

`KeyPress` has `Key` (`Character` with `Char`, or `Enter`, `Backspace`, `Delete`, `Escape`, `Tab`, arrows, `Home`, `End`, `PageUp`, `PageDown`, `F1`–`F12`) and `Shift`, `Control`, `Alt`. Typed characters in a frame come before other keys.

`window.DataDirectory` (or `context.Programs.DataDirectory`) is the mod's folder for saved files: `My Games\Last Call BBS\<user>\mods\<id>`. It's created on first use, and is available once the game is running, not in `OnLoad`.

## Full Access and Code Shaping

Mods with `scope = full` have access to all hooks and the `context.Game` and `context.Harmony` objects.

### Patching via CodeShape

To ensure patches remain compatible with game updates, use `CodeShape` instead of hard-coded obfuscated names. Patches should match **exactly one** location in the code.

```csharp
// Example: Patching a method in MainWindow
_context.Harmony.Patch(
    CodeShape.SingleMethod(
        CodeShape.DeclaredMethods(mainWindowType), 
        il => FindTarget(il).Any(), 
        "description of match"
    ),
    transpiler: new HarmonyMethod(typeof(MyMod), nameof(MyTranspiler))
);
```

## Replacing Assets

Mods with `content` or `full` scope can replace assets in the `Content\` folder.
*   **Note**: Most textures are packed `.tex` files. A `.tex` can be replaced as is, but there's no PNG-to-`.tex` converter. New images don't need one: see [Textures](#textures).

## Troubleshooting

*   **Mod fails to load**: Check the `LastCallBBS.Launcher.log` in the game folder.
*   **Compilation**: Always compile your project outside of the game folders to avoid accidental modification of game assets.
