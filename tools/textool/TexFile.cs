using System;
using System.Collections.Generic;
using System.IO;

namespace LastCallBBS.Textool;

internal enum TexFormat
{
    R = 1,
    Rgba = 2,
}

/// <summary>One frame of a .tex file: its header and its pixels as stored (bottom-up rows, premultiplied).</summary>
internal sealed class TexFrame
{
    public int Width, Height;
    public TexFormat Format = TexFormat.Rgba;
    public int LogicalWidth, LogicalHeight;
    public int CropX, CropY, CropWidth, CropHeight;
    public float PivotX, PivotY;
    public float OffsetX, OffsetY;
    public float CropSizeX, CropSizeY;
    public int ColorblindMode;
    public byte[] Pixels;

    public int BytesPerPixel => Format == TexFormat.R ? 1 : 4;

    /// <summary>Header fields that differ from <paramref name="other"/>, or an empty list.</summary>
    public List<string> Differences(TexFrame other)
    {
        var d = new List<string>();
        void Check(string name, object a, object b)
        {
            if (!a.Equals(b))
                d.Add($"{name} {a} != {b}");
        }
        Check("size", (Width, Height), (other.Width, other.Height));
        Check("format", Format, other.Format);
        Check("logical size", (LogicalWidth, LogicalHeight), (other.LogicalWidth, other.LogicalHeight));
        Check("crop", (CropX, CropY, CropWidth, CropHeight), (other.CropX, other.CropY, other.CropWidth, other.CropHeight));
        Check("pivot", (PivotX, PivotY), (other.PivotX, other.PivotY));
        Check("offset", (OffsetX, OffsetY), (other.OffsetX, other.OffsetY));
        Check("crop size", (CropSizeX, CropSizeY), (other.CropSizeX, other.CropSizeY));
        Check("colourblind mode", ColorblindMode, other.ColorblindMode);
        if (d.Count == 0 && !Same(Pixels, other.Pixels))
            d.Add("pixels differ");
        return d;
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }
}

internal static class TexFile
{
    public const int Version = 1005;

    public static List<TexFrame> Read(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        try
        {
            int version = r.ReadInt32();
            if (version != Version)
                throw new InvalidDataException($"version {version}, expected {Version}");
            int count = r.ReadInt32();
            if (count < 0 || count > 100000)
                throw new InvalidDataException($"bad frame count {count}");

            var frames = new List<TexFrame>();
            for (int i = 0; i < count; i++)
            {
                var f = new TexFrame
                {
                    Width = r.ReadInt32(),
                    Height = r.ReadInt32(),
                    Format = (TexFormat)r.ReadInt32(),
                    LogicalWidth = r.ReadInt32(),
                    LogicalHeight = r.ReadInt32(),
                    CropX = r.ReadInt32(),
                    CropY = r.ReadInt32(),
                    CropWidth = r.ReadInt32(),
                    CropHeight = r.ReadInt32(),
                    PivotX = r.ReadSingle(),
                    PivotY = r.ReadSingle(),
                    OffsetX = r.ReadSingle(),
                    OffsetY = r.ReadSingle(),
                    CropSizeX = r.ReadSingle(),
                    CropSizeY = r.ReadSingle(),
                    ColorblindMode = r.ReadInt32(),
                };
                if (f.Format != TexFormat.R && f.Format != TexFormat.Rgba)
                    throw new InvalidDataException($"frame {i}: unknown pixel format {(int)f.Format}");
                if (f.Width < 0 || f.Height < 0 || (long)f.Width * f.Height * f.BytesPerPixel > int.MaxValue)
                    throw new InvalidDataException($"frame {i}: bad size {f.Width}x{f.Height}");
                int length = r.ReadInt32();
                if (length < 0 || length > bytes.Length - r.BaseStream.Position)
                    throw new InvalidDataException($"frame {i}: compressed pixels run past the end of the file");
                try
                {
                    f.Pixels = Lz4.Decompress(r.ReadBytes(length), f.Width * f.Height * f.BytesPerPixel);
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidDataException($"frame {i}: {e.Message}");
                }
                frames.Add(f);
            }
            if (r.BaseStream.Position != bytes.Length)
                throw new InvalidDataException($"{bytes.Length - r.BaseStream.Position} bytes after the last frame");
            return frames;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("file ends too early");
        }
    }

    public static byte[] Write(IList<TexFrame> frames)
    {
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write(Version);
            w.Write(frames.Count);
            foreach (TexFrame f in frames)
            {
                if (f.Pixels.Length != f.Width * f.Height * f.BytesPerPixel)
                    throw new InvalidOperationException("pixel data doesn't match the frame size");
                w.Write(f.Width);
                w.Write(f.Height);
                w.Write((int)f.Format);
                w.Write(f.LogicalWidth);
                w.Write(f.LogicalHeight);
                w.Write(f.CropX);
                w.Write(f.CropY);
                w.Write(f.CropWidth);
                w.Write(f.CropHeight);
                w.Write(f.PivotX);
                w.Write(f.PivotY);
                w.Write(f.OffsetX);
                w.Write(f.OffsetY);
                w.Write(f.CropSizeX);
                w.Write(f.CropSizeY);
                w.Write(f.ColorblindMode);
                byte[] packed = Lz4.Compress(f.Pixels);
                w.Write(packed.Length);
                w.Write(packed);
            }
        }
        return stream.ToArray();
    }
}
