using System.Collections.Generic;
using System.Linq;

namespace LastCallBBS.FoodCourt;

/// <summary>
/// Cheap checks for logic that can never matter, from the netlist alone: no game run. They only
/// warn; nothing is removed.
/// </summary>
public static partial class NetlistCompiler
{
    private static IEnumerable<string> DeadCode(State st, List<Node> modules)
    {
        var warnings = new List<string>();
        string At(Node n) => n.Line > 0 ? $"line {n.Line}: " : "";
        string Out(Node n, int socket) => n.IsMixer ? n.Label : $"{n.Label}.{ShortName(n.Info.Sockets[socket].Name, output: true)}";
        // Blocks made by functions are labelled with the call, such as never(x).
        string In(Node n, int socket) => n.Label.Contains("(")
            ? $"{n.Label}, input {ShortName(n.Info.Sockets[socket].Name, output: false)}"
            : $"{n.Label}.{ShortName(n.Info.Sockets[socket].Name, output: false)}";
        bool IsSequencer(Node n) => n.Info.Id == ModuleCatalog.SequencerId;
        bool IsCounter(Node n) => n.Info.Name is "CounterSmall" or "CounterLarge";
        var live = new HashSet<Node>(modules);
        List<Use> uses = st.Uses.Where(u => live.Contains(u.To) && live.Contains(u.From.Node)).ToList();

        int[] Steps(Node seq, int channel) =>
            Enumerable.Range(0, ModuleCatalog.SequencerRows).Where(r => seq.Module.Bools[r * ModuleCatalog.SequencerChannels + channel]).ToArray();
        int Channel(Node seq, int socket) => Outputs(seq.Info).ToList().IndexOf(socket);
        Use InputUse(Node n, string socket) => uses.FirstOrDefault(u => u.To == n && u.Socket == FindSocket(n.Info, socket, output: false));

        // Never on: channels with no steps, every channel of a sequencer with no START, and mixers
        // whose inputs are all never on.
        var neverOn = new HashSet<object>();
        var why = new Dictionary<object, string>();
        foreach (Node seq in modules.Where(IsSequencer))
        {
            bool started = InputUse(seq, "START") != null;
            int[] outputs = Outputs(seq.Info);
            for (int c = 0; c < outputs.Length; c++)
            {
                if (started && Steps(seq, c).Length > 0)
                    continue;
                var key = (seq, outputs[c]);
                neverOn.Add(key);
                why[key] = started ? $"channel {"ABCD"[c]} has no steps" : $"{seq.Label} has no START, so it never runs";
            }
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Node m in modules.Where(x => x.IsMixer && !neverOn.Contains(x)))
            {
                int enable = EnableSocket(m);
                List<Use> inputs = uses.Where(u => u.To == m).ToList();
                List<Use> or = inputs.Where(u => u.Socket != enable).ToList();
                Use en = inputs.FirstOrDefault(u => u.Socket == enable);
                bool dead = or.All(u => neverOn.Contains(u.From.Key)) || (en != null && neverOn.Contains(en.From.Key));
                if (!dead)
                    continue;
                neverOn.Add(m);
                why[m] = "all its inputs are never on";
                changed = true;
            }
        }

        // A sequencer with no START is reported once, not for each channel.
        foreach (Node seq in modules.Where(x => IsSequencer(x) && InputUse(x, "START") == null && uses.Any(u => u.From.Node == x)))
            warnings.Add($"{At(seq)}{seq.Label} has no START, so it never runs");
        foreach (Use u in uses.Where(u => neverOn.Contains(u.From.Key) && !neverOn.Contains(u.To)))
        {
            if (why[u.From.Key].Contains("has no START"))
                continue;
            warnings.Add($"{At(u.To)}{In(u.To, u.Socket)}: its input {Out(u.From.Node, u.From.Socket)} is never on ({why[u.From.Key]})");
        }

        // Counters and sequencers that drive nothing.
        foreach (Node n in modules.Where(x => (IsCounter(x) || IsSequencer(x)) && x.Module.Removable && !uses.Any(u => u.From.Node == x)))
            warnings.Add($"{At(n)}{n.Label} drives nothing (${n.Info.Cost}k)");

        // Counter triggers wired with delta 0.
        foreach (Node n in modules.Where(IsCounter))
        {
            int[] triggers = Inputs(n.Info);
            for (int t = 0; t < triggers.Length; t++)
            {
                Use u = uses.FirstOrDefault(x => x.To == n && x.Socket == triggers[t]);
                if (u != null && n.Module.Ints[t] == 0)
                    warnings.Add($"{At(n)}{In(n, triggers[t])} has delta 0, so its input {Out(u.From.Node, u.From.Socket)} does nothing");
            }
        }

        // A sequencer's own channel whose only effect is on that sequencer's START, at steps 0 to 10.
        // The sequencer is running then, and a running sequencer ignores START.
        foreach (Node seq in modules.Where(IsSequencer))
        {
            Use start = InputUse(seq, "START");
            if (start == null)
                continue;
            int[] outputs = Outputs(seq.Info);
            for (int c = 0; c < outputs.Length; c++)
            {
                int[] steps = Steps(seq, c);
                if (steps.Length == 0 || steps.Contains(ModuleCatalog.SequencerRows - 1))
                    continue;
                // Follow the channel forward through mixers to every other block input it reaches.
                var sinks = new List<(Node, int)>();
                var seen = new HashSet<object>();
                var queue = new Queue<object>(new object[] { (seq, outputs[c]) });
                while (queue.Count > 0)
                {
                    object key = queue.Dequeue();
                    if (!seen.Add(key))
                        continue;
                    foreach (Use u in uses.Where(x => Equals(x.From.Key, key)))
                    {
                        if (u.To.IsMixer)
                            queue.Enqueue(u.To);
                        else
                            sinks.Add((u.To, u.Socket));
                    }
                }
                int startSocket = FindSocket(seq.Info, "START", output: false);
                if (sinks.Count == 0 || sinks.Any(k => k.Item1 != seq || k.Item2 != startSocket))
                    continue;
                Use stop = InputUse(seq, "STOP");
                string unless = stop == null ? "" : $", unless STOP ({Out(stop.From.Node, stop.From.Socket)}) is on in the same tick";
                warnings.Add($"{At(seq)}{seq.Label}.{"ABCD"[c]} only feeds {seq.Label}'s own START, at step{(steps.Length == 1 ? "" : "s")} {string.Join(", ", steps)}, " +
                             $"while it's running; a running sequencer ignores START{unless}. The wire and steps can likely go");
            }
        }
        return warnings;
    }
}
