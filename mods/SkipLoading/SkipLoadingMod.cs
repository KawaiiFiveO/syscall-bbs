using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.SkipLoading;

/// <summary>Removes the BBS's download timers. Each patch can fail without affecting the others.</summary>
public sealed class SkipLoadingMod : IMod
{
    // Set before patching; read by the transpilers below.
    private static FieldInfo _downloadSizeField;

    private ModContext _context;

    public void OnLoad(ModContext context)
    {
        _context = context;
        Type mainWindow = CodeShape.RequireType(context.Game, "Terminal.MainWindow");
        Type application = CodeShape.RequireType(context.Game, "Terminal.Application");

        Apply("InstantDownloads", true, "Instant downloads", () => PatchInstantDownloads(mainWindow, application));
        Apply("NoDownloadCooldown", true, "No download cooldown", () => PatchDownloadCooldown(mainWindow));
        Apply("SkipTwelveHourWait", false, "Skip 12-hour wait", () => PatchTwelveHourWait(mainWindow));
    }

    private void Apply(string setting, bool defaultValue, string name, Action patch)
    {
        if (!_context.Config.GetBool(setting, defaultValue))
        {
            _context.Log.Info($"{name}: disabled in settings.");
            return;
        }
        try
        {
            patch();
            _context.Log.Info($"{name}: applied.");
        }
        catch (Exception e)
        {
            _context.Log.Error(e.ToString());
            _context.ReportProblem($"{name}: {e.Message}");
        }
    }

    private void PatchMethod(MethodBase target, string transpiler)
    {
        _context.Log.Info($"  target: {target.DeclaringType}::{target.Name}");
        _context.Harmony.Patch(target, transpiler: new HarmonyMethod(typeof(SkipLoadingMod), transpiler));
    }

    // ---------------------------------------------------------------------------------
    // Instant downloads
    //
    // Original (Terminal.MainWindow, ticked every frame while a download is active):
    //     state.progress += Time.deltaTime;
    //     if (state.progress >= app.downloadSeconds) { mark downloaded; ... }
    //
    // Terminal.Application has exactly one float field: the download duration in seconds.
    // The completion check is the only place in MainWindow where that field is loaded and
    // immediately compared. We make the comparison see -Infinity, so the download completes
    // on the first tick. The field itself is untouched, so the file sizes and the
    // disk-usage meter shown elsewhere stay correct.
    // ---------------------------------------------------------------------------------
    private void PatchInstantDownloads(Type mainWindow, Type application)
    {
        _downloadSizeField = CodeShape.SingleField(application,
            f => !f.IsStatic && f.FieldType == typeof(float), "float field on Terminal.Application");

        PatchMethod(
            CodeShape.SingleMethod(CodeShape.DeclaredMethods(mainWindow), il => FindSizeComparisons(il).Any(), "download-completion check"),
            nameof(InstantDownloadsTranspiler));
    }

    /// <summary>Indexes of "ldfld Application.size" immediately followed by a comparison.</summary>
    private static IEnumerable<int> FindSizeComparisons(List<CodeInstruction> il)
    {
        for (int i = 0; i < il.Count - 1; i++)
        {
            if (il[i].opcode == OpCodes.Ldfld && Equals(il[i].operand, _downloadSizeField) && CodeShape.IsComparison(il[i + 1].opcode))
                yield return i;
        }
    }

    private static IEnumerable<CodeInstruction> InstantDownloadsTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        int hit = CodeShape.Single(FindSizeComparisons(il), "download-size comparison");

        // ... ldfld size      ->   ... ldfld size; pop; ldc.r4 -Infinity
        il.Insert(hit + 1, new CodeInstruction(OpCodes.Pop));
        il.Insert(hit + 2, new CodeInstruction(OpCodes.Ldc_R4, float.NegativeInfinity));
        return il;
    }

    // ---------------------------------------------------------------------------------
    // No download cooldown (the "quota")
    //
    // When a download completes, the game saves DateTime.Now.Ticks. The download screen then
    // computes:
    //     DateTime quotaEnd = new DateTime(save.LastDownloadTicks) + TimeSpan.FromSeconds(900);
    //     if (DateTime.Now < quotaEnd) { show "come back in mm:ss"; block downloads }
    // We replace that DateTime + TimeSpan addition with one returning DateTime.MinValue, so
    // the cooldown has always expired. This is matched by shape (new DateTime(long), then
    // TimeSpan.FromSeconds, then DateTime + TimeSpan) rather than the literal 900, so it
    // still works if an update changes the duration.
    // ---------------------------------------------------------------------------------
    private static readonly MethodInfo DateTimeAdd =
        AccessTools.Method(typeof(DateTime), "op_Addition", new[] { typeof(DateTime), typeof(TimeSpan) });
    private static readonly MethodInfo TimeSpanFromSeconds =
        AccessTools.Method(typeof(TimeSpan), nameof(TimeSpan.FromSeconds), new[] { typeof(double) });
    private static readonly ConstructorInfo DateTimeFromTicks =
        AccessTools.Constructor(typeof(DateTime), new[] { typeof(long) });

    private void PatchDownloadCooldown(Type mainWindow) =>
        PatchMethod(
            CodeShape.SingleMethod(CodeShape.DeclaredMethods(mainWindow), il => FindCooldownAdds(il).Any(), "method computing the download cooldown"),
            nameof(CooldownTranspiler));

    /// <summary>Indexes of "new DateTime(long) ... TimeSpan.FromSeconds(x); DateTime + TimeSpan".</summary>
    private static IEnumerable<int> FindCooldownAdds(List<CodeInstruction> il)
    {
        for (int i = 1; i < il.Count; i++)
        {
            if (!il[i].Calls(DateTimeAdd) || !il[i - 1].Calls(TimeSpanFromSeconds))
                continue;
            for (int j = i - 2; j >= Math.Max(0, i - 5); j--)
            {
                if (il[j].opcode == OpCodes.Newobj && Equals(il[j].operand, DateTimeFromTicks))
                {
                    yield return i;
                    break;
                }
            }
        }
    }

    private static IEnumerable<CodeInstruction> CooldownTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        int hit = CodeShape.Single(FindCooldownAdds(il), "cooldown addition");
        il[hit].operand = AccessTools.Method(typeof(SkipLoadingMod), nameof(CooldownExpired));
        return il;
    }

    public static DateTime CooldownExpired(DateTime lastDownload, TimeSpan cooldown) => DateTime.MinValue;

    // ---------------------------------------------------------------------------------
    // Skip the 12-hour wait
    //
    // Once every program is downloaded, the game stores (DateTime.Now + 12h).Ticks in the
    // save. Terminal.MainWindow's constructor then does:
    //     flag = save.TryGet(out long t) && DateTime.Now.Ticks > t;
    // and the story event fires on the next connection if the flag is set. We make that
    // one DateTime.Now read return DateTime.MaxValue. The flag still requires the saved
    // timestamp to exist, so nothing fires before you have actually downloaded everything.
    // ---------------------------------------------------------------------------------
    private static readonly MethodInfo DateTimeGetNow = AccessTools.PropertyGetter(typeof(DateTime), nameof(DateTime.Now));
    private static readonly MethodInfo DateTimeGetTicks = AccessTools.PropertyGetter(typeof(DateTime), nameof(DateTime.Ticks));

    private void PatchTwelveHourWait(Type mainWindow) =>
        PatchMethod(
            CodeShape.SingleMethod(CodeShape.InstanceConstructors(mainWindow), il => FindNowTicksComparisons(il).Any(), "constructor with a DateTime.Now.Ticks comparison"),
            nameof(TwelveHourTranspiler));

    /// <summary>Indexes of "call DateTime.get_Now" followed within a few ops by get_Ticks and a comparison.</summary>
    private static IEnumerable<int> FindNowTicksComparisons(List<CodeInstruction> il)
    {
        for (int i = 0; i < il.Count; i++)
        {
            if (!il[i].Calls(DateTimeGetNow))
                continue;
            int window = Math.Min(il.Count, i + 8);
            int ticks = -1;
            for (int j = i + 1; j < window; j++)
            {
                if (il[j].Calls(DateTimeGetTicks)) { ticks = j; break; }
            }
            if (ticks < 0)
                continue;
            for (int j = ticks + 1; j < window; j++)
            {
                if (CodeShape.IsComparison(il[j].opcode)) { yield return i; break; }
            }
        }
    }

    private static IEnumerable<CodeInstruction> TwelveHourTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        int hit = CodeShape.Single(FindNowTicksComparisons(il), "DateTime.Now.Ticks comparison");
        il[hit].operand = AccessTools.Method(typeof(SkipLoadingMod), nameof(FarFuture));
        return il;
    }

    public static DateTime FarFuture() => DateTime.MaxValue;
}
