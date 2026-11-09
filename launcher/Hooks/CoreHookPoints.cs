using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>Game.Update, Desktop.WindowOpened and Story.EntryUnlocked.</summary>
internal static class CoreHookPoints
{
    private const int StoryPollFrames = 30;

    private static Array _entries;
    private static FieldInfo _entriesField, _condition, _title;
    private static bool?[] _unlocked;
    private static int _frame;

    public static void Define()
    {
        HookHost.Define("Game.Update", AttachUpdate);
        HookHost.Define("Desktop.WindowOpened", AttachWindowOpened);
        HookHost.Define("Story.EntryUnlocked", AttachStory);
    }

    // The screen stack runs the active screen through the one call to IScreen.Update.
    private static void AttachUpdate()
    {
        MethodInfo update = CodeShape.RequireType(HookHost.Game, "IScreen").GetMethod("Update")
            ?? throw new ShapeMismatchException("IScreen.Update not found");
        MethodBase target = CodeShape.SingleMethod(
            CodeShape.DeclaredMethods(CodeShape.RequireType(HookHost.Game, "ScreenStack")),
            il => il.Any(i => i.Calls(update)), "method calling IScreen.Update");
        HookHost.Harmony.Patch(target, postfix: new HarmonyMethod(typeof(CoreHookPoints), nameof(UpdatePostfix)));
    }

    private static void UpdatePostfix() => HookHost.Raise("Game.Update");

    // DesktopWindow objects are created in exactly one method, whose first argument is the window's content.
    private static Type _desktopWindow;

    private static void AttachWindowOpened()
    {
        _desktopWindow = CodeShape.RequireType(HookHost.Game, "DesktopWindow");
        MethodBase target = CodeShape.SingleMethod(
            CodeShape.DeclaredMethods(CodeShape.RequireType(HookHost.Game, "DesktopScreen")),
            il => il.Any(IsNewWindow), "method creating a DesktopWindow");
        if (target.IsStatic || target.GetParameters().Length == 0 || target.GetParameters()[0].ParameterType.IsValueType)
            throw new ShapeMismatchException("window-creating method doesn't take the window's content first");
        HookHost.Harmony.Patch(target, transpiler: new HarmonyMethod(typeof(CoreHookPoints), nameof(WindowTranspiler)));
    }

    private static bool IsNewWindow(CodeInstruction i) =>
        i.opcode == OpCodes.Newobj && i.operand is ConstructorInfo c && c.DeclaringType == _desktopWindow;

    private static IEnumerable<CodeInstruction> WindowTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        int at = il.IndexOf(CodeShape.Single(il.Where(IsNewWindow), "DesktopWindow creation"));

        // Placed before the newobj so a method that returns early never reports a window.
        var load = new CodeInstruction(OpCodes.Ldarg_1);
        load.labels.AddRange(il[at].labels);
        il[at].labels.Clear();
        il.Insert(at, load);
        il.Insert(at + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CoreHookPoints), nameof(RaiseWindowOpened))));
        return il;
    }

    public static void RaiseWindowOpened(object content) =>
        HookHost.Raise("Desktop.WindowOpened", () => content.GetType().FullName);

    // The game has no unlock event: each entry has a condition checked when the Organizer draws.
    private static void AttachStory()
    {
        Type story = CodeShape.RequireType(HookHost.Game, "StoryEntries");
        _entriesField = CodeShape.Single(
            story.GetFields(CodeShape.AllDeclared).Where(f => f.IsStatic && f.FieldType.IsArray && ConditionOf(f.FieldType.GetElementType()) != null),
            "story entry array");
        Type entry = _entriesField.FieldType.GetElementType();
        _condition = ConditionOf(entry);
        _title = entry.GetFields(CodeShape.AllDeclared).Where(f => f.FieldType == typeof(string)).OrderBy(f => f.MetadataToken).FirstOrDefault()
            ?? throw new ShapeMismatchException("story entries have no title field");
        HookHost.Subscribe<object>("Game.Update", "launcher", LauncherLog.Instance, _ => PollStory());
    }

    private static FieldInfo ConditionOf(Type entry) =>
        entry.GetFields(CodeShape.AllDeclared).SingleOrDefault(f => f.FieldType == typeof(Func<bool>));

    private static void PollStory()
    {
        if (++_frame % StoryPollFrames != 0)
            return;
        _entries ??= _entriesField.GetValue(null) as Array;
        if (_entries == null)
            return;
        _unlocked ??= new bool?[_entries.Length];

        for (int i = 0; i < _entries.Length; i++)
        {
            object entry = _entries.GetValue(i);
            bool now;
            try
            {
                now = ((Func<bool>)_condition.GetValue(entry))();
            }
            catch (Exception)
            {
                continue; // not ready yet, for example the save hasn't loaded
            }

            // The first reading of each entry is a baseline, so old unlocks don't fire.
            bool fire = now && _unlocked[i] == false;
            _unlocked[i] = now;
            if (fire)
            {
                int index = i;
                HookHost.Raise("Story.EntryUnlocked", () => new StoryEntry(index, (string)_title.GetValue(entry)));
            }
        }
    }
}
