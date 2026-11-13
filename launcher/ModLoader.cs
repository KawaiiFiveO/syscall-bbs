using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>Finds mods in mods/&lt;Name&gt;/ and runs them. One mod failing never stops the others.</summary>
internal static class ModLoader
{
    private const string ManifestName = "mod.ini";
    private const string ConfigName = "config.ini";
    private const int MaxProblemsShown = 5;
    private static readonly Regex ValidId = new("^[a-z0-9][a-z0-9._-]*$");
    private static readonly List<string> Folders = new();

    /// <summary>Loads every mod and returns one message per failure.</summary>
    public static List<string> LoadAll(string modsDir, Assembly game, Settings settings)
    {
        var failures = new List<string>();
        if (!Directory.Exists(modsDir))
        {
            Log.Info($"No mods folder at {modsDir}.");
            return failures;
        }

        // Registered first so a mod can find a dependency in any mod's folder.
        Folders.AddRange(Directory.GetDirectories(modsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
        Log.Info($"Found {Folders.Count} mod folder(s) in {modsDir}.");

        var loaded = new HashSet<string>();
        foreach (Entry entry in Sort(ReadManifests(failures), failures))
        {
            try
            {
                Load(entry, game, settings, loaded, failures);
                loaded.Add(entry.Id);
                if (ContentOverlay.HasContent(entry.Folder))
                    ContentOverlay.Add(entry.Id, entry.Folder);
            }
            catch (Exception e)
            {
                Fail(entry.Label, e, failures);
            }
        }
        failures.AddRange(ContentOverlay.Apply(game));
        return failures;
    }

    private static void Fail(string label, Exception e, List<string> failures)
    {
        Exception cause = Unwrap(e);
        Log.Error($"{label}: failed to load: {Describe(cause)}");
        failures.Add($"{label}: {cause.GetType().Name}: {cause.Message}");
    }

    private static List<Entry> ReadManifests(List<string> failures)
    {
        var entries = new List<Entry>();
        var ids = new HashSet<string>();
        foreach (string folder in Folders)
        {
            string label = Path.GetFileName(folder);
            try
            {
                string manifestPath = Path.Combine(folder, ManifestName);
                if (!File.Exists(manifestPath))
                    throw new InvalidDataException($"missing {ManifestName}");
                ModConfig manifest = ModConfig.Load(manifestPath);
                string id = manifest.GetString("id");
                if (!ValidId.IsMatch(id))
                    throw new InvalidDataException($"{ManifestName} needs an id of lowercase letters, digits, '.', '_' or '-', got \"{id}\"");
                if (!ids.Add(id))
                    throw new InvalidDataException($"another mod already uses the id \"{id}\"");
                entries.Add(new Entry(folder, label, id, manifest));
            }
            catch (Exception e)
            {
                Fail(label, e, failures);
            }
        }
        return entries;
    }

    // Alphabetical by folder, except that a mod always comes after the mods it names.
    private static List<Entry> Sort(List<Entry> entries, List<string> failures)
    {
        var present = new HashSet<string>(entries.Select(e => e.Id));
        var placed = new HashSet<string>();
        var sorted = new List<Entry>();
        var waiting = new List<Entry>(entries);
        while (waiting.Count > 0)
        {
            Entry next = waiting.FirstOrDefault(e => e.Requires.Concat(e.After).All(d => !present.Contains(d) || placed.Contains(d)));
            if (next == null)
                break;
            waiting.Remove(next);
            placed.Add(next.Id);
            sorted.Add(next);
        }
        foreach (Entry entry in waiting)
            Fail(entry.Label, new InvalidDataException("its load order depends on itself: " + string.Join(", ", waiting.Select(e => e.Id))), failures);
        return sorted;
    }

    /// <summary>Finds an assembly in the requesting mod's folder, then in every other mod's.</summary>
    public static Assembly Resolve(string simpleName, Assembly requesting)
    {
        string own = requesting != null && !requesting.IsDynamic && requesting.Location.Length > 0
            ? Path.GetDirectoryName(requesting.Location)
            : null;
        IEnumerable<string> search = Folders.Where(f => string.Equals(f, own, StringComparison.OrdinalIgnoreCase))
            .Concat(Folders);
        foreach (string folder in search)
        {
            string path = Path.Combine(folder, simpleName + ".dll");
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }
        return null;
    }

    private static void Load(Entry entry, Assembly game, Settings settings, HashSet<string> loaded, List<string> failures)
    {
        string folder = entry.Folder, id = entry.Id;
        ModConfig manifest = entry.Manifest;
        foreach (string required in entry.Requires)
        {
            if (!loaded.Contains(required))
                throw new InvalidDataException($"requires \"{required}\", which isn't installed or failed to load");
        }

        string name = manifest.GetString("name", id);
        string version = manifest.GetString("version", "0.0.0");
        var log = new ModLog(id);

        HashSet<string> scopes = ReadScopes(manifest);
        bool full = scopes.Contains(ModScopes.Full);
        if (ContentOverlay.HasContent(folder) && !full && !scopes.Contains(ModScopes.Content))
            throw new InvalidDataException($"has a Content folder, so it needs scope = content");

        // A mod that only replaces game files has no code to run.
        if (scopes.Count == 1 && scopes.Contains(ModScopes.Content) && Directory.GetFiles(folder, "*.dll").Length == 0)
        {
            Log.Info($"Loaded {name} {version} ({id}).");
            return;
        }

        string dll = FindDll(folder, entry.Label, manifest.GetString("dll"));
        if (full)
            Log.Warn($"{name} has full access to the game (scope = full).");
        else
            CheckScope(folder, game, settings, id);

        Assembly assembly = Assembly.LoadFrom(dll);
        IMod mod = CreateEntryPoint(assembly);

        Harmony harmony = full ? new Harmony("mod." + id) : null;
        var context = new ModContext(id, name, version, folder, log,
            ModConfig.Load(Path.Combine(folder, ConfigName), log.Warn), full ? game : null, harmony,
            new ModHooks(new HookHost.Source(id, log, scopes)), new ModTextures(folder, RuntimeTextures.Source, full),
            new ModPrograms(DesktopPrograms.Host, id, log, full || scopes.Contains(ModScopes.Programs)), new ModSounds(GameSounds.Host, log), scopes.ToList(),
            problem =>
            {
                log.Error(problem);
                failures.Add($"{id}: {problem}");
            });
        try
        {
            mod.OnLoad(context);
        }
        catch
        {
            harmony?.UnpatchAll(harmony.Id);
            throw;
        }
        Log.Info($"Loaded {name} {version} ({id}).");
    }

    private static HashSet<string> ReadScopes(ModConfig manifest)
    {
        var scopes = new HashSet<string>(manifest.GetString("scope").Split(',')
            .Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0));
        if (scopes.Count == 0)
            throw new InvalidDataException($"{ManifestName} needs a scope line, such as: scope = foodcourt");
        string unknown = scopes.FirstOrDefault(s => !ModScopes.Known.Contains(s));
        if (unknown != null)
            throw new InvalidDataException($"unknown scope \"{unknown}\"; use {string.Join(", ", ModScopes.Known)}");
        return scopes;
    }

    private static void CheckScope(string folder, Assembly game, Settings settings, string id)
    {
        List<string> problems = ModScanner.Scan(folder, game.GetName().Name);
        if (problems.Count == 0)
            return;
        string details = string.Join("; ", problems.Take(MaxProblemsShown));
        if (problems.Count > MaxProblemsShown)
            details += $"; and {problems.Count - MaxProblemsShown} more";

        if (!settings.AllowsUnscoped(id))
            throw new InvalidDataException($"blocked because it reaches outside its scope: {details}. " +
                                           $"If you trust it, set AllowUnscoped.{id} = true in the launcher's ini.");
        Log.Warn($"{id} reaches outside its scope, allowed by AllowUnscoped: {details}");
    }

    private static string FindDll(string folder, string folderName, string configured)
    {
        if (configured.Length > 0)
        {
            if (configured != Path.GetFileName(configured))
                throw new InvalidDataException("dll in mod.ini must be a file name inside the mod's folder");
            string path = Path.Combine(folder, configured);
            return File.Exists(path) ? path : throw new FileNotFoundException($"{configured} not found in the mod's folder");
        }

        string named = Path.Combine(folder, folderName + ".dll");
        if (File.Exists(named))
            return named;
        string[] dlls = Directory.GetFiles(folder, "*.dll");
        return dlls.Length == 1
            ? dlls[0]
            : throw new InvalidDataException($"can't tell which DLL is the mod ({dlls.Length} found); set dll in mod.ini");
    }

    private static IMod CreateEntryPoint(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            Exception first = e.LoaderExceptions.FirstOrDefault(x => x != null);
            throw new TypeLoadException($"could not load the mod's types: {first?.Message}", e);
        }

        Type[] entries = types.Where(t => t.IsClass && !t.IsAbstract && typeof(IMod).IsAssignableFrom(t)).ToArray();
        if (entries.Length != 1)
            throw new InvalidDataException($"expected exactly one IMod class, found {entries.Length}");
        return (IMod)Activator.CreateInstance(entries[0]);
    }

    private static Exception Unwrap(Exception e) =>
        e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;

    private static string Describe(Exception e) => e.ToString();

    private sealed class Entry
    {
        public Entry(string folder, string label, string id, ModConfig manifest)
        {
            Folder = folder;
            Label = label;
            Id = id;
            Manifest = manifest;
            Requires = List(manifest, "requires");
            After = List(manifest, "after");
        }

        public string Folder { get; }
        public string Label { get; }
        public string Id { get; }
        public ModConfig Manifest { get; }
        public List<string> Requires { get; }
        public List<string> After { get; }

        private static List<string> List(ModConfig manifest, string key) =>
            manifest.GetString(key).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    private sealed class ModLog : IModLog
    {
        private readonly string _id;
        public ModLog(string id) => _id = id;
        public void Info(string message) => Log.Write("INFO", _id, message);
        public void Warn(string message) => Log.Write("WARN", _id, message);
        public void Error(string message) => Log.Write("ERROR", _id, message);
    }
}
