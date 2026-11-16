using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.WireColors;

/// <summary>
/// Shows the wire count on the puzzle screen's cost display, toggled with C. The display is drawn
/// by the game's LCD-digit call, which draws unlit "8"s at reduced alpha and the value on top; for
/// cost the format adds "$" and "k". The wire count keeps the unlit "$888k" and lights only the
/// digits. The cost sum the display shows is also the score saved on completion, so the patch
/// marks the one draw site rather than changing the sum. GIF frames, rendered by a second puzzle
/// screen through the same update, always show the cost.
/// </summary>
internal static class CostDisplay
{
    private static MethodInfo _costSum, _lcdDraw, _options, _spacing, _measure, _draw, _fromHex, _withAlpha;
    private static PropertyInfo _width;
    private static ConstructorInfo _vector;
    private static FieldInfo _x, _y;
    private static int _hex;
    private static float _unlitAlpha;
    private static Func<object, int> _wireCount;
    private static IModLog _log;
    private static bool _showWires, _recording, _pending, _failed;
    private static int _wires;

    public static void Toggle() => _showWires = !_showWires;

    public static void Apply(Assembly game, Harmony harmony, IModLog log)
    {
        _log = log;
        Type screen = CodeShape.RequireType(game, "Milwaukee.PuzzleScreen");
        Type solution = CodeShape.RequireType(game, "Milwaukee.Solution");
        Type fullSim = CodeShape.RequireType(game, "Milwaukee.FullSim");
        Type wire = CodeShape.RequireType(game, "Milwaukee.Wire");
        Type vector2 = CodeShape.RequireType(game, "Vector2");
        Type color = CodeShape.RequireType(game, "Color");
        Type options = CodeShape.RequireType(game, "TextDrawingOptions");
        Type bounds = CodeShape.RequireType(game, "Bounds2");
        MethodBase[] methods = CodeShape.DeclaredMethods(screen).ToArray();

        // The LCD-digit call: (position, value, digits, font, format, spacing, unlit alpha).
        _lcdDraw = CodeShape.Single(methods.OfType<MethodInfo>().Where(m => m.IsStatic && m.IsPublic && m.ReturnType == typeof(void) &&
            m.GetParameters().Select(p => p.ParameterType).ToArray() is Type[] p && p.Length == 7 && p[0] == vector2 &&
            p[1] == typeof(int) && p[2] == typeof(int) && p[4].IsEnum && p[5] == typeof(int) && p[6] == typeof(float)), "LCD digit draw");
        // The cost: the screen's only int() method summing over the modules.
        _costSum = (MethodInfo)CodeShape.SingleMethod(methods.Where(m => !m.IsStatic && m is MethodInfo mi && mi.ReturnType == typeof(int) && m.GetParameters().Length == 0),
            il => il.Any(i => i.operand is MethodInfo c && c.DeclaringType == typeof(Enumerable) && c.Name == "Sum"), "cost sum");
        CodeShape.Single(methods.Where(m => CodeShape.ReadIL(m) is List<CodeInstruction> il && Sites(il).Any()), "cost display draw");
        // GIF frames: the screen's public bool(bool) call that runs its own update.
        MethodInfo update = screen.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
            ?? throw new ShapeMismatchException("PuzzleScreen.Update not found");
        var gifFrame = (MethodInfo)CodeShape.SingleMethod(methods.Where(m => !m.IsStatic && m.IsPublic && m is MethodInfo mi && mi.ReturnType == typeof(bool) &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(bool) })), il => il.Any(i => i.Calls(update)), "GIF frame render");

        // What the LCD call uses: the text options factory, its color and its unlit alpha.
        List<CodeInstruction> lcd = CodeShape.ReadIL(_lcdDraw) ?? throw new ShapeMismatchException("can't read the LCD digit draw");
        _options = CodeShape.Single(lcd.Select(i => i.operand).OfType<MethodInfo>().Where(m => m.IsStatic && m.ReturnType == options &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string), vector2, _lcdDraw.GetParameters()[3].ParameterType, color })).Distinct(), "text options factory");
        _fromHex = color.GetMethod("FromHex", new[] { typeof(int) }) ?? throw new ShapeMismatchException("Color.FromHex not found");
        _hex = CodeShape.Single(Enumerable.Range(1, lcd.Count - 1).Where(i => lcd[i].Calls(_fromHex)).Select(i => lcd[i - 1].operand is int v ? v : (int?)null), "LCD color")
            ?? throw new ShapeMismatchException("LCD color isn't a constant");
        _unlitAlpha = CodeShape.Single(lcd.Where(i => i.opcode == OpCodes.Ldc_R4).Select(i => (float)i.operand), "unlit alpha");
        _withAlpha = color.GetMethod("WithAlpha", new[] { typeof(float) }) ?? throw new ShapeMismatchException("Color.WithAlpha not found");
        _spacing = options.GetMethod("WithAdditionalCharacterSpacing", new[] { typeof(float), typeof(bool) }) ?? throw new ShapeMismatchException("text spacing not found");
        _measure = options.GetMethod("Measure", Type.EmptyTypes) ?? throw new ShapeMismatchException("text Measure not found");
        _draw = options.GetMethod("Draw", Type.EmptyTypes) ?? throw new ShapeMismatchException("text Draw not found");
        _width = bounds.GetProperty("Width") ?? throw new ShapeMismatchException("Bounds2.Width not found");
        _vector = vector2.GetConstructor(new[] { typeof(float), typeof(float) }) ?? throw new ShapeMismatchException("Vector2 constructor not found");
        _x = vector2.GetField("X") ?? throw new ShapeMismatchException("Vector2.X not found");
        _y = vector2.GetField("Y") ?? throw new ShapeMismatchException("Vector2.Y not found");

        // Wires: the screen's solution, its simulation, and the simulation's wire list.
        MethodInfo screenSolution = CodeShape.Single(methods.OfType<MethodInfo>().Where(m => !m.IsStatic && m.IsPublic && !m.IsSpecialName &&
            m.ReturnType == solution && m.GetParameters().Length == 0), "puzzle screen solution");
        FieldInfo sim = CodeShape.SingleField(solution, f => !f.IsStatic && f.FieldType == fullSim, "solution simulation");
        Type wireList = typeof(List<>).MakeGenericType(wire);
        FieldInfo wires = CodeShape.SingleField(fullSim, f => !f.IsStatic && f.FieldType == wireList, "wire list");
        ParameterExpression s = Expression.Parameter(typeof(object), "screen");
        _wireCount = Expression.Lambda<Func<object, int>>(Expression.Property(Expression.Field(Expression.Field(
            Expression.Call(Expression.Convert(s, screen), screenSolution), sim), wires), "Count"), s).Compile();

        harmony.Patch(update, transpiler: new HarmonyMethod(typeof(CostDisplay), nameof(Transpiler)));
        harmony.Patch(_lcdDraw, prefix: new HarmonyMethod(typeof(CostDisplay), nameof(BeforeLcdDraw)));
        harmony.Patch(gifFrame, prefix: new HarmonyMethod(typeof(CostDisplay), nameof(BeforeGifFrame)),
            finalizer: new HarmonyMethod(typeof(CostDisplay), nameof(AfterGifFrame)));
    }

    // A cost sum whose next call to either the sum or the LCD call is the LCD call.
    private static IEnumerable<int> Sites(List<CodeInstruction> il) =>
        Enumerable.Range(0, il.Count).Where(i => il[i].Calls(_costSum) &&
            il.Skip(i + 1).FirstOrDefault(n => n.Calls(_lcdDraw) || n.Calls(_costSum)) is CodeInstruction next && next.Calls(_lcdDraw));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        int site = CodeShape.Single(Sites(il), "cost display draw");
        il.InsertRange(site + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CostDisplay), nameof(Mark))),
        });
        return il;
    }

    /// <summary>Called with the cost on its way to the display; the next LCD call is the cost display.</summary>
    public static int Mark(int cost, object screen)
    {
        _pending = false;
        if (!_showWires || _recording || _failed)
            return cost;
        try
        {
            _wires = _wireCount(screen);
            _pending = true;
        }
        catch (Exception e)
        {
            Fail(e);
        }
        return cost;
    }

    private static bool BeforeLcdDraw(object[] __args)
    {
        if (!_pending)
            return true;
        _pending = false;
        try
        {
            DrawWires(__args[0], (int)__args[2], __args[3], Convert.ToInt32(__args[5]), (float)__args[6]);
            return false;
        }
        catch (Exception e)
        {
            Fail(e);
            return true;
        }
    }

    // "$888k" unlit, then the digits lit over the 8s.
    private static void DrawWires(object position, int digits, object font, int spacing, float unlit)
    {
        object color = _fromHex.Invoke(null, new object[] { _hex });
        string eights = new string('8', digits);
        object Text(string text, object at, object c) => _spacing.Invoke(_options.Invoke(null, new[] { text, at, font, c }), new object[] { (float)spacing, true });
        float Width(string text) => (float)_width.GetValue(_measure.Invoke(Text(text, position, color), null));

        _draw.Invoke(Text("$" + eights + "k", position, _withAlpha.Invoke(color, new object[] { _unlitAlpha * unlit })), null);
        float offset = Width("$" + eights + "k") - Width(eights + "k");
        object digitsAt = _vector.Invoke(new[] { (float)_x.GetValue(position) + offset, _y.GetValue(position) });
        _draw.Invoke(Text(_wires.ToString().PadLeft(digits, '0'), digitsAt, color), null);
    }

    private static void BeforeGifFrame() => _recording = true;

    private static Exception AfterGifFrame(Exception __exception)
    {
        _recording = false;
        return __exception;
    }

    private static void Fail(Exception e)
    {
        _log.Error("Wire count display failed: " + e);
        _failed = true;
    }
}
