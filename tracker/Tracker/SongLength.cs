using System;

namespace Tracker;

/// <summary>Finds how long a module plays before it ends or loops, without rendering audio.</summary>
public static class SongLength
{
    /// <summary>Longest song measured; past this, playback is treated as endless.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromHours(2);

    /// <summary>The song's duration up to its end, F00 or loop point; <see cref="Limit"/> if it doesn't end.</summary>
    public static TimeSpan Measure(Module module)
    {
        var data = new sbyte[module.Samples.Length][];
        for (int i = 0; i < data.Length; i++)
            data[i] = (sbyte[])module.Samples[i].Data.Clone();
        var voices = new PaulaVoice[module.Channels];
        for (int i = 0; i < voices.Length; i++)
            voices[i] = new PaulaVoice(48000);
        var replayer = new Replayer(module, data, voices);
        replayer.Start(0, 0, resetSong: true);

        double seconds = 0, limit = Limit.TotalSeconds;
        while (seconds < limit)
        {
            replayer.Step();
            seconds += 1.0 / Timing.TickRate(replayer.Tempo);
            if (replayer.Stopped || replayer.Ended)
                return TimeSpan.FromSeconds(seconds);
        }
        return Limit;
    }
}
