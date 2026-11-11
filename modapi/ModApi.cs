using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace LastCallBBS.ModAPI;

/// <summary>A mod's entry point. Each mod DLL contains exactly one class implementing this.</summary>
public interface IMod
{
    /// <summary>Called once, before the game starts. An exception here disables the mod and undoes its patches.</summary>
    void OnLoad(ModContext context);
}

/// <summary>Writes to the console and the log file, labelled with the mod's id.</summary>
public interface IModLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>Thrown when a mod uses something outside the scope declared in its mod.ini.</summary>
public sealed class ModScopeException : Exception
{
    public ModScopeException(string message) : base(message) { }
}

/// <summary>What the loader gives a mod.</summary>
public sealed class ModContext
{
    private readonly Assembly _game;
    private readonly Harmony _harmony;
    private readonly Action<string> _reportProblem;

    public ModContext(string id, string name, string version, string directory, IModLog log, ModConfig config,
        Assembly game, Harmony harmony, ModHooks hooks, ModTextures textures, ModPrograms programs, IReadOnlyCollection<string> scopes, Action<string> reportProblem)
    {
        Textures = textures;
        Programs = programs;
        Id = id;
        Name = name;
        Version = version;
        Directory = directory;
        Log = log;
        Config = config;
        _game = game;
        _harmony = harmony;
        Hooks = hooks;
        Scopes = scopes;
        _reportProblem = reportProblem;
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }

    /// <summary>The mod's own folder, mods/&lt;Name&gt;.</summary>
    public string Directory { get; }

    public IModLog Log { get; }

    /// <summary>Settings from config.ini in the mod's folder. Empty if there is no such file.</summary>
    public ModConfig Config { get; }

    /// <summary>The scopes declared in mod.ini.</summary>
    public IReadOnlyCollection<string> Scopes { get; }

    /// <summary>The loaded game assembly. Only available with scope = full.</summary>
    public Assembly Game => _game ?? throw new ModScopeException("Game needs scope = full in mod.ini");

    /// <summary>This mod's own Harmony instance. Only available with scope = full. Its patches are removed if the mod fails to load.</summary>
    public Harmony Harmony => _harmony ?? throw new ModScopeException("Harmony needs scope = full in mod.ini");

    /// <summary>Named game events that keep working across game updates. Limited to the scopes in mod.ini.</summary>
    public ModHooks Hooks { get; }

    /// <summary>PNG files from the mod's folder, loaded as game textures.</summary>
    public ModTextures Textures { get; }

    /// <summary>Text programs on the desktop. Needs scope = programs (or full).</summary>
    public ModPrograms Programs { get; }

    /// <summary>
    /// Reports that part of the mod couldn't be applied, while the rest stays loaded. The problem
    /// is logged, fails --verify, and appears in the startup warning. To disable the whole mod
    /// instead, throw from OnLoad.
    /// </summary>
    public void ReportProblem(string message) => _reportProblem(message);
}
