// Console tool for the Tracker library: module information and WAV rendering.
using System;
using System.IO;
using Tracker;

internal static class Program
{
    private const string Usage =
        "Usage:\n" +
        "  tracker-cli info <module>\n" +
        "  tracker-cli render <module> <out.wav> [--model a500|a1200|none] [--separation 0-100] [--rate <hz>] [--karplus]";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "info")
                return Info(Module.Load(args[1]));
            if (args.Length >= 3 && args[0] == "render")
                return Render(args);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception e) when (e is IOException || e is ModuleFormatException || e is ArgumentException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Info(Module module)
    {
        Console.WriteLine($"Title:    {module.Title}");
        Console.WriteLine($"Format:   {(module.Tag.Length > 0 ? module.Tag : "15 samples")}, {module.Channels} channels");
        Console.WriteLine($"Length:   {module.SongLength} positions, {module.Patterns.Length} patterns");
        Console.WriteLine($"Tempo:    {module.InitialTempo} BPM");
        Console.WriteLine($"Duration: {SongLength.Measure(module):m\\:ss\\.fff}");
        Console.WriteLine("Samples:");
        for (int i = 0; i < module.Samples.Length; i++)
        {
            Sample s = module.Samples[i];
            if (s.Length == 0 && s.Name.Length == 0)
                continue;
            string loop = s.Loops ? $"loop {s.LoopStart}+{s.LoopLength}" : "";
            Console.WriteLine($"  {i + 1,2} {s.Name,-22} {s.Length,6} vol {s.Volume,2} fine {s.SignedFineTune,2} {loop}");
        }
        return 0;
    }

    private static int Render(string[] args)
    {
        Module module = Module.Load(args[1]);
        int rate = 48000;
        var model = AmigaModel.A500;
        float separation = 0.2f;
        bool karplus = false;
        for (int i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length:
                    model = (AmigaModel)Enum.Parse(typeof(AmigaModel), args[++i], ignoreCase: true);
                    break;
                case "--separation" when i + 1 < args.Length:
                    separation = int.Parse(args[++i]) / 100f;
                    break;
                case "--rate" when i + 1 < args.Length:
                    rate = int.Parse(args[++i]);
                    break;
                case "--karplus":
                    karplus = true;
                    break;
                default:
                    throw new ArgumentException($"unknown option {args[i]}\n{Usage}");
            }
        }

        var player = new ModulePlayer(module, rate) { Model = model, StereoSeparation = separation, KarplusStrong = karplus };
        var buffer = new float[4096 * 2];
        using var output = new BinaryWriter(File.Create(args[2]));
        WriteHeader(output, rate, 0);
        long frames = 0;
        int clipped = 0;
        while (true)
        {
            int got = player.Render(buffer, 4096);
            for (int i = 0; i < got * 2; i++)
            {
                float f = buffer[i];
                if (f > 1f || f < -1f)
                    clipped++;
                output.Write((short)Math.Round(Math.Max(-1f, Math.Min(1f, f)) * 32767f));
            }
            frames += got;
            if (got < 4096)
                break;
        }
        output.Seek(0, SeekOrigin.Begin);
        WriteHeader(output, rate, frames);
        Console.WriteLine($"{frames} frames, {(double)frames / rate:F2} s, {clipped} samples clipped");
        return 0;
    }

    private static void WriteHeader(BinaryWriter w, int rate, long frames)
    {
        long data = frames * 4;
        w.Write("RIFF".ToCharArray());
        w.Write((int)(36 + data));
        w.Write("WAVEfmt ".ToCharArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(rate);
        w.Write(rate * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data".ToCharArray());
        w.Write((int)data);
    }
}
