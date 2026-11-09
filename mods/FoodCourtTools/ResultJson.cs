using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LastCallBBS.FoodCourt;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.FoodCourtTools;

/// <summary>Formats run results. Status is "ok", "error", "timeout", or "failed" if the run couldn't happen.</summary>
internal static class ResultJson
{
    public static string Failure(string name, string message) =>
        $"{{\"file\":{Text(name)},\"status\":\"failed\",\"error\":{Text(message)}}}\n";

    public static string Success(string name, SolutionFile saved, RunOutcome outcome, List<List<FoodCourtTick>> trace)
    {
        bool finished = outcome.Status == "ok" && outcome.OrderTicks.Count > 0;

        var json = new StringBuilder("{");
        json.Append($"\"file\":{Text(name)},");
        json.Append($"\"level\":{Text(ModuleCatalog.LevelName(saved.Level))},");
        json.Append($"\"name\":{Text(saved.Name)},");
        json.Append($"\"status\":{Text(outcome.Status)},");
        json.Append($"\"error\":{Text(outcome.Error)},");
        // The game's score for a run is the slowest order's tick count, less one.
        json.Append($"\"ticks\":{(finished ? Number(outcome.OrderTicks.Max() - 1) : "null")},");
        json.Append($"\"cost\":{Number(saved.ComputeCost())},");
        json.Append($"\"savedSolved\":{(saved.Solved ? "true" : "false")},");
        json.Append($"\"savedTicks\":{Number(saved.Ticks)},");
        json.Append($"\"savedCost\":{Number(saved.Cost)},");
        json.Append($"\"orderTicks\":[{string.Join(",", outcome.OrderTicks.Select(Number))}],");
        json.Append($"\"orders\":[{string.Join(",", trace.Select(order => "[" + string.Join(",", order.Select(Tick)) + "]"))}]");
        json.Append("}\n");
        return json.ToString();
    }

    private static string Tick(FoodCourtTick tick)
    {
        string signals = string.Join(",", tick.Signals.Select(s => Text(SignalName(tick, s))));
        string products = string.Join(",", tick.Products.Select(p => $"[{p.FromX},{p.FromY},{p.ToX},{p.ToY}]"));
        return $"{{\"tick\":{Number(tick.Tick)},\"signals\":[{signals}],\"products\":[{products}]}}";
    }

    private static string SignalName(FoodCourtTick tick, FoodCourtSignal signal)
    {
        ModuleInfo info = ModuleCatalog.ByName(tick.Modules[signal.Module]);
        string socket = info != null && signal.Socket < info.Sockets.Count ? info.Sockets[signal.Socket].Name : "#" + signal.Socket;
        return $"m{signal.Module}.{socket}";
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(string value)
    {
        if (value == null)
            return "null";
        var text = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c == '"' || c == '\\')
                text.Append('\\').Append(c);
            else if (c < ' ')
                text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else
                text.Append(c);
        }
        return text.Append('"').ToString();
    }
}
