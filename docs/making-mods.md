# Making mods

A mod is a .NET Framework DLL in its own folder under `mods\`, with a `mod.ini`. `mods\Typewriter` and `mods\FoodCourtStats` in the source repository are complete examples.

## Project

A class library targeting `net472`, C# 10 or later:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="LastCallBBS.ModAPI" HintPath="<game folder>\LastCallBBS.ModAPI.dll" Private="false" />
  </ItemGroup>
</Project>
```

* `LastCallBBS.ModAPI.dll` and `0Harmony.dll` are in the game folder after installation.
* Mods with `scope = full` that patch the game also reference `0Harmony.dll`, with `Private="false"`.
* Inside the source repository, use `<ProjectReference Include="..\..\modapi\LastCallBBS.ModAPI.csproj" Private="false" />` instead.
* Build outside the game folder, then copy the DLL into `mods\<Name>\`.

## mod.ini

```ini
id = author.modname
name = My Mod
version = 1.0.0
scope = terminal, story
```

| Key | Required | Value |
|---|---|---|
| `id` | yes | Lowercase letters, digits, `.`, `_` and `-`. Unique among installed mods. |
| `scope` | yes | `full`, or a comma-separated list of scopes. See [Scopes](#scopes). |
| `name` | no | Display name. Defaults to `id`. |
| `version` | no | Defaults to `0.0.0`. |
| `dll` | no | DLL file name in the mod's folder. Defaults to `<folder name>.dll`, or the folder's only DLL. |
| `requires` | no | Comma-separated mod ids. The mod fails to load if one of them isn't installed or failed to load. |
| `after` | no | Comma-separated mod ids to load first, if they are installed. |

Mods load in alphabetical order by folder name. `requires` and `after` override that order.

## Entry point

The DLL contains exactly one class implementing `IMod`:

```csharp
using LastCallBBS.ModAPI;

namespace MyMod;

public sealed class MyMod : IMod
{
    public void OnLoad(ModContext context)
    {
        context.Hooks.Terminal.DownloadCompleted.Subscribe(name => context.Log.Info($"Downloaded {name}"));
    }
}
```

`OnLoad` runs once, before the game starts. An exception from `OnLoad` disables the mod and removes its Harmony patches.

`ModContext` members:

| Member | Value |
|---|---|
| `Id`, `Name`, `Version` | From `mod.ini`. |
| `Directory` | The mod's folder, `mods\<Name>`. |
| `Scopes` | The scopes from `mod.ini`. |
| `Log` | `Info`, `Warn` and `Error`. Writes to the console and `LastCallBBS.Launcher.log`, labelled with the mod's id. |
| `Config` | The mod's `config.ini`. See [Configuration](#configuration). |
| `Hooks` | See [Hooks](#hooks). |
| `Textures` | See [Textures](#textures). |
| `Programs` | See [Desktop programs](#desktop-programs). |
| `Sounds` | See [Sounds](#sounds). |
| `Game` | The game assembly. `scope = full` only. |
| `Harmony` | The mod's Harmony instance, id `mod.<id>`. `scope = full` only. |
| `ReportProblem(message)` | Reports that part of the mod couldn't be applied. The rest stays loaded. The problem is logged, fails `--verify` and appears in the startup warning. |

Using `Game`, `Harmony` or another member outside the mod's scope throws `ModScopeException`.

## Scopes

| Scope | Grants |
|---|---|
| `game` | `Hooks.Game` |
| `terminal` | `Hooks.Terminal` |
| `desktop` | `Hooks.Desktop` |
| `story` | `Hooks.Story` |
| `foodcourt` | `Hooks.FoodCourt` |
| `programs` | `Programs` |
| `content` | Asset replacement. See [Replacing assets](#replacing-assets). |
| `full` | Everything, plus `Game`, `Harmony` and `GameTexture`. |

The loader blocks a mod without `full` scope if any DLL in its folder:

* references the game assembly, `0Harmony`, `Mono.Cecil`, `Steamworks.NET` or `MonoMod*`;
* uses a type from `System.Reflection`, except `MemberInfo` and types ending in `Attribute`;
* calls `Type.GetType`, `Type.InvokeMember` or `Activator` methods other than the generic `CreateInstance<T>()`;
* calls native code.

A user can allow a blocked mod with `AllowUnscoped.<id> = true` in `LastCallBBS.Launcher.ini`.

## Hooks

Handlers run on the game's main thread. A handler that keeps throwing is removed.

| Hook | Data | Fires when |
|---|---|---|
| `Game.Update` | none | Once per frame, after the active screen updates. |
| `Terminal.DownloadStarted` | `string` | A download starts. Carries the program's internal name, such as `Milwaukee`. |
| `Terminal.DownloadCompleted` | `string` | A download finishes. |
| `Terminal.Connected` | none | The dial-in to Last Call BBS finishes. |
| `Desktop.WindowOpened` | `string` | A window opens. Carries its type name, such as `Terminal.MainWindow`. |
| `Story.EntryUnlocked` | `StoryEntry` | A story entry unlocks. Entries unlocked in earlier sessions don't fire. |
| `FoodCourt.PuzzleOpened` | `string` | A puzzle screen opens. Carries the level's internal name, such as `WineBar`. |
| `FoodCourt.TickCompleted` | `FoodCourtTick` | The simulation advances one tick. |

`Subscribe` throws `ShapeMismatchException` if a game update has broken the hook.

`StoryEntry` has `Index` and `Title`.

`FoodCourtTick` is a copy of the simulation state. Changing it has no effect on the game.

| Member | Value |
|---|---|
| `Tick` | Tick number. Restarts for each order. |
| `Modules` | Each module's type name. The position is the module's index in the solution file. |
| `Signals` | Sockets that are on this tick, as `FoodCourtSignal` (`Module`, `Socket`). |
| `PreviousSignals` | Sockets that were on the tick before. On tick 1, the scanner's opening pulse. |
| `Products` | Each product's move this tick, as `FoodCourtProduct` (`FromX`, `FromY`, `ToX`, `ToY`). |

## Configuration

`context.Config` reads `config.ini` in the mod's folder. A missing file or key returns the fallback.

```csharp
bool enabled = context.Config.GetBool("Enabled", true);
int count = context.Config.GetInt("Count", 10);
string label = context.Config.GetString("Label", "Default");
```

* Lines are `Key = Value`. Keys are case-insensitive.
* Lines starting with `#` or `;` are comments.
* `GetBool` accepts `true`, `false`, `yes`, `no`, `on`, `off`, `1` and `0`. An unrecognised value logs a warning and returns the fallback.
* `Contains(key)` checks whether a key is set.

## Textures

`context.Textures.Load("icon.png")` returns a `ModTexture` with `Width`, `Height` and `Path`.

* The path is relative to the mod's folder.
* Throws if the file is missing, outside the mod's folder or not a PNG.
* Loading the same file twice returns the same object.

`ModTexture.GameTexture` is the game's `Texture` object, with premultiplied alpha. `scope = full` only.

* Created on first read.
* Read it on the game's main thread after startup, such as in a hook handler, not in `OnLoad`.

## Desktop programs

A program is a text window opened from a desktop icon. Requires `scope = programs` or `full`. `mods\Typewriter` is an example.

```csharp
public void OnLoad(ModContext context) =>
    context.Programs.Add("Notes", context.Textures.Load("icon.png"), new NotesProgram());
```

* The icon is drawn at its own size, centred over its label. The game's icons are about 64×80, at most 100×100.
* Program icons fill a second column next to the game's, top to bottom, in load order.
* Each program has at most one window.

`IDesktopProgram` members are called on the game's main thread:

| Member | Called |
|---|---|
| `Title` | Every frame. |
| `Columns`, `Rows` | When the window opens. At most 106×34, or 106×33 with a menu. |
| `Opened(window)` | When the window opens, with a blank screen. |
| `KeyPressed(window, key)` | On each key press or auto-repeat, in the top window only. |
| `Update(window, seconds)` | Every frame while shown, after that frame's keys. The screen is drawn afterwards. |
| `Closed(window)` | When the window closes. |

An exception from any member stops the program until its window is reopened. The window shows the error and the log has the details.

### Menu

A program that also implements `IProgramMenu` gets a menu in its window's title bar and a minimize button. The frame is 16 pixels taller, so the grid fits at most 33 rows (`TextScreen.MaxRowsWithMenu`).

```csharp
public IReadOnlyList<ProgramMenuItem> MenuItems => new[]
{
    new ProgramMenuItem("New Page", () => _lines.Clear()),
    ProgramMenuItem.Separator,
    new ProgramMenuItem("Print", Print, enabled: _lines.Count > 0),
};
```

* `MenuItems` is read every frame. Return the same list when nothing has changed.
* Actions run on the game's main thread. An exception stops the program, as for the other members.
* A minimized window gets no `Update` calls. The next `Update` reports all the time since the last one.

`window.Screen` is a `TextScreen`. Cells are 9×15 pixels. Column 0, row 0 is the top left. Writes outside the grid are clipped.

| Method | Effect |
|---|---|
| `Put(column, row, c[, fg, bg])` | Writes one character. |
| `Write(column, row, text[, fg, bg])` | Writes text on one row, without wrapping. Returns the column after the text. |
| `Fill(column, row, width, height[, c])` | Fills a rectangle with `c` (default space) in the current colours. |
| `Clear()` | Blanks the grid in the current colours and moves the cursor to the top left. |
| `Scroll(top, bottom, lines)` | Moves rows `top` to `bottom - 1` up by `lines`, or down if negative. Uncovered rows are blanked. |
| `GetChar`, `GetForeground`, `GetBackground(column, row)` | Reads a cell. |

| Property | Value |
|---|---|
| `Columns`, `Rows` | Grid size. |
| `Foreground` | Colour for writes that don't specify one. Default `0xD8D8D8`. |
| `Background` | Background for writes that don't specify one, and the window background. Default `0x101418`. |
| `CursorColumn`, `CursorRow` | Cursor position. |
| `CursorVisible` | Shows a blinking block cursor. |
| `Font` | The font the grid is drawn in. See below. |

Colours are `TextColor`, created with `TextColor.FromRgb(0xRRGGBB)` or `new TextColor(r, g, b)`.

`TextFont` values:

| Value | Font |
|---|---|
| `Bold` | The desktop font as the terminal draws it (`system_bold`). The default. |
| `Regular` | The desktop font's thinner face (`system_regular`). |
| `Mono` | The game's monospaced font (`font_cpmono`): thin, ASCII only. Accented letters lose their accents; other characters are drawn from `Cga`. |
| `Cga` | The game's IBM PC font (`font_cga`): 8×8, code page 437, with accented letters and box drawing. Characters outside code page 437 show as `?`. |

`Bold` and `Regular` are proportional, with each glyph centred in its cell. They lack `=`, `@`, `$`, `_`, `{`, `}`, `|`, `~` and accented letters, and draw `<` and `>` as tall chevrons; those characters are drawn from `Mono` instead, then `Cga`. A character no font has shows as `?`.

`KeyPress` members:

| Member | Value |
|---|---|
| `Key` | A `ProgramKey`. See below. |
| `Char` | The typed character when `Key` is `Character`, otherwise `'\0'`. |
| `Shift`, `Control`, `Alt` | Whether each modifier was held. |

`ProgramKey` values:

| Group | Values |
|---|---|
| Typed text | `Character` |
| Editing | `Enter`, `Backspace`, `Delete`, `Escape`, `Tab` |
| Navigation | `Up`, `Down`, `Left`, `Right`, `Home`, `End`, `PageUp`, `PageDown` |
| Function | `F1` to `F12` |

Typed characters come before other keys in the same frame.

`window.DataDirectory` and `context.Programs.DataDirectory` are the mod's folder for saved files, `My Games\Last Call BBS\<user>\mods\<id>`. The folder is created on first use. It is available once the game is running, not in `OnLoad`.

## Sounds

`context.Sounds.PlayGameSound(name, volume)` plays one of the game's sound effects. Every mod can use it. Call it on the game's main thread (program callbacks and hooks) once the game is running, not in `OnLoad`.

```csharp
context.Sounds.PlayGameSound("desktop/win_open");
context.Sounds.PlayGameSound("desktop/load_m", 0.8f);
```

* `name` is the sound's path under `Content\sounds\fx` without the extension.
* A name with a folder of variants, such as `desktop/load_m` for `Content\sounds\fx\desktop\load_m.array`, plays one of them at random.
* `volume` is 0 to 1.
* It returns `false` if the sound couldn't play. The first failure for each name is logged.

Sounds that suit programs:

| Name | Sound | Length |
|---|---|---|
| `desktop/load_s`, `load_m`, `load_l` | Disk activity when the game loads a program: short, medium, long | 0.85, 1.25, 1.65 s |
| `desktop/load_panic` | Disk error | 1.0 s |
| `desktop/win_open`, `win_close`, `win_minimize`, `win_unminimize` | Window chrome | 0.6 s |
| `desktop/menu_open`, `menu_close` | Menu panel | 0.6 s |
| `terminal/dial_bbs`, `terminal/download` | Modem | |

## Patching the game

Mods with `scope = full` can patch the game with `context.Harmony`. Most game members have obfuscated names that differ between builds, so find targets by the shape of their code with `CodeShape`:

```csharp
Type window = CodeShape.RequireType(context.Game, "Terminal.MainWindow");
MethodBase target = CodeShape.SingleMethod(
    CodeShape.DeclaredMethods(window),
    il => il.Any(i => i.Calls(AccessTools.PropertyGetter(typeof(DateTime), nameof(DateTime.Now)))),
    "MainWindow method that reads DateTime.Now");
context.Harmony.Patch(target, transpiler: new HarmonyMethod(typeof(MyMod), nameof(Transpiler)));
```

| Member | Returns |
|---|---|
| `RequireType(game, fullName)` | A type by its readable name. |
| `DeclaredMethods(type)` | Every method and constructor the type declares. |
| `InstanceConstructors(type)` | The type's non-static constructors. |
| `SingleMethod(candidates, il => …, what)` | The one candidate whose original IL matches. |
| `SingleField(type, field => …, what)` | The one matching field the type declares. |
| `Single(items, what)` | The one item in `items`. |
| `ReadIL(method)` | A method's original instructions, or `null` if it has no body. |
| `IsComparison(opcode)` | Whether an opcode is `blt`, `bge`, `bgt`, `ble`, `clt` or `cgt`, in any form. |

`RequireType` and the `Single…` methods throw `ShapeMismatchException` unless exactly one item matches.

## Replacing assets

Mods with `scope = content` or `full` replace game files: `mods\<Name>\Content\<path>` is read instead of `Content\<path>`. A mod with only `content` scope needs no DLL.

Most textures are packed `.tex` files. A `.tex` can be replaced as is, but there is no PNG to `.tex` converter. To add new images, see [Textures](#textures).

## Troubleshooting

* Load errors are in `LastCallBBS.Launcher.log` in the game folder.
* `Last Call BBS (Modded).exe --verify` loads every mod, attaches every hook, reports problems and exits without starting the game.
