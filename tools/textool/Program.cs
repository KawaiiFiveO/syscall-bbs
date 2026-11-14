using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LastCallBBS.Textool;

internal static class Program
{
    private const string Usage =
        "Usage:\n" +
        "  textool unpack <file.tex or folder> <output folder>   write .tex files as PNG\n" +
        "  textool pack <file.png, .array folder or folder> <output folder>   write PNG as .tex files\n" +
        "  textool info <file.tex>...                            print each frame's header\n" +
        "  textool check <file.tex or folder>...                 confirm files unpack and pack back unchanged";

    private const string ArraySuffix = ".array";
    private const string SettingsSuffix = ".cfg";

    private static int Main(string[] args)
    {
        if (args.Length == 0)
            return Fail(Usage);
        string[] rest = args.Skip(1).ToArray();
        switch (args[0])
        {
            case "unpack" when rest.Length == 2:
                return Unpack(rest[0], rest[1]);
            case "pack" when rest.Length == 2:
                return Pack(rest[0], rest[1]);
            case "info" when rest.Length > 0:
                return Each(rest.SelectMany(TexFiles), Info);
            case "check" when rest.Length > 0:
                List<string> files = rest.SelectMany(TexFiles).ToList();
                int frames = 0;
                int code = Each(files, file => frames += Check(file));
                Console.WriteLine(code == 0 ? $"{files.Count} files, {frames} frames: all unchanged" : $"{files.Count} files: some changed");
                return code;
            default:
                return Fail(Usage);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    /// <summary>Runs <paramref name="action"/> on each file, reporting errors and carrying on.</summary>
    private static int Each(IEnumerable<string> files, Action<string> action)
    {
        int failures = 0;
        foreach (string file in files)
        {
            try
            {
                action(file);
            }
            catch (Exception e) when (e is InvalidDataException || e is NotSupportedException || e is IOException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{file}: {e.Message}");
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static IEnumerable<string> TexFiles(string path) =>
        Directory.Exists(path)
            ? Directory.GetFiles(path, "*.tex", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            : new[] { path };

    private static bool IsArray(string name) => name.EndsWith(ArraySuffix, StringComparison.OrdinalIgnoreCase);

    // unpack

    private static int Unpack(string input, string output)
    {
        GuardOutput(output);
        string root = Directory.Exists(input) ? input : Path.GetDirectoryName(Path.GetFullPath(input));
        int code = Each(TexFiles(input), file =>
        {
            string relative = Relative(root, file);
            string stem = Path.Combine(output, relative.Substring(0, relative.Length - ".tex".Length));
            List<TexFrame> frames = TexFile.Read(File.ReadAllBytes(file));
            if (IsArray(stem))
                UnpackArray(frames, stem);
            else if (frames.Count == 1)
                WriteImage(frames[0], stem + ".png");
            else
                throw new NotSupportedException($"{frames.Count} frames, but only {ArraySuffix}.tex files can hold more than one");
        });
        return code;
    }

    private static void UnpackArray(List<TexFrame> frames, string folder)
    {
        int digits = Math.Max(3, (frames.Count - 1).ToString().Length);
        var names = Enumerable.Range(0, frames.Count).Select(i => i.ToString().PadLeft(digits, '0') + ".png").ToList();
        if (Directory.Exists(folder))
        {
            var others = Directory.GetFiles(folder, "*.png").Select(Path.GetFileName)
                .Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (others.Count > 0)
                Console.Error.WriteLine($"{folder}: warning: also holds {string.Join(", ", others)}, which pack would add as frames");
        }
        for (int i = 0; i < frames.Count; i++)
            WriteImage(frames[i], Path.Combine(folder, names[i]));
    }

    private static void WriteImage(TexFrame frame, string png)
    {
        var (image, settings) = Converter.Unpack(frame);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png)));
        using (var stream = File.Create(png))
            Png.Write(image, stream);
        string cfg = png + SettingsSuffix;
        if (!settings.IsDefault)
            File.WriteAllText(cfg, settings.ToText());
        else if (File.Exists(cfg))
            File.Delete(cfg);
    }

    // pack

    private static int Pack(string input, string output)
    {
        GuardOutput(output);
        var jobs = new List<(string Source, string Target)>();
        if (File.Exists(input))
            jobs.Add((input, Path.Combine(output, Path.GetFileNameWithoutExtension(input) + ".tex")));
        else if (Directory.Exists(input) && IsArray(Path.GetFileName(Path.GetFullPath(input).TrimEnd('\\', '/'))))
            jobs.Add((input, Path.Combine(output, Path.GetFileName(Path.GetFullPath(input).TrimEnd('\\', '/')) + ".tex")));
        else if (Directory.Exists(input))
            CollectPackJobs(input, input, output, jobs);
        else
            return Fail($"{input}: not found");

        if (jobs.Count == 0)
            return Fail($"{input}: no .png files or {ArraySuffix} folders");
        int code = Each(jobs.Select(j => j.Source), source =>
        {
            string target = jobs.First(j => j.Source == source).Target;
            List<TexFrame> frames = Directory.Exists(source)
                ? ArrayFrames(source).Select(ReadImage).ToList()
                : new List<TexFrame> { ReadImage(source) };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target)));
            File.WriteAllBytes(target, TexFile.Write(frames));
        });
        Console.WriteLine($"{jobs.Count} files written to {output}");
        return code;
    }

    private static void CollectPackJobs(string root, string folder, string output, List<(string, string)> jobs)
    {
        foreach (string png in Directory.GetFiles(folder, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string relative = Relative(root, png);
            jobs.Add((png, Path.Combine(output, relative.Substring(0, relative.Length - ".png".Length) + ".tex")));
        }
        foreach (string sub in Directory.GetDirectories(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (IsArray(sub))
                jobs.Add((sub, Path.Combine(output, Relative(root, sub) + ".tex")));
            else
                CollectPackJobs(root, sub, output, jobs);
        }
    }

    private static List<string> ArrayFrames(string folder)
    {
        var frames = Directory.GetFiles(folder, "*.png").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        if (frames.Count == 0)
            throw new InvalidDataException("no .png frames");
        return frames;
    }

    private static TexFrame ReadImage(string png)
    {
        string cfg = png + SettingsSuffix;
        FrameSettings settings = File.Exists(cfg) ? FrameSettings.Parse(File.ReadAllText(cfg), cfg) : new FrameSettings();
        RgbaImage image;
        using (var stream = File.OpenRead(png))
        {
            try
            {
                image = Png.Read(stream);
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"{png}: {e.Message}");
            }
        }
        try
        {
            return Converter.Pack(image, settings);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{png}: {e.Message}");
        }
    }

    // info and check

    private static void Info(string file)
    {
        List<TexFrame> frames = TexFile.Read(File.ReadAllBytes(file));
        Console.WriteLine($"{file}: {frames.Count} frame{(frames.Count == 1 ? "" : "s")}");
        for (int i = 0; i < frames.Count; i++)
        {
            TexFrame f = frames[i];
            string line = $"  {i}: {f.Width}x{f.Height} {f.Format.ToString().ToLowerInvariant()}" +
                $", crop {f.CropX},{f.CropY},{f.CropWidth},{f.CropHeight}" +
                $", offset {f.OffsetX},{f.OffsetY}, crop size {f.CropSizeX},{f.CropSizeY}";
            if (f.ColorblindMode != 0)
                line += $", colourblind mode {f.ColorblindMode}";
            if (f.LogicalWidth != f.Width || f.LogicalHeight != f.Height)
                line += $", logical size {f.LogicalWidth}x{f.LogicalHeight}";
            if (f.PivotX != 0 || f.PivotY != 0)
                line += $", pivot {f.PivotX},{f.PivotY}";
            Console.WriteLine(line);
        }
    }

    /// <summary>
    /// Unpacks each frame to PNG and settings text in memory, reads them back, packs them and
    /// compares the result with the original, then does the same for the written file.
    /// </summary>
    private static int Check(string file)
    {
        List<TexFrame> original = TexFile.Read(File.ReadAllBytes(file));
        var packed = new List<TexFrame>();
        for (int i = 0; i < original.Count; i++)
        {
            var (image, settings) = Converter.Unpack(original[i]);
            var png = new MemoryStream();
            Png.Write(image, png);
            png.Position = 0;
            TexFrame again = Converter.Pack(Png.Read(png), FrameSettings.Parse(settings.ToText(), "settings"));
            List<string> d = original[i].Differences(again);
            if (d.Count > 0)
                throw new InvalidDataException($"frame {i}: {string.Join("; ", d)}");
            packed.Add(again);
        }
        List<TexFrame> reread = TexFile.Read(TexFile.Write(packed));
        for (int i = 0; i < original.Count; i++)
        {
            List<string> d = original[i].Differences(reread[i]);
            if (d.Count > 0)
                throw new InvalidDataException($"frame {i} after writing: {string.Join("; ", d)}");
        }
        return original.Count;
    }

    // paths

    private static string Relative(string root, string path)
    {
        string full = Path.GetFullPath(path), prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length) : Path.GetFileName(full);
    }

    /// <summary>Refuses to write into a game folder anywhere but its mods folder.</summary>
    private static void GuardOutput(string output)
    {
        string full = Path.GetFullPath(output).TrimEnd('\\', '/');
        for (string dir = full; dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (!File.Exists(Path.Combine(dir, "Last Call BBS.exe")))
                continue;
            string relative = full.Length > dir.Length ? full.Substring(dir.Length).TrimStart('\\', '/') : "";
            string first = relative.Split('\\', '/')[0];
            if (!first.Equals("mods", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"{output} is inside the game folder {dir}. Write into a mod's folder (mods\\<Name>\\Content\\...) or somewhere else.");
                Environment.Exit(2);
            }
            return;
        }
    }
}
