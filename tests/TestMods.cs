// One test mod per #if block. run-tests.ps1 lists what each one is expected to do.
using System;
using System.Linq;
using LastCallBBS.ModAPI;

#if helper || helper_unshipped
public static class Helper { public static string Say() => "helper ok"; }
#endif

#if ok_foodcourt
public class Mod : IMod { public void OnLoad(ModContext c) => c.Hooks.FoodCourt.TickCompleted.Subscribe(t => { }); }
#endif

#if ok_multi
public class Mod : IMod
{
    public void OnLoad(ModContext c)
    {
        c.Hooks.Terminal.Connected.Subscribe(() => { });
        c.Hooks.Story.EntryUnlocked.Subscribe(e => { });
    }
}
#endif

#if ok_generic
// "new T()" and GetType().Name are ordinary code, and must pass the scan.
public class Mod : IMod
{
    private static T Make<T>() where T : new() => new T();
    public void OnLoad(ModContext c) => c.Log.Info(Make<object>().GetType().Name);
}
#endif

#if ok_config
public class Mod : IMod
{
    public void OnLoad(ModContext c)
    {
        if (c.Config.GetString("greeting") != "hi there" || c.Version != "1.2.3" || c.Name != "Config Mod")
            throw new Exception("manifest or config was not read");
    }
}
#endif

#if full_access
public class Mod : IMod
{
    public void OnLoad(ModContext c) => c.Log.Info($"game={c.Game.GetName().Name} harmony={c.Harmony.Id}");
}
#endif

#if with_dep
public class Mod : IMod { public void OnLoad(ModContext c) => c.Log.Info(Helper.Say()); }
#endif

#if report_problem
public class Mod : IMod
{
    public void OnLoad(ModContext c)
    {
        c.ReportProblem("one feature unavailable");
        c.Log.Info("still running after the problem");
    }
}
#endif

#if throws_on_load
public class Mod : IMod { public void OnLoad(ModContext c) => throw new InvalidOperationException("boom on load"); }
#endif

#if partial_patch
public static class Target { public static int F() => 1; public static void Prefix() { } }
public class Mod : IMod
{
    public void OnLoad(ModContext c)
    {
        c.Harmony.Patch(typeof(Target).GetMethod("F"), prefix: new HarmonyLib.HarmonyMethod(typeof(Target).GetMethod("Prefix")));
        throw new Exception("failed after patching");
    }
}
#endif

#if missing_dep
public class Mod : IMod { public void OnLoad(ModContext c) => c.Log.Info(Helper.Say()); }
#endif

#if outside_hook
public class Mod : IMod { public void OnLoad(ModContext c) => c.Hooks.Terminal.Connected.Subscribe(() => { }); }
#endif

#if allowed_game
public class Mod : IMod { public void OnLoad(ModContext c) => c.Log.Info(c.Game.ToString()); }
#endif

#if uses_harmony
public class Mod : IMod { public void OnLoad(ModContext c) => c.Log.Info(c.Harmony.Id); }
#endif

#if references_game
public class Mod : IMod { public void OnLoad(ModContext c) => c.Log.Info(typeof(GameLogic).ToString()); }
#endif

#if uses_reflection
public class Mod : IMod { public void OnLoad(ModContext c) => c.Log.Info(typeof(int).GetMethods().Length.ToString()); }
#endif

#if dynamic_lookup
public class Mod : IMod { public void OnLoad(ModContext c) => c.Log.Info("" + Activator.CreateInstance(Type.GetType("X"))); }
#endif

#if native_call
public class Mod : IMod
{
    [System.Runtime.InteropServices.DllImport("kernel32")] private static extern uint GetTickCount();
    public void OnLoad(ModContext c) { }
}
#endif

#if clean
public class Mod : IMod { public void OnLoad(ModContext c) { } }
#endif

#if two_entries
public class ModA : IMod { public void OnLoad(ModContext c) { } }
public class ModB : IMod { public void OnLoad(ModContext c) { } }
#endif

#if zz_check
// Runs after the others, to show that a failed mod's patches were removed.
public class Mod : IMod
{
    public void OnLoad(ModContext c)
    {
        var owners = HarmonyLib.Harmony.GetAllPatchedMethods().SelectMany(m => HarmonyLib.Harmony.GetPatchInfo(m).Owners).Distinct();
        c.Log.Info("patch owners: " + string.Join(",", owners));
    }
}
#endif

#if steam_blocked
// Steam builds only. An unblocked call throws, because Steam isn't running during --verify.
public class Mod : IMod
{
    public void OnLoad(ModContext c)
    {
        var assembly = System.Reflection.Assembly.Load("Steamworks.NET");
        var methods = assembly.GetType("Steamworks.SteamUserStats").GetMethods()
            .Where(m => m.IsStatic && ((m.Name.Contains("Leaderboard") && m.ReturnType.Name == "SteamAPICall_t") || m.Name == "SetAchievement"));
        foreach (var m in methods)
        {
            object[] args = m.GetParameters().Select(p => p.ParameterType == typeof(string) ? "x" : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            try
            {
                m.Invoke(null, args);
                c.Log.Info(m.Name + ": BLOCKED");
            }
            catch (System.Reflection.TargetInvocationException)
            {
                c.Log.Info(m.Name + ": reached Steam");
            }
        }
    }
}
#endif
