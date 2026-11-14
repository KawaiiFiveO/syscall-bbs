using System;
using System.IO;

namespace LastCallBBS.Textool;

/// <summary>An image as PNG files hold it: top-down rows, RGBA, straight alpha.</summary>
internal sealed class RgbaImage
{
    public int Width, Height;
    public byte[] Pixels;
}

/// <summary>Converts between .tex frames and images, the way the game's packer does.</summary>
internal static class Converter
{
    public static TexFrame Pack(RgbaImage image, FrameSettings s)
    {
        int w = image.Width, h = image.Height;
        byte[] rgba = (byte[])image.Pixels.Clone();
        if (s.Premultiply)
        {
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int a = rgba[i + 3];
                rgba[i] = (byte)(rgba[i] * a / 255);
                rgba[i + 1] = (byte)(rgba[i + 1] * a / 255);
                rgba[i + 2] = (byte)(rgba[i + 2] * a / 255);
            }
        }
        rgba = FlipRows(rgba, w, h, 4);

        byte[] pixels;
        if (s.Format == TexFormat.R)
        {
            pixels = new byte[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = rgba[4 * i];
        }
        else
        {
            pixels = rgba;
            if (s.Additive)
                for (int i = 3; i < pixels.Length; i += 4)
                    pixels[i] = 0;
        }

        var crop = s.Crop ?? (0, 0, w, h);
        if (crop.X < 0 || crop.Y < 0 || crop.Width < 0 || crop.Height < 0 || crop.X + crop.Width > w || crop.Y + crop.Height > h)
            throw new InvalidDataException($"crop {crop.X},{crop.Y},{crop.Width},{crop.Height} isn't inside the {w}x{h} image");
        var cropSize = s.CropSize ?? ((float)w, (float)h);

        return new TexFrame
        {
            Width = w,
            Height = h,
            Format = s.Format,
            LogicalWidth = w,
            LogicalHeight = h,
            CropX = crop.X,
            CropY = crop.Y,
            CropWidth = crop.Width,
            CropHeight = crop.Height,
            OffsetX = s.OffsetX,
            OffsetY = s.OffsetY,
            CropSizeX = cropSize.X,
            CropSizeY = cropSize.Y,
            ColorblindMode = s.ColorblindMode,
            Pixels = pixels,
        };
    }

    /// <summary>
    /// The image and settings that <see cref="Pack"/> turns back into this frame. Colour is restored
    /// with <c>ceil(c * 255 / a)</c>, which packs to the same premultiplied value.
    /// </summary>
    public static (RgbaImage Image, FrameSettings Settings) Unpack(TexFrame f)
    {
        int w = f.Width, h = f.Height;
        if (f.LogicalWidth != w || f.LogicalHeight != h)
            throw new NotSupportedException($"logical size {f.LogicalWidth}x{f.LogicalHeight} differs from the image size {w}x{h}");
        if (f.PivotX != 0 || f.PivotY != 0)
            throw new NotSupportedException($"pivot {f.PivotX},{f.PivotY} isn't 0,0");

        var s = new FrameSettings
        {
            Format = f.Format,
            OffsetX = f.OffsetX,
            OffsetY = f.OffsetY,
            ColorblindMode = f.ColorblindMode,
        };
        if ((f.CropX, f.CropY, f.CropWidth, f.CropHeight) != (0, 0, w, h))
            s.Crop = (f.CropX, f.CropY, f.CropWidth, f.CropHeight);
        if ((f.CropSizeX, f.CropSizeY) != (w, h))
            s.CropSize = (f.CropSizeX, f.CropSizeY);

        byte[] src = FlipRows(f.Pixels, w, h, f.BytesPerPixel);
        byte[] rgba = new byte[w * h * 4];
        if (f.Format == TexFormat.R)
        {
            for (int i = 0; i < w * h; i++)
            {
                rgba[4 * i] = rgba[4 * i + 1] = rgba[4 * i + 2] = src[i];
                rgba[4 * i + 3] = 255;
            }
            return (new RgbaImage { Width = w, Height = h, Pixels = rgba }, s);
        }

        bool anyAlpha = false, anyColour = false, colourOverAlpha = false;
        for (int i = 0; i < src.Length; i += 4)
        {
            byte a = src[i + 3];
            anyAlpha |= a != 0;
            anyColour |= src[i] != 0 || src[i + 1] != 0 || src[i + 2] != 0;
            colourOverAlpha |= src[i] > a || src[i + 1] > a || src[i + 2] > a;
        }

        if (!anyAlpha && anyColour)
        {
            s.Additive = true;
            Buffer.BlockCopy(src, 0, rgba, 0, src.Length);
            for (int i = 3; i < rgba.Length; i += 4)
                rgba[i] = 255;
        }
        else if (colourOverAlpha)
        {
            s.Premultiply = false;
            Buffer.BlockCopy(src, 0, rgba, 0, src.Length);
        }
        else
        {
            for (int i = 0; i < src.Length; i += 4)
            {
                int a = src[i + 3];
                if (a == 0)
                    continue;
                rgba[i] = (byte)((src[i] * 255 + a - 1) / a);
                rgba[i + 1] = (byte)((src[i + 1] * 255 + a - 1) / a);
                rgba[i + 2] = (byte)((src[i + 2] * 255 + a - 1) / a);
                rgba[i + 3] = (byte)a;
            }
        }
        return (new RgbaImage { Width = w, Height = h, Pixels = rgba }, s);
    }

    private static byte[] FlipRows(byte[] src, int w, int h, int bpp)
    {
        byte[] dst = new byte[src.Length];
        int stride = w * bpp;
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src, y * stride, dst, (h - 1 - y) * stride, stride);
        return dst;
    }
}
