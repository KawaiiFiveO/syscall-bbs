using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.FoodCourtTools;

/// <summary>
/// Adds playback speeds between play and fast-forward. The game's play/fast button stays enabled
/// while fast-forwarding and steps through each speed in turn, ending at the game's own fast speed.
/// </summary>
/// <remarks>
/// The puzzle screen's update method keeps a run mode (0 stopped, 1 step, 2 play, 3 fast). It enables
/// the button with "mode != 3", handles a press with "mode = mode != 2 ? 2 : 3", and picks the tick
/// rate with "if (mode == 3) speed = fastSpeed". Each of the three sites is rewritten.
/// </remarks>
internal static class PlaybackSpeeds
{
    private const int Play = 2, Fast = 3;

    private static float[] _speeds;
    private static int _index;

    /// <summary>Patches the puzzle screen. Returns the speeds used, play excluded.</summary>
    public static float[] Apply(Assembly game, Harmony harmony, IEnumerable<float> extra)
    {
        Type screen = CodeShape.RequireType(game, "Milwaukee.PuzzleScreen");
        MethodBase update = FindUpdate(game, screen);
        List<CodeInstruction> il = CodeShape.ReadIL(update);
        FieldInfo mode = (FieldInfo)il[FindPressSite(il)].operand;
        FieldInfo fast = (FieldInfo)il[FindSpeedSite(il, mode)].operand;
        float playSpeed = FindPlaySpeed(il, mode, fast);
        float fastSpeed = (float)fast.GetValue(null);

        // The game's fast speed stays the top speed.
        _speeds = extra.Where(s => s > playSpeed && s < fastSpeed).Distinct().OrderBy(s => s).Append(fastSpeed).ToArray();
        _index = _speeds.Length - 1;
        harmony.Patch(update, transpiler: new HarmonyMethod(typeof(PlaybackSpeeds), nameof(Transpiler)));
        return _speeds;
    }

    /// <summary>Parses a comma-separated list of ticks per second. Invalid entries are reported and skipped.</summary>
    public static List<float> Parse(string text, Action<string> warn)
    {
        var speeds = new List<float>();
        foreach (string part in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float speed) && speed > 0 && !float.IsInfinity(speed))
                speeds.Add(speed);
            else
                warn($"PlaybackSpeeds: ignoring \"{part.Trim()}\"");
        }
        return speeds;
    }

    // The update method is the only one that ticks the simulation with tick(false).
    private static MethodBase FindUpdate(Assembly game, Type screen)
    {
        Type sim = CodeShape.RequireType(game, "Milwaukee.FullSim");
        FieldInfo[] signalSets = sim.GetFields(CodeShape.AllDeclared)
            .Where(f => !f.IsStatic && f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(HashSet<>)).ToArray();
        MethodInfo tick = (MethodInfo)CodeShape.SingleMethod(
            CodeShape.DeclaredMethods(screen).Where(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool)),
            il => il.Any(i => i.opcode == OpCodes.Stfld && signalSets.Contains(i.operand)), "tick method");
        return CodeShape.SingleMethod(CodeShape.DeclaredMethods(screen),
            il => il.Zip(il.Skip(1), (a, b) => a.opcode == OpCodes.Ldc_I4_0 && b.Calls(tick)).Any(x => x), "puzzle screen update method");
    }

    private static bool IsModeLoad(CodeInstruction i) =>
        i.opcode == OpCodes.Ldfld && i.operand is FieldInfo f && f.FieldType.IsEnum && f.DeclaringType.Name == "PuzzleScreen";

    private static bool Is(CodeInstruction i, params OpCode[] ops) => ops.Contains(i.opcode);

    // "mode = mode != 2 ? 2 : 3": ldfld mode, ldc.i4.2, bne.un, ldc.i4.3, br, ldc.i4.2, stfld mode.
    private static int FindPressSite(List<CodeInstruction> il) => CodeShape.Single(
        Enumerable.Range(0, Math.Max(0, il.Count - 6)).Where(i =>
            IsModeLoad(il[i]) && Is(il[i + 1], OpCodes.Ldc_I4_2) && Is(il[i + 2], OpCodes.Bne_Un, OpCodes.Bne_Un_S) &&
            Is(il[i + 3], OpCodes.Ldc_I4_3) && Is(il[i + 4], OpCodes.Br, OpCodes.Br_S) && Is(il[i + 5], OpCodes.Ldc_I4_2) &&
            il[i + 6].opcode == OpCodes.Stfld && Equals(il[i + 6].operand, il[i].operand)),
        "play/fast button press");

    // "if (mode == 3) speed = fastSpeed": ldfld mode, ldc.i4.3, bne.un, ldsfld float. Returns the ldsfld.
    private static int FindSpeedSite(List<CodeInstruction> il, FieldInfo mode) => CodeShape.Single(
        Enumerable.Range(0, Math.Max(0, il.Count - 3)).Where(i =>
            il[i].LoadsField(mode) && Is(il[i + 1], OpCodes.Ldc_I4_3) && Is(il[i + 2], OpCodes.Bne_Un, OpCodes.Bne_Un_S) &&
            il[i + 3].opcode == OpCodes.Ldsfld && il[i + 3].operand is FieldInfo f && f.FieldType == typeof(float)),
        "fast speed") + 3;

    // "else if (mode == 2) speed = playSpeed", right after the fast speed.
    private static float FindPlaySpeed(List<CodeInstruction> il, FieldInfo mode, FieldInfo fast)
    {
        FieldInfo play = (FieldInfo)CodeShape.Single(
            Enumerable.Range(0, Math.Max(0, il.Count - 3)).Where(i =>
                il[i].LoadsField(mode) && Is(il[i + 1], OpCodes.Ldc_I4_2) && Is(il[i + 2], OpCodes.Bne_Un, OpCodes.Bne_Un_S) &&
                il[i + 3].opcode == OpCodes.Ldsfld && il[i + 3].operand is FieldInfo f && f.FieldType == typeof(float) && f != fast)
            .Select(i => il[i + 3]), "play speed").operand;
        return (float)play.GetValue(null);
    }

    // "mode != 3" enabling the button: ldfld mode, ldc.i4.3, beq. Returns the ldfld.
    private static int FindEnableSite(List<CodeInstruction> il, FieldInfo mode) => CodeShape.Single(
        Enumerable.Range(0, Math.Max(0, il.Count - 2)).Where(i =>
            il[i].LoadsField(mode) && Is(il[i + 1], OpCodes.Ldc_I4_3) && Is(il[i + 2], OpCodes.Beq, OpCodes.Beq_S)),
        "play/fast button state");

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        int press = FindPressSite(il);
        FieldInfo mode = (FieldInfo)il[press].operand;
        int speed = FindSpeedSite(il, mode);
        int enable = FindEnableSite(il, mode);

        // Replaced in place so that branch labels stay on the same instructions.
        il[speed] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlaybackSpeeds), nameof(FastSpeed))).MoveLabelsFrom(il[speed]);
        // Inserted after the field loads, from the last site back so that earlier indexes stay valid.
        foreach ((int site, string method) in new[] { (press, nameof(PressMode)), (enable, nameof(EnableMode)) }.OrderByDescending(s => s.Item1))
            il.Insert(site + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlaybackSpeeds), method)));
        return il;
    }

    /// <summary>Ticks per second while fast-forwarding.</summary>
    public static float FastSpeed() => _speeds[_index];

    /// <summary>The mode the button's state is decided from: fast counts as play until the top speed.</summary>
    public static int EnableMode(int mode) => mode == Fast && _index < _speeds.Length - 1 ? Play : mode;

    /// <summary>
    /// The mode the press is decided from. Play and fast both report play, so the press leads to fast;
    /// from play that's the first speed, and from fast the next one.
    /// </summary>
    public static int PressMode(int mode)
    {
        if (mode == Play)
            _index = 0;
        else if (mode == Fast)
            _index = Math.Min(_index + 1, _speeds.Length - 1);
        else
            return mode;
        return Play;
    }
}
