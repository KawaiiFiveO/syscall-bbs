namespace Tracker;

/// <summary>PAL Amiga clocks.</summary>
public static class Timing
{
    /// <summary>The crystal, in Hz.</summary>
    public const double Crystal = 28375160.0;

    /// <summary>Paula's clock (the color clock): one period unit, in Hz.</summary>
    public const double PaulaClock = Crystal / 8.0;

    /// <summary>CIA timer clock, in Hz.</summary>
    public const double CiaClock = PaulaClock / 5.0;

    public const int MinTempo = 32;
    public const int MaxTempo = 255;

    /// <summary>
    /// Ticks per second at a tempo: Protracker loads the CIA timer with 1773447 / BPM, truncated,
    /// and the timer fires one count after reaching zero.
    /// </summary>
    public static double TickRate(int bpm) => CiaClock / (1773447 / bpm + 1);
}
