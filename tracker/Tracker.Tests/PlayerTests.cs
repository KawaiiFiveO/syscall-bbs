using System;
using Xunit;

namespace Tracker.Tests;

public class PlayerTests
{
    private static Module SquareAt(int period, int effect = 0, int parameter = 0)
    {
        var b = new ModuleBuilder();
        int s = b.Square(32);
        int p = b.Pattern();
        b.Note(p, 0, 0, period, s, effect, parameter);
        return b.Build();
    }

    private static float[] Render(ModulePlayer player, int frames)
    {
        var buffer = new float[frames * 2];
        player.Render(buffer, frames);
        return buffer;
    }

    [Fact]
    public void PitchFollowsThePaulaClock()
    {
        // A 32-byte square at period 428 repeats at PaulaClock / 428 / 32 Hz.
        var player = new ModulePlayer(SquareAt(428)) { Model = AmigaModel.None, StereoSeparation = 1f };
        float[] output = Render(player, 48000);
        int crossings = 0;
        for (int i = 2 * 4800; i < output.Length - 2; i += 2)
        {
            if (output[i] < 0 && output[i + 2] >= 0)
                crossings++;
        }
        double seconds = (48000 - 4800 - 1) / 48000.0;
        double expected = Timing.PaulaClock / 428 / 32;
        Assert.InRange(crossings / seconds, expected - 2, expected + 2);
    }

    [Fact]
    public void ChannelOneIsLeft()
    {
        var player = new ModulePlayer(SquareAt(428)) { Model = AmigaModel.None, StereoSeparation = 1f };
        float[] output = Render(player, 4800);
        double left = 0, right = 0;
        for (int i = 0; i < output.Length; i += 2)
        {
            left += Math.Abs(output[i]);
            right += Math.Abs(output[i + 1]);
        }
        Assert.True(left > 100 * right, $"left {left}, right {right}");
    }

    [Fact]
    public void BandLimitedOutputStaysInRange()
    {
        var player = new ModulePlayer(SquareAt(113)) { Model = AmigaModel.None, StereoSeparation = 1f };
        float[] output = Render(player, 48000);
        foreach (float f in output)
            Assert.InRange(f, -1.2f, 1.2f);
    }

    [Fact]
    public void FinishesAfterTheEndUnlessLooping()
    {
        Module m = SquareAt(428);
        double song = SongLength.Measure(m).TotalSeconds;
        var player = new ModulePlayer(m);
        int total = 0;
        var buffer = new float[4800 * 2];
        while (total < 48000 * 20)
        {
            int got = player.Render(buffer, 4800);
            total += got;
            if (got < 4800)
                break;
        }
        Assert.True(player.Finished);
        Assert.InRange(total / 48000.0, song, song + 1.1);

        var looping = new ModulePlayer(m) { Loop = true };
        buffer = new float[48000 * 2];
        for (int i = 0; i < 10; i++)
            Assert.Equal(48000, looping.Render(buffer, 48000));
        Assert.False(looping.Finished);
    }

    [Fact]
    public void ViewsTrackTheRowAtAFrame()
    {
        Module m = SquareAt(428);
        var player = new ModulePlayer(m);
        Render(player, 48000);
        PlayView view = player.CreateView();
        Assert.True(player.GetView(0, view));
        Assert.Equal(0, view.Row);
        // At 125 BPM and speed 6 a row lasts 6 ticks of 1/50 s.
        double row = 6 / Timing.TickRate(125);
        Assert.True(player.GetView((long)(row * 5.5 * 48000), view));
        Assert.Equal(5, view.Row);
    }

    [Fact]
    public void MutedChannelsAreSilent()
    {
        var player = new ModulePlayer(SquareAt(428)) { Model = AmigaModel.None };
        player.SetMuted(0, true);
        foreach (float f in Render(player, 4800))
            Assert.Equal(0f, f, 5);
    }

    [Fact]
    public void RestartResetsSampleData()
    {
        Module m = SquareAt(428, 0xE, 0xFF);
        var player = new ModulePlayer(m);
        Render(player, 48000);
        player.Restart();
        float[] first = Render(player, 2000);
        player.Restart();
        float[] second = Render(player, 2000);
        Assert.Equal(first, second);
    }
}
