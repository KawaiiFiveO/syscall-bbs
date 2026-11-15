using System;

namespace Tracker;

/// <summary>
/// Band-limited steps (BLEP): a channel's output is a staircase, and each step is corrected by a
/// minimum-phase residual so it carries no energy above Nyquist. The residual is built once from a
/// windowed sinc, made minimum-phase through the real cepstrum, then integrated.
/// </summary>
internal sealed class Blep
{
    /// <summary>Output samples each step's correction lasts.</summary>
    public const int Span = 16;

    private const int Oversampling = 64;
    private const double Cutoff = 0.45; // of the output rate; Nyquist is 0.5

    /// <summary>1 − step response, sampled <see cref="Oversampling"/> times per output sample, plus a guard point.</summary>
    private static readonly float[] Residual = BuildResidual();

    private readonly float[] _ring = new float[Span * 2];
    private int _at, _left;

    /// <summary>
    /// Adds a step from <paramref name="from"/> to <paramref name="to"/> that happened
    /// <paramref name="ago"/> output samples (0 to 1) before the next sample read.
    /// </summary>
    public void Add(double ago, float from, float to)
    {
        float amplitude = from - to;
        double position = ago * Oversampling;
        int index = (int)position;
        float frac = (float)(position - index);
        int at = _at;
        for (int i = 0; i < Span; i++, index += Oversampling)
        {
            float a = Residual[index], b = Residual[index + 1];
            _ring[at] += amplitude * (a + (b - a) * frac);
            at = (at + 1) & (_ring.Length - 1);
        }
        _left = Span;
    }

    /// <summary>The correction for the next output sample.</summary>
    public float Next()
    {
        if (_left == 0)
            return 0f;
        float value = _ring[_at];
        _ring[_at] = 0f;
        _at = (_at + 1) & (_ring.Length - 1);
        _left--;
        return value;
    }

    public void Reset()
    {
        Array.Clear(_ring, 0, _ring.Length);
        _left = 0;
    }

    private static float[] BuildResidual()
    {
        int length = Span * Oversampling;
        const int size = 16384;

        // Windowed sinc (Blackman), centered.
        var re = new double[size];
        var im = new double[size];
        double center = (length - 1) / 2.0;
        for (int n = 0; n < length; n++)
        {
            double x = (n - center) / Oversampling;
            double sinc = x == 0 ? 1.0 : Math.Sin(2 * Math.PI * Cutoff * x) / (2 * Math.PI * Cutoff * x);
            double w = 2 * Math.PI * n / (length - 1);
            double window = 0.42 - 0.5 * Math.Cos(w) + 0.08 * Math.Cos(2 * w);
            re[n] = sinc * window;
        }

        // Real cepstrum: log magnitude, back to time.
        Fft(re, im, false);
        for (int k = 0; k < size; k++)
        {
            double magnitude = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            re[k] = Math.Log(Math.Max(magnitude, 1e-12));
            im[k] = 0;
        }
        Fft(re, im, true);

        // Fold the cepstrum onto positive time, which makes the phase minimum.
        for (int n = 1; n < size / 2; n++)
            re[n] *= 2;
        for (int n = size / 2 + 1; n < size; n++)
            re[n] = 0;
        for (int n = 0; n < size; n++)
            im[n] = 0;

        // Back to frequency, exponentiate, back to time.
        Fft(re, im, false);
        for (int k = 0; k < size; k++)
        {
            double scale = Math.Exp(re[k]);
            double phase = im[k];
            re[k] = scale * Math.Cos(phase);
            im[k] = scale * Math.Sin(phase);
        }
        Fft(re, im, true);

        // Integrate to a step response, normalize to end at 1, store 1 − step.
        var residual = new float[length + 1];
        double sum = 0, total = 0;
        for (int n = 0; n < length; n++)
            total += re[n];
        for (int n = 0; n < length; n++)
        {
            sum += re[n];
            residual[n] = (float)(1.0 - sum / total);
        }
        residual[length] = 0f;
        return residual;
    }

    /// <summary>In-place radix-2 FFT. The inverse divides by the size.</summary>
    private static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci;
                    double ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    double next = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = next;
                }
            }
        }
        if (inverse)
        {
            for (int i = 0; i < n; i++)
            {
                re[i] /= n;
                im[i] /= n;
            }
        }
    }
}
