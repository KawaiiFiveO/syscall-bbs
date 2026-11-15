using Xunit;

namespace Tracker.Tests;

public class ReplayerTests
{
    private const int C2 = 428, C3 = 214, E2 = 339, G2 = 285;

    private static (ModuleBuilder Builder, int Pattern, int Sample) Song()
    {
        var b = new ModuleBuilder();
        int s = b.Square();
        int p = b.Pattern();
        b.Order(p);
        return (b, p, s);
    }

    [Fact]
    public void NoteSetsPeriodAndSampleVolume()
    {
        var (b, p, s) = Song();
        b.Sample(new sbyte[32], volume: 40);
        b.Note(p, 0, 0, C2, 2);
        var run = new Stepper(b.Build());
        run.Step(1);
        Assert.Equal(C2, run.Voices[0].Period);
        Assert.Equal(40, run.Voices[0].Volume);
        Assert.True(run.Voices[0].Active);
    }

    [Fact]
    public void FineTuneSelectsItsRow()
    {
        var b = new ModuleBuilder();
        b.Sample(new sbyte[32], fineTune: 15); // -1
        int p = b.Pattern();
        b.Note(p, 0, 0, C2, 1);
        var run = new Stepper(b.Build());
        run.Step(1);
        Assert.Equal(431, run.Voices[0].Period);
    }

    [Fact]
    public void VolumeSlideRunsAfterTickZero()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, C2, s, 0xC, 0x20);
        b.Note(p, 1, 0, 0, 0, 0xA, 0x04);
        var run = new Stepper(b.Build());
        int[] volumes = run.Volumes(12);
        Assert.Equal(new[] { 32, 32, 32, 32, 32, 32, 32, 28, 24, 20, 16, 12 }, volumes);
    }

    [Fact]
    public void PortaUpStopsAt113()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, 120, s, 0x1, 0x04);
        var run = new Stepper(b.Build());
        Assert.Equal(new[] { 120, 116, 113, 113 }, run.Periods(4));
    }

    [Fact]
    public void PortaUpPastZeroWrapsUnclamped()
    {
        // Protracker checks the low 12 bits only, so 120 - 255 isn't caught.
        var (b, p, s) = Song();
        b.Note(p, 0, 0, 120, s, 0x1, 0xFF);
        var run = new Stepper(b.Build());
        int[] periods = run.Periods(2);
        Assert.Equal((120 - 255) & 0xFFF, periods[1]);
    }

    [Fact]
    public void ArpeggioCyclesThroughSemitones()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, C2, s, 0x0, 0x47);
        var run = new Stepper(b.Build());
        Assert.Equal(new[] { C2, E2, G2, C2, E2, G2 }, run.Periods(6));
    }

    [Fact]
    public void TonePortamentoReachesTheNote()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, C2, s);
        b.Note(p, 1, 0, C3, 0, 0x3, 0x50);
        var run = new Stepper(b.Build());
        run.Step(6);
        int[] periods = run.Periods(6);
        Assert.Equal(new[] { C2, 348, 268, C3, C3, C3 }, periods);
    }

    [Fact]
    public void VibratoUsesTheSineTable()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, C2, s, 0x4, 0x48); // speed 4, depth 8
        var run = new Stepper(b.Build());
        int[] periods = run.Periods(4);
        // Position steps by 16 per tick: table entries 0, 4, 8; amount = sine * 8 >> 7.
        Assert.Equal(new[] { C2, C2 + (0 * 8 >> 7), C2 + (97 * 8 >> 7), C2 + (180 * 8 >> 7) }, periods);
    }

    [Fact]
    public void TempoChangeTakesEffectATickLate()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, C2, s, 0xF, 0x80);
        var run = new Stepper(b.Build());
        run.Step(1);
        Assert.Equal(125, run.Replayer.Tempo);
        run.Step(1);
        Assert.Equal(128, run.Replayer.Tempo);
    }

    [Fact]
    public void SpeedChangeIsImmediate()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, C2, s, 0xF, 0x02);
        var run = new Stepper(b.Build());
        run.Step(1);
        Assert.Equal(2, run.Replayer.Speed);
        run.Step(2);
        Assert.Equal(1, run.Replayer.ShownRow);
    }

    [Fact]
    public void PatternDelayRepeatsTheRow()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, C2, s, 0xE, 0xE2);
        var run = new Stepper(b.Build());
        run.Step(18);
        Assert.Equal(0, run.Replayer.ShownRow);
        run.Step(1);
        Assert.Equal(1, run.Replayer.ShownRow);
    }

    [Fact]
    public void PatternLoopRepeats()
    {
        var (b, p, s) = Song();
        b.Note(p, 1, 0, 0, 0, 0xE, 0x60);
        b.Note(p, 2, 0, 0, 0, 0xE, 0x62);
        var run = new Stepper(b.Build());
        var rows = new System.Collections.Generic.List<int>();
        for (int i = 0; i < 8; i++)
        {
            run.Step(6);
            rows.Add(run.Replayer.ShownRow);
        }
        Assert.Equal(new[] { 0, 1, 2, 1, 2, 1, 2, 3 }, rows);
    }

    [Fact]
    public void BreakAndJumpOnOneRowGoToTheBreakRowOfTheJumpPosition()
    {
        var b = new ModuleBuilder();
        int s = b.Square();
        int p0 = b.Pattern(), p1 = b.Pattern(), p2 = b.Pattern();
        b.Order(p0, p1, p2);
        b.Note(p0, 0, 0, 0, 0, 0xB, 0x02);
        b.Note(p0, 0, 1, 0, 0, 0xD, 0x16);
        var run = new Stepper(b.Build());
        run.Step(7);
        Assert.Equal(2, run.Replayer.ShownPosition);
        Assert.Equal(16, run.Replayer.ShownRow);
    }

    [Fact]
    public void NoteCutAndNoteDelay()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, C2, s, 0xE, 0xC2);
        b.Note(p, 0, 1, C3, s, 0xE, 0xD3);
        var run = new Stepper(b.Build());
        run.Step(1);
        Assert.False(run.Voices[1].Active);
        run.Step(2);
        Assert.Equal(0, run.Voices[0].Volume);
        Assert.False(run.Voices[1].Active);
        run.Step(1);
        Assert.True(run.Voices[1].Active);
        Assert.Equal(C3, run.Voices[1].Period);
    }

    [Fact]
    public void SampleOffsetIsAppliedTwiceToTheChannel()
    {
        // 9xx with a note: the DMA gets one offset, the channel keeps two (seen by later retriggers).
        var b = new ModuleBuilder();
        int s = b.Sample(new sbyte[2048]);
        int p = b.Pattern();
        b.Note(p, 0, 0, C2, s, 0x9, 0x02);
        var run = new Stepper(b.Build());
        run.Step(1);
        ChannelState ch = run.Replayer.Channel(0);
        Assert.Equal(2 * 512, ch.Start.Offset);
        Assert.Equal(1024 - 2 * 256, ch.Length);
    }

    [Fact]
    public void InstrumentWithoutNoteSwapsTheLoop()
    {
        var b = new ModuleBuilder();
        int first = b.Square();
        int second = b.Sample(new sbyte[64], loopStart: 32, loopLength: 32);
        int p = b.Pattern();
        b.Note(p, 0, 0, C2, first);
        b.Note(p, 1, 0, 0, second);
        var run = new Stepper(b.Build());
        run.Step(7);
        ChannelState ch = run.Replayer.Channel(0);
        Assert.Same(run.Data[second - 1], ch.LoopStart.Data);
        Assert.Equal(C2, run.Voices[0].Period);
    }

    [Fact]
    public void F00Stops()
    {
        var (b, p, s) = Song();
        b.Note(p, 3, 0, 0, 0, 0xF, 0x00);
        var run = new Stepper(b.Build());
        run.Step(18);
        Assert.False(run.Replayer.Stopped);
        run.Step(1);
        Assert.True(run.Replayer.Stopped);
    }

    [Fact]
    public void EndIsFoundOnTheLastTickOfTheLastRow()
    {
        var (b, p, s) = Song();
        var run = new Stepper(b.Build());
        run.Step(64 * 6 - 1);
        Assert.False(run.Replayer.Ended);
        run.Step(1);
        Assert.True(run.Replayer.Ended);
    }

    [Fact]
    public void FunkRepeatInvertsLoopBytes()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, C2, s, 0xE, 0xFF); // speed 15: one byte per tick
        var run = new Stepper(b.Build());
        sbyte before = run.Data[s - 1][1];
        run.Step(1);
        Assert.Equal((sbyte)(-1 - before), run.Data[s - 1][1]);
    }

    [Fact]
    public void LedFilterFollowsE0x()
    {
        var (b, p, s) = Song();
        b.Note(p, 0, 0, 0, 0, 0xE, 0x00);
        b.Note(p, 1, 0, 0, 0, 0xE, 0x01);
        var run = new Stepper(b.Build());
        run.Step(1);
        Assert.True(run.Replayer.LedFilter);
        run.Step(6);
        Assert.False(run.Replayer.LedFilter);
    }
}
