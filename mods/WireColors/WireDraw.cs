using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.WireColors;

/// <summary>
/// Draws colored wires with recolored copies of the game's wire textures. The puzzle screen draws
/// each wire with one call, given the wire (a Maybe, empty for a wire being plugged in), its two
/// ends, and whether it's hovered and lit. The call picks from 8 textures (off or lit, hovered or
/// not, left or right half); every load of one is passed through <see cref="Pick"/>. The art is
/// saturated orange with almost no blue, so a vertex tint can't recolor it.
/// </summary>
internal static class WireDraw
{
    private static MethodInfo _draw;
    private static Type _texture, _maybeWire;
    private static FieldInfo _hasWire, _wireValue, _texturePath, _hasPath, _pathValue;
    private static Recolor _recolor;
    private static IModLog _log;

    // Recolored copies by color, for each of the game's textures. Textures throw on GetHashCode,
    // and the table compares references.
    private static readonly ConditionalWeakTable<object, object[]> Copies = new();

    private static byte _color;

    /// <summary>The wire drawn hovered since the last <see cref="ClearHovered"/>, or null.</summary>
    public static object Hovered { get; private set; }

    public static void ClearHovered() => Hovered = null;

    public static void Apply(Assembly game, Harmony harmony, Recolor recolor, IModLog log)
    {
        _recolor = recolor;
        _log = log;
        Type screen = CodeShape.RequireType(game, "Milwaukee.PuzzleScreen");
        Type wire = CodeShape.RequireType(game, "Milwaukee.Wire");
        Type vector2 = CodeShape.RequireType(game, "Vector2");
        Type maybe = CodeShape.RequireType(game, "Maybe`1");
        _texture = CodeShape.RequireType(game, "Texture");
        _maybeWire = maybe.MakeGenericType(wire);

        _draw = CodeShape.Single(screen.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == typeof(void) &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { _maybeWire, vector2, vector2, typeof(bool), typeof(bool) })),
            "wire draw method");
        List<CodeInstruction> il = CodeShape.ReadIL(_draw) ?? throw new ShapeMismatchException("can't read the wire draw method");
        List<FieldInfo> loads = TextureLoads(il).Select(i => (FieldInfo)il[i].operand).ToList();
        if (loads.Count != 8 || loads.Distinct().Count() != 8 || loads.Select(f => f.DeclaringType).Distinct().Count() != 1)
            throw new ShapeMismatchException($"expected the wire draw method to load 8 textures of one set, found {loads.Count}");

        // Maybe<T> holds a flag and a value.
        _hasWire = CodeShape.SingleField(_maybeWire, f => !f.IsStatic && f.FieldType == typeof(bool), "Maybe flag");
        _wireValue = CodeShape.SingleField(_maybeWire, f => !f.IsStatic && f.FieldType == wire, "Maybe value");
        // A texture loaded from a file keeps the file's path under Content/Packed, without the extension.
        Type maybeString = maybe.MakeGenericType(typeof(string));
        _texturePath = CodeShape.SingleField(_texture, f => !f.IsStatic && f.FieldType == maybeString, "texture source path");
        _hasPath = CodeShape.SingleField(maybeString, f => !f.IsStatic && f.FieldType == typeof(bool), "Maybe flag");
        _pathValue = CodeShape.SingleField(maybeString, f => !f.IsStatic && f.FieldType == typeof(string), "Maybe value");

        harmony.Patch(_draw, transpiler: new HarmonyMethod(typeof(WireDraw), nameof(Transpiler)));
    }

    private static IEnumerable<int> TextureLoads(List<CodeInstruction> il) =>
        Enumerable.Range(0, il.Count).Where(i => il[i].opcode == OpCodes.Ldfld && il[i].operand is FieldInfo f && f.FieldType == _texture);

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        foreach (int i in TextureLoads(il).Reverse().ToList())
        {
            il.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WireDraw), nameof(Pick))));
            il.Insert(i + 2, new CodeInstruction(OpCodes.Castclass, _texture));
        }
        // Begin(wire, hovered) first. Argument 0 is the screen.
        CodeInstruction[] begin =
        {
            new(OpCodes.Ldarg_1),
            new(OpCodes.Box, _maybeWire),
            CodeInstruction.LoadArgument(4),
            new(OpCodes.Call, AccessTools.Method(typeof(WireDraw), nameof(Begin))),
        };
        begin[0].MoveLabelsFrom(il[0]);
        il.InsertRange(0, begin);
        return il;
    }

    public static void Begin(object maybeWire, bool hovered)
    {
        _color = 0;
        if (!(bool)_hasWire.GetValue(maybeWire))
            return;
        object wire = _wireValue.GetValue(maybeWire);
        _color = ColorStore.Get(wire);
        if (hovered)
            Hovered = wire;
    }

    public static object Pick(object texture)
    {
        if (_color == 0 || _color > Recolor.Colors)
            return texture;
        object[] copies = Copies.GetValue(texture, _ => new object[Recolor.Colors + 1]);
        return copies[_color] ??= Make(texture, _color);
    }

    // A failed copy is the original, so it's only tried once.
    private static object Make(object texture, byte color)
    {
        object path = _texturePath.GetValue(texture);
        if (!(bool)_hasPath.GetValue(path))
            return texture;
        try
        {
            return _recolor.Make((string)_pathValue.GetValue(path), color) ?? texture;
        }
        catch (Exception e)
        {
            _log.Error($"Can't recolor {_pathValue.GetValue(path)}: {e.Message}");
            return texture;
        }
    }
}
