using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LastCallBBS.FoodCourt;

internal static class Program
{
    private const string Usage =
        "Usage:\n" +
        "  fctext <file.solution or folder>...           print solutions as text\n" +
        "  fctext <file.solution> -o <file.txt>          write one solution to a text file\n" +
        "  fctext --check <file.solution or folder>...   confirm files are fully understood\n" +
        "  fctext --import <text file> <new.solution>    write a new solution from text\n" +
        "  fctext --check-placement <solution, text file or folder>...   apply the game's placement rules";

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--import")
            return args.Length == 3 ? Import(args[1], args[2]) : Fail(Usage);

        if (args.Length > 0 && args[0] == "--check-placement")
            return args.Length > 1 ? CheckPlacement(args.Skip(1).SelectMany(Expand).ToList()) : Fail(Usage);

        if (args.Length == 3 && args[1] == "-o")
            return Export(args[0], args[2]);

        bool check = args.Contains("--check");
        List<string> files = args.Where(a => a != "--check").SelectMany(Expand).ToList();
        if (files.Count == 0)
            return Fail(Usage);

        int failures = 0;
        foreach (string file in files)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(file);
                SolutionFile solution = SolutionFile.Read(bytes);
                if (check)
                    failures += Check(file, bytes, solution) ? 0 : 1;
                else
                    Console.Write(SolutionText.Format(solution));
            }
            catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is IOException)
            {
                Console.Error.WriteLine($"{file}: {e.Message}");
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static IEnumerable<string> Expand(string path) =>
        Directory.Exists(path) ? Directory.GetFiles(path, "*.solution").OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : new[] { path };

    private static int CheckPlacement(List<string> files)
    {
        int failures = 0;
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            try
            {
                SolutionFile solution = file.EndsWith(".solution", StringComparison.OrdinalIgnoreCase)
                    ? SolutionFile.Read(File.ReadAllBytes(file))
                    : SolutionParser.Parse(File.ReadAllText(file, Encoding.UTF8));
                List<string> problems = PlacementChecker.Check(solution);
                Console.WriteLine($"{(problems.Count == 0 ? "ok  " : "FAIL")}  {name}");
                foreach (string problem in problems)
                    Console.WriteLine($"      {problem}");
                failures += problems.Count == 0 ? 0 : 1;
            }
            catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is IOException || e is SolutionFormatException)
            {
                Console.Error.WriteLine($"{file}: {e.Message}");
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static int Export(string solutionPath, string textPath)
    {
        try
        {
            string text = SolutionText.Format(SolutionFile.Read(File.ReadAllBytes(solutionPath)));
            File.WriteAllText(textPath, text, new UTF8Encoding(false));
            Console.WriteLine($"Wrote {textPath}");
            return 0;
        }
        catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is IOException)
        {
            Console.Error.WriteLine($"{solutionPath}: {e.Message}");
            return 1;
        }
    }

    private static int Import(string textPath, string outputPath)
    {
        if (!outputPath.EndsWith(".solution", StringComparison.OrdinalIgnoreCase))
            return Fail("The output file must end in .solution");

        SolutionFile solution;
        try
        {
            solution = SolutionParser.Parse(File.ReadAllText(textPath, Encoding.UTF8));
        }
        catch (Exception e) when (e is SolutionFormatException || e is IOException)
        {
            Console.Error.WriteLine($"{textPath}: {e.Message}");
            return 1;
        }

        List<string> problems = SolutionValidator.Validate(solution);
        if (problems.Count > 0)
        {
            foreach (string problem in problems)
                Console.Error.WriteLine($"{textPath}: {problem}");
            return 1;
        }

        // A text file can't claim a result. The game fills these in when the solution is run.
        solution.Solved = false;
        solution.Ticks = 0;
        solution.Cost = 0;

        try
        {
            byte[] bytes = solution.Write();
            using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"{outputPath}: {e.Message}");
            return 1;
        }
        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    private static bool Check(string file, byte[] bytes, SolutionFile solution)
    {
        var problems = new List<string>();
        if (!solution.Write().SequenceEqual(bytes))
            problems.Add("rewriting it doesn't reproduce the file");
        if (solution.Solved && solution.ComputeCost() != solution.Cost)
            problems.Add($"catalog cost {solution.ComputeCost()} differs from saved cost {solution.Cost}");
        problems.AddRange(SolutionValidator.Validate(solution));

        string text = SolutionText.Format(solution);
        try
        {
            // Text always has the output end of a wire first, so compare against that form.
            var expected = SolutionFile.Read(bytes);
            foreach (SolutionWire w in expected.Wires.Where(w => SolutionText.InputFirst(expected, w)))
            {
                (w.ModuleA, w.ModuleB) = (w.ModuleB, w.ModuleA);
                (w.SocketA, w.SocketB) = (w.SocketB, w.SocketA);
            }
            if (!SolutionParser.Parse(text).Write().SequenceEqual(expected.Write()))
                problems.Add("the text form doesn't convert back to the same solution");
        }
        catch (SolutionFormatException e)
        {
            problems.Add("the text form can't be read back: " + e.Message);
        }

        string name = Path.GetFileName(file);
        if (problems.Count == 0)
            Console.WriteLine($"ok    {name} ({solution.Modules.Count} modules, {solution.Wires.Count} wires, {text.Length} chars)");
        else
            Console.WriteLine($"FAIL  {name}: {string.Join("; ", problems)}");
        return problems.Count == 0;
    }
}
