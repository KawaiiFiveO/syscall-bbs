using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

namespace LastCallBBS.Launcher;

/// <summary>
/// Loads "Last Call BBS.exe" into this process, loads mods, then runs the game's own entry
/// point. No game files are modified.
/// </summary>
internal static class Program
{
    private const string GameExeName = "Last Call BBS.exe";
    private static readonly string[] LauncherFlags = { "--verify", "--console" };

    private static string _gameDir;

    private static int Main(string[] args)
    {
        string launcherDir = AppDomain.CurrentDomain.BaseDirectory;
        Log.Open(Path.Combine(launcherDir, "LastCallBBS.Launcher.log"));

        // --verify: load and patch, report, but don't start the game.
        bool verifyOnly = args.Contains("--verify", StringComparer.OrdinalIgnoreCase);
        bool forceConsole = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
        string[] gameArgs = args.Where(a => !LauncherFlags.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();

        Settings settings;
        Assembly game;
        try
        {
            settings = Settings.Load(Path.Combine(launcherDir, "LastCallBBS.Launcher.ini"));

            // --verify is run by install.ps1, which reads the log file instead.
            if (forceConsole || (settings.ShowConsole && !verifyOnly))
                ConsoleWindow.Open();
            Log.ConsoleReady();

            _gameDir = ResolveGameDir(launcherDir, settings.GameDirectory);
            Log.Info($"Game directory: {_gameDir}");

            // The game looks for its Content folder relative to the working directory, and
            // loads SDL2 etc. by P/Invoke, so both must point at the game folder.
            Directory.SetCurrentDirectory(_gameDir);
            SetDllDirectory(_gameDir);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

            game = Assembly.LoadFrom(Path.Combine(_gameDir, GameExeName));
            Log.Info($"Loaded {game.FullName} (MVID {game.ManifestModule.ModuleVersionId})");
        }
        catch (Exception e)
        {
            Log.ConsoleReady();
            Log.Error("Could not load the game: " + e);
            Dialog.Error($"Could not load the game.\n\n{e.Message}\n\nSee LastCallBBS.Launcher.log for details.");
            return 1;
        }

        // Applied before any mod runs. Without it a modded score could reach the leaderboards,
        // so unlike a mod failure, this one stops the game from starting.
        string guardError = SteamGuard.Apply(game, settings);
        if (guardError != null && !verifyOnly)
        {
            Dialog.Error("Could not disable Steam leaderboards, so the modded game won't start.\n\n" +
                         guardError + "\n\nSee LastCallBBS.Launcher.log for details.");
            Log.Close();
            return 3;
        }

        HookHost.Init(game);
        List<string> failures = ModLoader.LoadAll(Path.Combine(launcherDir, "mods"), game, settings);
        if (guardError != null)
            failures.Add("Steam guard: " + guardError);

        if (verifyOnly)
        {
            failures.AddRange(HookHost.VerifyAll());
            failures.AddRange(ContentOverlay.Verify());
            failures.AddRange(RuntimeTextures.Verify());
            failures.AddRange(DesktopPrograms.Verify());
            Log.Info(failures.Count == 0 ? "Verify: all mods and hooks are working." : "Verify: some mods or hooks failed.");
            Log.Close();
            return failures.Count == 0 ? 0 : 2;
        }

        if (failures.Count > 0)
        {
            bool cont = Dialog.YesNo(
                "Some mods could not be loaded (the game may have been updated):\n\n  - " +
                string.Join("\n  - ", failures) +
                "\n\nStart the game anyway, without them?");
            if (!cont)
            {
                Log.Close();
                return 2;
            }
        }

        Log.Info("Starting game.");
        Log.Flush();
        try
        {
            MethodInfo entry = game.EntryPoint;
            object[] invokeArgs = entry.GetParameters().Length == 0 ? null : new object[] { gameArgs };
            entry.Invoke(null, invokeArgs);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            Log.Error("Game crashed: " + e.InnerException);
            Log.Close();
            ConsoleWindow.WaitForEnter();
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
        Log.Close();
        return 0;
    }

    private static string ResolveGameDir(string launcherDir, string configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"')));
        candidates.Add(launcherDir);

        foreach (string dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, GameExeName)))
                return Path.GetFullPath(dir);
        }
        throw new FileNotFoundException(
            $"Could not find \"{GameExeName}\". Put this launcher in the game folder, " +
            "or set GameDirectory in LastCallBBS.Launcher.ini.");
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;

        // Includes the game itself, whose assembly name differs from its file name.
        Assembly loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        if (loaded != null)
            return loaded;

        foreach (string ext in new[] { ".dll", ".exe" })
        {
            string path = Path.Combine(_gameDir, name + ext);
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }
        return ModLoader.Resolve(name, args.RequestingAssembly);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
}
