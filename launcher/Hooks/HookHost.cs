using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>
/// Owns every hook. A hook patches the game only when something first subscribes to it,
/// so a game update that breaks one hook affects only the mods using that hook.
/// </summary>
internal static class HookHost
{
    private const int MaxHandlerErrors = 3;
    private static readonly Dictionary<string, Point> Points = new();

    public static Assembly Game { get; private set; }
    public static Harmony Harmony { get; private set; }

    public static void Init(Assembly game)
    {
        Game = game;
        Harmony = new Harmony("modloader.hooks");
        CoreHookPoints.Define();
        TerminalHookPoints.Define();
        FoodCourtHookPoints.Define();
    }

    public static void Define(string name, Action attach) => Points.Add(name, new Point(name, attach));

    /// <summary>Attaches every hook and returns one message per hook that failed.</summary>
    public static List<string> VerifyAll()
    {
        var failures = new List<string>();
        foreach (Point point in Points.Values)
        {
            try
            {
                Attach(point);
                Log.Info($"Hook {point.Name}: OK");
            }
            catch (Exception e)
            {
                Log.Error($"Hook {point.Name}: FAILED - {e.Message}");
                failures.Add($"hook {point.Name}: {e.Message}");
            }
        }
        return failures;
    }

    public static void Subscribe<T>(string hook, string owner, IModLog log, Action<T> handler)
    {
        Point point = Points[hook];
        Attach(point);
        point.Handlers = point.Handlers.Append(new Handler(owner, log, args => handler((T)args))).ToArray();
    }

    /// <summary>Calls a hook's handlers. Builds the arguments only if someone is listening.</summary>
    public static void Raise(string hook, Func<object> makeArgs = null)
    {
        Point point = Points[hook];
        if (point.Handlers.Length == 0)
            return;

        object args;
        try
        {
            args = makeArgs?.Invoke();
        }
        catch (Exception e)
        {
            // Never let a hook take the game down. One report is enough.
            if (!point.ReportedArgsError)
                Log.Error($"Hook {hook}: could not read game state: {e}");
            point.ReportedArgsError = true;
            return;
        }

        foreach (Handler handler in point.Handlers)
        {
            try
            {
                handler.Call(args);
            }
            catch (Exception e)
            {
                handler.Log.Error($"{hook} handler threw: {e}");
                if (++handler.Errors >= MaxHandlerErrors)
                {
                    handler.Log.Error($"{hook} handler removed after {MaxHandlerErrors} errors.");
                    point.Handlers = point.Handlers.Where(h => h != handler).ToArray();
                }
            }
        }
    }

    private static void Attach(Point point)
    {
        if (point.Failure != null)
            throw new ShapeMismatchException($"hook {point.Name} is unavailable: {point.Failure}");
        if (point.Attached)
            return;
        try
        {
            point.AttachAction();
            point.Attached = true;
        }
        catch (Exception e)
        {
            point.Failure = e.Message;
            throw new ShapeMismatchException($"hook {point.Name} is unavailable: {e.Message}");
        }
    }

    /// <summary>Gives one mod access to the hooks, so errors are logged against that mod.</summary>
    public sealed class Source : IHookSource
    {
        private readonly string _owner;
        private readonly IModLog _log;
        private readonly ISet<string> _scopes;

        public Source(string owner, IModLog log, ISet<string> scopes)
        {
            _owner = owner;
            _log = log;
            _scopes = scopes;
        }

        public void Subscribe<T>(string hook, Action<T> handler)
        {
            string needed = ModScopes.OfHook(hook);
            if (!_scopes.Contains(ModScopes.Full) && !_scopes.Contains(needed))
                throw new ModScopeException($"{hook} needs \"{needed}\" in the scope line of mod.ini");
            HookHost.Subscribe(hook, _owner, _log, handler);
        }
    }

    private sealed class Point
    {
        public Point(string name, Action attach)
        {
            Name = name;
            AttachAction = attach;
        }

        public string Name { get; }
        public Action AttachAction { get; }
        public bool Attached;
        public string Failure;
        public bool ReportedArgsError;
        public Handler[] Handlers = new Handler[0];
    }

    private sealed class Handler
    {
        public Handler(string owner, IModLog log, Action<object> call)
        {
            Owner = owner;
            Log = log;
            Call = call;
        }

        public string Owner { get; }
        public IModLog Log { get; }
        public Action<object> Call { get; }
        public int Errors;
    }
}

/// <summary>Logger for hooks that the launcher itself subscribes to.</summary>
internal sealed class LauncherLog : IModLog
{
    public static readonly LauncherLog Instance = new();
    public void Info(string message) => Log.Info(message);
    public void Warn(string message) => Log.Warn(message);
    public void Error(string message) => Log.Error(message);
}
