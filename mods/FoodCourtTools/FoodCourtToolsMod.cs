using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using LastCallBBS.FoodCourt;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.FoodCourtTools;

/// <summary>
/// Runs solutions on demand. Put a .solution file in this mod's requests folder while the game is
/// running, and a .json result appears in its results folder. The request is then deleted.
/// A .solution file in the edits folder replaces the game's solution of the same file name instead.
/// </summary>
public sealed class FoodCourtToolsMod : IMod
{
    private const int PollFrames = 30;

    private IModLog _log;
    private SimulationRunner _runner;
    private LevelExporter _exporter;
    private SolutionEditor _editor;
    private string _requests, _results, _levels, _edits;
    private bool _wroteFolder;
    private int _frames, _lastTick;
    private bool _waveforms;
    private List<List<FoodCourtTick>> _trace;

    public void OnLoad(ModContext context)
    {
        _log = context.Log;
        _waveforms = context.Config.GetBool("Waveforms", true);
        try
        {
            _runner = new SimulationRunner(context.Game);
        }
        catch (ShapeMismatchException e)
        {
            context.ReportProblem("simulation runner unavailable: " + e.Message);
            return;
        }

        try
        {
            _exporter = new LevelExporter(context.Game);
        }
        catch (ShapeMismatchException e)
        {
            context.ReportProblem("level export unavailable: " + e.Message);
        }

        try
        {
            _editor = new SolutionEditor(context.Game);
        }
        catch (ShapeMismatchException e)
        {
            context.ReportProblem("live editing unavailable: " + e.Message);
        }

        _levels = Path.Combine(context.Directory, "levels");
        _edits = Path.Combine(context.Directory, "edits");
        _requests = Path.Combine(context.Directory, "requests");
        _results = Path.Combine(context.Directory, "results");
        Directory.CreateDirectory(_requests);
        Directory.CreateDirectory(_results);
        Directory.CreateDirectory(_edits);
        context.Hooks.FoodCourt.TickCompleted.Subscribe(OnTick);
        context.Hooks.Game.Update.Subscribe(Poll);
    }

    // Tick numbers restart with each order, which is how the orders in a trace are told apart.
    private void OnTick(FoodCourtTick tick)
    {
        if (_trace == null)
            return;
        if (_trace.Count == 0 || tick.Tick <= _lastTick)
        {
            // The scanner's opening pulse happens before tick 1, so it's added as tick 0.
            _trace.Add(new List<FoodCourtTick>
            {
                new(0, tick.Modules, tick.PreviousSignals, new FoodCourtSignal[0], new FoodCourtProduct[0]),
            });
        }
        _trace[_trace.Count - 1].Add(tick);
        _lastTick = tick.Tick;
    }

    private void Poll()
    {
        if (++_frames % PollFrames != 0)
            return;
        ExportLevelsOnce();
        ApplyEdits();
        string request = Directory.GetFiles(_requests, "*.solution").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (request == null)
            return;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(request);
        }
        catch (IOException)
        {
            return; // still being written; try again next time
        }
        if (bytes.Length == 0)
            return;

        string name = Path.GetFileNameWithoutExtension(request);
        string json = Process(name, bytes);
        WriteAtomically(Path.Combine(_results, name + ".json"), json);
        File.Delete(request);
        _log.Info("Ran " + name);
    }

    private void ApplyEdits()
    {
        if (_editor == null)
            return;
        if (!_wroteFolder)
        {
            // Tells tools/fcedit.ps1 where the game's solution files are.
            WriteAtomically(Path.Combine(_edits, "solutions-folder.txt"), _editor.SolutionsFolder() + "\n");
            _wroteFolder = true;
        }

        foreach (string edit in Directory.GetFiles(_edits, "*.solution").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(edit);
            }
            catch (IOException)
            {
                continue;
            }
            if (bytes.Length == 0)
                continue;

            string target = Path.GetFileNameWithoutExtension(edit);
            EditOutcome outcome;
            try
            {
                outcome = _editor.Apply(target, bytes);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                _log.Error(e.InnerException.ToString());
                outcome = EditOutcome.Rejected("the game threw " + e.InnerException.GetType().Name + ": " + e.InnerException.Message);
            }

            // A waiting edit stays in the folder until it can be applied. It's reported once per request.
            string result = Path.Combine(_edits, target + ".json");
            if (outcome.Status == "waiting" && File.Exists(result))
                continue;
            WriteAtomically(result, ResultJson.Edit(target, outcome));
            if (outcome.Status == "waiting")
                continue;
            File.Delete(edit);
            _log.Info(outcome.Status == "applied" ? $"Applied edit to {outcome.File}" : $"Rejected edit to {target}: {outcome.Message}");
        }
    }

    // Written then renamed, so a reader never sees half a file.
    private static void WriteAtomically(string path, string text)
    {
        string partial = path + ".partial";
        File.WriteAllText(partial, text, new UTF8Encoding(false));
        File.Delete(path);
        File.Move(partial, path);
    }

    // Level data only exists once the game has started, after mods load.
    private void ExportLevelsOnce()
    {
        if (_exporter == null)
            return;
        LevelExporter exporter = _exporter;
        _exporter = null;
        try
        {
            _log.Info($"Exported {exporter.ExportAll(_levels, _log)} level documents.");
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is TargetInvocationException)
        {
            _log.Error("Level export failed: " + e);
        }
    }

    private string Process(string name, byte[] bytes)
    {
        SolutionFile saved;
        try
        {
            saved = SolutionFile.Read(bytes);
        }
        catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is ArgumentException)
        {
            return ResultJson.Failure(name, "can't read the solution file: " + e.Message);
        }

        _trace = new List<List<FoodCourtTick>>();
        _lastTick = -1;
        try
        {
            RunOutcome outcome = _runner.Run(bytes);
            if (_waveforms)
            {
                for (int i = 0; i < _trace.Count; i++)
                    File.WriteAllText(Path.Combine(_results, $"{name}.order{i + 1}.vcd"), Waveform.Format(_trace[i]), new UTF8Encoding(false));
            }
            return ResultJson.Success(name, saved, outcome, _trace);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            _log.Error(e.InnerException.ToString());
            return ResultJson.Failure(name, "the game threw " + e.InnerException.GetType().Name + ": " + e.InnerException.Message);
        }
        finally
        {
            _trace = null;
        }
    }
}
