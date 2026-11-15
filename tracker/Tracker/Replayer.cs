using System;

namespace Tracker;

/// <summary>A channel's replayer state, as Protracker keeps it.</summary>
internal sealed class ChannelState
{
    public int Note;            // the row's period, 12 bits
    public int Command;         // effect << 8 | parameter
    public int SampleNumber;    // 0 to 30
    public SamplePointer Start, LoopStart, WaveStart;
    public ushort Length, RepeatLength; // in words
    public short Period, WantedPeriod;
    public int FineTune;        // 0 to 15
    public int Volume;          // 0 to 64
    public bool PortaDown;
    public byte PortaSpeed;
    public byte VibratoCommand, VibratoPosition, TremoloCommand, TremoloPosition;
    public byte WaveControl;    // vibrato in the low nibble, tremolo in the high
    public byte GlissFunk;      // glissando in the low nibble, funk speed in the high
    public byte SampleOffset;
    public byte FunkOffset;
    public int LoopRow, LoopCount;

    public int Effect => Command >> 8 & 0xF;
    public int Parameter => Command & 0xFF;
}

/// <summary>
/// Protracker 2.3D's replayer: reads the module tick by tick and writes Paula's registers.
/// Channels past the fourth behave the same as the first four.
/// </summary>
internal sealed class Replayer
{
    private readonly Module _module;
    private readonly sbyte[][] _sampleData;
    private readonly PaulaVoice[] _voices;
    private readonly ChannelState[] _channels;
    private readonly bool[] _visited = new bool[128 * Module.Rows];

    private int _breakRow;
    private bool _positionJump, _loopJump;
    private int _patternDelay, _patternDelayLeft;
    private int _lowMask = 0xFF;
    private int _pendingTempo = -1;
    private bool[] _trigger;

    public Replayer(Module module, sbyte[][] sampleData, PaulaVoice[] voices)
    {
        _module = module;
        _sampleData = sampleData;
        _voices = voices;
        _channels = new ChannelState[module.Channels];
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new ChannelState();
        _trigger = new bool[module.Channels];
        Tempo = module.InitialTempo;
    }

    public int Position { get; private set; }
    public int Pattern { get; private set; }

    /// <summary>The next row to read.</summary>
    public int Row { get; private set; }

    public int Tick { get; private set; }
    public int Speed { get; private set; } = 6;
    public int Tempo { get; private set; } = 125;

    /// <summary>The position, pattern and row whose notes were read last.</summary>
    public int ShownPosition { get; private set; }
    public int ShownPattern { get; private set; }
    public int ShownRow { get; private set; }

    /// <summary>Set by E0x: true for on.</summary>
    public bool LedFilter { get; private set; }

    /// <summary>E8x changes loop data when true. Most modules use E8x as a sync marker for demos.</summary>
    public bool KarplusStrong { get; set; }

    /// <summary>F00 was played.</summary>
    public bool Stopped { get; private set; }

    /// <summary>The song has reached its end: the next row to play was already played, or the positions wrapped. Cleared by <see cref="ClearEnd"/>.</summary>
    public bool Ended { get; private set; }

    private bool _wrapped;

    internal ChannelState Channel(int i) => _channels[i];

    /// <summary>Starts playing at a position and row, as Protracker's play command does.</summary>
    public void Start(int position, int row, bool resetSong)
    {
        if (resetSong)
        {
            for (int i = 0; i < _channels.Length; i++)
                _channels[i] = new ChannelState();
            Speed = 6;
            Tempo = _module.InitialTempo;
            LedFilter = false;
        }
        foreach (ChannelState ch in _channels)
        {
            ch.WaveControl = 0;
            ch.GlissFunk = 0;
            ch.FineTune = 0;
            ch.LoopCount = 0;
        }
        foreach (PaulaVoice v in _voices)
        {
            v.Stop();
            v.SetVolume(0);
        }
        _patternDelay = _patternDelayLeft = 0;
        _breakRow = 0;
        _positionJump = _loopJump = false;
        _pendingTempo = -1;
        Stopped = false;
        Position = position < _module.SongLength ? Math.Max(0, position) : 0;
        Pattern = _module.Orders[Position];
        Row = Math.Max(0, Math.Min(Module.Rows - 1, row));
        ShownPosition = Position;
        ShownPattern = Pattern;
        ShownRow = Row;
        Tick = Speed - 1;
        ClearEnd();
    }

    /// <summary>Forgets which rows were played, so the end is found again on the next pass.</summary>
    public void ClearEnd()
    {
        Array.Clear(_visited, 0, _visited.Length);
        Ended = _wrapped = false;
    }

    /// <summary>Plays one tick.</summary>
    public void Step()
    {
        // A tempo set by Fxx takes effect a tick late, as the CIA timer reloads on its next interrupt.
        if (_pendingTempo >= 0)
        {
            if (_pendingTempo >= Timing.MinTempo && _pendingTempo <= Timing.MaxTempo)
                Tempo = _pendingTempo;
            _pendingTempo = -1;
        }

        if (++Tick >= Speed)
        {
            Tick = 0;
            if (_patternDelayLeft == 0)
            {
                ShownPosition = Position;
                ShownPattern = Pattern;
                ShownRow = Row;
                _visited[(Position & 127) * Module.Rows + Row] = true;
                Array.Clear(_trigger, 0, _trigger.Length);
                for (int i = 0; i < _channels.Length; i++)
                {
                    PlayVoice(i);
                    _voices[i].SetVolume(_channels[i].Volume);
                }
                StartDma();
            }
            else
            {
                for (int i = 0; i < _channels.Length; i++)
                    CheckEffects(i);
            }

            Row++;
            if (_patternDelay > 0)
            {
                _patternDelayLeft = _patternDelay;
                _patternDelay = 0;
            }
            if (_patternDelayLeft > 0 && --_patternDelayLeft > 0)
                Row--;
            if (_loopJump)
            {
                Row = _breakRow;
                _breakRow = 0;
                _loopJump = false;
            }
            if (Row >= Module.Rows || _positionJump)
                NextPosition();
        }
        else
        {
            for (int i = 0; i < _channels.Length; i++)
                CheckEffects(i);
            if (_positionJump)
                NextPosition();
        }

        // On the last tick of a row, check whether the next row has been played before.
        if (Tick == Speed - 1 && _patternDelayLeft == 0 && (_wrapped || _visited[(Position & 127) * Module.Rows + Row]))
            Ended = true;
    }

    private void NextPosition()
    {
        Row = _breakRow;
        _breakRow = 0;
        _positionJump = false;
        Position = (Position + 1) & 127;
        if (Position >= _module.SongLength)
        {
            Position = 0;
            _wrapped = true;
        }
        Pattern = _module.Orders[Position];
    }

    private void StartDma()
    {
        for (int i = 0; i < _channels.Length; i++)
        {
            if (_trigger[i])
                _voices[i].Start();
        }
        // These are latched when the current sample ends.
        for (int i = 0; i < _channels.Length; i++)
        {
            _voices[i].SetPointer(_channels[i].LoopStart);
            _voices[i].SetLength(_channels[i].RepeatLength);
        }
    }

    // ---- Tick 0 ----

    private void PlayVoice(int i)
    {
        ChannelState ch = _channels[i];
        PaulaVoice voice = _voices[i];
        if (ch.Note == 0 && ch.Command == 0)
            voice.SetPeriod(ch.Period);

        Note note = _module.GetNote(Pattern, Row, i);
        ch.Note = note.Period;
        ch.Command = note.Effect << 8 | note.Parameter;

        if (note.Sample >= 1 && note.Sample <= 31)
        {
            Sample s = _module.Samples[note.Sample - 1];
            ch.SampleNumber = note.Sample - 1;
            ch.Start = new SamplePointer(_sampleData[ch.SampleNumber], 0);
            ch.FineTune = s.FineTune;
            ch.Volume = s.Volume;
            ch.Length = (ushort)(s.Length / 2);
            ch.RepeatLength = (ushort)(s.LoopLength / 2);
            int repeat = s.LoopStart / 2;
            if (repeat > 0)
            {
                // The sample is cut at the end of its loop.
                ch.LoopStart = ch.Start.Add(repeat * 2);
                ch.Length = (ushort)(repeat + ch.RepeatLength);
            }
            else
            {
                ch.LoopStart = ch.Start;
            }
            ch.WaveStart = ch.LoopStart;
            if (ch.Length == 0)
                ch.LoopStart = ch.WaveStart = Silence;
        }

        if ((ch.Note & 0xFFF) == 0)
        {
            CheckMoreEffects(i);
            return;
        }
        if ((ch.Command & 0xFF0) == 0xE50)
        {
            ch.FineTune = ch.Command & 0xF;
            SetPeriod(i);
            return;
        }
        switch (ch.Effect)
        {
            case 0x3:
            case 0x5:
                SetTonePorta(ch);
                CheckMoreEffects(i);
                break;
            case 0x9:
                // The offset is applied here and again by SetPeriod's effect check, after the DMA has its pointer.
                CheckMoreEffects(i);
                SetPeriod(i);
                break;
            default:
                SetPeriod(i);
                break;
        }
    }

    private static readonly SamplePointer Silence = new(new sbyte[2], 0);

    private void SetPeriod(int i)
    {
        ChannelState ch = _channels[i];
        PaulaVoice voice = _voices[i];
        int note = Tables.NoteIndex(ch.Note & 0xFFF);
        ch.Period = Tables.Periods[ch.FineTune * Tables.RowLength + note];

        if ((ch.Command & 0xFF0) != 0xED0)
        {
            voice.Stop();
            if ((ch.WaveControl & 0x04) == 0)
                ch.VibratoPosition = 0;
            if ((ch.WaveControl & 0x40) == 0)
                ch.TremoloPosition = 0;
            voice.SetLength(ch.Length);
            voice.SetPointer(ch.Start);
            if (ch.Start.IsNull)
            {
                ch.LoopStart = SamplePointer.Null;
                voice.SetLength(1);
                ch.RepeatLength = 1;
            }
            voice.SetPeriod(ch.Period);
            _trigger[i] = true;
        }
        CheckMoreEffects(i);
    }

    private void CheckMoreEffects(int i)
    {
        ChannelState ch = _channels[i];
        switch (ch.Effect)
        {
            case 0x9:
                SampleOffsetEffect(ch);
                return;
            case 0xB:
                Position = ch.Parameter - 1; // B00 lands on position 0 after the increment
                _breakRow = 0;
                _positionJump = true;
                return;
            case 0xD:
                _breakRow = (ch.Parameter >> 4) * 10 + (ch.Parameter & 0xF);
                if (_breakRow > 63)
                    _breakRow = 0;
                _positionJump = true;
                return;
            case 0xE:
                ExtendedEffect(i);
                return;
            case 0xF:
                SetSpeed(ch.Parameter);
                return;
            case 0xC:
                ch.Volume = Math.Min(ch.Parameter, 64);
                return;
            default:
                _voices[i].SetPeriod(ch.Period);
                return;
        }
    }

    private void SampleOffsetEffect(ChannelState ch)
    {
        if (ch.Parameter > 0)
            ch.SampleOffset = (byte)ch.Parameter;
        int offset = ch.SampleOffset << 7; // in words
        if (offset < ch.Length)
        {
            ch.Length = (ushort)(ch.Length - offset);
            ch.Start = ch.Start.Add(offset * 2);
        }
        else
        {
            ch.Length = 1;
        }
    }

    private void SetSpeed(int parameter)
    {
        if (parameter == 0)
            Stopped = true;
        else if (parameter < 32)
        {
            Speed = parameter;
            Tick = 0;
        }
        else
            _pendingTempo = parameter;
    }

    private void SetTonePorta(ChannelState ch)
    {
        int note = ch.Note & 0xFFF;
        int row = ch.FineTune * Tables.RowLength;
        int i = 0;
        while (note < Tables.Periods[row + i])
        {
            if (++i >= Tables.RowLength)
            {
                i = 35;
                break;
            }
        }
        if ((ch.FineTune & 8) != 0 && i > 0)
            i--;
        ch.WantedPeriod = Tables.Periods[row + i];
        ch.PortaDown = false;
        if (ch.Period == ch.WantedPeriod)
            ch.WantedPeriod = 0;
        else if (ch.Period > ch.WantedPeriod)
            ch.PortaDown = true;
    }

    // ---- Ticks after 0, and every tick of a pattern delay ----

    private void CheckEffects(int i)
    {
        ChannelState ch = _channels[i];
        PaulaVoice voice = _voices[i];
        UpdateFunk(ch);
        if ((ch.Command & 0xFFF) != 0)
        {
            switch (ch.Effect)
            {
                case 0x0: Arpeggio(i); break;
                case 0x1: PortaUp(i); break;
                case 0x2: PortaDown(i); break;
                case 0x3: TonePortamento(i); break;
                case 0x4: Vibrato(i); break;
                case 0x5:
                    TonePortaNoChange(i);
                    VolumeSlide(ch);
                    break;
                case 0x6:
                    VibratoNoChange(i);
                    VolumeSlide(ch);
                    break;
                case 0xE: ExtendedEffect(i); break;
                default:
                    voice.SetPeriod(ch.Period);
                    if (ch.Effect == 0x7)
                    {
                        Tremolo(i); // sets the volume itself
                        return;
                    }
                    if (ch.Effect == 0xA)
                        VolumeSlide(ch);
                    break;
            }
        }
        voice.SetVolume(ch.Volume);
    }

    private void Arpeggio(int i)
    {
        ChannelState ch = _channels[i];
        int step = Tick % 3;
        if (step == 0)
        {
            _voices[i].SetPeriod(ch.Period);
            return;
        }
        int add = step == 1 ? ch.Parameter >> 4 : ch.Parameter & 0xF;
        int row = ch.FineTune * Tables.RowLength;
        for (int note = 0; note < Tables.RowLength; note++)
        {
            if (ch.Period >= Tables.Periods[row + note])
            {
                // On finetune -1 this can read past the table, into the words after it.
                _voices[i].SetPeriod(Tables.Periods[row + note + add]);
                return;
            }
        }
    }

    private void PortaUp(int i)
    {
        ChannelState ch = _channels[i];
        ch.Period -= (short)(ch.Parameter & _lowMask);
        _lowMask = 0xFF;
        // The limit is checked on the low 12 bits, so a period that wraps below 0 isn't caught.
        if ((ch.Period & 0xFFF) < 113)
            ch.Period = (short)((ch.Period & 0xF000) | 113);
        _voices[i].SetPeriod(ch.Period & 0xFFF);
    }

    private void PortaDown(int i)
    {
        ChannelState ch = _channels[i];
        ch.Period += (short)(ch.Parameter & _lowMask);
        _lowMask = 0xFF;
        if ((ch.Period & 0xFFF) > 856)
            ch.Period = (short)((ch.Period & 0xF000) | 856);
        _voices[i].SetPeriod(ch.Period & 0xFFF);
    }

    private void TonePortamento(int i)
    {
        ChannelState ch = _channels[i];
        if (ch.Parameter > 0)
        {
            ch.PortaSpeed = (byte)ch.Parameter;
            ch.Command &= 0xFF00;
        }
        TonePortaNoChange(i);
    }

    private void TonePortaNoChange(int i)
    {
        ChannelState ch = _channels[i];
        if (ch.WantedPeriod <= 0)
            return;
        if (ch.PortaDown)
        {
            ch.Period -= ch.PortaSpeed;
            if (ch.Period <= ch.WantedPeriod)
            {
                ch.Period = ch.WantedPeriod;
                ch.WantedPeriod = 0;
            }
        }
        else
        {
            ch.Period += ch.PortaSpeed;
            if (ch.Period >= ch.WantedPeriod)
            {
                ch.Period = ch.WantedPeriod;
                ch.WantedPeriod = 0;
            }
        }

        if ((ch.GlissFunk & 0xF) == 0)
        {
            _voices[i].SetPeriod(ch.Period);
            return;
        }
        // Glissando: round to the nearest semitone at or below.
        int row = ch.FineTune * Tables.RowLength;
        int note = 0;
        while (ch.Period < Tables.Periods[row + note])
        {
            if (++note >= Tables.RowLength)
            {
                note = 35;
                break;
            }
        }
        _voices[i].SetPeriod(Tables.Periods[row + note]);
    }

    private void Vibrato(int i)
    {
        ChannelState ch = _channels[i];
        if ((ch.Command & 0x0F) > 0)
            ch.VibratoCommand = (byte)((ch.VibratoCommand & 0xF0) | (ch.Command & 0x0F));
        if ((ch.Command & 0xF0) > 0)
            ch.VibratoCommand = (byte)((ch.Command & 0xF0) | (ch.VibratoCommand & 0x0F));
        VibratoNoChange(i);
    }

    private void VibratoNoChange(int i)
    {
        ChannelState ch = _channels[i];
        int amount = Waveform(ch.WaveControl & 3, ch.VibratoPosition, ch.VibratoPosition);
        amount = (amount * (ch.VibratoCommand & 0xF)) >> 7;
        int period = ch.VibratoPosition < 128 ? ch.Period + amount : ch.Period - amount;
        _voices[i].SetPeriod(period & 0xFFFF);
        ch.VibratoPosition += (byte)((ch.VibratoCommand >> 2) & 0x3C);
    }

    private void Tremolo(int i)
    {
        ChannelState ch = _channels[i];
        if ((ch.Command & 0x0F) > 0)
            ch.TremoloCommand = (byte)((ch.TremoloCommand & 0xF0) | (ch.Command & 0x0F));
        if ((ch.Command & 0xF0) > 0)
            ch.TremoloCommand = (byte)((ch.Command & 0xF0) | (ch.TremoloCommand & 0x0F));
        // Protracker's ramp-down picks its direction from the vibrato position.
        int amount = Waveform(ch.WaveControl >> 4 & 3, ch.TremoloPosition, ch.VibratoPosition);
        amount = (amount * (ch.TremoloCommand & 0xF)) >> 6;
        int volume = ch.TremoloPosition < 128 ? Math.Min(64, ch.Volume + amount) : Math.Max(0, ch.Volume - amount);
        _voices[i].SetVolume(volume);
        ch.TremoloPosition += (byte)((ch.TremoloCommand >> 2) & 0x3C);
    }

    /// <summary>Waveform magnitude 0 to 255: sine, ramp (direction from <paramref name="rampPosition"/>) or square.</summary>
    private static int Waveform(int type, byte position, byte rampPosition)
    {
        int step = position >> 2 & 0x1F;
        switch (type)
        {
            case 0:
                return Tables.Sine[step];
            case 1:
                return rampPosition < 128 ? step << 3 : 255 - (step << 3);
            default:
                return 255;
        }
    }

    private static void VolumeSlide(ChannelState ch)
    {
        int parameter = ch.Parameter;
        if ((parameter & 0xF0) == 0)
            ch.Volume = Math.Max(0, ch.Volume - (parameter & 0xF));
        else
            ch.Volume = Math.Min(64, ch.Volume + (parameter >> 4));
    }

    // ---- Exx ----

    private void ExtendedEffect(int i)
    {
        ChannelState ch = _channels[i];
        int x = ch.Command & 0xF;
        switch (ch.Command >> 4 & 0xF)
        {
            case 0x0:
                if (Tick == 0)
                    LedFilter = (x & 1) == 0;
                break;
            case 0x1:
                if (Tick == 0)
                {
                    _lowMask = 0xF;
                    PortaUp(i);
                }
                break;
            case 0x2:
                if (Tick == 0)
                {
                    _lowMask = 0xF;
                    PortaDown(i);
                }
                break;
            case 0x3:
                ch.GlissFunk = (byte)((ch.GlissFunk & 0xF0) | x);
                break;
            case 0x4:
                ch.WaveControl = (byte)((ch.WaveControl & 0xF0) | x);
                break;
            case 0x5:
                ch.FineTune = x;
                break;
            case 0x6:
                PatternLoop(ch, x);
                break;
            case 0x7:
                ch.WaveControl = (byte)((x << 4) | (ch.WaveControl & 0xF));
                break;
            case 0x8:
                if (KarplusStrong)
                    Smooth(ch);
                break;
            case 0x9:
                if (x > 0 && !(Tick == 0 && (ch.Note & 0xFFF) > 0) && Tick % x == 0)
                    Retrigger(i);
                break;
            case 0xA:
                if (Tick == 0)
                    ch.Volume = Math.Min(64, ch.Volume + x);
                break;
            case 0xB:
                if (Tick == 0)
                    ch.Volume = Math.Max(0, ch.Volume - x);
                break;
            case 0xC:
                if (Tick == x)
                    ch.Volume = 0;
                break;
            case 0xD:
                if (Tick == x && (ch.Note & 0xFFF) > 0)
                    Retrigger(i);
                break;
            case 0xE:
                if (Tick == 0 && _patternDelayLeft == 0)
                    _patternDelay = x + 1;
                break;
            case 0xF:
                if (Tick == 0)
                {
                    ch.GlissFunk = (byte)((x << 4) | (ch.GlissFunk & 0xF));
                    if ((ch.GlissFunk & 0xF0) > 0)
                        UpdateFunk(ch);
                }
                break;
        }
    }

    private void PatternLoop(ChannelState ch, int count)
    {
        if (Tick != 0)
            return;
        if (count == 0)
        {
            ch.LoopRow = ShownRow;
            return;
        }
        // The counter is per channel, but the jump target is shared with Dxx.
        if (ch.LoopCount == 0)
            ch.LoopCount = count;
        else if (--ch.LoopCount == 0)
            return;
        _breakRow = ch.LoopRow;
        _loopJump = true;
        for (int row = _breakRow; row <= ShownRow; row++)
            _visited[(Position & 127) * Module.Rows + row] = false;
    }

    private void Retrigger(int i)
    {
        ChannelState ch = _channels[i];
        PaulaVoice voice = _voices[i];
        voice.Stop();
        voice.SetPointer(ch.Start);
        voice.SetLength(ch.Length);
        voice.SetPeriod(ch.Period);
        voice.Start();
        voice.SetPointer(ch.LoopStart);
        voice.SetLength(ch.RepeatLength);
    }

    /// <summary>EFx: inverts one loop byte each time the funk counter passes 128, walking through the loop.</summary>
    private static void UpdateFunk(ChannelState ch)
    {
        int speed = ch.GlissFunk >> 4;
        if (speed == 0)
            return;
        ch.FunkOffset += Tables.Funk[speed];
        if (ch.FunkOffset < 128)
            return;
        ch.FunkOffset = 0;
        if (ch.LoopStart.IsNull || ch.WaveStart.IsNull)
            return;
        SamplePointer next = ch.WaveStart.Add(1);
        if (next.Data != ch.LoopStart.Data || next.Offset >= ch.LoopStart.Offset + ch.RepeatLength * 2)
            next = ch.LoopStart;
        ch.WaveStart = next;
        next[0] = (sbyte)(-1 - next[0]);
    }

    /// <summary>E8x (Karplus-Strong): averages each loop byte with the next, wrapping at 64 KB.</summary>
    private static void Smooth(ChannelState ch)
    {
        if (ch.LoopStart.IsNull)
            return;
        SamplePointer loop = ch.LoopStart;
        int last = (ushort)(ch.RepeatLength * 2 - 2);
        for (int n = 0; n <= last; n++)
            loop[n] = (sbyte)((loop[n] + loop[n + 1]) >> 1);
        loop[last + 1] = (sbyte)((loop[last + 1] + loop[0]) >> 1);
    }
}
