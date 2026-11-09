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
/// </summary>
public sealed class FoodCourtToolsMod : IMod
{
    private const int PollFrames = 30;

    private IModLog _log;
    private SimulationRunner _runner;
    private LevelExporter _exporter;
    private string _requests, _results, _levels;
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

        _levels = Path.Combine(context.Directory, "levels");
        _requests = Path.Combine(context.Directory, "requests");
        _results = Path.Combine(context.Directory, "results");
        Directory.CreateDirectory(_requests);
        Directory.CreateDirectory(_results);
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
        // Written then renamed, so a result can never be read half-finished.
        string result = Path.Combine(_results, name + ".json");
        string partial = result + ".partial";
        File.WriteAllText(partial, json, new UTF8Encoding(false));
        File.Delete(result);
        File.Move(partial, result);
        File.Delete(request);
        _log.Info("Ran " + name);
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
