using System;
using System.Collections.Generic;
using System.Linq;

namespace LastCallBBS.FoodCourt;

/// <summary>
/// The optimisation step of <see cref="NetlistCompiler"/>. Mixers have no delay and no state, so any
/// rearrangement that computes the same signal at every non-mixer input behaves the same in the game.
/// Counters and sequencers are only changed where the result is identical: at_tick(k).
/// </summary>
public static partial class NetlistCompiler
{
    private const int MaxPulseTick = 100, SmallPulseTick = 10;

    private static void Optimise(State st)
    {
        int before = LogicCost(st);
        int mixersBefore = LiveMixers(st).Count();

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Node m in LiveMixers(st).ToList())
                changed |= Normalise(st, m);
            changed |= MergeDuplicates(st);
            changed |= Flatten(st);
        }
        RemoveUnused(st);
        int aliases = mixersBefore - LiveMixers(st).Count() - st.Optimised.Count(l => l.StartsWith("merged") || l.StartsWith("removed"));
        if (aliases > 0)
            st.Optimised.Insert(0, $"removed {aliases} one-input mixer{(aliases == 1 ? "" : "s")}; buffers are added back only where a signal needs them");

        ChoosePulses(st);

        int after = LogicCost(st);
        if (before > after)
            st.Optimised.Add($"saved ${before - after}k in total");
    }

    private static IEnumerable<Node> Live(State st) =>
        st.Order.SelectMany(x => (x.Info == null ? new Node[0] : new[] { x }).Concat(x.Extra)).Where(x => !x.Removed);

    private static IEnumerable<Node> LiveMixers(State st) => Live(st).Where(x => x.IsMixer);

    private static List<Use> InputsOf(State st, Node n) => st.Uses.Where(u => u.To == n).ToList();

    private static List<Use> OutputsOf(State st, Node n) => st.Uses.Where(u => u.From.Node == n).ToList();

    private static int EnableSocket(Node mixer) => mixer.IsComplex ? FindSocket(mixer.Info, "ENABLE", output: false) : -1;

    private static void Remove(State st, Node n)
    {
        n.Removed = true;
        st.Uses.RemoveAll(u => u.To == n);
    }

    // Repeated inputs, mixers that are only an alias, and mixers that are never on.
    private static bool Normalise(State st, Node m)
    {
        List<Use> inputs = InputsOf(st, m);
        int enable = EnableSocket(m);
        List<Use> or = inputs.Where(u => u.Socket != enable).ToList();
        bool changed = false;
        foreach (Use dup in or.GroupBy(u => KeyText(u.From.Key)).SelectMany(g => g.Skip(1)).ToList())
        {
            st.Uses.Remove(dup);
            or.Remove(dup);
            changed = true;
        }
        Use en = inputs.FirstOrDefault(u => u.Socket == enable);

        // No OR inputs, or a complex mixer without an enable: never on, so its users get nothing.
        if (or.Count == 0 || (m.IsComplex && en == null))
        {
            st.Optimised.Add($"removed {m.Label}: it has no {(or.Count == 0 ? "inputs" : "enable")}, so it's never on (-$1k)");
            st.Uses.RemoveAll(u => u.From.Node == m);
            Remove(st, m);
            return true;
        }

        // A simple mixer with one input is the same signal as that input.
        if (!m.IsComplex && or.Count == 1 && or[0].From.Node != m)
        {
            Src source = or[0].From;
            foreach (Use u in OutputsOf(st, m))
                u.From = new Src { Node = source.Node, Socket = source.Socket, Label = source.Label, At = u.From.At };
            Remove(st, m);
            return true;
        }
        return changed;
    }

    private static string Signature(State st, Node m)
    {
        int enable = EnableSocket(m);
        List<Use> inputs = InputsOf(st, m);
        string or = string.Join(",", inputs.Where(u => u.Socket != enable).Select(u => KeyText(u.From.Key)).OrderBy(k => k, StringComparer.Ordinal));
        Use en = inputs.FirstOrDefault(u => u.Socket == enable);
        return m.IsComplex ? $"and {KeyText(en?.From.Key ?? "")} ({or})" : $"or ({or})";
    }

    private static bool MergeDuplicates(State st)
    {
        bool changed = false;
        foreach (var group in LiveMixers(st).ToList().GroupBy(m => Signature(st, m)).Where(g => g.Count() > 1))
        {
            Node keep = group.First();
            foreach (Node dup in group.Skip(1))
            {
                foreach (Use u in OutputsOf(st, dup))
                    u.From = new Src { Node = keep, Label = keep.Label, At = u.From.At };
                Remove(st, dup);
                st.Optimised.Add($"merged {dup.Label} into {keep.Label}: same inputs (-$1k)");
                changed = true;
            }
        }
        return changed;
    }

    // A simple mixer used once, as an OR input of another mixer, is merged into it when the inputs fit.
    private static bool Flatten(State st)
    {
        foreach (Node c in LiveMixers(st).Where(x => !x.IsComplex).ToList())
        {
            List<Use> outs = OutputsOf(st, c);
            if (outs.Count != 1)
                continue;
            Use use = outs[0];
            Node p = use.To;
            if (p.Removed || !p.IsMixer || p == c || use.Socket == EnableSocket(p))
                continue;
            List<Use> cInputs = InputsOf(st, c);
            if (cInputs.Any(u => u.From.Node == p))
                continue; // would make a loop

            int enable = EnableSocket(p);
            List<Use> pOr = InputsOf(st, p).Where(u => u.Socket != enable && u != use).ToList();
            var keys = new HashSet<string>(pOr.Select(u => KeyText(u.From.Key)));
            List<Src> added = cInputs.Select(u => u.From).Where(f => keys.Add(KeyText(f.Key))).ToList();
            int[] orSockets = Inputs(p.Info).Where(k => k != enable).ToArray();
            if (pOr.Count + added.Count > orSockets.Length)
                continue;

            st.Uses.Remove(use);
            foreach (Src f in added)
                st.Uses.Add(new Use { From = f, To = p });
            // Renumber the OR inputs so they stay within the mixer's sockets.
            List<Use> now = InputsOf(st, p).Where(u => u.Socket != enable).ToList();
            for (int i = 0; i < now.Count; i++)
                now[i].Socket = orSockets[i];
            Remove(st, c);
            st.Optimised.Add($"merged {c.Label} into {p.Label}: used only there (-$1k)");
            return true;
        }
        return false;
    }

    private static void RemoveUnused(State st)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Node m in LiveMixers(st).ToList())
            {
                if (OutputsOf(st, m).Count > 0)
                    continue;
                Remove(st, m);
                st.Optimised.Add($"removed {m.Label}: nothing uses it (-$1k)");
                changed = true;
            }
        }
    }

    // --------------------------------------------------------------- costs

    private static int Buffers(int uses, int capacity) => uses <= capacity ? 0 : (uses - capacity + 2) / 3;

    /// <summary>Removable rack blocks plus the buffers fan-out will add.</summary>
    private static int LogicCost(State st)
    {
        List<Node> live = Live(st).ToList();
        int blocks = live.Where(x => x.Module.Removable && PlacementChecker.RackOnly.Contains(x.Info.Name)).Sum(x => x.Info.Cost);
        int buffers = st.Uses.Where(u => !u.From.Node.Removed).GroupBy(u => u.From.Key)
            .Sum(g => Buffers(g.Count(), g.First().From.Socket < 0 ? Outputs(g.First().From.Node.Info).Length : 1));
        return blocks + buffers;
    }

    // --------------------------------------------------------------- at_tick

    private sealed class Pulse
    {
        public int Tick;
        public string Text;
        public List<Use> Users;
        public List<int> ReportLines;
    }

    /// <summary>
    /// at_tick(k) and delay(scan.START, k) give one pulse at tick k, because START fires once. Each can be
    /// a sequencer channel ($5k per 4 channels), a small counter counting from_tick1() up from -(k-1),
    /// or for tick 1 a small counter fed by its own ZERO. Picks the cheapest mix by trying them all.
    /// </summary>
    private static void ChoosePulses(State st)
    {
        Node scanner = st.BlockNodes.Values.FirstOrDefault(x => x.Info.Name.StartsWith("PrimaryInputFor"));
        if (scanner == null)
            return;
        var startKey = (scanner, FindSocket(scanner.Info, "START", output: true));
        List<SharedSequencer> seqs = st.Sequencers.Where(q => !q.Node.Removed && Equals(q.Trigger, startKey)).ToList();
        if (seqs.Count == 0)
            return;

        var pulses = new List<Pulse>();
        var holds = new List<(string Pattern, string Text, List<Use> Users)>();
        foreach (SharedSequencer q in seqs)
        {
            foreach (KeyValuePair<string, int> ch in q.Channels)
            {
                int socket = FindSocket(q.Node.Info, "OUTPUT_" + "ABCD"[ch.Value], output: true);
                List<Use> users = st.Uses.Where(u => u.From.Node == q.Node && u.From.Socket == socket).ToList();
                if (ch.Key.StartsWith("delay"))
                    pulses.Add(new Pulse { Tick = int.Parse(ch.Key.Substring(5)), Text = q.Texts[ch.Key], Users = users,
                        ReportLines = q.ReportLines.TryGetValue(ch.Key, out List<int> lines) ? lines : new List<int>() });
                else
                    holds.Add((ch.Key, q.Texts[ch.Key], users));
            }
        }
        if (pulses.Count == 0 || pulses.Count > 10)
            return;

        st.Calls.TryGetValue("from_tick1()", out var tick1);
        bool haveTick1 = tick1.Result != null && !tick1.Result.Node.Removed;
        int startUses = st.Uses.Count(u => Equals(u.From.Key, startKey)) - seqs.Count;
        int tick1Uses = haveTick1 ? st.Uses.Count(u => Equals(u.From.Key, tick1.Result.Key)) : 0;

        // 0 = sequencer channel, 1 = counter from from_tick1(), 2 = self-fed counter (tick 1 only).
        int Cost(int[] choice)
        {
            int channels = holds.Count + choice.Count(c => c == 0);
            int sequencers = (channels + ModuleCatalog.SequencerChannels - 1) / ModuleCatalog.SequencerChannels;
            int cost = 5 * sequencers, fromStart = 0, fromTick1 = 0;
            for (int i = 0; i < pulses.Count; i++)
            {
                int users = pulses[i].Users.Count;
                if (choice[i] == 0)
                    cost += Buffers(users, 1);
                else if (choice[i] == 1)
                {
                    cost += (pulses[i].Tick <= SmallPulseTick ? 3 : 5) + Buffers(users, 1);
                    fromTick1++;
                    if (pulses[i].Tick > 1)
                        fromStart++;
                }
                else
                    cost += 3 + Buffers(users + 1, 1);
            }
            if (fromTick1 > 0 && !haveTick1)
                cost += 3;
            cost += Buffers(startUses + sequencers + fromStart, 1);
            if (fromTick1 > 0 || haveTick1)
                cost += Buffers(tick1Uses + fromTick1, 1);
            return cost;
        }

        int[] current = new int[pulses.Count];
        int[] best = (int[])current.Clone();
        int bestCost = Cost(best);
        int baseCost = bestCost;
        int[] options = new int[pulses.Count];
        void Search(int i)
        {
            if (i == pulses.Count)
            {
                int c = Cost(options);
                if (c < bestCost)
                {
                    bestCost = c;
                    best = (int[])options.Clone();
                }
                return;
            }
            for (int o = 0; o <= (pulses[i].Tick == 1 ? 2 : 1); o++)
            {
                options[i] = o;
                Search(i + 1);
            }
        }
        Search(0);
        if (best.All(c => c == 0))
            return;

        // Rebuild: the old START sequencers go; channels that stay are packed into new ones.
        Node anchor = seqs[0].Anchor;
        foreach (SharedSequencer q in seqs)
        {
            st.Uses.Remove(q.StartUse);
            Remove(st, q.Node);
        }
        st.Sequencers.RemoveAll(seqs.Contains);
        var scannerStart = new Src { Node = scanner, Socket = startKey.Item2, Label = "scan.START" };
        var staying = holds.Select(h => (h.Pattern, h.Text, h.Users))
            .Concat(pulses.Where((p, i) => best[i] == 0).Select(p => ("delay" + p.Tick, p.Text, p.Users))).ToList();
        SharedSequencer target = null;
        foreach (var (pattern, text, users) in staying)
        {
            if (target == null || target.Channels.Count == ModuleCatalog.SequencerChannels)
            {
                ModuleInfo info = ModuleCatalog.ById[ModuleCatalog.SequencerId];
                var node = new Node { Info = info, Label = "sequencer for scan.START", Module = new SolutionModule { Type = info.Id, Removable = true, Bools = new bool[info.Data.BoolCount] } };
                anchor.Extra.Add(node);
                var startUse = new Use { From = scannerStart, To = node, Socket = FindSocket(info, "START", output: false) };
                st.Uses.Add(startUse);
                target = new SharedSequencer { Trigger = startKey, Node = node, Anchor = anchor, StartUse = startUse };
                st.Sequencers.Add(target);
            }
            int channel = target.Channels.Count;
            target.Channels[pattern] = channel;
            target.Texts[pattern] = text;
            int n = int.Parse(pattern.Substring(pattern.StartsWith("delay") ? 5 : 4));
            for (int step = pattern.StartsWith("delay") ? n - 1 : 0; step < n; step++)
                target.Node.Module.Bools[step * ModuleCatalog.SequencerChannels + channel] = true;
            int socket = FindSocket(target.Node.Info, "OUTPUT_" + "ABCD"[channel], output: true);
            foreach (Use u in users)
                u.From = new Src { Node = target.Node, Socket = socket, Label = text, At = u.From.At };
        }

        for (int i = 0; i < pulses.Count; i++)
        {
            if (best[i] == 0)
                continue;
            Pulse p = pulses[i];
            Src made = best[i] == 1 ? PulseCounter(st, null, p.Text, p.Tick, anchor) : SelfFedPulse(st, p.Text, anchor);
            foreach (Use u in p.Users)
                u.From = new Src { Node = made.Node, Socket = made.Socket, Label = p.Text, At = u.From.At };
            // "line N: text: Sequencer ..." becomes "line N: text: built as a counter (see opt)".
            foreach (int line in p.ReportLines)
            {
                string old = st.Functions[line];
                int cut = old.IndexOf(": ", old.IndexOf(": ", StringComparison.Ordinal) + 2, StringComparison.Ordinal);
                if (cut > 0)
                    st.Functions[line] = old.Substring(0, cut) + ": built as a counter (see opt)";
            }
            st.Optimised.Add(best[i] == 1
                ? $"{p.Text}: a CounterSmall counting from_tick1() instead of a sequencer channel"
                : $"{p.Text}: a CounterSmall fed by its own ZERO instead of a sequencer channel");
        }
        st.Optimised.Add($"pulses from scan.START: ${baseCost}k as sequencer channels, ${bestCost}k as built");
    }

    /// <summary>
    /// A pulse at tick k: a counter that adds 1 every tick from tick 1 (from_tick1()) and starts at
    /// -(k-1) from START, so it's zero only at tick k.
    /// </summary>
    private static Src PulseCounter(State st, NetCall c, string text, int k, Node anchor)
    {
        Src tick1 = FromTick1(st, anchor, c?.At ?? default);
        bool large = k > SmallPulseTick;
        ModuleInfo info = ModuleCatalog.ByName(large ? "CounterLarge" : "CounterSmall");
        var node = new Node { Info = info, Label = text, Module = new SolutionModule { Type = info.Id, Removable = true } };
        int[] triggers = Inputs(info);
        node.Module.Ints = Enumerable.Range(0, triggers.Length).Select(i => i == 0 ? 1 : i == 1 && k > 1 ? -(k - 1) : 0).ToArray();
        anchor.Extra.Add(node);
        st.Uses.Add(new Use { From = tick1, To = node, Socket = triggers[0] });
        if (k > 1)
        {
            Node scanner = st.BlockNodes.Values.First(x => x.Info.Name.StartsWith("PrimaryInputFor"));
            st.Uses.Add(new Use { From = new Src { Node = scanner, Socket = FindSocket(scanner.Info, "START", output: true), Label = "scan.START" }, To = node, Socket = triggers[1] });
        }
        if (c != null)
            st.Functions.Add($"line {c.At.Line}: {text}: {info.Name} counting from_tick1() (+${info.Cost}k)");
        return new Src { Node = node, Socket = FindSocket(info, "ZERO_FLAG", output: true), Label = text };
    }

    /// <summary>A pulse at tick 1: a counter fed by its own ZERO, which is on at tick 1 and never again.</summary>
    private static Src SelfFedPulse(State st, string text, Node anchor)
    {
        ModuleInfo info = ModuleCatalog.ByName("CounterSmall");
        var node = new Node { Info = info, Label = text, Module = new SolutionModule { Type = info.Id, Removable = true, Ints = new[] { 1, 0 } } };
        anchor.Extra.Add(node);
        var zero = new Src { Node = node, Socket = FindSocket(info, "ZERO_FLAG", output: true), Label = text };
        st.Uses.Add(new Use { From = zero, To = node, Socket = Inputs(info)[0] });
        return zero;
    }

    /// <summary>The shared from_tick1() counter, made if no call has made it yet.</summary>
    private static Src FromTick1(State st, Node anchor, SourceSpot at)
    {
        if (st.Calls.TryGetValue("from_tick1()", out var made) && !made.Result.Node.Removed)
            return new Src { Node = made.Result.Node, Socket = made.Result.Socket, Label = "from_tick1()", At = at };
        ModuleInfo info = ModuleCatalog.ByName("CounterSmall");
        var node = new Node { Info = info, Label = "from_tick1()", Module = new SolutionModule { Type = info.Id, Removable = true, Ints = new[] { 0, 0 } } };
        anchor.Extra.Add(node);
        var src = new Src { Node = node, Socket = FindSocket(info, "ZERO_FLAG", output: true), Label = "from_tick1()", At = at };
        st.Calls["from_tick1()"] = (src, at.Line);
        st.Functions.Add($"from_tick1(): CounterSmall for at_tick() (+${info.Cost}k)");
        return src;
    }
}
