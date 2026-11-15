using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Tracker.Tests;

public class ModuleTests
{
    [Fact]
    public void LoadsAProtrackerModule()
    {
        var b = new ModuleBuilder { Title = "song" };
        b.Sample(new sbyte[100], volume: 48, fineTune: 3, loopStart: 20, loopLength: 40, name: "bass");
        int p = b.Pattern();
        b.Note(p, 5, 2, 428, 1, 0xC, 0x20);
        b.Order(p, p);
        Module m = b.Build();
        Assert.Equal("song", m.Title);
        Assert.Equal("M.K.", m.Tag);
        Assert.Equal(4, m.Channels);
        Assert.Equal(2, m.SongLength);
        Assert.Equal("bass", m.Samples[0].Name);
        Assert.Equal(100, m.Samples[0].Length);
        Assert.Equal(48, m.Samples[0].Volume);
        Assert.Equal(3, m.Samples[0].FineTune);
        Assert.Equal(20, m.Samples[0].LoopStart);
        Assert.Equal(40, m.Samples[0].LoopLength);
        Assert.Equal("C-2 01 C20", m.GetNote(p, 5, 2).ToString());
        Assert.Equal("--- 00 000", m.GetNote(p, 0, 0).ToString());
    }

    [Fact]
    public void LoadsMultiChannelModules()
    {
        var b = new ModuleBuilder(8);
        int p = b.Pattern();
        b.Note(p, 63, 7, 214, 0, 0xF, 0x06);
        Module m = b.Build();
        Assert.Equal(8, m.Channels);
        Assert.Equal("C-3 00 F06", m.GetNote(p, 63, 7).ToString());
    }

    [Fact]
    public void LoadsATruncatedFile()
    {
        var b = new ModuleBuilder();
        b.Sample(new sbyte[4000]);
        b.Pattern();
        byte[] file = b.Bytes();
        Module m = Module.Load(file.Take(file.Length - 3000).ToArray());
        Assert.Equal(4000, m.Samples[0].Data.Length);
    }

    [Fact]
    public void ClearsTheFirstWordOfSamplesThatDontLoop()
    {
        var b = new ModuleBuilder();
        b.Sample(new sbyte[] { 50, 50, 50, 50 });
        Module m = b.Build();
        Assert.Equal(new sbyte[] { 0, 0, 50, 50 }, m.Samples[0].Data);
    }

    [Fact]
    public void LengthensASampleToFitItsLoop()
    {
        var b = new ModuleBuilder();
        b.Sample(new sbyte[20], loopStart: 10, loopLength: 30);
        Module m = b.Build();
        Assert.Equal(40, m.Samples[0].Length);
        Assert.Equal(40, m.Samples[0].Data.Length);
    }

    [Fact]
    public void ReadsTextAfterTheSamples()
    {
        var b = new ModuleBuilder();
        b.Sample(new sbyte[8]);
        byte[] file = b.Bytes().Concat(System.Text.Encoding.ASCII.GetBytes("Hello\r\nfrom the\rcomposer\0\0")).ToArray();
        Assert.Equal("Hello\nfrom the\ncomposer", Module.Load(file).Comment);
    }

    [Fact]
    public void IgnoresBinaryAfterTheSamples()
    {
        var b = new ModuleBuilder();
        byte[] file = b.Bytes().Concat(new byte[] { 1, 2, 3, 200, 7, 8, 0x41 }).ToArray();
        Assert.Equal("", Module.Load(file).Comment);
    }

    [Fact]
    public void DecodesNamesAsLatin1OnTheAmigaAndCodePage437OnThePc()
    {
        var amiga = new ModuleBuilder { Title = "café" };
        Assert.Equal("café", Module.Load(Patch(amiga.Bytes(), 3, 0xE9)).Title);
        var pc = new ModuleBuilder(8);
        Assert.Equal("cafe░", Module.Load(Patch(pc.Bytes(), 4, 0xB0)).Title.Substring(0, 5));

        static byte[] Patch(byte[] file, int at, byte value)
        {
            file[0] = (byte)'c'; file[1] = (byte)'a'; file[2] = (byte)'f'; file[3] = (byte)'e';
            file[at] = value;
            return file;
        }
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        Assert.Throws<ModuleFormatException>(() => Module.Load(new byte[3000]));
        Assert.Throws<ModuleFormatException>(() => Module.Load(new byte[10]));
    }

    [Fact]
    public void LoadsAFifteenSampleModule()
    {
        // 15-sample header: title, 15 × 30-byte samples, length, tempo, orders; then patterns, then data.
        var file = new List<byte>();
        file.AddRange(new byte[20]);
        for (int i = 0; i < 15; i++)
        {
            var header = new byte[30];
            if (i == 0)
            {
                header[23] = 8;  // 16 bytes
                header[25] = 64; // volume
                header[29] = 1;  // loop length 1 word
            }
            file.AddRange(header);
        }
        file.Add(1);   // song length
        file.Add(120); // 50 Hz
        file.AddRange(new byte[128]);
        var pattern = new byte[1024];
        pattern[0] = 0x01;
        pattern[1] = 0xAC; // period 428
        pattern[2] = 0x10; // sample 1
        file.AddRange(pattern);
        file.AddRange(new byte[16]);
        Module m = Module.Load(file.ToArray());
        Assert.Equal("", m.Tag);
        Assert.Equal(125, m.InitialTempo);
        Assert.Equal("C-2 01 000", m.GetNote(0, 0, 0).ToString());
    }

    [Fact]
    public void NoteNamesFollowTheReplayerLookup()
    {
        Assert.Equal("C-1", Tables.NoteName(856));
        Assert.Equal("B-3", Tables.NoteName(113));
        Assert.Equal("C#2", Tables.NoteName(404));
        Assert.Equal("???", Tables.NoteName(100));
    }

    [Fact]
    public void MeasuresSongLength()
    {
        var b = new ModuleBuilder();
        b.Square();
        b.Pattern();
        Module m = b.Build();
        double expected = 64 * 6 / Timing.TickRate(125);
        Assert.Equal(expected, SongLength.Measure(m).TotalSeconds, 3);
    }

    /// <summary>Modules from TRACKER_MODULES and tracker/modules: each loads, measures and renders without NaN.</summary>
    [Fact]
    public void RealModulesPlay()
    {
        foreach (string path in LocalModules())
        {
            Module m = Module.Load(path);
            Assert.True(SongLength.Measure(m) > TimeSpan.Zero, path);
            var player = new ModulePlayer(m);
            var buffer = new float[48000 * 2];
            for (int second = 0; second < 5; second++)
            {
                int got = player.Render(buffer, 48000);
                for (int i = 0; i < got * 2; i++)
                    Assert.False(float.IsNaN(buffer[i]) || float.IsInfinity(buffer[i]), path);
                if (got < 48000)
                    break;
            }
        }
    }

    internal static IEnumerable<string> LocalModules()
    {
        var folders = new List<string>();
        string env = Environment.GetEnvironmentVariable("TRACKER_MODULES");
        if (!string.IsNullOrEmpty(env))
            folders.Add(env);
        for (string dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "Tracker", "Tracker.csproj")))
            {
                folders.Add(Path.Combine(dir, "modules"));
                break;
            }
        }
        string[] extensions = { ".mod", ".m15", ".stk", ".nst" };
        return folders.Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories))
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
    }
}
