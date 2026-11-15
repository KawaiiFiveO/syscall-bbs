using System;
using System.Collections.Generic;
using System.Text;

namespace Tracker.Tests;

/// <summary>Builds module files in memory for tests.</summary>
internal sealed class ModuleBuilder
{
    private readonly int _channels;
    private readonly List<(string Name, sbyte[] Data, int FineTune, int Volume, int LoopStart, int LoopLength)> _samples = new();
    private readonly List<int[]> _patterns = new();
    private readonly List<int> _orders = new();

    public ModuleBuilder(int channels = 4) => _channels = channels;

    public string Title { get; set; } = "test";

    /// <summary>Adds a sample; returns its number, from 1.</summary>
    public int Sample(sbyte[] data, int volume = 64, int fineTune = 0, int loopStart = 0, int loopLength = 0, string name = "")
    {
        _samples.Add((name, data, fineTune, volume, loopStart, loopLength));
        return _samples.Count;
    }

    /// <summary>A looped square wave of the given length in bytes.</summary>
    public int Square(int length = 32, int volume = 64)
    {
        var data = new sbyte[length];
        for (int i = 0; i < length; i++)
            data[i] = (sbyte)(i < length / 2 ? 100 : -100);
        return Sample(data, volume, loopStart: 0, loopLength: length);
    }

    /// <summary>Adds an empty pattern; returns its number.</summary>
    public int Pattern()
    {
        _patterns.Add(new int[64 * _channels * 4]);
        return _patterns.Count - 1;
    }

    public void Note(int pattern, int row, int channel, int period, int sample = 0, int effect = 0, int parameter = 0)
    {
        int at = (row * _channels + channel) * 4;
        int[] p = _patterns[pattern];
        p[at] = (sample & 0xF0) | (period >> 8 & 0xF);
        p[at + 1] = period & 0xFF;
        p[at + 2] = (sample & 0xF) << 4 | (effect & 0xF);
        p[at + 3] = parameter & 0xFF;
    }

    public void Order(params int[] patterns) => _orders.AddRange(patterns);

    public byte[] Bytes()
    {
        var file = new List<byte>();
        file.AddRange(Text(Title, 20));
        for (int i = 0; i < 31; i++)
        {
            if (i < _samples.Count)
            {
                var s = _samples[i];
                file.AddRange(Text(s.Name, 22));
                Word(file, s.Data.Length / 2);
                file.Add((byte)(s.FineTune & 0xF));
                file.Add((byte)s.Volume);
                Word(file, s.LoopStart / 2);
                Word(file, Math.Max(1, s.LoopLength / 2));
            }
            else
            {
                file.AddRange(new byte[22]);
                Word(file, 0);
                file.Add(0);
                file.Add(0);
                Word(file, 0);
                Word(file, 1);
            }
        }
        if (_orders.Count == 0)
            _orders.Add(0);
        file.Add((byte)_orders.Count);
        file.Add(127);
        for (int i = 0; i < 128; i++)
            file.Add((byte)(i < _orders.Count ? _orders[i] : 0));
        file.AddRange(Encoding.ASCII.GetBytes(_channels == 4 ? "M.K." : $"{_channels}CHN"));
        if (_patterns.Count == 0)
            Pattern();
        foreach (int[] p in _patterns)
            foreach (int b in p)
                file.Add((byte)b);
        foreach (var s in _samples)
            foreach (sbyte b in s.Data)
                file.Add((byte)b);
        return file.ToArray();
    }

    public Module Build() => Module.Load(Bytes());

    private static void Word(List<byte> file, int value)
    {
        file.Add((byte)(value >> 8));
        file.Add((byte)value);
    }

    private static byte[] Text(string text, int length)
    {
        var bytes = new byte[length];
        Encoding.ASCII.GetBytes(text, 0, Math.Min(text.Length, length), bytes, 0);
        return bytes;
    }
}

/// <summary>Steps a replayer tick by tick and records what Paula was told.</summary>
internal sealed class Stepper
{
    public Stepper(Module module)
    {
        Module = module;
        var data = new sbyte[module.Samples.Length][];
        for (int i = 0; i < data.Length; i++)
            data[i] = (sbyte[])module.Samples[i].Data.Clone();
        Data = data;
        Voices = new PaulaVoice[module.Channels];
        for (int i = 0; i < Voices.Length; i++)
            Voices[i] = new PaulaVoice(48000);
        Replayer = new Replayer(module, data, Voices);
        Replayer.Start(0, 0, resetSong: true);
    }

    public Module Module { get; }
    public sbyte[][] Data { get; }
    public PaulaVoice[] Voices { get; }
    public Replayer Replayer { get; }

    /// <summary>Plays <paramref name="ticks"/> ticks and returns channel 0's Paula period after each.</summary>
    public int[] Periods(int ticks, int channel = 0)
    {
        var periods = new int[ticks];
        for (int i = 0; i < ticks; i++)
        {
            Replayer.Step();
            periods[i] = Voices[channel].Period;
        }
        return periods;
    }

    public int[] Volumes(int ticks, int channel = 0)
    {
        var volumes = new int[ticks];
        for (int i = 0; i < ticks; i++)
        {
            Replayer.Step();
            volumes[i] = Voices[channel].Volume;
        }
        return volumes;
    }

    public void Step(int ticks)
    {
        for (int i = 0; i < ticks; i++)
            Replayer.Step();
    }
}
