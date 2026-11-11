using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.FoodCourt;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.FoodCourtTools;

internal sealed class EditOutcome
{
    public string Status;   // "applied", "waiting" or "rejected"
    public string Message;
    public string File;     // the game's file name for the solution, without .solution
    public bool Open;       // the solution was open in the puzzle screen

    public static EditOutcome Rejected(string message) => new() { Status = "rejected", Message = message };
}

/// <summary>
/// Replaces a solution in the running game with an edited copy. The edit goes into the solution's
/// undo history, so Ctrl+Z in the puzzle screen reverts it. If the puzzle is open, the screen applies
/// it the way it applies an undo, and only while it would accept one: not running, nothing held.
/// </summary>
internal sealed class SolutionEditor
{
    private readonly FieldInfo _gameInstance, _manager, _level, _name, _sim, _history, _modules, _screenState;
    private readonly MethodInfo _list, _create, _fileName, _save, _folder, _snapshot, _push, _pending;
    private readonly MethodInfo _findScreen, _screenSolution, _screenRunning, _screenCommit, _screenApply;
    private readonly MethodInfo _load;
    private readonly ConstructorInfo _newReader;
    private readonly Func<object, object> _readSolution, _readScreen;
    private readonly Type _levelType, _idleState;

    public SolutionEditor(Assembly game)
    {
        Type gameLogic = CodeShape.RequireType(game, "GameLogic");
        Type manager = CodeShape.RequireType(game, "Milwaukee.SolutionManager");
        Type solution = CodeShape.RequireType(game, "Milwaukee.Solution");
        Type screen = CodeShape.RequireType(game, "Milwaukee.PuzzleScreen");
        Type sim = CodeShape.RequireType(game, "Milwaukee.FullSim");
        Type module = CodeShape.RequireType(game, "Milwaukee.Module");
        Type screenStack = CodeShape.RequireType(game, "ScreenStack");

        _gameInstance = CodeShape.SingleField(gameLogic, f => f.IsStatic && f.FieldType == gameLogic, "GameLogic instance");
        _manager = CodeShape.SingleField(gameLogic, f => !f.IsStatic && f.FieldType == manager, "solution manager");

        _level = CodeShape.SingleField(solution, f => !f.IsStatic && f.FieldType.IsEnum, "solution level");
        _levelType = _level.FieldType;
        _name = CodeShape.SingleField(solution, f => !f.IsStatic && f.FieldType == typeof(string), "solution name");
        _sim = CodeShape.SingleField(solution, f => !f.IsStatic && f.FieldType == sim, "solution simulation");
        _modules = CodeShape.SingleField(sim, f => !f.IsStatic && f.FieldType == typeof(List<>).MakeGenericType(module), "module list");

        // The undo history is the solution's one generic field. It holds snapshots of the simulation.
        _history = CodeShape.SingleField(solution, f => !f.IsStatic && f.FieldType.IsGenericType && f.FieldType.GetGenericArguments().Length == 1, "undo history");
        Type history = _history.FieldType;
        Type snapshot = history.GetGenericArguments()[0];
        _snapshot = (MethodInfo)CodeShape.Single(
            solution.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.GetParameters().Length == 0 && m.ReturnType == snapshot), "snapshot method");

        // Push adds to the list without clearing it. The other one-argument method starts a new history.
        _push = (MethodInfo)CodeShape.SingleMethod(
            history.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(void) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == snapshot),
            il => il.Any(i => CallsList(i, "Add")) && !il.Any(i => CallsList(i, "Clear")), "undo history push");
        _pending = (MethodInfo)CodeShape.Single(
            history.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.ReturnType == typeof(bool) && m.GetParameters().Length == 0),
            "undo history pending flag");

        MethodInfo[] managerMethods = manager.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        _list = Single(managerMethods, m => m.ReturnType == solution.MakeArrayType() && OneParameter(m, _levelType), "solution list");
        _create = Single(managerMethods, m => m.ReturnType == solution && OneParameter(m, _levelType), "new solution method");
        _fileName = Single(managerMethods, m => m.ReturnType == typeof(string) && OneParameter(m, solution), "solution file name");
        // Saving and deleting both take a solution. Deleting moves the file away.
        MethodInfo fileMove = typeof(File).GetMethod(nameof(File.Move), new[] { typeof(string), typeof(string) });
        _save = (MethodInfo)CodeShape.SingleMethod(managerMethods.Where(m => m.ReturnType == typeof(void) && OneParameter(m, solution)),
            il => !il.Any(i => i.Calls(fileMove)), "save method");
        _folder = Single(manager.GetMethods(CodeShape.AllDeclared), m => m.IsStatic && m.ReturnType == typeof(string) && m.GetParameters().Length == 0, "solutions folder");

        _load = Single(solution.GetMethods(CodeShape.AllDeclared), m => m.IsStatic && m.GetParameters().Length == 1 && SimulationRunner.IsMaybeOf(m.ReturnType, solution), "solution loader");
        _newReader = _load.GetParameters()[0].ParameterType
            .GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(byte[]) }, null)
            ?? throw new ShapeMismatchException("the solution reader no longer takes a byte array");
        _readSolution = SimulationRunner.MaybeReader(_load.ReturnType, solution);

        // The screen stack can find the topmost screen of a given type.
        MethodInfo find = Single(screenStack.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly),
            m => m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0 && m.ReturnType != typeof(void), "screen finder");
        _findScreen = find.MakeGenericMethod(screen);
        _readScreen = SimulationRunner.MaybeReader(_findScreen.ReturnType, screen);

        MethodInfo[] screenMethods = screen.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && !m.IsSpecialName && m.GetParameters().Length == 0).ToArray();
        _screenSolution = Single(screenMethods, m => m.IsPublic && m.ReturnType == solution, "puzzle screen solution");
        _screenRunning = Single(screenMethods, m => m.IsPublic && m.ReturnType == typeof(bool), "puzzle screen running flag");
        // Commit records edits still pending in the history. It's the screen's only use of push.
        _screenCommit = (MethodInfo)CodeShape.SingleMethod(screenMethods.Where(m => m.ReturnType == typeof(void)),
            il => il.Any(i => i.operand is MethodInfo m && m.MetadataToken == _push.MetadataToken && m.Module == _push.Module), "puzzle screen commit");
        // Undo rebuilds the simulation from the history's current snapshot with the simulation's loader.
        _screenApply = (MethodInfo)CodeShape.SingleMethod(screenMethods.Where(m => m.ReturnType == typeof(void)),
            il => il.Any(i => i.operand is MethodInfo m && m.IsStatic && m.DeclaringType == sim && m.ReturnType == sim), "puzzle screen undo");

        // The screen's state is an abstract type with nested subtypes. It starts in the idle state.
        _screenState = CodeShape.SingleField(screen, f => !f.IsStatic && f.FieldType.IsAbstract && f.FieldType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Any(t => t.IsSubclassOf(f.FieldType)), "puzzle screen state");
        MethodBase constructor = CodeShape.Single(CodeShape.InstanceConstructors(screen), "PuzzleScreen constructor");
        List<CodeInstruction> ctorIl = CodeShape.ReadIL(constructor) ?? throw new ShapeMismatchException("can't read the PuzzleScreen constructor");
        _idleState = CodeShape.Single(
            ctorIl.Zip(ctorIl.Skip(1), (a, b) => (a, b))
                .Where(p => p.a.opcode == OpCodes.Newobj && p.b.opcode == OpCodes.Stfld && Equals(p.b.operand, _screenState))
                .Select(p => ((ConstructorInfo)p.a.operand).DeclaringType),
            "puzzle screen idle state");
    }

    /// <summary>The folder the game keeps solutions in.</summary>
    public string SolutionsFolder() => (string)_folder.Invoke(null, null);

    /// <summary>
    /// Applies an edited solution to the game's solution with the given file name, or to a new
    /// solution if there's none. "waiting" means try again later.
    /// </summary>
    public EditOutcome Apply(string target, byte[] bytes)
    {
        SolutionFile parsed;
        try
        {
            parsed = SolutionFile.Read(bytes);
        }
        catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is ArgumentException)
        {
            return EditOutcome.Rejected("can't read the solution file: " + e.Message);
        }
        List<string> problems = SolutionValidator.Validate(parsed);
        problems.AddRange(PlacementChecker.Check(parsed));
        if (problems.Count > 0)
            return EditOutcome.Rejected(string.Join("; ", problems));

        object loaded = _readSolution(_load.Invoke(null, new[] { _newReader.Invoke(new object[] { bytes }) }));
        if (loaded == null)
            return EditOutcome.Rejected("the game didn't accept this solution's format version");
        // The game's loader drops blocks the level bans.
        int kept = ((IList)_modules.GetValue(_sim.GetValue(loaded))).Count;
        if (kept != parsed.Modules.Count)
            return EditOutcome.Rejected($"{parsed.Modules.Count - kept} block(s) aren't allowed on this level");

        object manager = _manager.GetValue(_gameInstance.GetValue(null));
        object level = _level.GetValue(loaded);
        object existing = Find(manager, target, out object existingLevel);
        if (existing != null && !Equals(existingLevel, level))
            return EditOutcome.Rejected($"{target} is a {existingLevel} solution, but the edit is for {level}");

        object screen = existing == null ? null : _readScreen(_findScreen.Invoke(null, null));
        bool open = screen != null && ReferenceEquals(_screenSolution.Invoke(screen, null), existing);
        if (open)
        {
            if ((bool)_screenRunning.Invoke(screen, null))
                return new EditOutcome { Status = "waiting", Message = "the solution is running in the game", Open = true };
            if (_screenState.GetValue(screen)?.GetType() != _idleState)
                return new EditOutcome { Status = "waiting", Message = "the puzzle screen is busy", Open = true };

            object history = _history.GetValue(existing);
            _screenCommit.Invoke(screen, null);
            _push.Invoke(history, new[] { _snapshot.Invoke(loaded, null) });
            _screenApply.Invoke(screen, null);
        }
        else
        {
            existing ??= _create.Invoke(manager, new[] { level });
            object history = _history.GetValue(existing);
            // Keep unrecorded changes undoable.
            if ((bool)_pending.Invoke(history, null))
                _push.Invoke(history, new[] { _snapshot.Invoke(existing, null) });
            _sim.SetValue(existing, _sim.GetValue(loaded));
            _push.Invoke(history, new[] { _snapshot.Invoke(existing, null) });
        }
        _name.SetValue(existing, _name.GetValue(loaded));
        _save.Invoke(manager, new[] { existing });
        return new EditOutcome { Status = "applied", File = (string)_fileName.Invoke(manager, new[] { existing }), Open = open };
    }

    private object Find(object manager, string fileName, out object level)
    {
        foreach (object candidate in Enum.GetValues(_levelType))
        {
            foreach (object solution in (Array)_list.Invoke(manager, new[] { candidate }))
            {
                if (string.Equals((string)_fileName.Invoke(manager, new[] { solution }), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    level = candidate;
                    return solution;
                }
            }
        }
        level = null;
        return null;
    }

    private static MethodInfo Single(IEnumerable<MethodInfo> methods, Func<MethodInfo, bool> condition, string what) =>
        CodeShape.Single(methods.Where(condition), what);

    private static bool OneParameter(MethodInfo m, Type type) => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == type;

    private static bool CallsList(CodeInstruction i, string name) =>
        (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo m &&
        m.Name == name && m.DeclaringType.IsGenericType && m.DeclaringType.GetGenericTypeDefinition() == typeof(List<>);
}
