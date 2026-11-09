using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LastCallBBS.FoodCourt;

public sealed class SolutionModule
{
    public int Type;
    public bool Removable;
    public int RackX, RackY, FloorX, FloorY;
    public int[] Ints = new int[0];
    public bool[] Bools = new bool[0];
    public byte Rotation;

    // Fields only present in files saved by older game versions.
    public int? LegacyLead, LegacyTrail;
}

public sealed class SolutionWire
{
    public int ModuleA, SocketA, ModuleB, SocketB;
}

/// <summary>A .solution file, in the same layout the game reads and writes.</summary>
public sealed class SolutionFile
{
    public int Version, Level, Ticks, Cost;
    public string Name = "";
    public bool Solved;
    public List<SolutionModule> Modules = new();
    public List<SolutionWire> Wires = new();

    public static SolutionFile Read(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        var s = new SolutionFile { Version = r.ReadInt32(), Level = r.ReadInt32() };
        s.Name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
        s.Solved = r.ReadBoolean();
        s.Ticks = r.ReadInt32();
        s.Cost = r.ReadInt32();

        int modules = r.ReadInt32();
        for (int i = 0; i < modules; i++)
            s.Modules.Add(ReadModule(r, s.Version, i));

        int wires = r.ReadInt32();
        for (int i = 0; i < wires; i++)
            s.Wires.Add(new SolutionWire { ModuleA = r.ReadInt32(), SocketA = r.ReadInt32(), ModuleB = r.ReadInt32(), SocketB = r.ReadInt32() });

        long left = r.BaseStream.Length - r.BaseStream.Position;
        if (left != 0)
            throw new InvalidDataException($"{left} unread bytes after the last wire");
        return s;
    }

    private static SolutionModule ReadModule(BinaryReader r, int version, int index)
    {
        var m = new SolutionModule { Type = r.ReadInt32(), Removable = r.ReadBoolean() };
        if (!ModuleCatalog.ById.TryGetValue(m.Type, out ModuleInfo info))
            throw new InvalidDataException($"module {index} has unknown type {m.Type}");

        m.RackX = r.ReadInt32();
        m.RackY = r.ReadInt32();
        m.FloorX = r.ReadInt32();
        m.FloorY = r.ReadInt32();

        ModuleData data = info.Data;
        if (version < data.LegacyLeadBefore)
            m.LegacyLead = r.ReadInt32();
        m.Ints = new int[data.IntCount];
        for (int i = 0; i < m.Ints.Length; i++)
            m.Ints[i] = r.ReadInt32();
        m.Bools = new bool[data.BoolCount];
        for (int i = 0; i < m.Bools.Length; i++)
            m.Bools[i] = r.ReadBoolean();
        if (version < data.LegacyTrailBefore)
            m.LegacyTrail = r.ReadInt32();

        m.Rotation = r.ReadByte();
        return m;
    }

    public byte[] Write()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(Version);
        w.Write(Level);
        byte[] name = Encoding.UTF8.GetBytes(Name);
        w.Write(name.Length);
        w.Write(name);
        w.Write(Solved);
        w.Write(Ticks);
        w.Write(Cost);

        w.Write(Modules.Count);
        foreach (SolutionModule m in Modules)
        {
            w.Write(m.Type);
            w.Write(m.Removable);
            w.Write(m.RackX);
            w.Write(m.RackY);
            w.Write(m.FloorX);
            w.Write(m.FloorY);
            if (m.LegacyLead.HasValue)
                w.Write(m.LegacyLead.Value);
            foreach (int value in m.Ints)
                w.Write(value);
            foreach (bool value in m.Bools)
                w.Write(value);
            if (m.LegacyTrail.HasValue)
                w.Write(m.LegacyTrail.Value);
            w.Write(m.Rotation);
        }

        w.Write(Wires.Count);
        foreach (SolutionWire wire in Wires)
        {
            w.Write(wire.ModuleA);
            w.Write(wire.SocketA);
            w.Write(wire.ModuleB);
            w.Write(wire.SocketB);
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>The cost the game would show: the price of every removable module.</summary>
    public int ComputeCost()
    {
        int total = 0;
        foreach (SolutionModule m in Modules)
        {
            if (m.Removable)
                total += ModuleCatalog.ById[m.Type].Cost;
        }
        return total;
    }
}
