using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LastCallBBS.Textool;

/// <summary>PNG reading and writing through System.Drawing.</summary>
internal static class Png
{
    public static RgbaImage Read(Stream stream)
    {
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(stream);
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("not a readable image");
        }
        using (bitmap)
        {
            int w = bitmap.Width, h = bitmap.Height;
            byte[] bgra = new byte[w * h * 4];
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * w * 4, w * 4);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            SwapRedBlue(bgra);
            return new RgbaImage { Width = w, Height = h, Pixels = bgra };
        }
    }

    public static void Write(RgbaImage image, Stream stream)
    {
        if (image.Width == 0 || image.Height == 0)
            throw new NotSupportedException("PNG files can't be empty");
        byte[] bgra = (byte[])image.Pixels.Clone();
        SwapRedBlue(bgra);
        using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < image.Height; y++)
                Marshal.Copy(bgra, y * image.Width * 4, data.Scan0 + y * data.Stride, image.Width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        bitmap.Save(stream, ImageFormat.Png);
    }

    private static void SwapRedBlue(byte[] p)
    {
        for (int i = 0; i < p.Length; i += 4)
            (p[i], p[i + 2]) = (p[i + 2], p[i]);
    }
}
