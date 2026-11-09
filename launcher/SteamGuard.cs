using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>
/// Steam-only behaviour for modded runs. It only patches this process, so starting the game
/// normally is unaffected, and it does nothing on builds without Steam integration.
/// Steamworks' public names aren't obfuscated; game code is found by shape.
/// </summary>
internal static class SteamGuard
{
    private const string SteamworksAssembly = "Steamworks.NET";
    private const string HarmonyId = "modloader.steamguard";
    private const string AppId = "1511780";

    private static MethodInfo _restart, _getSteamId;
    private static string _saveFolder;

    /// <summary>Applies the patches and returns an error message, or null if all is well.</summary>
    public static string Apply(Assembly game, Settings settings)
    {
        if (!game.GetReferencedAssemblies().Any(a => a.Name == SteamworksAssembly))
        {
            Log.Info("Steam guard: this build has no Steam integration.");
            return null;
        }

        try
        {
            Assembly steamworks = Assembly.Load(SteamworksAssembly);
            Type stats = Require(steamworks, "Steamworks.SteamUserStats");
            Type gameLogic = CodeShape.RequireType(game, "GameLogic");
            var harmony = new Harmony(HarmonyId);

            SkipRelaunch(harmony, gameLogic, Require(steamworks, "Steamworks.SteamAPI"));

            MethodInfo[] leaderboard = stats.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.Contains("Leaderboard") && m.ReturnType.Name == "SteamAPICall_t").ToArray();
            foreach (string needed in new[] { "FindOrCreateLeaderboard", "UploadLeaderboardScore" })
            {
                if (leaderboard.All(m => m.Name != needed))
                    throw new MissingMethodException(needed);
            }
            foreach (MethodInfo method in leaderboard)
                Block(harmony, method);
            Log.Info($"Steam guard: leaderboards blocked ({leaderboard.Length} calls).");

            Toggle(harmony, settings.Achievements, "Achievements", stats, "SetAchievement", "StoreStats");
            Toggle(harmony, settings.RichPresence, "Rich Presence", Require(steamworks, "Steamworks.SteamFriends"), "SetRichPresence");

            if (settings.SaveFolder.Length > 0)
                RedirectSaves(harmony, gameLogic, Require(steamworks, "Steamworks.SteamUser"), settings.SaveFolder);

            // Cosmetic, so a game update breaking it shouldn't stop the game.
            if (settings.LocalScoreList)
            {
                try
                {
                    LocalScores.Install(harmony, game);
                    Log.Info("Steam guard: local score list on.");
                }
                catch (Exception e)
                {
                    Log.Warn("Steam guard: local score list unavailable: " + e.Message);
                }
            }
            return null;
        }
        catch (Exception e)
        {
            Log.Error("Steam guard FAILED: " + e);
            return e.Message;
        }
    }

    // ---- Skip the relaunch through Steam.
    //
    // RestartAppIfNecessary is small enough for the JIT to inline, which would bypass a patch
    // on it, so the game's own call is rewritten instead.

    private static void SkipRelaunch(Harmony harmony, Type gameLogic, Type steamApi)
    {
        _restart = steamApi.GetMethod("RestartAppIfNecessary", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("SteamAPI", "RestartAppIfNecessary");
        MethodBase target = CodeShape.SingleMethod(CodeShape.DeclaredMethods(gameLogic),
            il => il.Any(i => i.Calls(_restart)), "method calling RestartAppIfNecessary");
        harmony.Patch(target, transpiler: new HarmonyMethod(typeof(SteamGuard), nameof(RelaunchTranspiler)));

        // Steam normally provides this when it starts a game. SteamAPI.Init needs it to know the app.
        foreach (string name in new[] { "SteamAppId", "SteamGameId" })
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                Environment.SetEnvironmentVariable(name, AppId);
        }
        Log.Info("Steam guard: relaunch through Steam disabled.");
    }

    private static IEnumerable<CodeInstruction> RelaunchTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        CodeInstruction call = CodeShape.Single(il.Where(i => i.Calls(_restart)), "RestartAppIfNecessary call");

        // Drop the app id argument and answer "no restart needed".
        int at = il.IndexOf(call);
        call.opcode = OpCodes.Pop;
        call.operand = null;
        il.Insert(at + 1, new CodeInstruction(OpCodes.Ldc_I4_0));
        return il;
    }

    // ---- Use another save folder.

    private static void RedirectSaves(Harmony harmony, Type gameLogic, Type steamUser, string folder)
    {
        if (folder != Path.GetFileName(folder) || folder.Trim('.').Length == 0 || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"SaveFolder must be a single folder name, got \"{folder}\"");
        _saveFolder = folder;
        _getSteamId = steamUser.GetMethod("GetSteamID", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("SteamUser", "GetSteamID");

        MethodBase target = CodeShape.SingleMethod(
            CodeShape.DeclaredMethods(gameLogic).Where(m => m is MethodInfo mi && mi.ReturnType == typeof(string) && m.GetParameters().Length == 0),
            il => il.Any(i => i.Calls(_getSteamId)), "save folder method");
        harmony.Patch(target, transpiler: new HarmonyMethod(typeof(SteamGuard), nameof(SaveFolderTranspiler)));
        Log.Info($"Steam guard: saves use the \"{folder}\" folder this session.");
    }

    private static IEnumerable<CodeInstruction> SaveFolderTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo toString = AccessTools.Method(typeof(ulong), nameof(ToString), Type.EmptyTypes);
        List<CodeInstruction> il = instructions.ToList();
        int id = il.FindIndex(i => i.Calls(_getSteamId));
        int text = id < 0 ? -1 : il.FindIndex(id, i => i.Calls(toString));
        if (text < 0)
            throw new ShapeMismatchException("save folder method no longer formats the Steam ID");

        il.Insert(text + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SteamGuard), nameof(SaveFolderName))));
        return il;
    }

    public static string SaveFolderName(string steamId) => _saveFolder;

    // ---- Shared helpers.

    private static void Toggle(Harmony harmony, bool enabled, string feature, Type type, params string[] methods)
    {
        if (enabled)
        {
            Log.Info($"Steam guard: {feature} left on.");
            return;
        }
        foreach (string name in methods)
            Block(harmony, type.GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(type.Name, name));
        Log.Info($"Steam guard: {feature} blocked.");
    }

    private static void Block(Harmony harmony, MethodInfo method)
    {
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(SteamGuard), nameof(SkipCall)));
        Patches info = Harmony.GetPatchInfo(method);
        if (info == null || !info.Prefixes.Any(p => p.owner == HarmonyId))
            throw new InvalidOperationException($"could not patch {method.Name}");
    }

    // Skips the original. The caller gets an empty result: an invalid call handle, or false.
    private static bool SkipCall() => false;

    private static Type Require(Assembly assembly, string name) =>
        assembly.GetType(name) ?? throw new TypeLoadException(name + " not found");
}

/// <summary>
/// Fills 20th Century Food Court's score list from local scores, as the GOG build does. With
/// leaderboards blocked, the Steam list would otherwise be empty.
/// </summary>
internal static class LocalScores
{
    private static ConstructorInfo _entry;
    private static FieldInfo _best, _previous, _current;
    private static MethodInfo _ifPresent;
    private static object _panel;
    private static bool _showingPrevious;
    private static bool _reportedError;

    public static void Install(Harmony harmony, Assembly game)
    {
        Type manager = CodeShape.RequireType(game, "Milwaukee.ScoreManager");
        MethodInfo getter = (MethodInfo)CodeShape.Single(
            manager.GetMethods(CodeShape.AllDeclared).Where(IsListGetter), "score list getter");
        Type entry = getter.ReturnType.GetGenericArguments()[0];
        _entry = entry.GetConstructor(new[] { typeof(string), typeof(int), typeof(bool), typeof(bool) })
            ?? throw new ShapeMismatchException("LeaderboardEntry constructor has changed");

        MethodBase draw = CodeShape.SingleMethod(CodeShape.DeclaredMethods(manager), il => il.Any(i => i.Calls(getter)), "score panel draw method");
        ParameterInfo[] parameters = draw.GetParameters();
        if (parameters.Length < 3 || parameters[2].ParameterType != typeof(bool))
            throw new ShapeMismatchException("score panel draw method has changed");
        Type panel = parameters[0].ParameterType;

        // The panel builder stores the best score, then the previous score, then (in a debug path) the current one.
        MethodBase builder = CodeShape.Single(
            manager.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.ReturnType == panel), "score panel builder");
        FieldInfo[] scores = CodeShape.ReadIL(builder)
            .Where(i => i.opcode == OpCodes.Stfld && i.operand is FieldInfo f && f.DeclaringType == panel && IsMaybeInt(f))
            .Select(i => (FieldInfo)i.operand).Distinct().ToArray();
        if (scores.Length != 3)
            throw new ShapeMismatchException($"expected 3 score fields on the panel, found {scores.Length}");
        (_best, _previous, _current) = (scores[0], scores[1], scores[2]);

        _ifPresent = CodeShape.Single(
            _best.FieldType.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m =>
                m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Action<int>)),
            "Maybe<int> callback method");

        harmony.Patch(draw,
            prefix: new HarmonyMethod(typeof(LocalScores), nameof(DrawPrefix)),
            postfix: new HarmonyMethod(typeof(LocalScores), nameof(DrawPostfix)));
        harmony.Patch(getter, postfix: new HarmonyMethod(typeof(LocalScores), nameof(GetterPostfix)));
    }

    private static bool IsListGetter(MethodInfo m) =>
        !m.IsStatic && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string) &&
        m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(List<>) &&
        m.ReturnType.GetGenericArguments()[0].Name == "LeaderboardEntry";

    private static bool IsMaybeInt(FieldInfo f) =>
        f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition().Name == "Maybe`1" &&
        f.FieldType.GetGenericArguments()[0] == typeof(int);

    private static void DrawPrefix(object[] __args)
    {
        _panel = __args[0];
        _showingPrevious = (bool)__args[2];
    }

    private static void DrawPostfix() => _panel = null;

    private static void GetterPostfix(object __result)
    {
        if (_panel == null || !(__result is IList list))
            return;
        try
        {
            var scores = new List<(int Score, string Label, bool Highlight)>();
            Read(_current, "Current Score", true, scores);
            Read(_best, "Best Score", false, scores);
            Read(_previous, _showingPrevious ? "Previous Score" : "This Solution", !_showingPrevious, scores);

            list.Clear();
            foreach (var (score, label, highlight) in scores.OrderBy(s => s.Score))
                list.Add(_entry.Invoke(new object[] { label, score, highlight, false }));
        }
        catch (Exception e)
        {
            if (!_reportedError)
                Log.Warn("Local score list failed: " + e.Message);
            _reportedError = true;
        }
    }

    private static void Read(FieldInfo field, string label, bool highlight, List<(int, string, bool)> scores)
    {
        Action<int> add = value => scores.Add((value, label, highlight));
        _ifPresent.Invoke(field.GetValue(_panel), new object[] { add });
    }
}
