using System;

namespace Tracker;

/// <summary>What one channel was doing at a tick.</summary>
public struct ChannelView
{
    /// <summary>The period sent to Paula.</summary>
    public int Period;

    /// <summary>Paula volume, 0 to 64.</summary>
    public int Volume;

    /// <summary>Peak output level over the tick, 0 to 1.</summary>
    public float Level;

    /// <summary>Sample last selected, 1 to 31, or 0.</summary>
    public int Sample;
}

/// <summary>The song position at a tick, kept so a display can show what is being heard.</summary>
public sealed class PlayView
{
    internal PlayView(int channels) => Channels = new ChannelView[channels];

    /// <summary>The output frame where this tick starts.</summary>
    public long Frame { get; internal set; }

    public int Position { get; internal set; }
    public int Pattern { get; internal set; }
    public int Row { get; internal set; }
    public int Tick { get; internal set; }
    public int Speed { get; internal set; }
    public int Tempo { get; internal set; }

    /// <summary>Song time at this tick.</summary>
    public TimeSpan Elapsed { get; internal set; }

    public ChannelView[] Channels { get; }

    internal void CopyTo(PlayView other)
    {
        other.Frame = Frame;
        other.Position = Position;
        other.Pattern = Pattern;
        other.Row = Row;
        other.Tick = Tick;
        other.Speed = Speed;
        other.Tempo = Tempo;
        other.Elapsed = Elapsed;
        Array.Copy(Channels, other.Channels, Channels.Length);
    }
}

/// <summary>
/// Plays a module as interleaved stereo float samples. <see cref="Render"/> can run on an audio
/// thread while other members are called from another thread.
/// </summary>
public sealed class ModulePlayer
{
    private const int HistorySize = 1024;
    private const double TailSeconds = 1.0;

    private readonly object _gate = new();
    private readonly sbyte[][] _sampleData;
    private readonly PaulaVoice[] _voices;
    private readonly Replayer _replayer;
    private readonly PlayView[] _history;
    private readonly bool[] _muted;
    private OutputFilters _filters;
    private AmigaModel _model = AmigaModel.A500;
    private float _separation = 0.2f;
    private long _frames;
    private long _historyCount;
    private double _untilTick;
    private double _songSeconds;
    private bool _tail;
    private long _tailFrames, _tailLength;
    private bool _finished;

    public ModulePlayer(Module module, int sampleRate = 48000)
    {
        Module = module ?? throw new ArgumentNullException(nameof(module));
        if (sampleRate < 32000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Paula emulation needs at least 32000 Hz");
        SampleRate = sampleRate;
        _sampleData = new sbyte[module.Samples.Length][];
        _voices = new PaulaVoice[module.Channels];
        for (int i = 0; i < _voices.Length; i++)
            _voices[i] = new PaulaVoice(sampleRate);
        _muted = new bool[module.Channels];
        CopySamples();
        _replayer = new Replayer(module, _sampleData, _voices);
        _history = new PlayView[HistorySize];
        for (int i = 0; i < HistorySize; i++)
            _history[i] = new PlayView(module.Channels);
        _filters = new OutputFilters(sampleRate, _model);
        _tailLength = (long)(TailSeconds * sampleRate);
        Restart();
    }

    public Module Module { get; }
    public int SampleRate { get; }

    /// <summary>Keep playing from the song's loop point when it ends. When false, playback finishes at the end.</summary>
    public bool Loop { get; set; }

    /// <summary>Multiplies the output. Default 1: four channels at full volume reach full scale.</summary>
    public float Gain { get; set; } = 1f;

    /// <summary>Lets E8x change sample data, as Protracker does. Off by default; most modules use E8x as a sync marker.</summary>
    public bool KarplusStrong
    {
        get => _replayer.KarplusStrong;
        set
        {
            lock (_gate)
                _replayer.KarplusStrong = value;
        }
    }

    /// <summary>Amiga channels are hard left or right (left, right, right, left). 0 is mono, 1 is full Amiga panning. Default 0.2.</summary>
    public float StereoSeparation
    {
        get => _separation;
        set => _separation = Math.Max(0f, Math.Min(1f, value));
    }

    /// <summary>Output filters to model. Default A500.</summary>
    public AmigaModel Model
    {
        get => _model;
        set
        {
            lock (_gate)
            {
                bool led = _filters.Led;
                _model = value;
                _filters = new OutputFilters(SampleRate, value) { Led = led };
            }
        }
    }

    /// <summary>True once playback has ended: after the song's end when <see cref="Loop"/> is off, or after F00.</summary>
    public bool Finished => _finished;

    /// <summary>Frames rendered since the last restart.</summary>
    public long FramesRendered => _frames;

    public bool IsMuted(int channel) => _muted[channel];

    public void SetMuted(int channel, bool muted) => _muted[channel] = muted;

    /// <summary>Starts the song from the beginning, with fresh sample data.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            CopySamples();
            foreach (PaulaVoice v in _voices)
                v.Reset();
            _filters.Reset();
            _replayer.Start(0, 0, resetSong: true);
            ResetClock();
        }
    }

    /// <summary>Jumps to a position, keeping speed, tempo and channel state, as Protracker does.</summary>
    public void Seek(int position, int row = 0)
    {
        lock (_gate)
        {
            _replayer.Start(Math.Max(0, Math.Min(Module.SongLength - 1, position)), row, resetSong: false);
            ResetClock();
        }
    }

    /// <summary>The current position, for seeking relative to it.</summary>
    public int Position
    {
        get
        {
            lock (_gate)
                return _replayer.ShownPosition;
        }
    }

    private void ResetClock()
    {
        _frames = 0;
        _historyCount = 0;
        _untilTick = 0;
        _songSeconds = 0;
        _tail = false;
        _finished = false;
    }

    private void CopySamples()
    {
        for (int i = 0; i < _sampleData.Length; i++)
        {
            sbyte[] source = Module.Samples[i].Data;
            if (_sampleData[i] == null || _sampleData[i].Length != source.Length)
                _sampleData[i] = new sbyte[source.Length];
            Array.Copy(source, _sampleData[i], source.Length);
        }
    }

    /// <summary>
    /// Copies the state of the tick that contains <paramref name="frame"/> into <paramref name="into"/>,
    /// or the oldest kept if it's older. Returns false before the first tick.
    /// </summary>
    public bool GetView(long frame, PlayView into)
    {
        if (into == null || into.Channels.Length != Module.Channels)
            throw new ArgumentException("the view must come from CreateView", nameof(into));
        lock (_gate)
        {
            if (_historyCount == 0)
                return false;
            long newest = _historyCount - 1;
            long oldest = Math.Max(0, _historyCount - HistorySize);
            long pick = newest;
            while (pick > oldest && _history[pick % HistorySize].Frame > frame)
                pick--;
            _history[pick % HistorySize].CopyTo(into);
            return true;
        }
    }

    /// <summary>A view sized for this module, for <see cref="GetView"/>.</summary>
    public PlayView CreateView() => new(Module.Channels);

    /// <summary>
    /// Writes up to <paramref name="frames"/> frames of interleaved stereo into <paramref name="buffer"/>.
    /// Returns the frames written; fewer than asked means playback has finished.
    /// </summary>
    public int Render(float[] buffer, int frames)
    {
        if (buffer.Length < frames * 2)
            throw new ArgumentException("the buffer holds fewer than frames * 2 samples", nameof(buffer));
        lock (_gate)
        {
            int written = 0;
            while (written < frames && !_finished)
            {
                if (_untilTick <= 0 && !_tail)
                {
                    DoTick();
                    continue;
                }
                int chunk = frames - written;
                if (!_tail)
                    chunk = Math.Min(chunk, (int)Math.Ceiling(_untilTick));
                else
                    chunk = (int)Math.Min(chunk, _tailLength - _tailFrames);
                Mix(buffer, written, chunk);
                written += chunk;
                _frames += chunk;
                if (_tail)
                {
                    _tailFrames += chunk;
                    if (_tailFrames >= _tailLength)
                        _finished = true;
                }
                else
                {
                    _untilTick -= chunk;
                }
            }
            return written;
        }
    }

    private void DoTick()
    {
        // Levels measured over the previous tick go to its view.
        if (_historyCount > 0)
        {
            PlayView last = _history[(_historyCount - 1) % HistorySize];
            for (int i = 0; i < _voices.Length; i++)
            {
                last.Channels[i].Level = _muted[i] ? 0f : Math.Min(1f, _voices[i].Peak);
                _voices[i].Peak = 0f;
            }
        }

        _replayer.Step();
        _filters.Led = _replayer.LedFilter;

        PlayView view = _history[_historyCount % HistorySize];
        view.Frame = _frames;
        view.Position = _replayer.ShownPosition;
        view.Pattern = _replayer.ShownPattern;
        view.Row = _replayer.ShownRow;
        view.Tick = _replayer.Tick;
        view.Speed = _replayer.Speed;
        view.Tempo = _replayer.Tempo;
        view.Elapsed = TimeSpan.FromSeconds(_songSeconds);
        for (int i = 0; i < _voices.Length; i++)
        {
            view.Channels[i].Period = _voices[i].Period;
            view.Channels[i].Volume = _voices[i].Active && !_muted[i] ? _voices[i].Volume : 0;
            view.Channels[i].Sample = _replayer.Channel(i).SampleNumber + 1;
            view.Channels[i].Level = 0f;
        }
        _historyCount++;

        double tick = 1.0 / Timing.TickRate(_replayer.Tempo);
        _songSeconds += tick;
        _untilTick += tick * SampleRate;

        if (_replayer.Stopped || (_replayer.Ended && !Loop))
        {
            _tail = true;
            _tailFrames = 0;
        }
        else if (_replayer.Ended)
        {
            _replayer.ClearEnd();
        }
    }

    private void Mix(float[] buffer, int offset, int frames)
    {
        int channels = _voices.Length;
        float normalize = Gain * 2f / Math.Max(2, channels);
        float side = _separation;
        for (int f = 0; f < frames; f++)
        {
            float left = 0f, right = 0f;
            for (int i = 0; i < channels; i++)
            {
                float s = _voices[i].Next();
                if (_muted[i])
                    continue;
                // Amiga panning: left, right, right, left.
                int lane = i & 3;
                if (lane == 0 || lane == 3)
                    left += s;
                else
                    right += s;
            }
            _filters.Process(ref left, ref right);
            float mid = (left + right) * 0.5f;
            float diff = (left - right) * 0.5f * side;
            left = (mid + diff) * normalize;
            right = (mid - diff) * normalize;
            if (_tail)
            {
                float fade = 1f - (float)(_tailFrames + f) / _tailLength;
                left *= fade;
                right *= fade;
            }
            buffer[(offset + f) * 2] = left;
            buffer[(offset + f) * 2 + 1] = right;
        }
    }
}
