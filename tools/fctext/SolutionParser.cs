using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LastCallBBS.FoodCourt;

public sealed class SolutionFormatException : Exception
{
    public SolutionFormatException(int line, string message) : base($"line {line}: {message}") { }
}

/// <summary>Reads the text format written by <see cref="SolutionText"/>. Lines starting with # are comments.</summary>
public static class SolutionParser
{
    private static readonly Regex ModuleLine = new(@"^m(\d+)\s+(\S+)(.*)$");
    private static readonly Regex WireLine = new(@"^m(\d+)\.(\S+)\s*->\s*m(\d+)\.(\S+)$");

    public static SolutionFile Parse(string text)
    {
        var s = new SolutionFile { Version = ModuleCatalog.CurrentVersion };
        bool haveLevel = false, haveName = false;

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int number = i + 1;
            string raw = lines[i].TrimEnd('\r');
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;

            Match wire = WireLine.Match(line);
            Match module = ModuleLine.Match(line);
            if (wire.Success)
                s.Wires.Add(ParseWire(s, wire, number));
            else if (module.Success)
                s.Modules.Add(ParseModule(s, module, number));
            else if (line.StartsWith("level "))
            {
                string name = line.Substring(6).Trim();
                s.Level = ModuleCatalog.LevelId(name) ?? throw new SolutionFormatException(number, $"unknown level \"{name}\"");
                haveLevel = true;
            }
            else if (raw == "name" || raw.StartsWith("name "))
            {
                s.Name = raw.Length > 5 ? raw.Substring(5) : "";
                haveName = true;
            }
            else if (line.StartsWith("version "))
                s.Version = Int(line.Substring(8), number);
            else if (line == "unsolved")
                s.Solved = false;
            else if (line.StartsWith("solved"))
                ParseSolved(s, line, number);
            else
                throw new SolutionFormatException(number, $"can't read \"{line}\"");
        }

        if (!haveLevel)
            throw new SolutionFormatException(1, "missing level line");
        if (!haveName)
            throw new SolutionFormatException(1, "missing name line");
        return s;
    }

    private static void ParseSolved(SolutionFile s, string line, int number)
    {
        s.Solved = true;
        foreach (string part in line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            string[] pair = part.Split('=');
            if (pair.Length == 2 && pair[0] == "ticks")
                s.Ticks = Int(pair[1], number);
            else if (pair.Length == 2 && pair[0] == "cost")
                s.Cost = Int(pair[1], number);
            else
                throw new SolutionFormatException(number, $"can't read \"{part}\"");
        }
    }

    private static SolutionModule ParseModule(SolutionFile s, Match match, int number)
    {
        if (Int(match.Groups[1].Value, number) != s.Modules.Count)
            throw new SolutionFormatException(number, $"expected module m{s.Modules.Count} here, because modules must be numbered in order");
        string typeName = match.Groups[2].Value;
        ModuleInfo info = ModuleCatalog.ByName(typeName) ?? throw new SolutionFormatException(number, $"unknown module type \"{typeName}\"");

        var m = new SolutionModule
        {
            Type = info.Id,
            Removable = true,
            Ints = new int[info.Data.IntCount],
            Bools = new bool[info.Data.BoolCount],
        };

        foreach (string part in match.Groups[3].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "fixed")
            {
                m.Removable = false;
                continue;
            }
            int eq = part.IndexOf('=');
            if (eq <= 0)
                throw new SolutionFormatException(number, $"can't read \"{part}\"");
            ApplyField(m, part.Substring(0, eq), part.Substring(eq + 1), number);
        }
        return m;
    }

    private static void ApplyField(SolutionModule m, string key, string value, int number)
    {
        switch (key)
        {
            case "rack":
                (m.RackX, m.RackY) = Pair(value, number);
                break;
            case "floor":
                (m.FloorX, m.FloorY) = Pair(value, number);
                break;
            case "rot":
                m.Rotation = (byte)Int(value, number);
                break;
            case "values":
                int[] ints = value.Split(',').Select(v => Int(v, number)).ToArray();
                if (ints.Length != m.Ints.Length)
                    throw new SolutionFormatException(number, $"expected {m.Ints.Length} values, found {ints.Length}");
                m.Ints = ints;
                break;
            case "grid":
                m.Bools = ParseGrid(value, m.Bools.Length, number);
                break;
            case "flags":
                if (value.Length != m.Bools.Length || value.Any(c => c != '0' && c != '1'))
                    throw new SolutionFormatException(number, $"expected {m.Bools.Length} flags of 0 or 1");
                m.Bools = value.Select(c => c == '1').ToArray();
                break;
            case "legacy-lead":
                m.LegacyLead = Int(value, number);
                break;
            case "legacy-trail":
                m.LegacyTrail = Int(value, number);
                break;
            default:
                throw new SolutionFormatException(number, $"unknown field \"{key}\"");
        }
    }

    private static bool[] ParseGrid(string value, int cells, int number)
    {
        string[] rows = value.Split('/');
        if (cells != ModuleCatalog.SequencerRows * ModuleCatalog.SequencerChannels || rows.Length != ModuleCatalog.SequencerRows ||
            rows.Any(r => r.Length != ModuleCatalog.SequencerChannels || r.Any(c => c != '#' && c != '.')))
        {
            throw new SolutionFormatException(number, $"grid needs {ModuleCatalog.SequencerRows} rows of {ModuleCatalog.SequencerChannels} # or . characters");
        }
        return rows.SelectMany(r => r.Select(c => c == '#')).ToArray();
    }

    private static SolutionWire ParseWire(SolutionFile s, Match match, int number)
    {
        var wire = new SolutionWire { ModuleA = Int(match.Groups[1].Value, number), ModuleB = Int(match.Groups[3].Value, number) };
        wire.SocketA = Socket(s, wire.ModuleA, match.Groups[2].Value, number);
        wire.SocketB = Socket(s, wire.ModuleB, match.Groups[4].Value, number);
        return wire;
    }

    private static int Socket(SolutionFile s, int module, string name, int number)
    {
        if (module >= s.Modules.Count)
            throw new SolutionFormatException(number, $"module m{module} isn't defined above this wire");
        ModuleInfo info = ModuleCatalog.ById[s.Modules[module].Type];
        if (name.StartsWith("#"))
            return Int(name.Substring(1), number);
        for (int i = 0; i < info.Sockets.Count; i++)
        {
            if (info.Sockets[i].Name == name)
                return i;
        }
        throw new SolutionFormatException(number, $"{info.Name} has no socket \"{name}\" (it has {string.Join(", ", info.Sockets.Select(x => x.Name))})");
    }

    private static (int, int) Pair(string value, int number)
    {
        string[] parts = value.Split(',');
        if (parts.Length != 2)
            throw new SolutionFormatException(number, $"expected x,y but found \"{value}\"");
        return (Int(parts[0], number), Int(parts[1], number));
    }

    private static int Int(string value, int number)
    {
        if (int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int result))
            return result;
        throw new SolutionFormatException(number, $"expected a number but found \"{value}\"");
    }
}
