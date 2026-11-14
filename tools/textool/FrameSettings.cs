using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LastCallBBS.Textool;

/// <summary>
/// The settings in a frame's <c>.png.cfg</c>. The keys are the game's own, except
/// <c>format</c> and <c>crop</c>, which the game takes from its code and from PSD guides.
/// </summary>
internal sealed class FrameSettings
{
    public TexFormat Format = TexFormat.Rgba;
    public bool Premultiply = true;
    public bool Additive;
    public (int X, int Y, int Width, int Height)? Crop;
    public float OffsetX, OffsetY;
    public (float X, float Y)? CropSize;
    public int ColorblindMode;

    public bool IsDefault => ToText().Length == 0;

    public string ToText()
    {
        var sb = new StringBuilder();
        if (Format == TexFormat.R)
            sb.Append("format: r\n");
        if (!Premultiply)
            sb.Append("premultiply-alpha: false\n");
        if (Additive)
            sb.Append("blend: additive\n");
        if (Crop is { } c)
            sb.Append($"crop: {c.X},{c.Y},{c.Width},{c.Height}\n");
        if (OffsetX != 0 || OffsetY != 0)
            sb.Append($"crop-offset: {F(OffsetX)},{F(OffsetY)}\n");
        if (CropSize is { } s)
            sb.Append($"crop-size: {F(s.X)},{F(s.Y)}\n");
        if (ColorblindMode != 0)
            sb.Append($"colorblind-mode: {ColorblindMode}\n");
        return sb.ToString();
    }

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static FrameSettings Parse(string text, string source)
    {
        var s = new FrameSettings();
        var seen = new HashSet<string>();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;
            string where = $"{source}:{n + 1}";
            int colon = line.IndexOf(':');
            if (colon < 0)
                throw new InvalidDataException($"{where}: expected key: value");
            string key = line.Substring(0, colon).Trim().ToLowerInvariant();
            string value = line.Substring(colon + 1).Trim().ToLowerInvariant();
            if (!seen.Add(key))
                throw new InvalidDataException($"{where}: {key} is given twice");

            switch (key)
            {
                case "format":
                    s.Format = value == "r" ? TexFormat.R : value == "rgba" ? TexFormat.Rgba
                        : throw new InvalidDataException($"{where}: format must be r or rgba");
                    break;
                case "premultiply-alpha":
                    s.Premultiply = value == "true" || (value == "false" ? false
                        : throw new InvalidDataException($"{where}: premultiply-alpha must be true or false"));
                    break;
                case "blend":
                    s.Additive = value == "additive" ? true
                        : throw new InvalidDataException($"{where}: blend must be additive");
                    break;
                case "crop":
                    int[] c = Numbers(value, 4, where).Select(v => Int(v, where)).ToArray();
                    s.Crop = (c[0], c[1], c[2], c[3]);
                    break;
                case "crop-offset":
                    float[] o = Numbers(value, 2, where).Select(v => Float(v, where)).ToArray();
                    (s.OffsetX, s.OffsetY) = (o[0], o[1]);
                    break;
                case "crop-size":
                    float[] z = Numbers(value, 2, where).Select(v => Float(v, where)).ToArray();
                    s.CropSize = (z[0], z[1]);
                    break;
                case "colorblind-mode":
                    s.ColorblindMode = Int(value, where);
                    break;
                default:
                    throw new InvalidDataException($"{where}: unknown key {key}");
            }
        }
        if (s.Format == TexFormat.R && s.Additive)
            throw new InvalidDataException($"{source}: blend: additive can't be used with format: r");
        return s;
    }

    private static string[] Numbers(string value, int count, string where)
    {
        string[] parts = value.Split(',');
        if (parts.Length != count)
            throw new InvalidDataException($"{where}: expected {count} numbers separated by commas");
        return parts;
    }

    private static int Int(string v, string where) =>
        int.TryParse(v.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int r) ? r
            : throw new InvalidDataException($"{where}: {v.Trim()} isn't a whole number");

    private static float Float(string v, string where) =>
        float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r) && !float.IsNaN(r) && !float.IsInfinity(r) ? r
            : throw new InvalidDataException($"{where}: {v.Trim()} isn't a number");
}
