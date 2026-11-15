using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>
/// Silences the game's music while a program window asks for it. Every frame, the music manager
/// asks each screen (the desktop and full-screen games) for its music state through
/// IScreen.GetMusicState and plays the first that isn't Ignore; the states are Silence, Music and
/// Ignore. That call is replaced with one that returns Silence for the screens being muted.
/// </summary>
internal static class MusicFilter
{
    private static MethodInfo _getMusicState;
    private static Type _desktop;
    private static Func<object, object> _get;
    private static ConstructorInfo _silenceCtor;
    private static object _silence;
    private static Func<GameMusic> _wanted;
    private static string _failure;
    private static bool _attached;

    /// <summary>Patches the music manager. <paramref name="wanted"/> is the strongest muting any open window asks for.</summary>
    public static void Attach(Harmony harmony, Func<GameMusic> wanted)
    {
        if (_failure != null)
            throw new ShapeMismatchException(_failure);
        if (_attached)
            return;
        try
        {
            Assembly game = HookHost.Game;
            Type screen = CodeShape.RequireType(game, "IScreen");
            _desktop = CodeShape.RequireType(game, "DesktopScreen");
            _getMusicState = screen.GetMethod("GetMusicState", Type.EmptyTypes) ?? throw new ShapeMismatchException("IScreen.GetMusicState not found");
            Type state = _getMusicState.ReturnType;
            // The manager keeps the current state in a field; only its update asks the screens.
            IEnumerable<MethodBase> candidates = game.GetTypes()
                .Where(t => t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(f => f.FieldType == state))
                .SelectMany(CodeShape.DeclaredMethods);
            MethodBase manager = CodeShape.SingleMethod(candidates, il => il.Any(i => i.Calls(_getMusicState)), "music manager update");
            // Silence is the state the manager starts from when no screen asks for music.
            _silenceCtor = CodeShape.Single(CodeShape.ReadIL(manager)
                .Where(i => i.opcode == OpCodes.Newobj && i.operand is ConstructorInfo c && c.DeclaringType.IsSubclassOf(state) && c.GetParameters().Length == 0)
                .Select(i => (ConstructorInfo)i.operand), "silence state in the music manager");
            ParameterExpression s = Expression.Parameter(typeof(object), "screen");
            _get = Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Call(Expression.Convert(s, screen), _getMusicState), typeof(object)), s).Compile();
            _wanted = wanted;
            harmony.Patch(manager, transpiler: new HarmonyMethod(typeof(MusicFilter), nameof(Transpiler)));
            _attached = true;
            Log.Info("Game music: OK");
        }
        catch (Exception e)
        {
            _failure = (e as ShapeMismatchException ?? e.InnerException as ShapeMismatchException)?.Message ?? e.Message;
            throw new ShapeMismatchException(_failure);
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo get = AccessTools.Method(typeof(MusicFilter), nameof(Get));
        foreach (CodeInstruction i in instructions)
        {
            if (i.Calls(_getMusicState))
            {
                var call = new CodeInstruction(OpCodes.Call, get);
                call.MoveLabelsFrom(i);
                call.MoveBlocksFrom(i);
                yield return call;
                yield return new CodeInstruction(OpCodes.Castclass, _getMusicState.ReturnType);
                continue;
            }
            yield return i;
        }
    }

    /// <summary>The screen's music state, or Silence if it's being muted.</summary>
    public static object Get(object screen)
    {
        try
        {
            GameMusic wanted = _wanted();
            if (wanted == GameMusic.Muted || (wanted == GameMusic.MutedOnDesktop && _desktop.IsInstanceOfType(screen)))
                return _silence ??= _silenceCtor.Invoke(null);
        }
        catch (Exception e)
        {
            Log.Error($"Game music: {e.Message}");
            _wanted = () => GameMusic.Playing;
        }
        return _get(screen);
    }
}
