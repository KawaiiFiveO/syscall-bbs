using System;
using LastCallBBS.ModAPI;
using Tracker;

namespace LastCallBBS.Mods.TrackerPlayer;

/// <summary>Feeds a module player to the game's mixer. Read runs on the audio thread.</summary>
internal sealed class ModuleStream : IAudioSource
{
    private readonly ModulePlayer _player;
    private volatile bool _paused;

    public ModuleStream(ModulePlayer player) => _player = player;

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public int Read(float[] buffer, int frames)
    {
        if (_paused)
        {
            Array.Clear(buffer, 0, frames * 2);
            return frames;
        }
        return _player.Render(buffer, frames);
    }
}
