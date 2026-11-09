using System.Collections.Generic;
using System.Linq;
using System.Text;
using LastCallBBS.FoodCourt;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.FoodCourtTools;

/// <summary>Writes one order's signals as a VCD waveform, which GTKWave and similar viewers can open.</summary>
internal static class Waveform
{
    private const int FirstIdChar = 33, IdChars = 94;

    public static string Format(IReadOnlyList<FoodCourtTick> ticks)
    {
        var text = new StringBuilder();
        text.Append("$version Food Court Tools $end\n$timescale 1 ns $end\n$scope module factory $end\n");

        var ids = new Dictionary<(int Module, int Socket), string>();
        IReadOnlyList<string> modules = ticks[0].Modules;
        for (int m = 0; m < modules.Count; m++)
        {
            ModuleInfo info = ModuleCatalog.ByName(modules[m]);
            List<int> outputs = info == null ? new List<int>() : Enumerable.Range(0, info.Sockets.Count).Where(s => info.Sockets[s].IsOutput).ToList();
            if (outputs.Count == 0)
                continue;
            text.Append($"$scope module m{m}_{modules[m]} $end\n");
            foreach (int socket in outputs)
            {
                string id = Id(ids.Count);
                ids[(m, socket)] = id;
                text.Append($"$var wire 1 {id} {info.Sockets[socket].Name} $end\n");
            }
            text.Append("$upscope $end\n");
        }
        text.Append("$upscope $end\n$enddefinitions $end\n");

        var values = new Dictionary<string, bool>();
        foreach (FoodCourtTick tick in ticks)
        {
            var on = new HashSet<string>(tick.Signals.Where(s => ids.ContainsKey((s.Module, s.Socket))).Select(s => ids[(s.Module, s.Socket)]));
            text.Append('#').Append(tick.Tick).Append('\n');
            foreach (string id in ids.Values)
            {
                bool now = on.Contains(id);
                if (!values.TryGetValue(id, out bool before) || before != now)
                    text.Append(now ? '1' : '0').Append(id).Append('\n');
                values[id] = now;
            }
        }
        // One more time step, so the last tick has visible width.
        text.Append('#').Append(ticks[ticks.Count - 1].Tick + 1).Append('\n');
        return text.ToString();
    }

    private static string Id(int number)
    {
        var id = new StringBuilder();
        do
        {
            id.Append((char)(FirstIdChar + number % IdChars));
            number = number / IdChars - 1;
        }
        while (number >= 0);
        return id.ToString();
    }
}
