using System.Collections.Generic;
using System.Linq;

namespace LastCallBBS.FoodCourt;

/// <summary>
/// Structural checks for a solution before it's written. The game reads every solution at startup
/// and a malformed file can stop it from starting, so an import must not produce one. This doesn't
/// check that parts fit on the floor or rack, or that the factory works.
/// </summary>
public static class SolutionValidator
{
    private const int MaxRotation = 3, SmallCounterLimit = 9, LargeCounterLimit = 99;

    public static List<string> Validate(SolutionFile s)
    {
        var problems = new List<string>();
        if (s.Version < ModuleCatalog.MinVersion || s.Version > ModuleCatalog.CurrentVersion)
            problems.Add($"version {s.Version} isn't one the game accepts ({ModuleCatalog.MinVersion} to {ModuleCatalog.CurrentVersion})");
        if (s.Name.Trim().Length == 0)
            problems.Add("the name is empty");
        if (!ModuleCatalog.LevelExists(s.Level))
            problems.Add($"unknown level {s.Level}");
        else
            CheckFixedParts(s, problems);

        for (int i = 0; i < s.Modules.Count; i++)
            CheckModule(s, i, problems);
        CheckWires(s, problems);
        return problems;
    }

    private static void CheckFixedParts(SolutionFile s, List<string> problems)
    {
        string primary = "PrimaryInputFor" + ModuleCatalog.LevelName(s.Level);
        foreach (string required in new[] { primary, "Output" })
        {
            int count = s.Modules.Count(m => !m.Removable && ModuleCatalog.ById[m.Type].Name == required);
            if (count != 1)
                problems.Add($"expected exactly one fixed {required}, found {count}");
        }
    }

    private static void CheckModule(SolutionFile s, int index, List<string> problems)
    {
        SolutionModule m = s.Modules[index];
        ModuleInfo info = ModuleCatalog.ById[m.Type];
        if (m.Rotation > MaxRotation)
            problems.Add($"m{index}: rotation must be 0 to {MaxRotation}");
        if (m.LegacyLead.HasValue != (s.Version < info.Data.LegacyLeadBefore))
            problems.Add($"m{index}: legacy-lead doesn't match version {s.Version}");
        if (m.LegacyTrail.HasValue != (s.Version < info.Data.LegacyTrailBefore))
            problems.Add($"m{index}: legacy-trail doesn't match version {s.Version}");

        int limit = info.Name == "CounterSmall" ? SmallCounterLimit : info.Name == "CounterLarge" ? LargeCounterLimit : -1;
        if (limit >= 0 && m.Ints.Any(v => v < -limit || v > limit))
            problems.Add($"m{index}: {info.Name} values must be from -{limit} to {limit}");
    }

    private static void CheckWires(SolutionFile s, List<string> problems)
    {
        var used = new HashSet<(int, int)>();
        for (int i = 0; i < s.Wires.Count; i++)
        {
            SolutionWire w = s.Wires[i];
            SocketInfo a = Socket(s, w.ModuleA, w.SocketA);
            SocketInfo b = Socket(s, w.ModuleB, w.SocketB);
            string label = $"wire {i + 1} (m{w.ModuleA} to m{w.ModuleB})";
            if (a == null || b == null)
            {
                problems.Add($"{label}: refers to a socket that doesn't exist");
                continue;
            }
            if (a.IsOutput == b.IsOutput)
                problems.Add($"{label}: must connect an output to an input");
            if (!used.Add((w.ModuleA, w.SocketA)))
                problems.Add($"{label}: {a.Name} already has a wire");
            if (!used.Add((w.ModuleB, w.SocketB)))
                problems.Add($"{label}: {b.Name} already has a wire");
        }
    }

    private static SocketInfo Socket(SolutionFile s, int module, int socket)
    {
        if (module < 0 || module >= s.Modules.Count)
            return null;
        IReadOnlyList<SocketInfo> sockets = ModuleCatalog.ById[s.Modules[module].Type].Sockets;
        return socket >= 0 && socket < sockets.Count ? sockets[socket] : null;
    }
}
