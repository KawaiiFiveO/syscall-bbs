using System;

namespace Tracker;

/// <summary>The Amiga's output filters to model.</summary>
public enum AmigaModel
{
    /// <summary>No filtering aside from the LED filter.</summary>
    None,

    /// <summary>Amiga 500: low-pass at about 4.4 kHz and high-pass at about 5 Hz.</summary>
    A500,

    /// <summary>Amiga 1200: high-pass at about 5 Hz only.</summary>
    A1200,
}

/// <summary>A stereo one-pole RC filter.</summary>
internal sealed class OnePole
{
    private readonly float _a;
    private readonly bool _highPass;
    private float _left, _right;

    public OnePole(double sampleRate, double cutoff, bool highPass)
    {
        _a = (float)(1.0 - Math.Exp(-2.0 * Math.PI * cutoff / sampleRate));
        _highPass = highPass;
    }

    public void Process(ref float left, ref float right)
    {
        _left += _a * (left - _left);
        _right += _a * (right - _right);
        if (_highPass)
        {
            left -= _left;
            right -= _right;
        }
        else
        {
            left = _left;
            right = _right;
        }
    }

    public void Reset() => _left = _right = 0f;

    /// <summary>Cutoff of an RC stage, in Hz.</summary>
    public static double Cutoff(double ohms, double farads) => 1.0 / (2.0 * Math.PI * ohms * farads);
}

/// <summary>A stereo two-pole low-pass (bilinear-transformed biquad), for the Sallen-Key "LED" filter.</summary>
internal sealed class TwoPole
{
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private float _l1, _l2, _r1, _r2;

    public TwoPole(double sampleRate, double cutoff, double q)
    {
        double w = 2.0 * Math.PI * cutoff / sampleRate;
        double alpha = Math.Sin(w) / (2.0 * q);
        double cos = Math.Cos(w);
        double a0 = 1.0 + alpha;
        _b0 = (float)((1.0 - cos) / 2.0 / a0);
        _b1 = (float)((1.0 - cos) / a0);
        _b2 = _b0;
        _a1 = (float)(-2.0 * cos / a0);
        _a2 = (float)((1.0 - alpha) / a0);
    }

    public void Process(ref float left, ref float right)
    {
        // Transposed direct form II.
        float l = _b0 * left + _l1;
        _l1 = _b1 * left - _a1 * l + _l2;
        _l2 = _b2 * left - _a2 * l;
        float r = _b0 * right + _r1;
        _r1 = _b1 * right - _a1 * r + _r2;
        _r2 = _b2 * right - _a2 * r;
        left = l;
        right = r;
    }

    public void Reset() => _l1 = _l2 = _r1 = _r2 = 0f;
}

/// <summary>The output stage: the model's RC filters and the LED filter.</summary>
internal sealed class OutputFilters
{
    private readonly OnePole _lowPass, _highPass;
    private readonly TwoPole _led;
    private bool _ledOn;

    public OutputFilters(double sampleRate, AmigaModel model)
    {
        if (model == AmigaModel.A500)
        {
            _lowPass = new OnePole(sampleRate, OnePole.Cutoff(360, 0.1e-6), false);
            _highPass = new OnePole(sampleRate, OnePole.Cutoff(1390, 22.33e-6), true);
        }
        else if (model == AmigaModel.A1200)
        {
            _highPass = new OnePole(sampleRate, OnePole.Cutoff(1360, 22e-6), true);
        }
        // Sallen-Key: R1 = R2 = 10 kΩ, C1 = 6800 pF, C2 = 3900 pF.
        double r = 10000, c1 = 6.8e-9, c2 = 3.9e-9;
        double rc = Math.Sqrt(r * r * c1 * c2);
        _led = new TwoPole(sampleRate, 1.0 / (2.0 * Math.PI * rc), rc / (c2 * (r + r)));
    }

    public bool Led
    {
        get => _ledOn;
        set
        {
            if (value != _ledOn)
                _led.Reset();
            _ledOn = value;
        }
    }

    public void Process(ref float left, ref float right)
    {
        _lowPass?.Process(ref left, ref right);
        if (_ledOn)
            _led.Process(ref left, ref right);
        _highPass?.Process(ref left, ref right);
    }

    public void Reset()
    {
        _lowPass?.Reset();
        _highPass?.Reset();
        _led.Reset();
    }
}
