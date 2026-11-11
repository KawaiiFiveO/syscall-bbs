using System.Collections.Generic;
using System.Linq;

namespace LastCallBBS.FoodCourt;

/// <summary>
/// The placement rules the game enforces while editing, which the simulator doesn't check.
/// Taken from the game's module definitions and its floor and rack drop handling.
/// </summary>
public static class PlacementChecker
{
    private const int FloorWidth = 6, FloorHeight = 7, RackWidth = 11, RackHeight = 3;
    private static readonly (int X, int Y)[] Front = { (1, 0), (0, 1), (-1, 0), (0, -1) };

    private static readonly HashSet<string> FloorOnly = new()
    {
        "Conveyor", "EnroberInput", "Output", "Trash", "SlicerDoubleVertical", "SlicerDoubleHorizontal", "SlicerTriple",
        "Spinner", "Roller", "Docker", "Flattener",
    };

    private static readonly HashSet<string> RackOnly = new() { "MixerSimple", "MixerComplex", "Sequencer", "CounterSmall", "CounterLarge" };
    private static readonly HashSet<string> PumpTargets = new() { "Conveyor", "Router", "Sorter" };

    private static bool IsPump(string name) => name.StartsWith("LiquidInput");

    private static int RackSpan(string name) =>
        name is "Sequencer" or "CounterLarge" or "Animatronic" || name.StartsWith("ScannerFor") || name.StartsWith("PrimaryInputFor") || name.StartsWith("FreezerInput") ? 2 : 1;

    private static int FloorSpan(string name) => name == "Output" ? 2 : 1;

    private static int? RequiredRow(string name) => name == "Output" ? 0 : name.StartsWith("PrimaryInputFor") ? FloorHeight - 1 : null;

    private static int[] AllowedRotations(string name)
    {
        if (name == "Output" || name.StartsWith("PrimaryInputFor")) return new[] { 3 };
        if (name == "ToppingInputVerticalOnly") return new[] { 1, 3 };
        if (name == "Espresso") return new[] { 0, 2 };
        return null;
    }

    private sealed class Part
    {
        public int Index;
        public string Name;
        public SolutionModule Module;
        public string Label => $"m{Index} {Name}";
        public bool Covers(int x, int y) => y == Module.FloorY && x >= Module.FloorX && x < Module.FloorX + FloorSpan(Name);
        public (int X, int Y) Target => (Module.FloorX + Front[Module.Rotation & 3].X, Module.FloorY + Front[Module.Rotation & 3].Y);
    }

    /// <summary>Returns one message per broken rule. An empty list means the layout can be placed.</summary>
    public static List<string> Check(SolutionFile solution)
    {
        var problems = new List<string>();
        var parts = solution.Modules
            .Select((m, i) => new Part { Index = i, Module = m, Name = ModuleCatalog.ById.TryGetValue(m.Type, out ModuleInfo info) ? info.Name : $"type {m.Type}" })
            .ToList();
        List<Part> floor = parts.Where(p => !RackOnly.Contains(p.Name)).ToList();
        List<Part> rack = parts.Where(p => !FloorOnly.Contains(p.Name)).ToList();

        CheckFloor(floor, problems);
        CheckRack(rack, problems);
        CheckPumps(floor, problems);
        return problems;
    }

    private static void CheckFloor(List<Part> floor, List<string> problems)
    {
        foreach (Part p in floor)
        {
            SolutionModule m = p.Module;
            if (m.FloorX < 0 || m.FloorY < 0 || m.FloorX + FloorSpan(p.Name) > FloorWidth || m.FloorY >= FloorHeight)
                problems.Add($"{p.Label} at {m.FloorX},{m.FloorY} doesn't fit on the {FloorWidth}x{FloorHeight} floor");
            if (RequiredRow(p.Name) is int row && m.FloorY != row)
                problems.Add($"{p.Label} must be on row y={row}, not y={m.FloorY}");
            int[] rotations = AllowedRotations(p.Name);
            if (rotations != null && !rotations.Contains(m.Rotation))
                problems.Add($"{p.Label} can't use rot={m.Rotation}; allowed: {string.Join(", ", rotations)}");
        }

        for (int i = 0; i < floor.Count; i++)
        for (int j = i + 1; j < floor.Count; j++)
        {
            Part a = floor[i], b = floor[j];
            if (Enumerable.Range(0, FloorSpan(a.Name)).Any(dx => b.Covers(a.Module.FloorX + dx, a.Module.FloorY)))
                problems.Add($"{a.Label} and {b.Label} overlap on the floor at {a.Module.FloorX},{a.Module.FloorY}");
        }
    }

    private static void CheckRack(List<Part> rack, List<string> problems)
    {
        foreach (Part p in rack)
        {
            SolutionModule m = p.Module;
            if (m.RackX < 0 || m.RackY < 0 || m.RackX + RackSpan(p.Name) > RackWidth || m.RackY >= RackHeight)
                problems.Add($"{p.Label} at rack {m.RackX},{m.RackY} doesn't fit on the {RackWidth}x{RackHeight} rack");
        }

        for (int i = 0; i < rack.Count; i++)
        for (int j = i + 1; j < rack.Count; j++)
        {
            SolutionModule a = rack[i].Module, b = rack[j].Module;
            if (a.RackY == b.RackY && a.RackX < b.RackX + RackSpan(rack[j].Name) && b.RackX < a.RackX + RackSpan(rack[i].Name))
                problems.Add($"{rack[i].Label} and {rack[j].Label} overlap on the rack at y={a.RackY}");
        }
    }

    private static void CheckPumps(List<Part> floor, List<string> problems)
    {
        List<Part> pumps = floor.Where(p => IsPump(p.Name)).ToList();
        foreach (Part pump in pumps)
        {
            (int x, int y) = pump.Target;
            if (x < 0 || y < 0 || x >= FloorWidth || y >= FloorHeight)
            {
                problems.Add($"{pump.Label} at {pump.Module.FloorX},{pump.Module.FloorY} aims off the floor");
                continue;
            }
            foreach (Part other in floor.Where(p => p != pump && p.Covers(x, y) && !PumpTargets.Contains(p.Name)))
                problems.Add($"{pump.Label} aims at {other.Label} at {x},{y}; a pump can only aim at an empty tile, conveyor, router or sorter");
        }

        for (int i = 0; i < pumps.Count; i++)
        for (int j = i + 1; j < pumps.Count; j++)
            if (pumps[i].Target == pumps[j].Target)
                problems.Add($"{pumps[i].Label} and {pumps[j].Label} both aim at {pumps[i].Target.X},{pumps[i].Target.Y}");
    }
}
