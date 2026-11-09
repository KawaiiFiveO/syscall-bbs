using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>Read-only hooks for 20th Century Food Court (namespace Milwaukee).</summary>
internal static class FoodCourtHookPoints
{
    private static bool _resolved;
    private static Type _screen;
    private static MethodBase _tickMethod;
    private static MethodInfo _getSim, _getTick, _getPuzzle, _getModuleType;
    private static FieldInfo _current, _modules, _products, _from, _to, _moduleKind, _signalModule, _signalSocket, _level, _x, _y;

    public static void Define()
    {
        HookHost.Define("FoodCourt.PuzzleOpened", AttachPuzzleOpened);
        HookHost.Define("FoodCourt.TickCompleted", AttachTick);
    }

    private static Type Milwaukee(string name) => CodeShape.RequireType(HookHost.Game, "Milwaukee." + name);

    private static void ResolveTick()
    {
        if (_resolved)
            return;

        _screen = Milwaukee("PuzzleScreen");
        Type sim = Milwaukee("FullSim");
        Type module = Milwaukee("Module");
        Type moduleType = Milwaukee("ModuleType");
        Type product = Milwaukee("SimProduct");

        // The two signal sets. The tick method copies "current" into "previous", which identifies both.
        FieldInfo[] sets = sim.GetFields(CodeShape.AllDeclared).Where(f => IsSignalSet(f, module)).ToArray();
        if (sets.Length != 2)
            throw new ShapeMismatchException($"expected 2 signal sets on FullSim, found {sets.Length}");
        Func<CodeInstruction, bool> writesSet = i => i.opcode == OpCodes.Stfld && sets.Contains(i.operand);
        _tickMethod = CodeShape.SingleMethod(CodeShape.DeclaredMethods(_screen), il => il.Any(writesSet), "PuzzleScreen tick method");
        List<CodeInstruction> tick = CodeShape.ReadIL(_tickMethod);
        var previous = (FieldInfo)CodeShape.Single(tick.Where(writesSet), "write to the previous signal set").operand;
        _current = sets.Single(f => f != previous);

        Type signal = _current.FieldType.GetGenericArguments()[0];
        _signalModule = CodeShape.SingleField(signal, f => !f.IsStatic && f.FieldType == module, "module field on a signal");
        _signalSocket = CodeShape.SingleField(signal, f => !f.IsStatic && f.FieldType == typeof(int), "socket field on a signal");

        // "sim.tick = sim.tick + 1" gives the getter.
        _getTick = FindTickGetter(tick, sim);
        _getSim = (MethodInfo)CodeShape.Single(
            _screen.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == sim && m.GetParameters().Length == 0),
            "PuzzleScreen simulation getter");

        _modules = CodeShape.SingleField(sim, f => !f.IsStatic && f.FieldType == typeof(List<>).MakeGenericType(module), "module list on FullSim");
        _products = CodeShape.SingleField(sim, f => !f.IsStatic && f.FieldType == typeof(List<>).MakeGenericType(product), "product list on FullSim");

        // Each tick does "product.from = product.to".
        (_from, _to) = FindProductPositions(tick, product);
        _x = _from.FieldType.GetField("X") ?? throw new ShapeMismatchException("Index2.X not found");
        _y = _from.FieldType.GetField("Y") ?? throw new ShapeMismatchException("Index2.Y not found");

        _getModuleType = (MethodInfo)CodeShape.Single(
            module.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == moduleType && m.GetParameters().Length == 0),
            "Module type getter");
        _moduleKind = CodeShape.SingleField(moduleType,
            f => !f.IsStatic && f.FieldType.IsEnum && Enum.GetNames(f.FieldType).Contains("Sequencer"), "kind field on ModuleType");

        _resolved = true;
    }

    private static bool IsSignalSet(FieldInfo f, Type module)
    {
        if (f.IsStatic || !f.FieldType.IsGenericType || f.FieldType.GetGenericTypeDefinition() != typeof(HashSet<>))
            return false;
        FieldInfo[] fields = f.FieldType.GetGenericArguments()[0].GetFields(CodeShape.AllDeclared).Where(x => !x.IsStatic).ToArray();
        return fields.Count(x => x.FieldType == module) == 1 && fields.Count(x => x.FieldType == typeof(int)) == 1;
    }

    private static MethodInfo FindTickGetter(List<CodeInstruction> il, Type sim)
    {
        var hits = new List<MethodInfo>();
        for (int i = 0; i + 3 < il.Count; i++)
        {
            // Declared on FullSim's base class, so compare by inheritance.
            if (il[i].operand is MethodInfo get && get.DeclaringType.IsAssignableFrom(sim) && get.ReturnType == typeof(int) && get.GetParameters().Length == 0 &&
                il[i + 1].opcode == OpCodes.Ldc_I4_1 && il[i + 2].opcode == OpCodes.Add &&
                il[i + 3].operand is MethodInfo set && set.DeclaringType.IsAssignableFrom(sim) && set.GetParameters().Length == 1)
            {
                hits.Add(get);
            }
        }
        return CodeShape.Single(hits, "tick counter increment");
    }

    private static (FieldInfo From, FieldInfo To) FindProductPositions(List<CodeInstruction> il, Type product)
    {
        var hits = new List<(FieldInfo, FieldInfo)>();
        for (int i = 0; i + 1 < il.Count; i++)
        {
            if (il[i].opcode == OpCodes.Ldfld && il[i + 1].opcode == OpCodes.Stfld &&
                il[i].operand is FieldInfo to && il[i + 1].operand is FieldInfo from &&
                to.DeclaringType == product && from.DeclaringType == product &&
                to.FieldType == from.FieldType && to.FieldType.Name == "Index2")
            {
                hits.Add((from, to));
            }
        }
        // The tick starts with "from = to" for every product; later, a blocked move does the reverse.
        if (hits.Count == 0 || hits.Skip(1).Any(h => h != (hits[0].Item2, hits[0].Item1)))
            throw new ShapeMismatchException($"unexpected product position updates: found {hits.Count}");
        return hits[0];
    }

    private static void AttachPuzzleOpened()
    {
        Type puzzle = Milwaukee("Puzzle");
        _getPuzzle = (MethodInfo)CodeShape.Single(
            Milwaukee("Solution").GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == puzzle && m.GetParameters().Length == 0),
            "Solution puzzle getter");
        _level = CodeShape.SingleField(puzzle,
            f => !f.IsStatic && f.FieldType.IsEnum && Enum.GetNames(f.FieldType).Contains("WineBar"), "level field on Puzzle");

        MethodBase ctor = CodeShape.Single(CodeShape.InstanceConstructors(Milwaukee("PuzzleScreen")), "PuzzleScreen constructor");
        if (ctor.GetParameters().Length != 1 || ctor.GetParameters()[0].ParameterType != _getPuzzle.DeclaringType)
            throw new ShapeMismatchException("PuzzleScreen constructor no longer takes a Solution");
        HookHost.Harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(FoodCourtHookPoints), nameof(PuzzlePostfix)));
    }

    private static void PuzzlePostfix(object[] __args) =>
        HookHost.Raise("FoodCourt.PuzzleOpened", () => _level.GetValue(_getPuzzle.Invoke(__args[0], null)).ToString());

    private static void AttachTick()
    {
        ResolveTick();
        HookHost.Harmony.Patch(_tickMethod,
            prefix: new HarmonyMethod(typeof(FoodCourtHookPoints), nameof(TickPrefix)),
            postfix: new HarmonyMethod(typeof(FoodCourtHookPoints), nameof(TickPostfix)));
    }

    private static int ReadTick(object screen) => (int)_getTick.Invoke(_getSim.Invoke(screen, null), null);

    private static void TickPrefix(object __instance, out int __state)
    {
        __state = -1;
        try { __state = ReadTick(__instance); }
        catch (Exception e) { Log.Error("FoodCourt.TickCompleted: could not read the tick: " + e.Message); }
    }

    // The tick method can return without advancing, so only a changed counter counts as a tick.
    private static void TickPostfix(object __instance, int __state)
    {
        if (__state < 0)
            return;
        HookHost.Raise("FoodCourt.TickCompleted", () =>
        {
            int tick = ReadTick(__instance);
            return tick == __state ? null : Snapshot(_getSim.Invoke(__instance, null), tick);
        });
    }

    private static FoodCourtTick Snapshot(object sim, int tick)
    {
        var modules = (IList)_modules.GetValue(sim);
        var indexes = new Dictionary<object, int>();
        var names = new string[modules.Count];
        for (int i = 0; i < modules.Count; i++)
        {
            indexes[modules[i]] = i;
            names[i] = _moduleKind.GetValue(_getModuleType.Invoke(modules[i], null)).ToString();
        }

        var signals = new List<FoodCourtSignal>();
        foreach (object signal in (IEnumerable)_current.GetValue(sim))
        {
            if (indexes.TryGetValue(_signalModule.GetValue(signal), out int index))
                signals.Add(new FoodCourtSignal(index, (int)_signalSocket.GetValue(signal)));
        }

        var products = new List<FoodCourtProduct>();
        foreach (object product in (IEnumerable)_products.GetValue(sim))
        {
            object from = _from.GetValue(product), to = _to.GetValue(product);
            products.Add(new FoodCourtProduct((int)_x.GetValue(from), (int)_y.GetValue(from), (int)_x.GetValue(to), (int)_y.GetValue(to)));
        }
        return new FoodCourtTick(tick, names, signals, products);
    }
}
