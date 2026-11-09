using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>Terminal.DownloadStarted, DownloadCompleted and Connected.</summary>
internal static class TerminalHookPoints
{
    private static readonly MethodInfo GetNow = AccessTools.PropertyGetter(typeof(DateTime), nameof(DateTime.Now));
    private static readonly MethodInfo GetTicks = AccessTools.PropertyGetter(typeof(DateTime), nameof(DateTime.Ticks));

    private static Type _window;
    private static FieldInfo _program, _seconds;

    public static void Define()
    {
        HookHost.Define("Terminal.DownloadStarted", () => Patch(il => FindDownloadStart(il).Any(), "download start", nameof(StartTranspiler)));
        HookHost.Define("Terminal.DownloadCompleted", () => Patch(IsDownloadTick, "download tick", nameof(CompletedTranspiler)));
        HookHost.Define("Terminal.Connected", () => Patch(il => FindConnected(il).Any(), "end of the dial-in", nameof(ConnectedTranspiler)));
    }

    private static void Patch(Func<List<CodeInstruction>, bool> condition, string what, string transpiler)
    {
        Resolve();
        MethodBase target = CodeShape.SingleMethod(CodeShape.DeclaredMethods(_window), condition, what);
        HookHost.Harmony.Patch(target, transpiler: new HarmonyMethod(typeof(TerminalHookPoints), transpiler));
    }

    private static void Resolve()
    {
        if (_window != null)
            return;
        Type application = CodeShape.RequireType(HookHost.Game, "Terminal.Application");
        if (application.IsValueType)
            throw new ShapeMismatchException("Terminal.Application is no longer a class");
        _seconds = CodeShape.SingleField(application, f => !f.IsStatic && f.FieldType == typeof(float), "float field on Terminal.Application");
        _program = CodeShape.SingleField(application,
            f => !f.IsStatic && f.FieldType.IsEnum && Enum.GetNames(f.FieldType).Contains("Milwaukee"), "program field on Terminal.Application");
        _window = CodeShape.RequireType(HookHost.Game, "Terminal.MainWindow");
    }

    public static void RaiseStarted(object application) =>
        HookHost.Raise("Terminal.DownloadStarted", () => _program.GetValue(application).ToString());

    public static void RaiseCompleted(object application) =>
        HookHost.Raise("Terminal.DownloadCompleted", () => _program.GetValue(application).ToString());

    public static void RaiseConnected() => HookHost.Raise("Terminal.Connected");

    // ---- DownloadStarted: pressing D stores a negative head start in the Maybe<float> progress.

    private static bool IsMaybeFloat(FieldInfo f) =>
        f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition().Name == "Maybe`1" &&
        f.FieldType.GetGenericArguments()[0] == typeof(float);

    private static IEnumerable<int> FindDownloadStart(List<CodeInstruction> il)
    {
        for (int i = 1; i < il.Count; i++)
        {
            if (il[i].opcode != OpCodes.Stfld || !IsMaybeFloat((FieldInfo)il[i].operand))
                continue;
            for (int j = i - 1; j >= Math.Max(0, i - 3); j--)
            {
                if (il[j].opcode == OpCodes.Ldc_R4 && (float)il[j].operand < 0)
                {
                    yield return i;
                    break;
                }
            }
        }
    }

    private static IEnumerable<CodeInstruction> StartTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        int at = CodeShape.Single(FindDownloadStart(il), "download start");
        CodeInstruction application = LoadApplication(il, at);
        il.Insert(at + 1, application);
        il.Insert(at + 2, Call(nameof(RaiseStarted)));
        return il;
    }

    // ---- DownloadCompleted: the tick method saves DateTime.Now.Ticks once the download is done.

    private static bool IsDownloadTick(List<CodeInstruction> il)
    {
        for (int i = 0; i < il.Count - 1; i++)
        {
            if (il[i].opcode == OpCodes.Ldfld && Equals(il[i].operand, _seconds) && CodeShape.IsComparison(il[i + 1].opcode))
                return true;
        }
        return false;
    }

    private static IEnumerable<CodeInstruction> CompletedTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        int now = il.FindIndex(i => i.Calls(GetNow));
        int ticks = now < 0 ? -1 : il.FindIndex(now, i => i.Calls(GetTicks));
        int save = ticks < 0 ? -1 : il.FindIndex(ticks + 1, i => i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt);
        if (save < 0)
            throw new ShapeMismatchException("download tick no longer saves DateTime.Now.Ticks");

        CodeInstruction application = LoadApplication(il, now);
        il.Insert(save + 1, application);
        il.Insert(save + 2, Call(nameof(RaiseCompleted)));
        return il;
    }

    /// <summary>Copies the "load the Application local" instruction used closest before an index.</summary>
    private static CodeInstruction LoadApplication(List<CodeInstruction> il, int before)
    {
        for (int k = before - 1; k >= 1; k--)
        {
            if (il[k].opcode == OpCodes.Ldfld && Equals(il[k].operand, _program) && il[k - 1].IsLdloc())
                return new CodeInstruction(il[k - 1].opcode, il[k - 1].operand);
        }
        throw new ShapeMismatchException("could not find which program is being downloaded");
    }

    // ---- Connected: both dial-in timers end by swapping the state object, but only the
    // final one then reads the window's "BBS has closed" flag.

    private static IEnumerable<int> FindConnected(List<CodeInstruction> il)
    {
        for (int i = 0; i + 1 < il.Count; i++)
        {
            if (il[i].opcode != OpCodes.Ldc_R4 || (float)il[i].operand <= 0 || !CodeShape.IsComparison(il[i + 1].opcode))
                continue;
            int created = FindFrom(il, i + 2, 12, x => x.opcode == OpCodes.Newobj);
            int stored = created < 0 ? -1 : FindFrom(il, created + 1, 3, x => x.opcode == OpCodes.Stfld);
            if (stored >= 0 && FindFrom(il, stored + 1, 16, IsWindowFlag) >= 0)
                yield return stored;
        }
    }

    private static bool IsWindowFlag(CodeInstruction i) =>
        i.opcode == OpCodes.Ldfld && i.operand is FieldInfo f && f.FieldType == typeof(bool) && f.DeclaringType == _window;

    private static int FindFrom(List<CodeInstruction> il, int start, int count, Predicate<CodeInstruction> match)
    {
        for (int i = start; i < Math.Min(il.Count, start + count); i++)
        {
            if (match(il[i]))
                return i;
        }
        return -1;
    }

    private static IEnumerable<CodeInstruction> ConnectedTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        int at = CodeShape.Single(FindConnected(il), "end of the dial-in");
        il.Insert(at + 1, Call(nameof(RaiseConnected)));
        return il;
    }

    private static CodeInstruction Call(string method) =>
        new(OpCodes.Call, AccessTools.Method(typeof(TerminalHookPoints), method));
}
