using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.FoodCourtTools;

internal sealed class RunOutcome
{
    public string Status;          // "ok", "error" or "timeout"
    public string Error;
    public List<int> OrderTicks = new();
}

/// <summary>
/// Runs a solution through the game's own simulation, the way its GIF export does: a separate copy
/// on a <c>PuzzleScreen</c> that never goes on screen. Run mode skips the completion screen, which is
/// the only thing that records scores. Ticks are called directly, so nothing is drawn or animated.
/// </summary>
internal sealed class SimulationRunner
{
    private const int MaxTicks = 5000;

    private readonly ConstructorInfo _newScreen, _newReader;
    private readonly MethodInfo _load, _tick, _start, _getSim, _getPuzzle;
    private readonly FieldInfo _running, _orderTicks, _error, _errorMessage, _sampleOrders;
    private readonly Func<object, object> _readSolution, _readError;

    public SimulationRunner(Assembly game)
    {
        Type screen = CodeShape.RequireType(game, "Milwaukee.PuzzleScreen");
        Type sim = CodeShape.RequireType(game, "Milwaukee.FullSim");
        Type solution = CodeShape.RequireType(game, "Milwaukee.Solution");

        _newScreen = (ConstructorInfo)CodeShape.Single(
            CodeShape.InstanceConstructors(screen).Where(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == solution),
            "PuzzleScreen constructor");

        _load = CodeShape.Single(
            solution.GetMethods(CodeShape.AllDeclared).Where(m => m.IsStatic && m.GetParameters().Length == 1 && IsMaybeOf(m.ReturnType, solution)),
            "solution loader");
        _newReader = _load.GetParameters()[0].ParameterType
            .GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(byte[]) }, null)
            ?? throw new ShapeMismatchException("the solution reader no longer takes a byte array");
        _readSolution = MaybeReader(_load.ReturnType, solution);

        // The tick is the one method that copies a signal set on the simulation.
        FieldInfo[] signalSets = sim.GetFields(CodeShape.AllDeclared)
            .Where(f => !f.IsStatic && f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(HashSet<>)).ToArray();
        _tick = (MethodInfo)CodeShape.SingleMethod(
            screen.GetMethods(CodeShape.AllDeclared).Cast<MethodBase>().Where(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool)),
            il => il.Any(i => i.opcode == OpCodes.Stfld && signalSets.Contains(i.operand)), "tick method");

        // Starting an order is the one no-argument method calling tick(true). It also sets "running".
        MethodBase startOrder = CodeShape.SingleMethod(NoArgVoidMethods(screen),
            il => Pairs(il).Any(p => p.Item1.opcode == OpCodes.Ldc_I4_1 && p.Item2.Calls(_tick)), "method starting an order");
        List<CodeInstruction> startIl = CodeShape.ReadIL(startOrder);
        _running = (FieldInfo)CodeShape.Single(
            Pairs(startIl).Where(p => p.Item1.opcode == OpCodes.Ldc_I4_1 && IsBoolWrite(p.Item2, sim)).Select(p => p.Item2.operand),
            "running flag");

        // Starting a run is the public method that sets a flag and starts an order.
        _start = (MethodInfo)CodeShape.SingleMethod(NoArgVoidMethods(screen).Where(m => m.IsPublic),
            il => il.Any(i => i.Calls((MethodInfo)startOrder)) && il.Any(i => IsBoolWrite(i, sim)), "method starting a run");

        _getSim = (MethodInfo)CodeShape.Single(
            screen.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == sim && m.GetParameters().Length == 0),
            "simulation getter");
        _orderTicks = CodeShape.SingleField(screen, f => !f.IsStatic && f.FieldType == typeof(List<int>), "list of order times");

        // A simulation error is a Maybe of a type holding a message and the affected tiles.
        _error = CodeShape.SingleField(sim, f => !f.IsStatic && IsMaybe(f.FieldType) && HasRange(f.FieldType.GetGenericArguments()[0]), "simulation error field");
        Type errorType = _error.FieldType.GetGenericArguments()[0];
        _errorMessage = CodeShape.SingleField(errorType, f => !f.IsStatic && f.FieldType == typeof(string), "simulation error message");
        _readError = MaybeReader(_error.FieldType, errorType);

        // In run mode the order list returns a level's short sample, if it has one: the getter
        // checks that array's length and may return it, so it's the array loaded twice.
        MethodInfo orders = (MethodInfo)CodeShape.Single(
            sim.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.GetParameters().Length == 0 && m.ReturnType.IsArray && m.ReturnType.GetElementType().Name == "Product"),
            "order list getter");
        _sampleOrders = CodeShape.Single(
            CodeShape.ReadIL(orders).Where(i => i.opcode == OpCodes.Ldfld && i.operand is FieldInfo f && f.FieldType == orders.ReturnType)
                .GroupBy(i => (FieldInfo)i.operand).Where(g => g.Count() == 2).Select(g => g.Key),
            "sample order list");
        _getPuzzle = CodeShape.Single(
            solution.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.GetParameters().Length == 0 && m.ReturnType == _sampleOrders.DeclaringType),
            "solution puzzle getter");
    }

    public RunOutcome Run(byte[] solutionBytes)
    {
        object reader = _newReader.Invoke(new object[] { solutionBytes });
        object solution = _readSolution(_load.Invoke(null, new[] { reader }));
        if (solution == null)
            return new RunOutcome { Status = "error", Error = "the game didn't recognise this solution's format version" };

        // Blank the sample so every order runs, as it does when the player presses run.
        object puzzle = _getPuzzle.Invoke(solution, null);
        object sample = _sampleOrders.GetValue(puzzle);
        _sampleOrders.SetValue(puzzle, Array.CreateInstance(_sampleOrders.FieldType.GetElementType(), 0));
        try
        {
            return RunAllOrders(solution);
        }
        finally
        {
            _sampleOrders.SetValue(puzzle, sample);
        }
    }

    private RunOutcome RunAllOrders(object solution)
    {
        object screen = _newScreen.Invoke(new[] { solution });
        _start.Invoke(screen, null);
        object sim = _getSim.Invoke(screen, null);

        for (int ticks = 0; (bool)_running.GetValue(sim); ticks++)
        {
            string error = ReadError(sim);
            if (error != null)
                return new RunOutcome { Status = "error", Error = error, OrderTicks = OrderTicks(screen) };
            if (ticks >= MaxTicks)
                return new RunOutcome { Status = "timeout", Error = $"still running after {MaxTicks} ticks", OrderTicks = OrderTicks(screen) };
            _tick.Invoke(screen, new object[] { false });
        }

        string finalError = ReadError(sim);
        return new RunOutcome { Status = finalError == null ? "ok" : "error", Error = finalError, OrderTicks = OrderTicks(screen) };
    }

    private List<int> OrderTicks(object screen) => new((IEnumerable<int>)_orderTicks.GetValue(screen));

    private string ReadError(object sim)
    {
        object error = _readError(_error.GetValue(sim));
        return error == null ? null : (string)_errorMessage.GetValue(error) ?? "unknown simulation error";
    }

    private static IEnumerable<MethodBase> NoArgVoidMethods(Type type) =>
        type.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 0);

    private static IEnumerable<(CodeInstruction, CodeInstruction)> Pairs(List<CodeInstruction> il) => il.Zip(il.Skip(1), (a, b) => (a, b));

    // Some of the simulation's fields are declared on its base class.
    private static bool IsBoolWrite(CodeInstruction i, Type sim) =>
        i.opcode == OpCodes.Stfld && i.operand is FieldInfo f && f.FieldType == typeof(bool) && f.DeclaringType.IsAssignableFrom(sim);

    private static bool IsMaybe(Type type) => type.IsGenericType && type.GetGenericTypeDefinition().Name == "Maybe`1";

    internal static bool IsMaybeOf(Type type, Type value) => IsMaybe(type) && type.GetGenericArguments()[0] == value;

    private static bool HasRange(Type type) =>
        type.GetFields(CodeShape.AllDeclared).Any(f => !f.IsStatic && f.FieldType.Name == "Range2");

    // Maybe<T> has a method that calls an Action<T> if a value is present. Returns null if there's none.
    internal static Func<object, object> MaybeReader(Type maybe, Type value)
    {
        Type callback = typeof(Action<>).MakeGenericType(value);
        MethodInfo ifPresent = CodeShape.Single(
            maybe.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == callback),
            "Maybe callback method");
        return (Func<object, object>)typeof(SimulationRunner).GetMethod(nameof(MakeReader), BindingFlags.NonPublic | BindingFlags.Static)
            .MakeGenericMethod(value).Invoke(null, new object[] { ifPresent });
    }

    private static Func<object, object> MakeReader<T>(MethodInfo ifPresent) => maybe =>
    {
        object result = null;
        Action<T> capture = value => result = value;
        ifPresent.Invoke(maybe, new object[] { capture });
        return result;
    };
}
