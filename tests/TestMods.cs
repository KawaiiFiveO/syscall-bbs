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

#if textures
// The folder holds icon.png (3x2) and fake.png (not a PNG). The game texture itself needs the
// renderer, which --verify doesn't start.
public class Mod : IMod
{
    public void OnLoad(ModContext c)
    {
        ModTexture icon = c.Textures.Load("icon.png");
        c.Log.Info($"icon {icon.Width}x{icon.Height}, same object {ReferenceEquals(icon, c.Textures.Load("./ICON.png"))}");
        Expect(c, "outside", () => c.Textures.Load("../ok_config/icon.png"));
        Expect(c, "missing", () => c.Textures.Load("nothing.png"));
        Expect(c, "not png", () => c.Textures.Load("fake.png"));
        Expect(c, "scoped game texture", () => _ = icon.GameTexture);
    }

    private static void Expect(ModContext c, string what, Action action)
    {
        try
        {
            action();
            c.Log.Info($"{what}: not rejected");
        }
        catch (Exception e)
        {
            c.Log.Info($"{what}: rejected ({e.GetType().Name})");
        }
    }
}
#endif

#if programs
// The window itself needs the running game; --verify checks the grid, the registration and the scope.
public class Mod : IMod
{
    public void OnLoad(ModContext c)
    {
        var s = new TextScreen(10, 4);
        s.Write(8, 0, "abcd");
        s.Write(0, 1, "line1");
        s.Write(0, 2, "line2");
        s.Write(0, 3, "line3");
        s.Scroll(1, 4, 1);
        s.Write(0, 3, "x");
        s.Scroll(1, 4, -1);
        c.Log.Info($"screen [{Row(s, 0)}|{Row(s, 1)}|{Row(s, 2)}|{Row(s, 3)}]");
        Expect(c, "oversized screen", () => new TextScreen(TextScreen.MaxColumns + 1, 1));
        c.Programs.Add("Test Program", c.Textures.Load("icon.png"), new Program());
        Expect(c, "data folder before the game runs", () => _ = c.Programs.DataDirectory);
    }

    private static string Row(TextScreen s, int row) => new string(Enumerable.Range(0, s.Columns).Select(col => s.GetChar(col, row)).ToArray());

    private static void Expect(ModContext c, string what, Action action)
    {
        try
        {
            action();
            c.Log.Info($"{what}: not rejected");
        }
        catch (Exception e)
        {
            c.Log.Info($"{what}: rejected ({e.GetType().Name})");
        }
    }

    private sealed class Program : IDesktopProgram
    {
        public string Title => "Test";
        public int Columns => 10;
        public int Rows => 4;
        public void Opened(ProgramWindow window) { }
        public void Update(ProgramWindow window, float seconds) { }
        public void KeyPressed(ProgramWindow window, KeyPress key) { }
        public void Closed(ProgramWindow window) { }
    }
}
#endif

#if programs_scoped_out
public class Mod : IMod { public void OnLoad(ModContext c) => c.Programs.Add("X", c.Textures.Load("icon.png"), null); }
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
