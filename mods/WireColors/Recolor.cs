using System;
using System.IO;
using LastCallBBS.FoodCourt;
using LastCallBBS.ModAPI;
using LastCallBBS.Textool;

namespace LastCallBBS.Mods.WireColors;

/// <summary>
/// Recolors the game's wire textures. Each pixel is matched to a mix of the art's lit orange with
/// white, and drawn as the same mix of the new color, so shading and highlights stay in place.
/// </summary>
internal sealed class Recolor
{
    public const int Colors = WireColorBlock.Count;

    private static readonly double[] Orange = Channels(WireColorBlock.Rgb[0]);

    private readonly ModTextures _textures;
    private string _packed;

    public Recolor(ModTextures textures) => _textures = textures;

    /// <summary>A recolored copy of the texture at a path under Content/Packed, or null if it isn't one RGBA frame.</summary>
    public object Make(string path, byte color)
    {
        _packed ??= Path.Combine(FindContent(), "Packed");
        TexFrame frame = TexFile.Read(File.ReadAllBytes(Path.Combine(_packed, path + ".tex")))[0];
        if (frame.Format != TexFormat.Rgba)
            return null;
        double[] target = Channels(WireColorBlock.Rgb[color]);

        // .tex pixels are bottom row first and premultiplied; textures from pixels are neither.
        int stride = frame.Width * 4;
        var rgba = new byte[frame.Pixels.Length];
        for (int y = 0; y < frame.Height; y++)
        {
            int from = (frame.Height - 1 - y) * stride, to = y * stride;
            for (int x = 0; x < stride; x += 4)
            {
                int alpha = frame.Pixels[from + x + 3];
                rgba[to + x + 3] = (byte)alpha;
                if (alpha == 0)
                    continue;
                var pixel = new double[3];
                for (int c = 0; c < 3; c++)
                    pixel[c] = Math.Min(1.0, frame.Pixels[from + x + c] / (double)alpha);
                (double tint, double white) = Mix(pixel);
                for (int c = 0; c < 3; c++)
                    rgba[to + x + c] = (byte)Math.Round(255 * Math.Min(1.0, tint * target[c] + white));
            }
        }
        return _textures.FromPixels(frame.Width, frame.Height, rgba).GameTexture;
    }

    /// <summary>The amounts of orange and white whose sum is closest to the pixel (least squares, neither negative).</summary>
    public static (double Tint, double White) Mix(double[] pixel)
    {
        double oo = 0, ow = 0, po = 0, pw = 0;
        for (int c = 0; c < 3; c++)
        {
            oo += Orange[c] * Orange[c];
            ow += Orange[c];
            po += pixel[c] * Orange[c];
            pw += pixel[c];
        }
        const double ww = 3;
        double det = oo * ww - ow * ow;
        double tint = (po * ww - pw * ow) / det, white = (oo * pw - ow * po) / det;
        if (tint < 0)
            return (0, Math.Max(0, pw / ww));
        if (white < 0)
            return (Math.Max(0, po / oo), 0);
        return (tint, white);
    }

    private static double[] Channels(int rgb) => new[] { (rgb >> 16 & 0xFF) / 255.0, (rgb >> 8 & 0xFF) / 255.0, (rgb & 0xFF) / 255.0 };

    // The game finds Content by walking up from the current folder.
    private static string FindContent()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
        {
            string content = Path.Combine(dir.FullName, "Content");
            if (Directory.Exists(content))
                return content;
        }
        throw new DirectoryNotFoundException("the game's Content folder wasn't found");
    }
}
