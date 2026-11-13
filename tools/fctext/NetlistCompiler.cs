using System;
using System.Collections.Generic;
using System.Linq;

namespace LastCallBBS.FoodCourt;

public sealed class NetlistOptions
{
    /// <summary>Every automatic change is an error, so the output has exactly the blocks in the source.</summary>
    public bool Strict;

    /// <summary>Optimise the mixer network and choose how to build at_tick(). Off with -O0 and with Strict.</summary>
    public bool Optimise = true;
}

public sealed class NetlistResult
{
    public SolutionFile Solution;
    public List<string> Report = new();
    public List<string> Warnings = new();
}

/// <summary>Compiles a netlist to a solution. See specs/food-court-netlist.md.</summary>
public static partial class NetlistCompiler
{
    private const int MaxOrTerms = 4, MaxComplexTerms = 3;

    private sealed class Node
    {
        public ModuleInfo Info;
        public SolutionModule Module = new();
        public string Label;            // for messages
        public bool IsMixer => Info.Name is "MixerSimple" or "MixerComplex";
        public bool IsComplex => Info.Name == "MixerComplex";
        public List<Node> Extra = new(); // mixers made for this statement, placed after it
        public bool Removed;             // dropped by the optimiser
        public int Line;                 // source line, 0 if the compiler made it
    }

    /// <summary>A signal: one output socket of a block, or a mixer's net (any of its outputs).</summary>
    private sealed class Src
    {
        public Node Node;
        public int Socket = -1;         // -1 for a mixer's net
        public string Label;
        public SourceSpot At;
        public object Key => Socket < 0 ? (object)Node : (Node, Socket);
    }

    private sealed class Use
    {
        public Src From;
        public Node To;
        public int Socket;
    }

    private sealed class State
    {
        public NetlistOptions Options;
        public Dictionary<string, (NetBlock Decl, Node Node)> Blocks = new();
        public Dictionary<string, NetAssign> Assigns = new();
        public Dictionary<NetAssign, Node> AssignNodes = new();
        public Dictionary<NetBlock, Node> BlockNodes = new();
        public Dictionary<NetAssign, Node> CallAnchors = new();
        public Dictionary<NetAssign, Src> CallResults = new();
        public HashSet<NetAssign> Lowering = new();
        public HashSet<string> Wires = new();
        public HashSet<string> Used = new();
        public List<Node> Order = new();
        public List<Node> Buffers = new();
        public List<Use> Uses = new();
        public List<string> Added = new();
        public List<string> Functions = new();
        public List<string> Optimised = new();
        public Dictionary<string, (Src Result, int Line)> Calls = new();
        public List<SharedSequencer> Sequencers = new();
        public List<SharedCounter> Counters = new();
    }

    /// <summary>A counter made by count(), shared by comparisons of the same sum.</summary>
    private sealed class SharedCounter
    {
        public string Key, NegatedKey;
        public Node Node;
        public int Line, ReportIndex;
        public List<Use> Triggers = new();
    }

    /// <summary>A sequencer made by delay() and hold(), shared by calls with the same trigger.</summary>
    private sealed class SharedSequencer
    {
        public object Trigger;
        public Node Node, Anchor;
        public int Line;
        public Use StartUse;
        public Dictionary<string, int> Channels = new();
        public Dictionary<string, string> Texts = new();
        public Dictionary<string, List<int>> ReportLines = new();
    }

    public static NetlistResult Compile(Netlist n, NetlistOptions options)
    {
        var st = new State { Options = options };
        var solution = new SolutionFile { Version = ModuleCatalog.CurrentVersion, Name = n.Name };
        solution.Level = ModuleCatalog.LevelId(n.Level) ?? throw n.LevelAt.Error($"unknown level \"{n.Level}\"");

        // Names first, so that anything can refer to anything.
        foreach (NetStatement s in n.Statements)
        {
            switch (s)
            {
                case NetBlock b:
                    Node node = NewBlock(b);
                    st.BlockNodes[b] = node;
                    if (b.Name != null)
                    {
                        Claim(st, b.Name, b.At);
                        st.Blocks[b.Name] = (b, node);
                    }
                    st.Order.Add(node);
                    break;
                case NetAssign a:
                    Claim(st, a.Name, a.At);
                    st.Assigns[a.Name] = a;
                    // A mixer named by an assign keeps its place in the source order.
                    if (a.Value is NetCall or NetCount)
                    {
                        // Holds the function's block in the source order. Not a module itself.
                        var anchor = new Node { Label = a.Name };
                        st.CallAnchors[a] = anchor;
                        st.Order.Add(anchor);
                    }
                    else if (a.Value is not NetRef)
                    {
                        Node mixer = NewMixer(a.Value is NetAnd, a.Name);
                        mixer.Line = a.At.Line;
                        st.AssignNodes[a] = mixer;
                        st.Order.Add(mixer);
                    }
                    break;
                case NetWireDecl w:
                    foreach (string name in w.Names)
                        st.Wires.Add(name);
                    break;
            }
        }
        foreach (string w in st.Wires.Where(w => !st.Assigns.ContainsKey(w) && !st.Blocks.ContainsKey(w)))
            throw n.Statements.OfType<NetWireDecl>().First(d => d.Names.Contains(w)).At.Error($"wire {w} is declared but never assigned");

        // Then inputs, in source order.
        foreach (NetStatement s in n.Statements)
        {
            if (s is NetBlock b)
                ConnectBlock(st, b, st.BlockNodes[b]);
            else if (s is NetAssign a && st.AssignNodes.TryGetValue(a, out Node mixer))
                FillMixer(st, mixer, a.Value, mixer);
            else if (s is NetAssign c && st.CallAnchors.ContainsKey(c))
                LowerCallAssign(st, c);
        }

        var result = new NetlistResult();
        foreach (string name in st.Assigns.Keys.Where(k => !st.Used.Contains(k)))
            result.Warnings.Add($"line {st.Assigns[name].At.Line}: {name} is never used");

        if (st.Options.Optimise && !st.Options.Strict)
            Optimise(st);
        List<Node> modules = st.Order.SelectMany(x => (x.Info == null ? new Node[0] : new[] { x }).Concat(x.Extra)).Where(x => !x.Removed).ToList();
        result.Warnings.AddRange(DeadCode(st, modules));
        WireUp(st, modules);
        modules.AddRange(st.Buffers);
        PackRack(modules);

        solution.Modules = modules.Select(x => x.Module).ToList();
        var index = modules.Select((x, i) => (x, i)).ToDictionary(p => p.x, p => p.i);
        var nextOutput = new Dictionary<Node, int>();
        foreach (Use u in st.Uses)
        {
            int socket = u.From.Socket;
            if (socket < 0)
            {
                int[] outputs = Outputs(u.From.Node.Info);
                nextOutput.TryGetValue(u.From.Node, out int k);
                socket = outputs[k];
                nextOutput[u.From.Node] = k + 1;
            }
            solution.Wires.Add(new SolutionWire { ModuleA = index[u.From.Node], SocketA = socket, ModuleB = index[u.To], SocketB = u.Socket });
        }

        List<string> problems = SolutionValidator.Validate(solution);
        problems.AddRange(PlacementChecker.Check(solution).Select(p => DescribeModules(p, modules)));
        if (problems.Count > 0)
            throw new NetlistException(1, 1, string.Join("\n  ", new[] { "the result isn't a valid solution:" }.Concat(problems)));

        result.Solution = solution;
        result.Report.AddRange(Report(modules, st));
        return result;
    }

    // --------------------------------------------------------------- declarations

    private static string BlockLabel(NetBlock b) => b.Name ?? $"{b.Type} at line {b.At.Line}";

    private static void Claim(State st, string name, SourceSpot at)
    {
        if (st.Blocks.ContainsKey(name) || st.Assigns.ContainsKey(name))
            throw at.Error($"{name} is already defined");
        if (name is "level" or "name" or "fixed" or "assign" or "wire")
            throw at.Error($"{name} is a keyword");
    }

    private static Node NewBlock(NetBlock b)
    {
        ModuleInfo info = ModuleCatalog.ByName(b.Type) ?? throw b.At.Error($"unknown block type \"{b.Type}\"");
        var node = new Node { Info = info, Label = BlockLabel(b), Line = b.At.Line };
        SolutionModule m = node.Module;
        m.Type = info.Id;
        m.Removable = !b.Fixed;

        bool rackOnly = PlacementChecker.RackOnly.Contains(info.Name);
        if (rackOnly && b.FloorX != null)
            throw b.At.Error($"{info.Name} goes on the rack only, so it takes no @(x,y)");
        if (!rackOnly)
        {
            if (b.FloorX == null)
                throw b.At.Error($"{info.Name} goes on the floor: give its position and facing, like @(1,2) down");
            m.FloorX = b.FloorX.Value;
            m.FloorY = b.FloorY.Value;
            m.Rotation = (byte)b.Facing.Value;
        }

        bool sequencer = info.Id == ModuleCatalog.SequencerId;
        int boolParams = sequencer ? 0 : info.Data.BoolCount;
        int expected = info.Data.IntCount + boolParams;
        int given = b.Params?.Count ?? 0;
        if (given != expected)
            throw b.At.Error(expected == 0 ? $"{info.Name} takes no #(...)" : $"{info.Name} takes {expected} value{(expected == 1 ? "" : "s")} in #(...), not {given}");
        m.Ints = (b.Params ?? new List<int>()).Take(info.Data.IntCount).ToArray();
        m.Bools = new bool[info.Data.BoolCount];
        for (int i = 0; i < boolParams; i++)
        {
            int v = b.Params[info.Data.IntCount + i];
            if (v is not (0 or 1))
                throw b.At.Error($"{info.Name}'s flag must be 0 or 1");
            m.Bools[i] = v == 1;
        }

        if (b.Steps != null && !sequencer)
            throw b.At.Error($"only a Sequencer has steps, not {info.Name}");
        if (sequencer && b.Steps != null)
        {
            foreach (KeyValuePair<int, List<int>> channel in b.Steps)
            {
                foreach (int step in channel.Value)
                    m.Bools[step * ModuleCatalog.SequencerChannels + channel.Key] = true;
            }
        }
        return node;
    }

    private static Node NewMixer(bool complex, string label)
    {
        ModuleInfo info = ModuleCatalog.ByName(complex ? "MixerComplex" : "MixerSimple");
        return new Node { Info = info, Label = label, Module = new SolutionModule { Type = info.Id, Removable = true } };
    }

    private static void ConnectBlock(State st, NetBlock b, Node node)
    {
        var done = new HashSet<int>();
        foreach (NetPort port in b.Ports)
        {
            int socket = FindSocket(node.Info, port.Socket, output: false);
            if (socket < 0)
                throw port.At.Error($"{node.Info.Name} has no input \"{port.Socket}\"; its inputs are {SocketList(node.Info, output: false)}");
            if (!done.Add(socket))
                throw port.At.Error($"input {port.Socket} is connected twice");
            Connect(st, Lower(st, port.Value, node), node, socket);
        }
    }

    // --------------------------------------------------------------- sockets

    private static readonly Dictionary<string, string> ShortOutputs = new()
    {
        ["A"] = "OUTPUT_A", ["B"] = "OUTPUT_B", ["C"] = "OUTPUT_C", ["D"] = "OUTPUT_D", ["ZERO"] = "ZERO_FLAG", ["POS"] = "POSITIVE_FLAG",
    };

    private static readonly Dictionary<string, string> ShortInputs = new()
    {
        ["A"] = "INPUT_A", ["B"] = "INPUT_B", ["C"] = "INPUT_C", ["D"] = "INPUT_D", ["EN"] = "ENABLE",
        ["TA"] = "TRIGGER_A", ["TB"] = "TRIGGER_B", ["TC"] = "TRIGGER_C", ["TD"] = "TRIGGER_D",
    };

    /// <summary>The short form of a socket name, as <c>--to-netlist</c> writes it.</summary>
    public static string ShortName(string socket, bool output) =>
        (output ? ShortOutputs : ShortInputs).FirstOrDefault(p => p.Value == socket).Key ?? socket;

    public static int FindSocket(ModuleInfo info, string name, bool output)
    {
        string full = (output ? ShortOutputs : ShortInputs).TryGetValue(name, out string f) ? f : name;
        for (int i = 0; i < info.Sockets.Count; i++)
        {
            if (info.Sockets[i].IsOutput == output && (info.Sockets[i].Name == name || info.Sockets[i].Name == full))
                return i;
        }
        return -1;
    }

    private static int[] Outputs(ModuleInfo info) => Enumerable.Range(0, info.Sockets.Count).Where(i => info.Sockets[i].IsOutput).ToArray();

    private static int[] Inputs(ModuleInfo info) => Enumerable.Range(0, info.Sockets.Count).Where(i => !info.Sockets[i].IsOutput).ToArray();

    private static string SocketList(ModuleInfo info, bool output)
    {
        string[] names = info.Sockets.Where(s => s.IsOutput == output).Select(s => s.Name).ToArray();
        return names.Length == 0 ? "none" : string.Join(", ", names);
    }

    // --------------------------------------------------------------- expressions

    private static Src Resolve(State st, NetRef r, HashSet<string> seen = null)
    {
        st.Used.Add(r.Name);
        if (st.Blocks.TryGetValue(r.Name, out var block))
        {
            Node node = block.Node;
            if (node.IsMixer)
                return new Src { Node = node, Label = r.Name, At = r.At };
            int[] outputs = Outputs(node.Info);
            if (r.Socket == null)
            {
                if (outputs.Length == 1)
                    return new Src { Node = node, Socket = outputs[0], Label = r.Name, At = r.At };
                throw r.At.Error(outputs.Length == 0
                    ? $"{r.Name} ({node.Info.Name}) has no outputs"
                    : $"{r.Name} has several outputs; name one: {SocketList(node.Info, output: true)}");
            }
            int socket = FindSocket(node.Info, r.Socket, output: true);
            if (socket < 0)
                throw r.At.Error($"{node.Info.Name} has no output \"{r.Socket}\"; its outputs are {SocketList(node.Info, output: true)}");
            return new Src { Node = node, Socket = socket, Label = r.ToString(), At = r.At };
        }
        if (st.Assigns.TryGetValue(r.Name, out NetAssign a))
        {
            if (r.Socket != null)
                throw r.At.Error($"{r.Name} is a net, so it has no sockets");
            if (st.AssignNodes.TryGetValue(a, out Node mixer))
                return new Src { Node = mixer, Label = r.Name, At = r.At };
            if (st.CallAnchors.ContainsKey(a))
            {
                Src made = LowerCallAssign(st, a);
                return new Src { Node = made.Node, Socket = made.Socket, Label = r.Name, At = r.At };
            }
            seen ??= new HashSet<string>();
            if (!seen.Add(r.Name))
                throw a.At.Error($"{r.Name} is an alias of itself");
            Src alias = Resolve(st, (NetRef)a.Value, seen);
            alias.At = r.At;
            return alias;
        }
        if (st.Wires.Contains(r.Name))
            throw r.At.Error($"wire {r.Name} is never assigned");
        throw r.At.Error($"unknown name \"{r.Name}\"");
    }

    /// <summary>Turns an expression into a signal, adding mixers after <paramref name="anchor"/> as needed.</summary>
    private static Src Lower(State st, NetExpr e, Node anchor)
    {
        if (e is NetRef r)
            return Resolve(st, r);
        if (e is NetNumber number)
            throw number.At.Error("a number isn't a signal");
        if (e is NetCall call)
            return Call(st, call, anchor);
        if (e is NetCount count)
            return Count(st, count, anchor);
        Node mixer = NewMixer(e is NetAnd, $"expression at line {e.At.Line}");
        mixer.Line = e.At.Line;
        anchor.Extra.Add(mixer);
        FillMixer(st, mixer, e, anchor);
        return new Src { Node = mixer, Label = mixer.Label, At = e.At };
    }

    private static List<NetExpr> OrTerms(NetExpr e) => e is NetOr or ? or.Terms.SelectMany(OrTerms).ToList() : new List<NetExpr> { e };

    private static void FillMixer(State st, Node mixer, NetExpr e, Node anchor)
    {
        int[] inputs = Inputs(mixer.Info);
        if (e is NetAnd and)
        {
            // MixerComplex is ENABLE & (A | B | C). The enable is the side that isn't an OR.
            NetExpr enable = and.Left, rest = and.Right;
            if (enable is NetOr && rest is not NetOr)
                (enable, rest) = (rest, enable);
            Connect(st, Lower(st, enable, anchor), mixer, inputs[0]);
            FillInputs(st, mixer, inputs.Skip(1).ToArray(), OrTerms(rest), anchor, e);
        }
        else
            FillInputs(st, mixer, inputs, OrTerms(e), anchor, e);
    }

    private static void FillInputs(State st, Node mixer, int[] sockets, List<NetExpr> terms, Node anchor, NetExpr whole)
    {
        if (terms.Count > sockets.Length)
        {
            if (st.Options.Strict)
                throw whole.At.Error($"this OR has {terms.Count} terms but the mixer has {sockets.Length} inputs (--strict doesn't split it)");
            // Keep all but one input for terms and OR the rest in another mixer.
            var rest = new NetOr { At = whole.At, Terms = terms.Skip(sockets.Length - 1).ToList() };
            terms = terms.Take(sockets.Length - 1).Concat(new NetExpr[] { rest }).ToList();
            st.Added.Add($"line {whole.At.Line}: split an OR of {rest.Terms.Count + sockets.Length - 1} terms (+$1k)");
        }
        for (int i = 0; i < terms.Count; i++)
            Connect(st, Lower(st, terms[i], anchor), mixer, sockets[i]);
    }

    // --------------------------------------------------------------- functions

    private static Src LowerCallAssign(State st, NetAssign a)
    {
        if (st.CallResults.TryGetValue(a, out Src done))
            return done;
        if (!st.Lowering.Add(a))
            throw a.At.Error($"{a.Name} feeds back into its own function; write that counter or sequencer as a block instead");
        Src result = a.Value is NetCall call ? Call(st, call, st.CallAnchors[a]) : Count(st, (NetCount)a.Value, st.CallAnchors[a]);
        st.Lowering.Remove(a);
        st.CallResults[a] = result;
        return result;
    }

    private static Src Call(State st, NetCall c, Node anchor)
    {
        (int signals, bool number, int min, int max) = c.Name switch
        {
            "from_tick1" => (0, false, 0, 0),
            "never" or "since" => (1, false, 0, 0),
            "balanced" or "latch" => (2, false, 0, 0),
            "exactly" => (1, true, 1, 9),
            "delay" or "hold" => (1, true, 1, ModuleCatalog.SequencerRows),
            "at_tick" => (0, true, 0, MaxPulseTick),
            _ => throw c.At.Error($"unknown function \"{c.Name}\"; the functions are count, at_tick, from_tick1, never, balanced, since, exactly, latch, delay and hold"),
        };
        int expected = signals + (number ? 1 : 0);
        if (c.Args.Count != expected)
            throw c.At.Error($"{c.Name} takes {expected} argument{(expected == 1 ? "" : "s")}, not {c.Args.Count}");
        var args = new List<Src>();
        for (int i = 0; i < signals; i++)
            args.Add(Lower(st, c.Args[i], anchor));
        int n = 0;
        if (number)
        {
            if (c.Args[signals] is not NetNumber num)
                throw c.Args[signals].At.Error($"{c.Name}'s last argument must be a number");
            n = num.Value;
            if (n < min || n > max)
                throw num.At.Error($"{c.Name} takes a number from {min} to {max}, not {n}");
        }

        // The same call twice is one block.
        string key = c.Name + "(" + string.Join(",", args.Select(a => KeyText(a.Key))) + (number ? "," + n : "") + ")";
        string text = $"{c.Name}({string.Join(", ", args.Select(a => a.Label).Concat(number ? new[] { n.ToString() } : new string[0]))})";
        if (st.Calls.TryGetValue(key, out var earlier))
        {
            st.Functions.Add($"line {c.At.Line}: {text}: same block as line {earlier.Line}");
            return new Src { Node = earlier.Result.Node, Socket = earlier.Result.Socket, Label = text, At = c.At };
        }

        Src result;
        if (c.Name == "at_tick")
        {
            // Tick 0 is the scanner's START itself; later ticks are delay(scan.START, k).
            Src start = ScannerStart(st, c);
            if (n == 0)
                st.Functions.Add($"line {c.At.Line}: {text}: scan.START itself, no block");
            result = n == 0 ? start
                : n > ModuleCatalog.SequencerRows ? PulseCounter(st, c, text, n, anchor)
                : SequencerCall(st, c, "delay", text, start, n, anchor);
        }
        else if (c.Name is "delay" or "hold")
            result = SequencerCall(st, c, c.Name, text, args[0], n, anchor);
        else
            result = CounterCall(st, c, text, args, n, anchor);
        result.At = c.At;
        st.Calls[key] = (result, c.At.Line);
        return result;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Node, object> NodeIds = new();
    private static int _nextId;

    private static string KeyText(object key) => key switch
    {
        Node node => "#" + NodeIds.GetValue(node, _ => ++_nextId),
        ValueTuple<Node, int> t => $"#{NodeIds.GetValue(t.Item1, _ => ++_nextId)}.{t.Item2}",
        _ => key.ToString(),
    };

    private static Src CounterCall(State st, NetCall c, string text, List<Src> args, int n, Node anchor)
    {
        bool large = c.Name is "since" or "latch";
        ModuleInfo info = ModuleCatalog.ByName(large ? "CounterLarge" : "CounterSmall");
        var counter = new Node { Info = info, Label = text, Line = c.At.Line, Module = new SolutionModule { Type = info.Id, Removable = true } };
        // Unwired triggers never fire; they get the game's default delta, 0.
        counter.Module.Ints = c.Name switch
        {
            "from_tick1" => new[] { 0, 0 },
            "never" => new[] { 1, 0 },
            "balanced" => new[] { 1, -1 },
            "exactly" => new[] { 1, -n },
            "since" => new[] { 1, 0, 0, 0 },
            _ => new[] { 1, -1, 0, 0 }, // latch
        };
        anchor.Extra.Add(counter);
        int[] triggers = Inputs(info);
        if (args.Count > 0)
            Connect(st, args[0], counter, triggers[0]);
        if (args.Count > 1)
            Connect(st, args[1], counter, triggers[1]);
        if (c.Name == "exactly")
            Connect(st, ScannerStart(st, c), counter, triggers[1]);
        st.Functions.Add($"line {c.At.Line}: {text}: {info.Name} (+${info.Cost}k)");
        int output = FindSocket(info, large ? "POSITIVE_FLAG" : "ZERO_FLAG", output: true);
        return new Src { Node = counter, Socket = output, Label = text };
    }

    private static Src ScannerStart(State st, NetCall c)
    {
        Node scanner = st.BlockNodes.Values.FirstOrDefault(x => x.Info.Name.StartsWith("PrimaryInputFor"))
            ?? throw c.At.Error("exactly() needs the scanner's START, but there's no PrimaryInputFor block");
        return new Src { Node = scanner, Socket = FindSocket(scanner.Info, "START", output: true), Label = "scan.START", At = c.At };
    }

    private static Src ScannerStart(State st, NetExpr at)
    {
        Node scanner = st.BlockNodes.Values.FirstOrDefault(x => x.Info.Name.StartsWith("PrimaryInputFor"))
            ?? throw at.At.Error("a constant in count() uses the scanner's START, but there's no PrimaryInputFor block");
        return new Src { Node = scanner, Socket = FindSocket(scanner.Info, "START", output: true), Label = "scan.START", At = at.At };
    }

    private static Src SequencerCall(State st, NetCall c, string kind, string text, Src trigger, int n, Node anchor)
    {
        // delay(x, n) is on at step n-1; hold(x, n) at steps 0 to n-1. Calls with the same trigger
        // share a sequencer while it has a free channel.
        string pattern = kind + n;
        SharedSequencer shared = st.Sequencers.FirstOrDefault(q => Equals(q.Trigger, trigger.Key) &&
            (q.Channels.ContainsKey(pattern) || q.Channels.Count < ModuleCatalog.SequencerChannels));
        string note;
        if (shared == null)
        {
            ModuleInfo info = ModuleCatalog.ById[ModuleCatalog.SequencerId];
            var node = new Node { Info = info, Label = text, Line = c.At.Line, Module = new SolutionModule { Type = info.Id, Removable = true, Bools = new bool[info.Data.BoolCount] } };
            anchor.Extra.Add(node);
            var startUse = new Use { From = trigger, To = node, Socket = FindSocket(info, "START", output: false) };
            st.Uses.Add(startUse);
            shared = new SharedSequencer { Trigger = trigger.Key, Node = node, Anchor = anchor, Line = c.At.Line, StartUse = startUse };
            st.Sequencers.Add(shared);
            note = $"Sequencer (+${info.Cost}k)";
        }
        else
            note = $"shares the sequencer from line {shared.Line}";

        if (!shared.Channels.TryGetValue(pattern, out int channel))
        {
            channel = shared.Channels.Count;
            shared.Channels[pattern] = channel;
            shared.Texts[pattern] = $"{text} at line {c.At.Line}";
            int first = kind == "delay" ? n - 1 : 0;
            for (int step = first; step < n; step++)
                shared.Node.Module.Bools[step * ModuleCatalog.SequencerChannels + channel] = true;
        }
        if (!shared.ReportLines.TryGetValue(pattern, out List<int> lines))
            shared.ReportLines[pattern] = lines = new List<int>();
        lines.Add(st.Functions.Count);
        st.Functions.Add($"line {c.At.Line}: {text}: {note}, channel {"ABCD"[channel]}");
        int output = FindSocket(shared.Node.Info, "OUTPUT_" + "ABCD"[channel], output: true);
        return new Src { Node = shared.Node, Socket = output, Label = text };
    }

    // --------------------------------------------------------------- count()

    private const int SmallLimit = 9, LargeLimit = 99;

    private static Src Count(State st, NetCount c, Node anchor)
    {
        if (c.Op == "!=")
            throw c.At.Error("count(...) != needs a NOT; use == on the opposite count, or > and < together");

        // Weighted terms, with the same signal merged and a constant as a weight on START.
        var terms = new List<(Src Src, int Weight)>();
        void Add(Src src, int weight)
        {
            int i = terms.FindIndex(t => Equals(t.Src.Key, src.Key));
            if (i < 0)
                terms.Add((src, weight));
            else
                terms[i] = (terms[i].Src, terms[i].Weight + weight);
        }
        foreach ((int weight, NetExpr signal) in c.Terms)
        {
            if (signal is NetNumber)
                throw signal.At.Error("in count(), multiply a signal by a number, not two numbers");
            Add(Lower(st, signal, anchor), weight);
        }
        if (c.Constant != c.Rhs)
            Add(ScannerStart(st, c), c.Constant - c.Rhs);
        terms.RemoveAll(t => t.Weight == 0);

        bool negate = c.Op is "<" or "<=";
        if (negate)
            terms = terms.Select(t => (t.Src, -t.Weight)).ToList();
        bool needPos = c.Op != "==";
        string key = SumKey(terms, 1), negatedKey = SumKey(terms, -1);

        SharedCounter counter = st.Counters.FirstOrDefault(k => k.Key == key || (!needPos && k.NegatedKey == key));
        if (counter != null)
        {
            if (needPos && counter.Node.Info.Name == "CounterSmall")
                Upgrade(st, counter);
            st.Functions.Add($"line {c.At.Line}: {NetlistParser.Describe(c)}: shares the counter from line {counter.Line}");
        }
        else
        {
            if (terms.Count > 4)
                throw c.At.Error($"count() can use at most 4 different signals, counting a constant as one; this has {terms.Count}");
            int biggest = terms.Count == 0 ? 0 : terms.Max(t => Math.Abs(t.Weight));
            if (biggest > LargeLimit)
                throw c.At.Error($"count() weights can be at most {LargeLimit}");
            bool large = needPos || terms.Count > 2 || biggest > SmallLimit;
            ModuleInfo info = ModuleCatalog.ByName(large ? "CounterLarge" : "CounterSmall");
            var node = new Node { Info = info, Label = NetlistParser.Describe(c), Line = c.At.Line, Module = new SolutionModule { Type = info.Id, Removable = true } };
            int[] triggers = Inputs(info);
            // Unwired triggers never fire; they get the game's default delta, 0.
            node.Module.Ints = Enumerable.Range(0, triggers.Length).Select(i => i < terms.Count ? terms[i].Weight : 0).ToArray();
            anchor.Extra.Add(node);
            counter = new SharedCounter { Key = key, NegatedKey = negatedKey, Node = node, Line = c.At.Line, ReportIndex = st.Functions.Count };
            for (int i = 0; i < terms.Count; i++)
            {
                var use = new Use { From = terms[i].Src, To = node, Socket = triggers[i] };
                st.Uses.Add(use);
                counter.Triggers.Add(use);
            }
            st.Counters.Add(counter);
            st.Functions.Add($"line {c.At.Line}: {NetlistParser.Describe(c)}: {info.Name} (+${info.Cost}k)");
        }

        Node n = counter.Node;
        var zero = new Src { Node = n, Socket = FindSocket(n.Info, "ZERO_FLAG", output: true), Label = NetlistParser.Describe(c), At = c.At };
        if (c.Op == "==")
            return zero;
        var pos = new Src { Node = n, Socket = FindSocket(n.Info, "POSITIVE_FLAG", output: true), Label = NetlistParser.Describe(c), At = c.At };
        if (c.Op is ">" or "<")
            return pos;

        // >= and <= are ZERO or POS, through one mixer.
        Node mixer = NewMixer(false, NetlistParser.Describe(c));
        anchor.Extra.Add(mixer);
        int[] inputs = Inputs(mixer.Info);
        Connect(st, zero, mixer, inputs[0]);
        Connect(st, pos, mixer, inputs[1]);
        return new Src { Node = mixer, Label = NetlistParser.Describe(c), At = c.At };
    }

    private static string SumKey(List<(Src Src, int Weight)> terms, int sign) =>
        string.Join("+", terms.Select(t => $"{sign * t.Weight}*{KeyText(t.Src.Key)}").OrderBy(k => k, StringComparer.Ordinal));

    // A later comparison needs POS, so a shared small counter becomes a large one.
    private static void Upgrade(State st, SharedCounter counter)
    {
        Node n = counter.Node;
        ModuleInfo small = n.Info, large = ModuleCatalog.ByName("CounterLarge");
        foreach (Use u in counter.Triggers)
            u.Socket = FindSocket(large, small.Sockets[u.Socket].Name, output: false);
        int[] ints = n.Module.Ints;
        n.Module.Ints = Enumerable.Range(0, Inputs(large).Length).Select(i => i < ints.Length ? ints[i] : 0).ToArray();
        n.Info = large;
        n.Module.Type = large.Id;
        // ZERO_FLAG is socket 0 on both, so existing uses of it stay valid.
        st.Functions[counter.ReportIndex] = st.Functions[counter.ReportIndex].Replace($"{small.Name} (+${small.Cost}k)", $"{large.Name} (+${large.Cost}k)");
    }

    private static void Connect(State st, Src from, Node to, int socket) =>
        st.Uses.Add(new Use { From = from, To = to, Socket = socket });

    // --------------------------------------------------------------- fan-out

    private static void WireUp(State st, List<Node> modules)
    {
        foreach (var group in st.Uses.GroupBy(u => u.From.Key).ToList())
        {
            Src from = group.First().From;
            int capacity = from.Socket < 0 ? Outputs(from.Node.Info).Length : 1;
            List<Use> uses = group.ToList();
            if (uses.Count <= capacity)
                continue;
            if (st.Options.Strict)
                throw uses[capacity].From.At.Error($"{from.Label} has {uses.Count} uses but {capacity} output{(capacity == 1 ? "" : "s")}; this use is one too many (--strict doesn't add buffers)");

            // Each buffer takes one outlet and gives four.
            var outlets = Enumerable.Repeat(from, capacity).ToList();
            int buffers = 0;
            while (outlets.Count < uses.Count)
            {
                Src feed = outlets[0];
                outlets.RemoveAt(0);
                Node buffer = NewMixer(false, $"buffer for {from.Label}");
                st.Buffers.Add(buffer);
                st.Uses.Add(new Use { From = feed, To = buffer, Socket = Inputs(buffer.Info)[0] });
                var bufferSrc = new Src { Node = buffer, Label = buffer.Label };
                outlets.AddRange(Enumerable.Repeat(bufferSrc, Outputs(buffer.Info).Length));
                buffers++;
            }
            for (int i = 0; i < uses.Count; i++)
                uses[i].From = outlets[i];
            st.Added.Add($"{from.Label}: {uses.Count} uses, {capacity} output{(capacity == 1 ? "" : "s")}: {buffers} buffer{(buffers == 1 ? "" : "s")} (+${buffers}k)");
        }
    }

    // --------------------------------------------------------------- rack, report

    private static void PackRack(List<Node> modules)
    {
        var used = new bool[PlacementChecker.RackWidth, PlacementChecker.RackHeight];
        List<Node> rack = modules.Where(x => !PlacementChecker.FloorOnly.Contains(x.Info.Name)).ToList();
        foreach (Node x in rack.Where(x => PlacementChecker.RackSpan(x.Info.Name) == 2).Concat(rack.Where(x => PlacementChecker.RackSpan(x.Info.Name) != 2)))
        {
            int span = PlacementChecker.RackSpan(x.Info.Name);
            bool placed = false;
            for (int y = 0; y < PlacementChecker.RackHeight && !placed; y++)
            for (int rx = 0; rx + span <= PlacementChecker.RackWidth && !placed; rx++)
            {
                if (Enumerable.Range(rx, span).Any(k => used[k, y]))
                    continue;
                for (int k = rx; k < rx + span; k++)
                    used[k, y] = true;
                x.Module.RackX = rx;
                x.Module.RackY = y;
                placed = true;
            }
            if (!placed)
                throw new NetlistException(1, 1, $"the rack is full: no room for {x.Label} ({x.Info.Name})");
        }
    }

    private static string DescribeModules(string problem, List<Node> modules) =>
        System.Text.RegularExpressions.Regex.Replace(problem, @"\bm(\d+) (\w+)", m =>
        {
            int i = int.Parse(m.Groups[1].Value);
            return i < modules.Count ? modules[i].Label : m.Value;
        });

    private static IEnumerable<string> Report(List<Node> modules, State st)
    {
        string Group(IEnumerable<Node> nodes) => string.Join(", ", nodes.GroupBy(x => x.Info.Name).Select(g =>
            g.Count() == 1 ? $"{g.Key} {g.First().Info.Cost}" : $"{g.Key} x{g.Count()} {g.Sum(x => x.Info.Cost)}"));

        List<Node> paid = modules.Where(x => x.Module.Removable).ToList();
        List<Node> logic = paid.Where(x => PlacementChecker.RackOnly.Contains(x.Info.Name)).ToList();
        List<Node> floor = paid.Except(logic).ToList();
        yield return $"floor   ${floor.Sum(x => x.Info.Cost)}k   {Group(floor)}";
        yield return $"logic   ${logic.Sum(x => x.Info.Cost)}k   {Group(logic)}";
        yield return $"total   ${paid.Sum(x => x.Info.Cost)}k";
        foreach (string function in st.Functions)
            yield return "func    " + function;
        foreach (string added in st.Added)
            yield return "added   " + added;
        foreach (string optimised in st.Optimised)
            yield return "opt     " + optimised;
    }
}
