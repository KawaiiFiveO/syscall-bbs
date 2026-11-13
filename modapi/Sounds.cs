using System;
using System.Collections.Generic;

namespace LastCallBBS.ModAPI;

/// <summary>Implemented by the loader. Mods use <see cref="ModSounds"/> instead.</summary>
public interface ISoundHost
{
    /// <summary>Plays a sound under Content\sounds\fx. Throws if it can't.</summary>
    void Play(string name, float volume);
}

/// <summary>
/// Plays the game's sound effects. Call it on the game's main thread (program callbacks and
/// hooks) once the game is running, not in OnLoad.
/// </summary>
public sealed class ModSounds
{
    private readonly ISoundHost _host;
    private readonly IModLog _log;
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    public ModSounds(ISoundHost host, IModLog log)
    {
        _host = host;
        _log = log;
    }

    /// <summary>
    /// Plays one of the game's sounds, named by its path under Content\sounds\fx without the
    /// extension, such as "desktop/win_open". A name with a folder of variants, such as
    /// "desktop/load_m" (Content\sounds\fx\desktop\load_m.array), plays one of them at random.
    /// <paramref name="volume"/> is 0 to 1. Returns false if the sound couldn't play; the first
    /// failure for each name is logged.
    /// </summary>
    public bool PlayGameSound(string name, float volume = 1f)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains(".."))
            throw new ArgumentException("a sound is named by its path under Content\\sounds\\fx, such as desktop/win_open", nameof(name));
        string clean = name.Replace('\\', '/').Trim('/');
        try
        {
            _host.Play(clean, Math.Max(0f, Math.Min(1f, volume)));
            return true;
        }
        catch (Exception e)
        {
            if (_failed.Add(clean))
                _log.Warn($"Sound {clean} couldn't play: {e.Message}");
            return false;
        }
    }
}
