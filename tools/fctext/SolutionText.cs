using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LastCallBBS.FoodCourt;

/// <summary>Writes a solution in a readable, line-based text format.</summary>
public static class SolutionText
{
    public static string Format(SolutionFile s)
    {
        var text = new StringBuilder();
        text.Append("level ").AppendLine(ModuleCatalog.LevelName(s.Level));
        text.Append("name ").AppendLine(s.Name);
        text.Append("version ").AppendLine(s.Version.ToString());
        text.AppendLine(s.Solved ? $"solved ticks={s.Ticks} cost={s.Cost}" : "unsolved");
        text.AppendLine();

        for (int i = 0; i < s.Modules.Count; i++)
            text.AppendLine(FormatModule(i, s.Modules[i]));
        text.AppendLine();
        foreach (SolutionWire wire in s.Wires)
            text.AppendLine(FormatWire(s, wire));
        return text.ToString();
    }

    private static string FormatModule(int index, SolutionModule m)
    {
        ModuleInfo info = ModuleCatalog.ById[m.Type];
        var parts = new List<string> { "m" + index, info.Name };
        if (!m.Removable)
            parts.Add("fixed");
        parts.Add($"rack={m.RackX},{m.RackY}");
        parts.Add($"floor={m.FloorX},{m.FloorY}");
        if (m.Rotation != 0)
            parts.Add("rot=" + m.Rotation);
        if (m.Ints.Length > 0)
            parts.Add("values=" + string.Join(",", m.Ints));
        if (m.Type == ModuleCatalog.SequencerId)
            parts.Add("grid=" + FormatGrid(m.Bools));
        else if (m.Bools.Length > 0)
            parts.Add("flags=" + string.Concat(m.Bools.Select(b => b ? '1' : '0')));
        if (m.LegacyLead.HasValue)
            parts.Add("legacy-lead=" + m.LegacyLead);
        if (m.LegacyTrail.HasValue)
            parts.Add("legacy-trail=" + m.LegacyTrail);
        return string.Join(" ", parts);
    }

    // One group per row, one character per channel (A to D).
    private static string FormatGrid(bool[] cells)
    {
        var rows = new List<string>();
        for (int row = 0; row < ModuleCatalog.SequencerRows; row++)
        {
            rows.Add(string.Concat(Enumerable.Range(0, ModuleCatalog.SequencerChannels)
                .Select(channel => cells[row * ModuleCatalog.SequencerChannels + channel] ? '#' : '.')));
        }
        return string.Join("/", rows);
    }

    private static string FormatWire(SolutionFile s, SolutionWire w)
    {
        string a = Endpoint(s, w.ModuleA, w.SocketA);
        string b = Endpoint(s, w.ModuleB, w.SocketB);
        return InputFirst(s, w) ? $"{b} -> {a}" : $"{a} -> {b}";
    }

    /// <summary>Saved wires can have either end first. The text always puts the output first when it's known.</summary>
    public static bool InputFirst(SolutionFile s, SolutionWire w) =>
        IsOutput(s, w.ModuleB, w.SocketB) && !IsOutput(s, w.ModuleA, w.SocketA);

    private static SocketInfo Socket(SolutionFile s, int module, int socket)
    {
        if (module < 0 || module >= s.Modules.Count)
            return null;
        IReadOnlyList<SocketInfo> sockets = ModuleCatalog.ById[s.Modules[module].Type].Sockets;
        return socket >= 0 && socket < sockets.Count ? sockets[socket] : null;
    }

    private static bool IsOutput(SolutionFile s, int module, int socket) => Socket(s, module, socket)?.IsOutput == true;

    private static string Endpoint(SolutionFile s, int module, int socket) =>
        $"m{module}.{Socket(s, module, socket)?.Name ?? "#" + socket}";
}
