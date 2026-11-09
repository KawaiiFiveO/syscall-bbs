using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>
/// Makes mods/&lt;Name&gt;/Content/... replace the game's Content/... while the game runs. Nothing is
/// written to the game folder. Managed reads all pass through FileStream; sounds and PNGs are
/// opened by native libraries, so those calls are redirected where the game makes them.
/// </summary>
internal static class ContentOverlay
{
    private const string FolderName = "Content";
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    // Filled before the patches go on and only read afterwards, so asset threads can share it.
    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
    private static string _root;
    private static Func<string, IntPtr, int> _openSound;
    private static Func<string, IntPtr> _openImage;

    // Harmony reruns a transpiler whenever that method is patched again, so this has to keep every redirect.
    private static readonly Dictionary<MethodInfo, MethodInfo> NativeRedirects = new();

    public static bool HasContent(string modFolder) => Directory.Exists(Path.Combine(modFolder, FolderName));

    public static void Add(string id, string modFolder)
    {
        string cwd = Directory.GetCurrentDirectory();
        _root = cwd.Replace('\\', '/').TrimEnd('/') + "/";
        string content = Path.Combine(modFolder, FolderName);
        foreach (string file in Directory.GetFiles(content, "*", SearchOption.AllDirectories))
        {
            string key = FolderName + "/" + file.Substring(content.Length + 1).Replace('\\', '/');
            if (Files.ContainsKey(key))
                Log.Warn($"{id} replaces {key}, which an earlier mod also replaced.");
            if (!File.Exists(Path.Combine(cwd, key)))
                Log.Warn($"{id}: {key} doesn't match any game file.");
            Files[key] = Relative(file, cwd);
        }
    }

    // ov_fopen takes an ANSI path, which a relative path keeps away from non-ASCII folder names.
    private static string Relative(string file, string cwd)
    {
        string prefix = cwd.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? file.Substring(prefix.Length) : file;
    }

    /// <summary>Installs the redirects. Returns one message per part that couldn't be applied.</summary>
    public static List<string> Apply(Assembly game)
    {
        var problems = new List<string>();
        if (Files.Count == 0)
            return problems;

        var harmony = new Harmony("modloader.content");
        Try(problems, "file redirect", () =>
        {
            MethodInfo init = CodeShape.Single(typeof(FileStream).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Where(m =>
            {
                string[] names = m.GetParameters().Select(p => p.Name).ToArray();
                return m.Name == "Init" && names.Contains("path") && names.Contains("mode") && names.Contains("access");
            }), "FileStream.Init");
            harmony.Patch(init, prefix: new HarmonyMethod(typeof(ContentOverlay), nameof(FileStreamPrefix)));
        });

        Type[] types = LoadableTypes(game);
        if (Files.Keys.Any(k => k.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)))
        {
            Try(problems, "sound redirect", () =>
            {
                MethodInfo open = FindExtern(types, "ov_fopen");
                _openSound = (Func<string, IntPtr, int>)Delegate.CreateDelegate(typeof(Func<string, IntPtr, int>), open);
                Redirect(harmony, types, open, nameof(OpenSound));
            });
        }
        if (Files.Keys.Any(k => ImageExtensions.Any(e => k.EndsWith(e, StringComparison.OrdinalIgnoreCase))))
        {
            Try(problems, "image redirect", () =>
            {
                // The string overload is a small wrapper around the import that takes bytes.
                MethodInfo open = (MethodInfo)CodeShape.Single(CallersOf(types, FindExtern(types, "IMG_Load")).Where(m =>
                    m.IsStatic && m is MethodInfo i && i.ReturnType == typeof(IntPtr) &&
                    m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)), "IMG_Load wrapper");
                _openImage = (Func<string, IntPtr>)Delegate.CreateDelegate(typeof(Func<string, IntPtr>), open);
                Redirect(harmony, types, open, nameof(OpenImage));
            });
        }

        if (problems.Count == 0)
            Log.Info($"Content overlay: {Files.Count} file(s) replaced.");
        return problems;
    }

    /// <summary>For --verify: confirms a replaced file really is what the game would read.</summary>
    public static List<string> Verify()
    {
        var problems = new List<string>();
        if (Files.Count == 0)
            return problems;
        KeyValuePair<string, string> sample = Files.First();
        byte[] expected = ReadDirect(sample.Value);
        using (var stream = File.OpenRead(sample.Key))
        {
            var read = new byte[expected.Length];
            int count = stream.Read(read, 0, read.Length);
            if (count != expected.Length || !read.SequenceEqual(expected))
                problems.Add("Content overlay: OpenRead didn't return the mod's file");
        }
        if (!File.ReadAllBytes(sample.Key).SequenceEqual(expected))
            problems.Add("Content overlay: ReadAllBytes didn't return the mod's file");
        if (problems.Count == 0)
            Log.Info("Content overlay: redirect verified.");
        return problems;
    }

    // The mod's own path isn't under a redirected name, so this reads it untouched.
    private static byte[] ReadDirect(string path) => File.ReadAllBytes(path);

    private static void Try(List<string> problems, string what, Action action)
    {
        try
        {
            action();
            Log.Info($"Content overlay: {what} applied.");
        }
        catch (Exception e)
        {
            Log.Error($"Content overlay: {what} failed: {e}");
            problems.Add($"Content overlay: {what}: {e.Message}");
        }
    }

    private static Type[] LoadableTypes(Assembly game)
    {
        try
        {
            return game.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t != null).ToArray();
        }
    }

    private static MethodInfo FindExtern(Type[] types, string entryPoint) => CodeShape.Single(
        types.SelectMany(t => t.GetMethods(Declared)).Where(m =>
            (m.Attributes & MethodAttributes.PinvokeImpl) != 0 &&
            m.GetCustomAttributes(typeof(DllImportAttribute), false).Cast<DllImportAttribute>().Any(a => a.EntryPoint == entryPoint)),
        "native import " + entryPoint);

    // Comparing raw IL for "call <token>" keeps this quick across the whole game.
    private static List<MethodBase> CallersOf(Type[] types, MethodInfo target)
    {
        byte[] token = BitConverter.GetBytes(target.MetadataToken);
        var callers = new List<MethodBase>();
        foreach (Type type in types)
        {
            IEnumerable<MethodBase> methods = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared));
            foreach (MethodBase method in methods)
            {
                if (method.IsAbstract || (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                    continue;
                byte[] il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null)
                    continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] == OpCodes.Call.Value && il[i + 1] == token[0] && il[i + 2] == token[1] && il[i + 3] == token[2] && il[i + 4] == token[3])
                    {
                        callers.Add(method);
                        break;
                    }
                }
            }
        }
        return callers;
    }

    private static void Redirect(Harmony harmony, Type[] types, MethodInfo target, string replacement)
    {
        List<MethodBase> callers = CallersOf(types, target);
        if (callers.Count == 0)
            throw new ShapeMismatchException("nothing in the game calls " + target.Name);
        NativeRedirects[target] = typeof(ContentOverlay).GetMethod(replacement, BindingFlags.NonPublic | BindingFlags.Static);
        var transpiler = new HarmonyMethod(typeof(ContentOverlay), nameof(ReplaceCall));
        foreach (MethodBase caller in callers)
            harmony.Patch(caller, transpiler: transpiler);
    }

    private static IEnumerable<CodeInstruction> ReplaceCall(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction i in instructions)
        {
            if (i.operand is MethodInfo called && NativeRedirects.TryGetValue(called, out MethodInfo replacement))
                i.operand = replacement;
            yield return i;
        }
    }

    private static void FileStreamPrefix(ref string path, FileMode mode, FileAccess access)
    {
        if (mode == FileMode.Open && access == FileAccess.Read)
            path = Resolve(path);
    }

    private static int OpenSound(string path, IntPtr file) => _openSound(Resolve(path), file);

    private static IntPtr OpenImage(string path) => _openImage(Resolve(path));

    private static string Resolve(string path)
    {
        if (path == null || path.IndexOf(FolderName, StringComparison.OrdinalIgnoreCase) < 0)
            return path;
        string key = path.Replace('\\', '/');
        if (key.Length > 1 && (key[1] == ':' || key[0] == '/'))
        {
            if (!key.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                return path;
            key = key.Substring(_root.Length);
        }
        while (key.StartsWith("./", StringComparison.Ordinal))
            key = key.Substring(2);
        return Files.TryGetValue(key, out string target) ? target : path;
    }
}
