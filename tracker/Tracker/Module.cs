using System;
using System.IO;
using System.Text;

namespace Tracker;

/// <summary>Thrown when a file isn't a module this library can read.</summary>
public sealed class ModuleFormatException : Exception
{
    public ModuleFormatException(string message) : base(message) { }
}

/// <summary>One of a module's 31 sample slots.</summary>
public sealed class Sample
{
    /// <summary>The longest sample Protracker plays: 65535 words.</summary>
    public const int MaxLength = 0xFFFF * 2;

    public string Name { get; internal set; } = "";

    /// <summary>Length in bytes, always even.</summary>
    public int Length { get; internal set; }

    /// <summary>0 to 7, then 8 to 15 for -8 to -1.</summary>
    public int FineTune { get; internal set; }

    /// <summary>0 to 64.</summary>
    public int Volume { get; internal set; }

    /// <summary>Loop start in bytes.</summary>
    public int LoopStart { get; internal set; }

    /// <summary>Loop length in bytes; 2 for no loop.</summary>
    public int LoopLength { get; internal set; } = 2;

    /// <summary>Signed 8-bit sample data, <see cref="Length"/> bytes. EFx and E8x change it during play.</summary>
    public sbyte[] Data { get; internal set; } = new sbyte[0];

    public bool Loops => LoopStart + LoopLength > 2;

    /// <summary>FineTune as -8 to 7.</summary>
    public int SignedFineTune => FineTune < 8 ? FineTune : FineTune - 16;
}

/// <summary>One cell of a pattern.</summary>
public readonly struct Note
{
    public Note(int period, int sample, int effect, int parameter)
    {
        Period = period;
        Sample = sample;
        Effect = effect;
        Parameter = parameter;
    }

    /// <summary>Amiga period, 0 for none.</summary>
    public int Period { get; }

    /// <summary>1 to 31, 0 for none.</summary>
    public int Sample { get; }

    /// <summary>0 to 15.</summary>
    public int Effect { get; }

    /// <summary>0 to 255.</summary>
    public int Parameter { get; }

    public bool IsEmpty => Period == 0 && Sample == 0 && Effect == 0 && Parameter == 0;

    /// <summary>The note played, "C-1" to "B-3", "---" for none, "???" below B-3.</summary>
    public string NoteName => Tables.NoteName(Period);

    /// <summary>As Protracker shows it, such as "C-3 01 C20" or "--- 00 000".</summary>
    public override string ToString() => $"{NoteName} {Sample:X2} {Effect:X1}{Parameter:X2}";
}

/// <summary>A Protracker module, or one in a compatible format.</summary>
public sealed class Module
{
    public const int Rows = 64;
    public const int MaxChannels = 32;

    /// <summary>Song name, up to 20 characters.</summary>
    public string Title { get; private set; } = "";

    /// <summary>The format tag at offset 1080, such as "M.K." or "8CHN"; "" for 15-sample modules.</summary>
    public string Tag { get; private set; } = "";

    public int Channels { get; private set; } = 4;

    /// <summary>31 samples, numbered 1 to 31 in patterns. 15-sample modules have 16 empty slots.</summary>
    public Sample[] Samples { get; } = new Sample[31];

    /// <summary>The pattern played at each position, 128 entries.</summary>
    public byte[] Orders { get; } = new byte[128];

    /// <summary>Positions played, 1 to 128.</summary>
    public int SongLength { get; private set; } = 1;

    /// <summary>Patterns, each <see cref="Rows"/> × <see cref="Channels"/> notes, row by row.</summary>
    public Note[][] Patterns { get; private set; } = new Note[0][];

    /// <summary>
    /// Readable text stored after the sample data, with lines separated by line feeds; "" if there is none.
    /// Most messages in modules are in the sample names instead.
    /// </summary>
    public string Comment { get; private set; } = "";

    /// <summary>Starting tempo in BPM: 125, or converted from a 15-sample module's tempo.</summary>
    public int InitialTempo { get; private set; } = 125;

    public Note GetNote(int pattern, int row, int channel) =>
        pattern < Patterns.Length ? Patterns[pattern][row * Channels + channel] : default;

    public static Module Load(string path) => Load(File.ReadAllBytes(path));

    public static Module Load(byte[] file)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));
        var module = new Module();
        for (int i = 0; i < module.Samples.Length; i++)
            module.Samples[i] = new Sample();
        Format format = Detect(file, out int channels);
        if (format == Format.Unknown)
            throw new ModuleFormatException("not a Protracker module");
        module.Channels = channels;
        // PC trackers' multichannel modules use code page 437 for names; Amiga modules, Latin-1.
        bool pc = format == Format.MultiChannel && file[1082] == 'C' && file[1083] == 'H' || file.Length >= 1084 && file[1081] == 'C' && file[1082] == 'H' && file[1083] == 'N';
        module._pcText = pc;
        if (format == Format.Fifteen)
            module.LoadFifteen(file);
        else
            module.LoadThirtyOne(file, format);
        module.Sanitize();
        return module;
    }

    private bool _pcText;

    private enum Format { Unknown, Protracker, StarTrekker, NoiseTracker, HisMastersNoiseTracker, MultiChannel, Fifteen }

    private static Format Detect(byte[] file, out int channels)
    {
        channels = 4;
        if (file.Length >= 1084)
        {
            string tag = Encoding.ASCII.GetString(file, 1080, 4);
            switch (tag)
            {
                case "M.K.": case "M!K!": case "NSMS": case "LARD": case "PATT":
                    return Format.Protracker;
                case "FLT4":
                    return Format.StarTrekker;
                case "N.T.":
                    return Format.NoiseTracker;
                case "M&K!": case "FEST":
                    return Format.HisMastersNoiseTracker;
            }
            if (IsDigit(tag[0]) && tag.Substring(1) == "CHN")
            {
                channels = tag[0] - '0';
                return channels > 0 ? Format.MultiChannel : Format.Unknown;
            }
            if (IsDigit(tag[0]) && IsDigit(tag[1]) && tag.Substring(2) == "CH")
            {
                channels = (tag[0] - '0') * 10 + tag[1] - '0';
                return channels > 0 && channels <= MaxChannels ? Format.MultiChannel : Format.Unknown;
            }
            if (tag == "FLT8" || tag == "CD81" || tag == "OKTA" || tag == "OCTA")
            {
                channels = 8;
                return Format.MultiChannel;
            }
        }
        return LooksLikeFifteen(file) ? Format.Fifteen : Format.Unknown;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    // A 15-sample module has no tag, so check that its header is plausible.
    private static bool LooksLikeFifteen(byte[] file)
    {
        if (file.Length < 600 + 1024)
            return false;
        for (int i = 0; i < 15; i++)
        {
            int at = 20 + i * 30;
            if (file[at + 24] != 0 || file[at + 25] > 64)
                return false;
            for (int c = 0; c < 22; c++)
            {
                byte b = file[at + c];
                if (b != 0 && (b < 32 || b > 126))
                    return false;
            }
        }
        int length = file[470];
        if (length == 0 || length > 128 || file[471] > 220)
            return false;
        for (int i = 0; i < 128; i++)
        {
            if (file[472 + i] > 63)
                return false;
        }
        return true;
    }

    private void LoadThirtyOne(byte[] file, Format format)
    {
        var reader = new Reader(file, _pcText);
        Title = reader.Text(20);
        for (int i = 0; i < 31; i++)
        {
            Sample s = Samples[i];
            s.Name = reader.Text(22);
            s.Length = reader.Word() * 2;
            s.FineTune = reader.Byte() & 0xF;
            s.Volume = reader.Byte();
            s.LoopStart = reader.Word() * 2;
            s.LoopLength = reader.Word() * 2;
            // SoundTracker modules converted to 31 samples can keep the loop start in bytes.
            if (s.LoopLength > 2 && s.LoopStart + s.LoopLength > s.Length && s.LoopStart / 2 + s.LoopLength <= s.Length)
                s.LoopStart /= 2;
        }
        SongLength = reader.Byte();
        reader.Skip(1); // 127 in Protracker modules, a restart position in others
        if (format == Format.Protracker && SongLength == 129)
            SongLength = 127;
        if (SongLength == 0 || SongLength > 128)
            throw new ModuleFormatException($"song length {SongLength} is out of range");
        reader.Bytes(Orders, 128);
        Tag = reader.Text(4);
        ReadPatterns(reader, Channels, sampleMask: 31);
        if (format != Format.Protracker)
            ConvertEffects(format);
        ReadSampleData(reader, skipBeforeLoop: false);
        Comment = reader.TrailingText();
    }

    private void LoadFifteen(byte[] file)
    {
        var reader = new Reader(file, _pcText);
        Title = reader.Text(20);
        bool late = false;
        for (int i = 0; i < 15; i++)
        {
            Sample s = Samples[i];
            s.Name = reader.Text(22);
            s.Length = reader.Word() * 2;
            if (s.Length > 9999)
                late = true; // only late Ultimate SoundTracker versions have longer samples
            reader.Skip(1); // no finetune
            s.Volume = reader.Byte();
            s.LoopStart = reader.Word(); // in bytes in Ultimate SoundTracker
            s.LoopLength = reader.Word() * 2;
        }
        SongLength = reader.Byte();
        int tempo = reader.Byte();
        if (tempo == 0 || Title.StartsWith("jjk55", StringComparison.Ordinal))
            tempo = 120;
        if (tempo != 120)
        {
            // The byte sets the CIA timer; 120 means 50 Hz.
            int ciaPeriod = (240 - Math.Min(tempo, 220)) * 122;
            double hz = Timing.CiaClock / (ciaPeriod + 1);
            InitialTempo = (int)(hz * (125.0 / 50.0) + 0.5);
        }
        reader.Bytes(Orders, 128);

        ReadPatterns(reader, 4, sampleMask: 0xFF);
        bool veryLate = false;
        foreach (Note[] pattern in Patterns)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                Note n = pattern[i];
                if (n.Sample > 31)
                    pattern[i] = n = new Note(n.Period, 0, n.Effect, n.Parameter);
                if (n.Effect >= 0xC && n.Effect <= 0xE)
                    late = true;
                if (n.Effect == 0xF)
                    late = veryLate = true;
            }
        }
        foreach (Note[] pattern in Patterns)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                Note n = pattern[i];
                int effect = n.Effect, parameter = n.Parameter;
                if (!late)
                {
                    // SoundTracker 1: 1xy is arpeggio, 2xy slides down by x or up by y.
                    if (effect == 1)
                        effect = 0;
                    else if (effect == 2 && (parameter & 0xF0) != 0)
                        parameter >>= 4;
                    else if (effect == 2 && (parameter & 0x0F) != 0)
                        effect = 1;
                }
                else if (effect == 0xD)
                {
                    if (veryLate)
                        parameter = 0;
                    else
                        effect = 0xA;
                }
                if (effect == 0xF && parameter == 0)
                    effect = 0;
                if (effect == 0xE && (parameter >> 4 == 0x8 || parameter >> 4 == 0xF))
                    effect = parameter = 0;
                pattern[i] = new Note(n.Period, n.Sample, effect, parameter);
            }
        }
        ReadSampleData(reader, skipBeforeLoop: true);
        Comment = reader.TrailingText();
    }

    private void ReadPatterns(Reader reader, int channels, int sampleMask)
    {
        int count = 0;
        foreach (byte order in Orders)
            count = Math.Max(count, order + 1);
        Patterns = new Note[count][];
        for (int p = 0; p < count; p++)
        {
            var notes = new Note[Rows * channels];
            for (int i = 0; i < notes.Length; i++)
            {
                byte a = reader.Byte(), b = reader.Byte(), c = reader.Byte(), d = reader.Byte();
                notes[i] = new Note((a & 0x0F) << 8 | b, ((a & 0xF0) | c >> 4) & sampleMask, c & 0x0F, d);
            }
            Patterns[p] = notes;
        }
    }

    private void ConvertEffects(Format format)
    {
        foreach (Note[] pattern in Patterns)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                Note n = pattern[i];
                int effect = n.Effect, parameter = n.Parameter;
                if (format == Format.NoiseTracker || format == Format.HisMastersNoiseTracker)
                {
                    if (effect == 0xD)
                        parameter = 0;
                    if (effect == 0xF && parameter == 0)
                        effect = 0;
                }
                else if (format == Format.StarTrekker)
                {
                    if (effect == 0xE)
                        effect = parameter = 0;
                    if (effect == 0xF && parameter > 0x1F)
                        parameter = 0x1F;
                }
                // Karplus-Strong and funk repeat are Protracker's own.
                if (effect == 0xE && (parameter >> 4 == 0x8 || parameter >> 4 == 0xF))
                    effect = parameter = 0;
                pattern[i] = new Note(n.Period, n.Sample, effect, parameter);
            }
        }
    }

    private void ReadSampleData(Reader reader, bool skipBeforeLoop)
    {
        for (int i = 0; i < Samples.Length; i++)
        {
            Sample s = Samples[i];
            int toSkip = 0;
            if (skipBeforeLoop)
            {
                // Ultimate SoundTracker plays only the loop of a looped sample, and nothing after it.
                if (s.LoopStart > 0 && s.LoopLength < s.Length && s.LoopStart < s.Length)
                {
                    s.Length -= s.LoopStart;
                    reader.Skip(s.LoopStart);
                    s.LoopStart = 0;
                }
                int loopEnd = s.LoopStart + s.LoopLength;
                if (loopEnd > 2 && s.Length > loopEnd)
                {
                    toSkip = s.Length - loopEnd;
                    s.Length = loopEnd;
                }
            }
            var data = new sbyte[s.Length];
            reader.Signed(data, s.Length);
            s.Data = data;
            reader.Skip(toSkip);
        }
    }

    private void Sanitize()
    {
        if (Channels > MaxChannels)
            throw new ModuleFormatException($"{Channels} channels is more than {MaxChannels}");
        foreach (Sample s in Samples)
        {
            if (s.Volume > 64)
                s.Volume = 64;
            if (s.LoopLength < 2)
                s.LoopLength = 2;
            if (s.LoopStart > Sample.MaxLength || s.LoopStart + s.LoopLength > Sample.MaxLength)
            {
                s.LoopStart = 0;
                s.LoopLength = 2;
            }
            // A loop past the end of the sample: lengthen the sample with silence.
            if (s.Length > 0 && s.LoopLength > 2 && s.LoopStart + s.LoopLength > s.Length)
                s.Length = s.LoopStart + s.LoopLength;
            if (s.Data.Length != s.Length)
            {
                var data = new sbyte[s.Length];
                Array.Copy(s.Data, data, Math.Min(s.Data.Length, s.Length));
                s.Data = data;
            }
            // Protracker clears the first word of a sample that doesn't loop; the DMA repeats it after the end.
            if (s.Length >= 2 && s.LoopStart + s.LoopLength <= 2)
                s.Data[0] = s.Data[1] = 0;
        }
    }

    /// <summary>Reads big-endian fields; past the end of the file, everything reads as zero.</summary>
    private sealed class Reader
    {
        private readonly byte[] _file;
        private readonly bool _pc;
        private int _at;

        public Reader(byte[] file, bool pc)
        {
            _file = file;
            _pc = pc;
        }

        public byte Byte()
        {
            byte b = _at < _file.Length ? _file[_at] : (byte)0;
            _at++;
            return b;
        }

        public int Word() => Byte() << 8 | Byte();

        public void Skip(int count) => _at += count;

        public void Bytes(byte[] into, int count)
        {
            for (int i = 0; i < count; i++)
                into[i] = Byte();
        }

        public void Signed(sbyte[] into, int count)
        {
            int available = Math.Max(0, Math.Min(count, _file.Length - _at));
            if (available > 0)
                Buffer.BlockCopy(_file, _at, into, 0, available);
            _at += count;
        }

        public string Text(int length)
        {
            var text = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                char c = Charset.Decode(Byte(), _pc);
                text.Append(c < ' ' ? ' ' : c);
            }
            return text.ToString().TrimEnd();
        }

        /// <summary>The rest of the file as text, if it reads as text: at least 4 bytes, nearly all printable.</summary>
        public string TrailingText()
        {
            int start = Math.Min(_at, _file.Length), end = _file.Length;
            while (end > start && _file[end - 1] == 0)
                end--;
            end = Math.Min(end, start + 65536);
            if (end - start < 4)
                return "";
            int printable = 0;
            for (int i = start; i < end; i++)
            {
                char c = Charset.Decode(_file[i], _pc);
                if (c >= ' ' || c == '\n' || c == '\r' || c == '\t')
                    printable++;
            }
            if (printable < (end - start) * 9 / 10)
                return "";
            var text = new StringBuilder(end - start);
            for (int i = start; i < end; i++)
            {
                char c = Charset.Decode(_file[i], _pc);
                if (c == '\r')
                {
                    text.Append('\n');
                    if (i + 1 < end && _file[i + 1] == '\n')
                        i++;
                }
                else
                    text.Append(c == '\n' ? '\n' : c == '\t' ? ' ' : c < ' ' ? ' ' : c);
            }
            return text.ToString().TrimEnd();
        }
    }
}
