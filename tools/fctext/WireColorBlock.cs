using System;
using System.Collections.Generic;
using System.IO;

namespace LastCallBBS.FoodCourt;

/// <summary>
/// Wire colors, kept in a block after a solution's wire list: the magic bytes "WCOL", an int
/// count equal to the wire count, then one byte per wire in wire order. 0 is the game's own
/// orange. The unmodded game stops reading after the wires, so it ignores the block.
/// </summary>
public static class WireColorBlock
{
    public static readonly byte[] Magic = { (byte)'W', (byte)'C', (byte)'O', (byte)'L' };

    /// <summary>Color names by value. Value 0 is the game's own color.</summary>
    public static readonly string[] Names = { "none", "red", "yellow", "green", "cyan", "blue", "purple", "pink", "white" };

    /// <summary>The highest color value with a name.</summary>
    public const int Count = 8;

    /// <summary>The color of a lit wire, as 0xRRGGBB, by value. Entry 0 is the game's orange.</summary>
    public static readonly int[] Rgb = { 0xFF9C00, 0xFF3C30, 0xFFE030, 0x48E048, 0x30E0E8, 0x5080FF, 0xB868FF, 0xFF78C8, 0xE8E8E8 };

    /// <summary>The name of a color value, or the number for values without a name.</summary>
    public static string Name(byte value) => value <= Count ? Names[value] : value.ToString();

    /// <summary>Reads a color name or number from 0 to 255. Returns null if it's neither.</summary>
    public static byte? Parse(string text)
    {
        int index = Array.IndexOf(Names, text.ToLowerInvariant());
        if (index >= 0)
            return (byte)index;
        return byte.TryParse(text, out byte value) ? value : null;
    }

    /// <summary>
    /// Reads the block at <paramref name="offset"/>. Returns null, and a length of 0, if there's no
    /// block there or its count isn't <paramref name="wireCount"/>.
    /// </summary>
    public static byte[] TryRead(byte[] data, int offset, int wireCount, out int length)
    {
        length = 0;
        int header = Magic.Length + 4;
        if (offset < 0 || data.Length - offset < header)
            return null;
        for (int i = 0; i < Magic.Length; i++)
        {
            if (data[offset + i] != Magic[i])
                return null;
        }
        int count = BitConverter.ToInt32(data, offset + Magic.Length);
        if (count != wireCount || data.Length - offset - header < count)
            return null;
        var colors = new byte[count];
        Array.Copy(data, offset + header, colors, 0, count);
        length = header + count;
        return colors;
    }

    /// <summary>The block for these colors, one per wire.</summary>
    public static byte[] Block(IReadOnlyList<byte> colors)
    {
        var block = new byte[Magic.Length + 4 + colors.Count];
        Array.Copy(Magic, block, Magic.Length);
        Array.Copy(BitConverter.GetBytes(colors.Count), 0, block, Magic.Length, 4);
        for (int i = 0; i < colors.Count; i++)
            block[Magic.Length + 4 + i] = colors[i];
        return block;
    }

    /// <summary>True if any wire has a color other than the game's own.</summary>
    public static bool AnySet(IReadOnlyList<byte> colors)
    {
        foreach (byte c in colors)
        {
            if (c != 0)
                return true;
        }
        return false;
    }

    internal static InvalidDataException Unreadable(long left) =>
        new($"{left} unread bytes after the last wire, and they aren't a wire color block");
}
