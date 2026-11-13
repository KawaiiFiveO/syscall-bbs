using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>
/// Plays the game's sound effects for mods, through the game's own sound loader (a name under
/// Content, without extension, to a Sound) and Audio's play call.
/// </summary>
internal static class GameSounds
{
    public static readonly ISoundHost Host = new SoundHost();

    private static readonly Dictionary<string, object> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Random Pick = new();
    private static MethodInfo _load, _play;
    private static string _failure;

    /// <summary>Finds the game code. Returns one message if it couldn't be found.</summary>
    public static List<string> Verify()
    {
        try
        {
            Resolve();
            Log.Info("Sounds: OK");
            return new List<string>();
        }
        catch (ShapeMismatchException e)
        {
            Log.Error($"Sounds: FAILED - {e.Message}");
            return new List<string> { $"sounds: {e.Message}" };
        }
    }

    private static void Resolve()
    {
        if (_failure != null)
            throw new ShapeMismatchException(_failure);
        if (_play != null)
            return;
        try
        {
            Assembly game = HookHost.Game;
            Type sound = CodeShape.RequireType(game, "Sound");
            Type audio = CodeShape.RequireType(game, "Audio");

            // The loader: the game's only static method from a name to a Sound.
            _load = CodeShape.Single(game.GetTypes().SelectMany(t => t.GetMethods(CodeShape.AllDeclared)).Where(m => m.IsStatic && m.ReturnType == sound &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string) })), "sound loader");

            // Audio's full play call: (sound, loop, fade-in, delay, flag, volume, start time), returning the voice.
            _play = CodeShape.Single(audio.GetMethods(CodeShape.AllDeclared).Where(m => m.IsStatic && m.ReturnType == typeof(uint) &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { sound, typeof(bool), typeof(float), typeof(float), typeof(bool), typeof(float), typeof(double) })),
                "sound play call");
        }
        catch (ShapeMismatchException e)
        {
            _failure = e.Message;
            throw;
        }
    }

    private static object Invoke(MethodInfo method, params object[] args)
    {
        try
        {
            return method.Invoke(null, args);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private sealed class SoundHost : ISoundHost
    {
        public void Play(string name, float volume)
        {
            Resolve();
            // The game finds Content from its folder, which is the current directory.
            string fx = Path.Combine(Environment.CurrentDirectory, "Content", "sounds", "fx");
            string local = name.Replace('/', Path.DirectorySeparatorChar);
            string chosen = name;
            string variants = Path.Combine(fx, local + ".array");
            if (Directory.Exists(variants))
            {
                string[] files = Directory.GetFiles(variants)
                    .Where(f => f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (files.Length == 0)
                    throw new FileNotFoundException($"{local}.array in Content\\sounds\\fx has no sounds");
                chosen = name + ".array/" + files[Pick.Next(files.Length)];
            }
            else if (!File.Exists(Path.Combine(fx, local + ".ogg")) && !File.Exists(Path.Combine(fx, local + ".wav")))
            {
                throw new FileNotFoundException($"Content\\sounds\\fx has no sound {name}");
            }

            string full = "sounds/fx/" + chosen;
            if (!Loaded.TryGetValue(full, out object sound))
                Loaded[full] = sound = Invoke(_load, full);
            Invoke(_play, sound, false, 0f, 0f, false, volume, 0.0);
        }
    }
}
