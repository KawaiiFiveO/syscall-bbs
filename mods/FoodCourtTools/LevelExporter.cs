using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using LastCallBBS.FoodCourt;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.FoodCourtTools;

/// <summary>Writes one Markdown file per level from the game's own level data.</summary>
internal sealed class LevelExporter
{
    private readonly FieldInfo _table, _level, _title, _description, _banned, _specials, _trashLimit, _orders, _steps, _solids, _others, _modules;
    private readonly FieldInfo _stepLabel, _stepModule, _stepFrom, _stepTo, _solidItems, _solidName, _solidProduct, _otherItems, _otherName, _otherKind;
    private readonly FieldInfo _scannerOutputs, _outputPredicate;
    private readonly ConstructorInfo _newSim;
    private readonly MethodInfo _save, _serialize, _getModuleType;
    private readonly Func<object, object> _readTrashLimit;
    private readonly FieldInfo[] _levelBlocks;

    // Offered on every level unless banned. From the game's block screen.
    private static readonly string[] BasePalette =
        { "Conveyor", "Sensor", "Router", "Sorter", "Stacker", "MixerSimple", "MixerComplex", "CounterSmall", "CounterLarge", "Sequencer" };
    private const int MaxSpecialsWithTrash = 2;

    public LevelExporter(Assembly game)
    {
        Type puzzles = CodeShape.RequireType(game, "Milwaukee.Puzzles");
        Type puzzle = CodeShape.RequireType(game, "Milwaukee.Puzzle");
        Type sim = CodeShape.RequireType(game, "Milwaukee.FullSim");
        Type solution = CodeShape.RequireType(game, "Milwaukee.Solution");
        Type module = CodeShape.RequireType(game, "Milwaukee.Module");
        Type moduleType = CodeShape.RequireType(game, "Milwaukee.ModuleType");
        Type product = CodeShape.RequireType(game, "Milwaukee.Product");

        _table = CodeShape.SingleField(puzzles, f => f.IsStatic && f.FieldType == puzzle.MakeArrayType(), "level table");
        FieldInfo[] fields = puzzle.GetFields(CodeShape.AllDeclared).Where(f => !f.IsStatic).OrderBy(f => f.MetadataToken).ToArray();

        _level = CodeShape.Single(fields.Where(f => IsEnumWith(f.FieldType, "WineBar")), "level id field");
        FieldInfo[] strings = fields.Where(f => f.FieldType == typeof(string)).ToArray();
        if (strings.Length < 2)
            throw new ShapeMismatchException("level title and description not found");
        (_title, _description) = (strings[0], strings[1]);

        _levelBlocks = fields.Where(f => IsEnumWith(f.FieldType, "Sequencer")).ToArray();

        // Banned blocks are the array the solution loader checks. The other array adds level-specific blocks.
        FieldInfo[] blockArrays = fields.Where(f => f.FieldType.IsArray && IsEnumWith(f.FieldType.GetElementType(), "Sequencer")).ToArray();
        MethodBase loader = CodeShape.Single(
            solution.GetMethods(CodeShape.AllDeclared).Where(m => m.IsStatic && m.GetParameters().Length == 1 && m.ReturnType.IsGenericType && m.ReturnType.GetGenericArguments()[0] == solution),
            "solution loader");
        _banned = (FieldInfo)CodeShape.Single(CodeShape.ReadIL(loader)
            .Where(i => i.opcode == OpCodes.Ldfld && blockArrays.Contains(i.operand)).Select(i => i.operand), "banned block list");
        _specials = CodeShape.Single(blockArrays.Where(f => f != _banned), "level block list");

        MethodInfo orderGetter = (MethodInfo)CodeShape.Single(
            sim.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.GetParameters().Length == 0 && m.ReturnType == product.MakeArrayType()),
            "order list getter");
        // The sample list is loaded twice by the getter, and the full list once.
        _orders = CodeShape.Single(CodeShape.ReadIL(orderGetter)
            .Where(i => i.opcode == OpCodes.Ldfld && i.operand is FieldInfo f && f.FieldType == orderGetter.ReturnType)
            .GroupBy(i => (FieldInfo)i.operand).Where(g => g.Count() == 1).Select(g => g.Key), "full order list");

        // The freezer index is the optional int the simulation's constructor reads. The other is the trash limit.
        _newSim = (ConstructorInfo)CodeShape.Single(
            CodeShape.InstanceConstructors(sim).Where(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == _level.FieldType),
            "simulation constructor");
        FieldInfo[] optionalInts = fields.Where(f => IsMaybe(f.FieldType) && f.FieldType.GetGenericArguments()[0] == typeof(int)).ToArray();
        _trashLimit = null;
        _readTrashLimit = null;
        if (optionalInts.Length == 2)
        {
            FieldInfo freezer = FreezerField(optionalInts, _newSim);
            _trashLimit = optionalInts.FirstOrDefault(f => f != freezer);
            if (_trashLimit != null)
                _readTrashLimit = MaybeReader(_trashLimit.FieldType, typeof(int));
        }

        // Recipe steps hold two product arrays.
        _steps = CodeShape.Single(fields.Where(f => f.FieldType.IsArray && Fields(f.FieldType.GetElementType()).Count(x => x.FieldType == product.MakeArrayType()) == 2), "recipe steps");
        Type step = _steps.FieldType.GetElementType();
        _stepLabel = CodeShape.SingleField(step, f => !f.IsStatic && f.FieldType == typeof(string), "step label");
        _stepModule = CodeShape.SingleField(step, f => !f.IsStatic && IsEnumWith(f.FieldType, "Sequencer"), "step module");
        FieldInfo[] stepProducts = Fields(step).Where(f => f.FieldType == product.MakeArrayType()).OrderBy(f => f.MetadataToken).ToArray();
        (_stepFrom, _stepTo) = (stepProducts[0], stepProducts[1]);

        // Solid inputs list (name, product). Other inputs list (name, kind, ...).
        _solids = CodeShape.Single(fields.Where(f => f.FieldType.IsArray && ItemArray(f.FieldType.GetElementType(), x => x.FieldType == product) != null), "solid inputs");
        _solidItems = ItemArray(_solids.FieldType.GetElementType(), x => x.FieldType == product);
        Type solidItem = _solidItems.FieldType.GetElementType();
        _solidName = CodeShape.SingleField(solidItem, f => !f.IsStatic && f.FieldType == typeof(string), "solid input name");
        _solidProduct = CodeShape.SingleField(solidItem, f => !f.IsStatic && f.FieldType == product, "solid input product");

        _others = CodeShape.Single(fields.Where(f => f != _solids && f.FieldType.IsArray && ItemArray(f.FieldType.GetElementType(), x => x.FieldType.IsEnum) != null), "other inputs");
        _otherItems = ItemArray(_others.FieldType.GetElementType(), x => x.FieldType.IsEnum);
        Type otherItem = _otherItems.FieldType.GetElementType();
        _otherName = CodeShape.SingleField(otherItem, f => !f.IsStatic && f.FieldType == typeof(string), "input name");
        _otherKind = CodeShape.SingleField(_others.FieldType.GetElementType(), f => !f.IsStatic && f.FieldType.IsEnum, "input kind");

        // Scanner outputs hold a predicate over the order.
        _scannerOutputs = CodeShape.SingleField(moduleType, f => !f.IsStatic && f.FieldType.IsArray && Fields(f.FieldType.GetElementType()).Any(x => x.FieldType == typeof(Func<,>).MakeGenericType(product, typeof(bool))), "scanner outputs");
        _outputPredicate = CodeShape.SingleField(_scannerOutputs.FieldType.GetElementType(), f => !f.IsStatic && f.FieldType == typeof(Func<,>).MakeGenericType(product, typeof(bool)), "scanner predicate");
        _getModuleType = (MethodInfo)CodeShape.Single(
            module.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == moduleType && m.GetParameters().Length == 0), "module type getter");
        _modules = CodeShape.SingleField(sim, f => !f.IsStatic && f.FieldType == typeof(List<>).MakeGenericType(module), "module list");

        // Cloning a simulation serialises it, which gives both the save method and the serialiser.
        MethodBase clone = CodeShape.Single(sim.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == sim && m.GetParameters().Length == 0), "simulation clone");
        List<CodeInstruction> cloneIl = CodeShape.ReadIL(clone);
        _save = (MethodInfo)CodeShape.Single(cloneIl.Where(i => i.opcode == OpCodes.Ldftn).Select(i => i.operand), "save method");
        _serialize = CodeShape.Single(cloneIl.Where(i => i.operand is MethodInfo m && m.IsStatic && m.ReturnType == typeof(byte[])).Select(i => (MethodInfo)i.operand), "serialiser");
    }

    /// <summary>Writes every level and returns how many succeeded. A level that fails is logged and skipped.</summary>
    public int ExportAll(string folder, IModLog log)
    {
        Array table = (Array)_table.GetValue(null);
        if (table == null)
            return 0;
        Directory.CreateDirectory(folder);
        int written = 0;
        foreach (object puzzle in table)
        {
            if (puzzle == null)
                continue;
            string name = _level.GetValue(puzzle).ToString();
            try
            {
                File.WriteAllText(Path.Combine(folder, name + ".md"), Document(puzzle, name), new UTF8Encoding(false));
                written++;
            }
            catch (Exception e)
            {
                log.Error($"Could not export level {name}: {(e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e)}");
            }
        }
        return written;
    }

    private string Document(object puzzle, string name)
    {
        object sim = _newSim.Invoke(new[] { _level.GetValue(puzzle) });
        var lines = new List<string>
        {
            "# " + _title.GetValue(puzzle),
            "",
            $"Internal name `{name}`, level {Convert.ToInt32(_level.GetValue(puzzle))}.",
            "",
            OneLine((string)_description.GetValue(puzzle)),
            "",
        };
        lines.AddRange(OrdersSection(puzzle, sim));
        lines.AddRange(BlocksSection(puzzle));
        lines.AddRange(InputsSection(puzzle));
        lines.AddRange(StepsSection(puzzle));
        lines.Add("## Starting layout");
        lines.Add("");
        lines.Add("Fixed parts can be moved but not removed. This imports with `fctext --import`.");
        lines.Add("");
        lines.Add("```");
        lines.Add(SolutionText.Format(StartingLayout(sim, Convert.ToInt32(_level.GetValue(puzzle)))).TrimEnd('\n'));
        lines.Add("```");
        return string.Join("\n", lines) + "\n";
    }

    private IEnumerable<string> OrdersSection(object puzzle, object sim)
    {
        object[] outputs = ScannerOutputs(sim);
        IReadOnlyList<SocketInfo> sockets = FirstModuleInfo(sim).Sockets;
        var lines = new List<string> { "## Orders", "", "| # | Order | Scanner outputs |", "|---|---|---|" };
        int number = 0;
        foreach (object order in (Array)_orders.GetValue(puzzle))
        {
            number++;
            var on = new List<string>();
            for (int i = 0; i < outputs.Length; i++)
            {
                var predicate = (Delegate)_outputPredicate.GetValue(outputs[i]);
                if ((bool)predicate.DynamicInvoke(order))
                    on.Add(i < sockets.Count ? sockets[i].Name : "#" + i);
            }
            lines.Add($"| {number} | {Describe(order)} | {string.Join(" ", on)} |");
        }
        lines.Add("");
        return lines;
    }

    private IEnumerable<string> BlocksSection(object puzzle)
    {
        var lines = new List<string> { "## Blocks", "" };
        List<string> specials = ((IEnumerable)_specials.GetValue(puzzle)).Cast<object>().Select(b => b.ToString()).ToList();
        List<string> banned = ((IEnumerable)_banned.GetValue(puzzle)).Cast<object>().Select(b => b.ToString()).ToList();

        var offered = new List<string>(BasePalette);
        offered.AddRange(_levelBlocks.Select(f => f.GetValue(puzzle).ToString()).Where(n => n.StartsWith("ScannerFor")));
        offered.AddRange(specials);
        if (specials.Count <= MaxSpecialsWithTrash)
            offered.Add("Trash");
        lines.Add("Available (cost in $k): " + string.Join(", ", offered.Except(banned).Select(n => $"{n} {ModuleCatalog.ByName(n)?.Cost}")));
        lines.Add("");
        lines.Add("Banned: " + (banned.Count == 0 ? "none" : string.Join(", ", banned)));
        lines.Add("");
        if (_trashLimit != null && _readTrashLimit(_trashLimit.GetValue(puzzle)) is int limit)
        {
            lines.Add($"Trash limit: {limit}");
            lines.Add("");
        }
        return lines;
    }

    private IEnumerable<string> InputsSection(object puzzle)
    {
        var lines = new List<string> { "## Inputs", "", "`values=` on a dispenser picks one of these. Socket `IN1` selects the first item, and so on.", "", "| Dispenser | values | Socket | Item | Product |", "|---|---|---|---|---|" };
        int index = 0;
        foreach (object solid in (Array)_solids.GetValue(puzzle))
        {
            int socket = 0;
            foreach (object item in (Array)_solidItems.GetValue(solid))
            {
                socket++;
                lines.Add($"| Solid | {index} | IN{socket} | {_solidName.GetValue(item)} | {Describe(_solidProduct.GetValue(item))} |");
            }
            index++;
        }
        index = 0;
        foreach (object other in (Array)_others.GetValue(puzzle))
        {
            int socket = 0;
            foreach (object item in (Array)_otherItems.GetValue(other))
            {
                socket++;
                lines.Add($"| {_otherKind.GetValue(other)} | {index} | IN{socket} | {_otherName.GetValue(item)} | |");
            }
            index++;
        }
        lines.Add("");
        return lines;
    }

    private IEnumerable<string> StepsSection(object puzzle)
    {
        var lines = new List<string> { "## Recipe steps", "", "| Step | Block | From | To |", "|---|---|---|---|" };
        foreach (object step in (Array)_steps.GetValue(puzzle))
            lines.Add($"| {_stepLabel.GetValue(step)} | {_stepModule.GetValue(step)} | {Join((IEnumerable)_stepFrom.GetValue(step))} | {Join((IEnumerable)_stepTo.GetValue(step))} |");
        lines.Add("");
        return lines;
    }

    private SolutionFile StartingLayout(object sim, int level)
    {
        Delegate write = Delegate.CreateDelegate(_serialize.GetParameters()[0].ParameterType, sim, _save);
        var modules = (byte[])_serialize.Invoke(null, new object[] { write });
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        byte[] name = Encoding.UTF8.GetBytes("New Solution");
        w.Write(ModuleCatalog.CurrentVersion);
        w.Write(level);
        w.Write(name.Length);
        w.Write(name);
        w.Write(false);
        w.Write(0);
        w.Write(0);
        w.Write(modules);
        w.Flush();
        return SolutionFile.Read(stream.ToArray());
    }

    private object[] ScannerOutputs(object sim)
    {
        object first = ((IList)_modules.GetValue(sim))[0];
        return ((Array)_scannerOutputs.GetValue(_getModuleType.Invoke(first, null))).Cast<object>().ToArray();
    }

    private ModuleInfo FirstModuleInfo(object sim)
    {
        object first = ((IList)_modules.GetValue(sim))[0];
        FieldInfo kind = _getModuleType.ReturnType.GetFields(CodeShape.AllDeclared).Single(f => !f.IsStatic && IsEnumWith(f.FieldType, "Sequencer"));
        return ModuleCatalog.ByName(kind.GetValue(_getModuleType.Invoke(first, null)).ToString());
    }

    private static FieldInfo FreezerField(FieldInfo[] candidates, ConstructorInfo ctor)
    {
        var read = new HashSet<object>(CodeShape.ReadIL(ctor).Where(i => i.opcode == OpCodes.Ldfld || i.opcode == OpCodes.Ldflda).Select(i => i.operand));
        return candidates.FirstOrDefault(read.Contains);
    }

    private static string Join(IEnumerable items)
    {
        string text = string.Join(" + ", items.Cast<object>().Select(Describe));
        return text.Length == 0 ? "none" : text;
    }

    // The game's descriptions print a collection as its type name. Replace those with the contents.
    private static string Describe(object value)
    {
        if (value == null)
            return "none";
        string text = value.ToString();
        if (!NeedsExpanding(text))
            return text;

        for (Type type = value.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (FieldInfo field in Fields(type).Where(f => f.DeclaringType == type).OrderBy(f => f.MetadataToken))
            {
                object member = field.GetValue(value);
                string raw = member?.ToString();
                if (raw == null || !NeedsExpanding(raw))
                    continue;
                int at = text.IndexOf(raw, StringComparison.Ordinal);
                if (at < 0)
                    continue;
                string contents = member is IEnumerable list && !(member is string)
                    ? "[" + string.Join(", ", list.Cast<object>().Select(Describe)) + "]"
                    : Describe(member);
                text = text.Remove(at, raw.Length).Insert(at, contents);
            }
        }

        // A product type with no description of its own, such as food that's mid-cook.
        return text.StartsWith(ScrambledNamePrefix) ? "(unnamed product)" : text;
    }

    private static bool NeedsExpanding(string text) =>
        text.Contains(ScrambledNamePrefix) || text.Contains("System.Collections");

    private const string ScrambledNamePrefix = "#=q";

    private static string OneLine(string text) => (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    private static IEnumerable<FieldInfo> Fields(Type type) => type.GetFields(CodeShape.AllDeclared).Where(f => !f.IsStatic);

    private static FieldInfo ItemArray(Type holder, Func<FieldInfo, bool> itemHas) =>
        Fields(holder).FirstOrDefault(f => f.FieldType.IsArray && f.FieldType.GetElementType().IsClass &&
            Fields(f.FieldType.GetElementType()).Any(x => x.FieldType == typeof(string)) &&
            Fields(f.FieldType.GetElementType()).Any(itemHas));

    private static bool IsEnumWith(Type type, string member) => type.IsEnum && Enum.GetNames(type).Contains(member);

    private static bool IsMaybe(Type type) => type.IsGenericType && type.GetGenericTypeDefinition().Name == "Maybe`1";

    private static Func<object, object> MaybeReader(Type maybe, Type value)
    {
        Type callback = typeof(Action<>).MakeGenericType(value);
        MethodInfo ifPresent = CodeShape.Single(
            maybe.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == callback),
            "Maybe callback method");
        return (Func<object, object>)typeof(LevelExporter).GetMethod(nameof(MakeReader), BindingFlags.NonPublic | BindingFlags.Static)
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
