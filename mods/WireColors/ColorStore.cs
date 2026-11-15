using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LastCallBBS.FoodCourt;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.WireColors;

/// <summary>
/// Each wire's color, saved in a block after the wire list by the simulation's serializer. Saving,
/// undo, Copy Solution and simulation clones all go through the serializer, so they keep colors.
/// </summary>
internal static class ColorStore
{
    private sealed class Box
    {
        public byte Color;
    }

    // Keyed by the game's Wire objects. Moving a wire's end keeps its object, and so its color.
    private static readonly ConditionalWeakTable<object, Box> Colors = new();

    private static FieldInfo _wires, _readerBytes, _readerPosition, _writerBytes;
    private static IModLog _log;
    private static bool _failed;

    /// <summary>While set, the serializer writes no color block, as the unmodded game does.</summary>
    [ThreadStatic] public static bool Vanilla;

    public static void Apply(Assembly game, Harmony harmony, IModLog log)
    {
        _log = log;
        Type sim = CodeShape.RequireType(game, "Milwaukee.FullSim");
        Type wire = CodeShape.RequireType(game, "Milwaukee.Wire");
        _wires = CodeShape.SingleField(sim, f => !f.IsStatic && f.FieldType == typeof(List<>).MakeGenericType(wire), "wire list");

        // The loader is static and returns a new simulation; it takes the reader, the format version and the level.
        MethodInfo read = CodeShape.Single(sim.GetMethods(CodeShape.AllDeclared).Where(m => m.IsStatic && m.ReturnType == sim && m.GetParameters().Length == 3),
            "simulation loader");
        Type reader = read.GetParameters()[0].ParameterType;
        _readerBytes = CodeShape.SingleField(reader, f => !f.IsStatic && f.FieldType == typeof(byte[]), "reader's bytes");
        _readerPosition = CodeShape.SingleField(reader, f => !f.IsStatic && f.FieldType == typeof(int), "reader's position");

        // The writer collects bytes in a list.
        MethodInfo write = CodeShape.Single(sim.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == typeof(void) &&
            m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.GetFields(CodeShape.AllDeclared).Any(f => !f.IsStatic && f.FieldType == typeof(List<byte>))),
            "simulation writer");
        Writer = write.GetParameters()[0].ParameterType;
        _writerBytes = CodeShape.SingleField(Writer, f => !f.IsStatic && f.FieldType == typeof(List<byte>), "writer's bytes");

        harmony.Patch(write, postfix: new HarmonyMethod(typeof(ColorStore), nameof(AfterWrite)));
        harmony.Patch(read, postfix: new HarmonyMethod(typeof(ColorStore), nameof(AfterRead)));
    }

    /// <summary>The game's binary writer type.</summary>
    public static Type Writer { get; private set; }

    public static byte Get(object wire) => Colors.TryGetValue(wire, out Box box) ? box.Color : (byte)0;

    public static void Set(object wire, byte color)
    {
        if (color == 0)
            Colors.Remove(wire);
        else
            Colors.GetOrCreateValue(wire).Color = color;
    }

    private static void AfterWrite(object __instance, object __0)
    {
        if (Vanilla)
            return;
        try
        {
            IList wires = (IList)_wires.GetValue(__instance);
            byte[] colors = wires.Cast<object>().Select(Get).ToArray();
            if (WireColorBlock.AnySet(colors))
                ((List<byte>)_writerBytes.GetValue(__0)).AddRange(WireColorBlock.Block(colors));
        }
        catch (Exception e)
        {
            Fail("saving wire colors", e);
        }
    }

    // The block comes straight after the wires. Anything else there is left for the game to read.
    private static void AfterRead(object __0, object __result)
    {
        try
        {
            IList wires = (IList)_wires.GetValue(__result);
            byte[] data = (byte[])_readerBytes.GetValue(__0);
            int position = (int)_readerPosition.GetValue(__0);
            byte[] colors = WireColorBlock.TryRead(data, position, wires.Count, out int length);
            if (colors == null)
                return;
            _readerPosition.SetValue(__0, position + length);
            for (int i = 0; i < colors.Length; i++)
                Set(wires[i], colors[i]);
        }
        catch (Exception e)
        {
            Fail("loading wire colors", e);
        }
    }

    private static void Fail(string what, Exception e)
    {
        if (_failed)
            return;
        _failed = true;
        _log.Error($"Failed {what}: {e}");
    }
}
