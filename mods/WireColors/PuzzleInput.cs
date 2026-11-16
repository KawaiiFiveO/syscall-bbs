using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.WireColors;

/// <summary>
/// Keys on the puzzle screen. Middle-click and the number keys color the hovered wire; F5 exports
/// the open solution without colors; C toggles the cost display's wire count. The game's input calls ignore the puzzle screen while another
/// screen is on top, and wires are only drawn hovered while the solution is stopped and nothing is
/// held, so colors change only when the game would accept an edit.
/// </summary>
internal static class PuzzleInput
{
    private const string ChangeSound = "milwaukee/patch_place", ExportSound = "milwaukee/accept", ErrorSound = "milwaukee/error";

    private static Func<int, bool> _keyPressed;
    private static Func<bool> _middleHeld;
    private static int[] _colorKeys, _keypadKeys;
    private static int _exportKey, _wireCountKey;
    private static MethodInfo _changed, _commit, _screenSolution, _writeSolution, _fileName;
    private static FieldInfo _gameInstance, _manager, _writerBytes;
    private static Func<string> _exportFolder;
    private static ModSounds _sounds;
    private static IModLog _log;
    private static bool _middleWasHeld, _failed;

    public static void Apply(Assembly game, Harmony harmony, ModContext context)
    {
        _sounds = context.Sounds;
        _log = context.Log;
        ModPrograms programs = context.Programs;
        _exportFolder = () => Path.Combine(programs.DataDirectory, "export");
        ResolveInput(game);
        ResolveScreen(game);

        Type screen = CodeShape.RequireType(game, "Milwaukee.PuzzleScreen");
        MethodInfo update = screen.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
            ?? throw new ShapeMismatchException("PuzzleScreen.Update not found");
        harmony.Patch(update, prefix: new HarmonyMethod(typeof(PuzzleInput), nameof(BeforeUpdate)), postfix: new HarmonyMethod(typeof(PuzzleInput), nameof(AfterUpdate)));
    }

    private static void ResolveInput(Assembly game)
    {
        MethodInfo typed = CodeShape.Single(game.GetTypes().SelectMany(t => t.GetMethods(CodeShape.AllDeclared))
            .Where(m => m.IsStatic && m.IsPublic && m.ReturnType == typeof(List<char>) && m.GetParameters().Length == 0), "typed-characters getter");
        Type input = typed.DeclaringType;
        Type key = CodeShape.RequireType(game, "Key");
        MethodInfo[] statics = input.GetMethods(CodeShape.AllDeclared).Where(m => m.IsStatic && m.IsPublic).ToArray();

        // The "pressed or repeated this frame" check that takes modifiers and a key: the only one
        // reading the key set directly. Modifiers 0 means none held, so Ctrl+1 and the like pass by.
        MethodInfo modifiers = CodeShape.Single(statics.Where(m => m.GetParameters().Length == 0 && m.ReturnType.IsEnum && m.ReturnType != key), "modifier state getter");
        MethodInfo keyContains = typeof(HashSet<>).MakeGenericType(key).GetMethod("Contains");
        var pressed = (MethodInfo)CodeShape.SingleMethod(statics.Where(m => m.ReturnType == typeof(bool) &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { modifiers.ReturnType, key })),
            il => il.Any(i => i.Calls(keyContains)), "pressed-or-repeated key check");
        ParameterExpression k = Expression.Parameter(typeof(int), "key");
        _keyPressed = Expression.Lambda<Func<int, bool>>(Expression.Call(pressed,
            Expression.Convert(Expression.Constant(0), modifiers.ReturnType), Expression.Convert(k, key)), k).Compile();

        int KeyValue(string name) => Enum.IsDefined(key, name) ? Convert.ToInt32(Enum.Parse(key, name)) : throw new ShapeMismatchException($"Key.{name} not found");
        _colorKeys = Enumerable.Range(0, 10).Select(n => KeyValue("NR_" + n)).ToArray();
        _keypadKeys = Enumerable.Range(0, 10).Select(n => KeyValue("KP_" + n)).ToArray();
        _exportKey = KeyValue("F5");
        _wireCountKey = KeyValue("c");

        // The held check for a mouse button: the only (button) method reading the held set once.
        Type button = CodeShape.Single(game.GetTypes().Where(t => t.IsEnum && new[] { "Left", "Middle", "Right", "Back" }.All(n => Enum.IsDefined(t, n))), "mouse button enum");
        MethodInfo buttonContains = typeof(HashSet<>).MakeGenericType(button).GetMethod("Contains");
        var held = (MethodInfo)CodeShape.SingleMethod(statics.Where(m => m.ReturnType == typeof(bool) &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { button })),
            il => il.Count(i => i.Calls(buttonContains)) == 1, "mouse button held check");
        _middleHeld = Expression.Lambda<Func<bool>>(Expression.Call(held, Expression.Constant(Enum.Parse(button, "Middle")))).Compile();
    }

    private static void ResolveScreen(Assembly game)
    {
        Type screen = CodeShape.RequireType(game, "Milwaukee.PuzzleScreen");
        Type solution = CodeShape.RequireType(game, "Milwaukee.Solution");
        MethodInfo[] screenMethods = screen.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && !m.IsSpecialName && m.GetParameters().Length == 0).ToArray();
        _screenSolution = CodeShape.Single(screenMethods.Where(m => m.IsPublic && m.ReturnType == solution), "puzzle screen solution");

        // The undo history is the solution's one generic field. "Changed" marks it pending, and is
        // the screen's only call doing so; "commit" records a pending change, the screen's only push.
        FieldInfo history = CodeShape.SingleField(solution, f => !f.IsStatic && f.FieldType.IsGenericType && f.FieldType.GetGenericArguments().Length == 1, "undo history");
        Type snapshot = history.FieldType.GetGenericArguments()[0];
        MethodInfo[] historyMethods = history.FieldType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        MethodInfo markPending = CodeShape.Single(historyMethods.Where(m => m.ReturnType == typeof(void) && m.GetParameters().Length == 0), "undo history pending marker");
        var push = (MethodInfo)CodeShape.SingleMethod(historyMethods.Where(m => m.ReturnType == typeof(void) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == snapshot),
            il => il.Any(i => CallsList(i, "Add")) && !il.Any(i => CallsList(i, "Clear")), "undo history push");
        _changed = (MethodInfo)CodeShape.SingleMethod(screenMethods.Where(m => m.IsPublic && m.ReturnType == typeof(void)),
            il => il.Any(i => Same(i.operand, markPending)), "puzzle screen change method");
        _commit = (MethodInfo)CodeShape.SingleMethod(screenMethods.Where(m => m.ReturnType == typeof(void)),
            il => il.Any(i => Same(i.operand, push)), "puzzle screen commit");

        // Export: the solution's own writer, and the file name the game saves it under.
        _writeSolution = CodeShape.Single(solution.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == typeof(void) &&
            m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == ColorStore.Writer), "solution writer");
        _writerBytes = CodeShape.SingleField(ColorStore.Writer, f => !f.IsStatic && f.FieldType == typeof(List<byte>), "writer's bytes");
        Type gameLogic = CodeShape.RequireType(game, "GameLogic");
        Type manager = CodeShape.RequireType(game, "Milwaukee.SolutionManager");
        _gameInstance = CodeShape.SingleField(gameLogic, f => f.IsStatic && f.FieldType == gameLogic, "GameLogic instance");
        _manager = CodeShape.SingleField(gameLogic, f => !f.IsStatic && f.FieldType == manager, "solution manager");
        _fileName = CodeShape.Single(manager.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(string) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == solution), "solution file name");
    }

    private static void BeforeUpdate() => WireDraw.ClearHovered();

    private static void AfterUpdate(object __instance)
    {
        try
        {
            if (_keyPressed(_exportKey))
                Export(__instance);
            if (_keyPressed(_wireCountKey))
                CostDisplay.Toggle();
            bool middle = _middleHeld();
            bool clicked = middle && !_middleWasHeld;
            _middleWasHeld = middle;

            object wire = WireDraw.Hovered;
            if (wire == null)
                return;
            byte current = ColorStore.Get(wire);
            int? color = clicked ? (current >= Recolor.Colors ? 0 : current + 1) : PressedNumber();
            if (color == null || color == current)
                return;
            ColorStore.Set(wire, (byte)color);
            // As the game does after any edit: an undo step, and a save.
            _changed.Invoke(__instance, null);
            _commit.Invoke(__instance, null);
            _sounds.PlayGameSound(ChangeSound);
        }
        catch (Exception e)
        {
            if (!_failed)
                _log.Error("Puzzle screen input failed: " + e);
            _failed = true;
        }
    }

    // 1 to 8 pick a color and 0 clears it, on the number row or the keypad.
    private static int? PressedNumber()
    {
        for (int n = 0; n <= Recolor.Colors; n++)
        {
            if (_keyPressed(_colorKeys[n]) || _keyPressed(_keypadKeys[n]))
                return n;
        }
        return null;
    }

    private static void Export(object screen)
    {
        object solution = _screenSolution.Invoke(screen, null);
        object manager = _manager.GetValue(_gameInstance.GetValue(null));
        string name = (string)_fileName.Invoke(manager, new[] { solution });
        byte[] bytes;
        ColorStore.Vanilla = true;
        try
        {
            object writer = Activator.CreateInstance(ColorStore.Writer, true);
            _writeSolution.Invoke(solution, new[] { writer });
            bytes = ((List<byte>)_writerBytes.GetValue(writer)).ToArray();
        }
        finally
        {
            ColorStore.Vanilla = false;
        }

        try
        {
            string folder = _exportFolder();
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name + ".solution");
            File.WriteAllBytes(path, bytes);
            _log.Info("Exported without colors: " + path);
            _sounds.PlayGameSound(ExportSound);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            _log.Error("Export failed: " + e.Message);
            _sounds.PlayGameSound(ErrorSound);
        }
    }

    private static bool Same(object operand, MethodInfo method) =>
        operand is MethodInfo m && m.MetadataToken == method.MetadataToken && m.Module == method.Module;

    private static bool CallsList(CodeInstruction i, string name) =>
        (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo m &&
        m.Name == name && m.DeclaringType.IsGenericType && m.DeclaringType.GetGenericTypeDefinition() == typeof(List<>);
}
