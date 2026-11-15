using System;

namespace Tracker;

/// <summary>A position in sample memory. Reads outside the data return 0.</summary>
internal readonly struct SamplePointer
{
    public static readonly SamplePointer Null = new(null, 0);

    public SamplePointer(sbyte[] data, int offset)
    {
        Data = data;
        Offset = offset;
    }

    public sbyte[] Data { get; }
    public int Offset { get; }
    public bool IsNull => Data == null;

    public SamplePointer Add(int bytes) => new(Data, Offset + bytes);

    public sbyte this[int i]
    {
        get
        {
            int at = Offset + i;
            return Data != null && at >= 0 && at < Data.Length ? Data[at] : (sbyte)0;
        }
        set
        {
            int at = Offset + i;
            if (Data != null && at >= 0 && at < Data.Length)
                Data[at] = value;
        }
    }
}

/// <summary>
/// One Paula audio channel. The replayer writes its registers; DMA reads a word at a time from the
/// latched pointer, and when the latched length runs out, latches the registers again, which by then
/// hold the loop.
/// </summary>
internal sealed class PaulaVoice
{
    private readonly double _clockPerSample;
    private readonly Blep _blep = new();

    // Registers, as last written.
    private SamplePointer _pointer;
    private ushort _length;
    private double _delta;
    private float _volume;

    // DMA state.
    private bool _active;
    private SamplePointer _location;
    private ushort _lengthLeft;
    private sbyte _second;
    private int _bytesLeft;
    private bool _justStarted;

    // Output timing: position within the current Paula sample, and Paula samples per output sample.
    private double _phase, _step;
    private bool _fetch;
    private double _fetchAgo;
    private float _value;

    public PaulaVoice(int sampleRate) => _clockPerSample = Timing.PaulaClock / sampleRate;

    /// <summary>The largest |output| since the last read, 0 to 1.</summary>
    public float Peak { get; set; }

    /// <summary>The period last written, for display.</summary>
    public int Period { get; private set; }

    /// <summary>The volume last written, 0 to 64, for display.</summary>
    public int Volume { get; private set; }

    public bool Active => _active;

    /// <summary>AUDxLC.</summary>
    public void SetPointer(SamplePointer pointer) => _pointer = pointer;

    /// <summary>AUDxLEN, in words; 0 means 65536.</summary>
    public void SetLength(int words) => _length = (ushort)words;

    /// <summary>AUDxPER. 0 means 65536; below 113 is raised to 113.</summary>
    public void SetPeriod(int period)
    {
        Period = period & 0xFFFF;
        int real = Period == 0 ? 65536 : Math.Max(Period, 113);
        _delta = _clockPerSample / real;
    }

    /// <summary>AUDxVOL: 0 to 64, bit 6 set means 64.</summary>
    public void SetVolume(int volume)
    {
        int v = volume & 127;
        Volume = v > 64 ? 64 : v;
        _volume = Volume / (128f * 64f);
    }

    /// <summary>DMACON on: latch the registers and fetch at once.</summary>
    public void Start()
    {
        _location = _pointer;
        _lengthLeft = _length;
        _bytesLeft = 0;
        _justStarted = true;
        _step = _delta;
        _phase = 0;
        _fetch = true;
        _fetchAgo = 0;
        _active = true;
    }

    /// <summary>DMACON off. The output falls to 0.</summary>
    public void Stop()
    {
        if (!_active)
            return;
        _active = false;
        if (_value != 0f)
            _blep.Add(0, _value, 0f);
        _value = 0f;
    }

    public void Reset()
    {
        _active = false;
        _value = 0f;
        _blep.Reset();
        _pointer = _location = SamplePointer.Null;
        Peak = 0f;
    }

    /// <summary>The next output sample, -1 to about 1.</summary>
    public float Next()
    {
        if (!_active)
            return _blep.Next();
        if (_fetch)
        {
            _fetch = false;
            Fetch();
        }
        float output = _value + _blep.Next();
        _phase += _step;
        if (_phase >= 1.0)
        {
            // A Paula sample ends; the next one starts between this output sample and the next.
            _phase -= 1.0;
            _fetchAgo = _phase / _step;
            _step = _delta; // Paula reads the period register only here
            _fetch = true;
        }
        float magnitude = output < 0 ? -output : output;
        if (magnitude > Peak)
            Peak = magnitude;
        return output;
    }

    private void Fetch()
    {
        sbyte sample;
        if (_bytesLeft == 0)
        {
            if (!_justStarted && --_lengthLeft == 0)
            {
                _lengthLeft = _length;
                _location = _pointer;
            }
            _justStarted = false;
            sample = _location[0];
            _second = _location[1];
            _location = _location.Add(2);
            _bytesLeft = 1;
        }
        else
        {
            sample = _second;
            _bytesLeft = 0;
        }
        float value = sample * _volume;
        if (value != _value)
        {
            _blep.Add(_fetchAgo, _value, value);
            _value = value;
        }
    }
}
