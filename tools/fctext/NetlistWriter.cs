using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LastCallBBS.FoodCourt;

/// <summary>
/// Writes a solution as a netlist, one statement per module in module order, so that compiling it
/// with --strict gives back the same modules and wires.
/// </summary>
public static class NetlistWriter
{
    public static string Format(SolutionFile s)
    {
        string[] names = Names(s);

        // Each wire as output (module, socket) to input (module, socket).
        var drivers = new Dictionary<(int, int), (int, int)>();
        foreach (SolutionWire w in s.Wires)
        {
            bool aOut = IsOutput(s, w.ModuleA, w.SocketA), bOut = IsOutput(s, w.ModuleB, w.SocketB);
            if (aOut == bOut)
                throw new InvalidDataException($"wire m{w.ModuleA}.#{w.SocketA} - m{w.ModuleB}.#{w.SocketB} doesn't join an output to an input");
            var (from, to) = aOut ? ((w.ModuleA, w.SocketA), (w.ModuleB, w.SocketB)) : ((w.ModuleB, w.SocketB), (w.ModuleA, w.SocketA));
            drivers[to] = from;
        }

        Dictionary<int, List<Idiom>> idioms = FindIdioms(s, names, drivers);
        var renamed = new Dictionary<(int, int), string>();
        foreach (Idiom idiom in idioms.Values.SelectMany(x => x))
            renamed[idiom.Output] = idiom.Name;

        string Source(int module, int socket)
        {
            if (renamed.TryGetValue((module, socket), out string net))
                return net;
            ModuleInfo info = Info(s, module);
            return IsMixer(info) ? names[module] : $"{names[module]}.{NetlistCompiler.ShortName(info.Sockets[socket].Name, output: true)}";
        }

        var lines = new List<string>
        {
            $"// {s.Name}" + (s.Solved ? $": solved, {s.Ticks} ticks, ${s.Cost}k" : ": unsolved"),
            $"level {ModuleCatalog.LevelName(s.Level)};",
            $"name \"{s.Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\";",
            "",
        };
        for (int i = 0; i < s.Modules.Count; i++)
        {
            SolutionModule m = s.Modules[i];
            ModuleInfo info = ModuleCatalog.ById[m.Type];
            if (idioms.TryGetValue(i, out List<Idiom> forms))
            {
                foreach (Idiom idiom in forms)
                    lines.Add($"assign {idiom.Name} = {idiom.Render(Source)};");
                continue;
            }
            var inputs = Enumerable.Range(0, info.Sockets.Count)
                .Where(k => !info.Sockets[k].IsOutput && drivers.ContainsKey((i, k)))
                .Select(k => (Socket: k, Value: Source(drivers[(i, k)].Item1, drivers[(i, k)].Item2)))
                .ToList();

            string assign = IsMixer(info) && m.Removable ? MixerAssign(info, inputs) : null;
            if (assign != null)
            {
                lines.Add($"assign {names[i]} = {assign};");
                continue;
            }

            var parts = new List<string>();
            if (!m.Removable)
                parts.Add("fixed");
            parts.Add(info.Name);
            bool sequencer = info.Id == ModuleCatalog.SequencerId;
            int[] values = m.Ints.Concat(sequencer ? new int[0] : m.Bools.Select(b => b ? 1 : 0)).ToArray();
            if (values.Length > 0)
                parts.Add($"#({string.Join(", ", values)})");
            if (names[i] != null)
                parts.Add(names[i]);
            if (!PlacementChecker.RackOnly.Contains(info.Name))
                parts.Add($"@({m.FloorX},{m.FloorY}) {NetlistParser.Facings[m.Rotation & 3]}");
            if (inputs.Count > 0)
                parts.Add("(" + string.Join(", ", inputs.Select(p => $".{NetlistCompiler.ShortName(info.Sockets[p.Socket].Name, output: false)}({p.Value})")) + ")");

            string line = string.Join(" ", parts);
            if (sequencer)
                lines.AddRange(Steps(line, m.Bools));
            else
                lines.Add(line + ";");
        }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>A counter output written as a function or count(), assigned to a name.</summary>
    private sealed class Idiom
    {
        public string Name;
        public (int Module, int Socket) Output;
        public string Function;                       // never, balanced, ... or null for count()
        public List<(int Weight, (int Module, int Socket) From)> Terms;
        public int Number;                             // exactly()'s n
        public string Op;                              // count(): "==" or ">"

        public string Render(System.Func<int, int, string> source)
        {
            string Src((int Module, int Socket) f) => source(f.Module, f.Socket);
            switch (Function)
            {
                case "from_tick1": return "from_tick1()";
                case "exactly": return $"exactly({Src(Terms[0].From)}, {Number})";
                case null: break;
                default: return $"{Function}({string.Join(", ", Terms.Select(t => Src(t.From)))})";
            }
            var text = new StringBuilder("count(");
            for (int i = 0; i < Terms.Count; i++)
            {
                int w = Terms[i].Weight;
                string magnitude = System.Math.Abs(w) == 1 ? "" : System.Math.Abs(w) + "*";
                text.Append(i == 0 ? (w < 0 ? "-" : "") : (w < 0 ? " - " : " + ")).Append(magnitude).Append(Src(Terms[i].From));
            }
            return text.Append($") {Op} 0").ToString();
        }
    }

    /// <summary>
    /// Counters that can be written as a function or count() and compile back to the same block:
    /// same type, deltas and trigger sockets. Anything else stays an explicit block.
    /// </summary>
    private static Dictionary<int, List<Idiom>> FindIdioms(SolutionFile s, string[] names, Dictionary<(int, int), (int, int)> drivers)
    {
        var result = new Dictionary<int, List<Idiom>>();
        var used = new HashSet<(int, int)>(drivers.Values);
        var callKeys = new HashSet<string>();
        var countKeys = new HashSet<string>();
        int scanner = s.Modules.FindIndex(m => ModuleCatalog.ById[m.Type].Name.StartsWith("PrimaryInputFor"));
        (int, int) start = scanner < 0 ? (-1, -1) : (scanner, NetlistCompiler.FindSocket(ModuleCatalog.ById[s.Modules[scanner].Type], "START", output: true));

        for (int i = 0; i < s.Modules.Count; i++)
        {
            SolutionModule m = s.Modules[i];
            ModuleInfo info = ModuleCatalog.ById[m.Type];
            bool large = info.Name == "CounterLarge";
            if (!m.Removable || (!large && info.Name != "CounterSmall"))
                continue;

            // A counter adds the same whichever trigger a signal is on, so only the wired triggers count.
            int[] triggers = Enumerable.Range(0, info.Sockets.Count).Where(k => !info.Sockets[k].IsOutput).ToArray();
            int[] wiredAt = Enumerable.Range(0, triggers.Length).Where(t => drivers.ContainsKey((i, triggers[t]))).ToArray();
            int wired = wiredAt.Length;
            var sources = wiredAt.Select(t => drivers[(i, triggers[t])]).ToList();
            if (sources.Distinct().Count() != sources.Count || sources.Any(f => f.Item1 == i))
                continue;
            int[] w = wiredAt.Select(t => m.Ints[t]).ToArray();
            if (w.Any(v => v == 0))
                continue;

            int zero = NetlistCompiler.FindSocket(info, "ZERO_FLAG", output: true);
            int pos = large ? NetlistCompiler.FindSocket(info, "POSITIVE_FLAG", output: true) : -1;
            bool usedZero = used.Contains((i, zero)), usedPos = pos >= 0 && used.Contains((i, pos));
            if (!usedZero && !usedPos)
                continue;
            List<(int, (int, int))> terms = w.Select((v, k) => (v, sources[k])).ToList();

            // A function, when one output is used and it matches exactly.
            string function = null;
            int number = 0;
            if (!large && usedZero && wired == 0)
                function = "from_tick1";
            else if (!large && usedZero && wired == 1 && w[0] == 1)
                function = "never";
            else if (!large && usedZero && wired == 2 && w[0] == 1 && w[1] < 0 && w[1] >= -9 && sources[1] == start)
            {
                function = "exactly";
                number = -w[1];
            }
            else if (!large && usedZero && wired == 2 && w[0] == 1 && w[1] == -1)
                function = "balanced";
            else if (large && usedPos && !usedZero && wired == 1 && w[0] == 1)
                function = "since";
            else if (large && usedPos && !usedZero && wired == 2 && w[0] == 1 && w[1] == -1)
                function = "latch";
            if (function != null)
            {
                // Identical calls share one block, so only the first can be written this way.
                string key = function + ":" + string.Join(",", function == "exactly" ? sources.Take(1) : sources) + (function == "exactly" ? ":" + number : "");
                if (callKeys.Add(key))
                {
                    result[i] = new List<Idiom> { new() { Name = names[i], Output = (i, usedZero ? zero : pos), Function = function, Terms = terms, Number = number } };
                    continue;
                }
            }

            // count(): the compiler must pick the same counter, and no earlier count() may share it.
            if (wired == 0)
                continue;
            bool compilerLarge = usedPos || wired > 2 || w.Any(v => System.Math.Abs(v) > 9);
            if (compilerLarge != large)
                continue;
            string sum = string.Join("+", terms.Select(t => $"{t.Item1}*{t.Item2}").OrderBy(x => x, System.StringComparer.Ordinal));
            string negated = string.Join("+", terms.Select(t => $"{-t.Item1}*{t.Item2}").OrderBy(x => x, System.StringComparer.Ordinal));
            if (countKeys.Contains(sum) || (usedZero && countKeys.Contains(negated)))
                continue;
            countKeys.Add(sum);
            var forms = new List<Idiom>();
            if (usedZero)
                forms.Add(new Idiom { Name = names[i], Output = (i, zero), Terms = terms, Op = "==" });
            if (usedPos)
                forms.Add(new Idiom { Name = usedZero ? names[i] + "_pos" : names[i], Output = (i, pos), Terms = terms, Op = ">" });
            result[i] = forms;
        }
        return result;
    }

    // A mixer with at least two inputs (or an enable and one input) reads best as an expression.
    // Smaller ones stay explicit instances, because an assign of a single signal is only an alias.
    private static string MixerAssign(ModuleInfo info, List<(int Socket, string Value)> inputs)
    {
        if (info.Name == "MixerSimple")
            return inputs.Count >= 2 ? string.Join(" | ", inputs.Select(p => p.Value)) : null;
        int enable = NetlistCompiler.FindSocket(info, "ENABLE", output: false);
        string en = inputs.FirstOrDefault(p => p.Socket == enable).Value;
        List<string> rest = inputs.Where(p => p.Socket != enable).Select(p => p.Value).ToList();
        if (en == null || rest.Count == 0)
            return null;
        return rest.Count == 1 ? $"{en} & {rest[0]}" : $"{en} & ({string.Join(" | ", rest)})";
    }

    private static IEnumerable<string> Steps(string head, bool[] cells)
    {
        var channels = new List<string>();
        for (int c = 0; c < ModuleCatalog.SequencerChannels; c++)
        {
            int[] on = Enumerable.Range(0, ModuleCatalog.SequencerRows).Where(r => cells[r * ModuleCatalog.SequencerChannels + c]).ToArray();
            if (on.Length > 0)
                channels.Add($"    {"ABCD"[c]} = [{string.Join(", ", on)}];");
        }
        if (channels.Count == 0)
            return new[] { head + " { }" };
        return new[] { head + " {" }.Concat(channels).Concat(new[] { "}" });
    }

    private static string[] Names(SolutionFile s)
    {
        var names = new string[s.Modules.Count];
        var counts = new Dictionary<string, int>();
        var totals = s.Modules.GroupBy(m => Prefix(ModuleCatalog.ById[m.Type])).ToDictionary(g => g.Key ?? "", g => g.Count());
        for (int i = 0; i < s.Modules.Count; i++)
        {
            ModuleInfo info = ModuleCatalog.ById[s.Modules[i].Type];
            string prefix = Prefix(info);
            if (prefix == null)
                continue; // nothing can refer to it
            counts.TryGetValue(prefix, out int n);
            counts[prefix] = ++n;
            bool single = prefix is "scan" or "out" or "stack" && totals[prefix] == 1;
            names[i] = single ? prefix : prefix + n;
        }
        return names;
    }

    private static string Prefix(ModuleInfo info)
    {
        string t = info.Name;
        if (info.Sockets.Count == 0 && t != "Output")
            return null;
        if (t.StartsWith("PrimaryInputFor")) return "scan";
        if (t.StartsWith("ScannerFor")) return "scanner";
        if (t == "Output") return "out";
        if (t is "MixerSimple" or "MixerComplex") return "mix";
        if (t == "Sequencer") return "seq";
        if (t == "CounterSmall") return "ctr";
        if (t == "CounterLarge") return "lctr";
        if (t == "Stacker") return "stack";
        if (t.StartsWith("LiquidInput")) return "pump";
        if (t.StartsWith("SolidInput")) return "solid";
        if (t.StartsWith("FreezerInput")) return "freezer";
        if (t.StartsWith("ToppingInput")) return "topping";
        return char.ToLowerInvariant(t[0]) + t.Substring(1);
    }

    private static ModuleInfo Info(SolutionFile s, int module) => ModuleCatalog.ById[s.Modules[module].Type];

    private static bool IsMixer(ModuleInfo info) => info.Name is "MixerSimple" or "MixerComplex";

    private static bool IsOutput(SolutionFile s, int module, int socket)
    {
        if (module < 0 || module >= s.Modules.Count)
            throw new InvalidDataException($"a wire refers to module m{module}, which doesn't exist");
        IReadOnlyList<SocketInfo> sockets = Info(s, module).Sockets;
        if (socket < 0 || socket >= sockets.Count)
            throw new InvalidDataException($"a wire uses socket #{socket} of m{module}, which isn't in the catalogue");
        return sockets[socket].IsOutput;
    }

    /// <summary>
    /// Compiles the netlist form with --strict and compares it with the original. Rack positions,
    /// and which output or input of a mixer each wire uses, aren't compared.
    /// </summary>
    public static List<string> CheckRoundTrip(SolutionFile original)
    {
        var problems = new List<string>();
        SolutionFile back;
        try
        {
            back = NetlistCompiler.Compile(NetlistParser.Parse(Format(original)), new NetlistOptions { Strict = true }).Solution;
        }
        catch (System.Exception e) when (e is NetlistException || e is InvalidDataException)
        {
            problems.Add("the netlist form doesn't compile: " + e.Message);
            return problems;
        }

        if (back.Modules.Count != original.Modules.Count)
        {
            problems.Add($"the netlist form has {back.Modules.Count} modules, not {original.Modules.Count}");
            return problems;
        }
        for (int i = 0; i < original.Modules.Count; i++)
        {
            SolutionModule a = original.Modules[i], b = back.Modules[i];
            bool floor = !PlacementChecker.RackOnly.Contains(ModuleCatalog.ById[a.Type].Name);
            bool counter = ModuleCatalog.ById[a.Type].Name is "CounterSmall" or "CounterLarge";
            if (a.Type != b.Type || a.Removable != b.Removable || (!counter && !a.Ints.SequenceEqual(b.Ints)) || !a.Bools.SequenceEqual(b.Bools) ||
                floor && (a.FloorX != b.FloorX || a.FloorY != b.FloorY || a.Rotation != b.Rotation))
                problems.Add($"m{i} differs in the netlist form");
        }

        string[] wa = WireKeys(original), wb = WireKeys(back);
        if (!wa.SequenceEqual(wb))
            problems.Add($"the netlist form's wires differ ({wa.Except(wb).Count()} missing, {wb.Except(wa).Count()} extra)");
        return problems;
    }

    private static string[] WireKeys(SolutionFile s) => s.Wires.Select(w =>
    {
        string a = End(s, w.ModuleA, w.SocketA), b = End(s, w.ModuleB, w.SocketB);
        return IsOutput(s, w.ModuleA, w.SocketA) ? $"{a}>{b}" : $"{b}>{a}";
    }).OrderBy(k => k, System.StringComparer.Ordinal).ToArray();

    // Mixer outputs are interchangeable, and so are a mixer's OR inputs. A counter's triggers are
    // compared by delta, because a counter adds the same whichever trigger a signal is on.
    private static string End(SolutionFile s, int module, int socket)
    {
        ModuleInfo info = Info(s, module);
        if (info.Name is "CounterSmall" or "CounterLarge" && !info.Sockets[socket].IsOutput)
        {
            int trigger = Enumerable.Range(0, socket).Count(k => !info.Sockets[k].IsOutput);
            return $"m{module}.delta{s.Modules[module].Ints[trigger]}";
        }
        if (!IsMixer(info))
            return $"m{module}.{socket}";
        string name = info.Sockets[socket].Name;
        return $"m{module}.{(info.Sockets[socket].IsOutput ? "out" : name == "ENABLE" ? "en" : "in")}";
    }
}
