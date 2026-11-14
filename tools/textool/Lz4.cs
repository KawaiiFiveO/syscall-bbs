using System;
using System.IO;

namespace LastCallBBS.Textool;

/// <summary>LZ4 block format (no frame header), as the game reads it.</summary>
internal static class Lz4
{
    private const int MinMatch = 4;
    private const int LastLiterals = 5;   // the last 5 bytes are always literals
    private const int MatchFindLimit = 12; // no match starts in the last 12 bytes
    private const int MaxOffset = 65535;
    private const int HashBits = 16;

    public static byte[] Compress(byte[] src)
    {
        var dst = new MemoryStream(src.Length / 2 + 16);
        int[] table = new int[1 << HashBits];
        for (int i = 0; i < table.Length; i++)
            table[i] = -1;

        int anchor = 0, pos = 0;
        int matchLimit = src.Length - MatchFindLimit;
        while (pos < matchLimit)
        {
            int h = Hash(src, pos);
            int candidate = table[h];
            table[h] = pos;
            if (candidate < 0 || pos - candidate > MaxOffset || !Equal4(src, candidate, pos))
            {
                pos++;
                continue;
            }

            int end = pos + MinMatch, max = src.Length - LastLiterals;
            while (end < max && src[end] == src[candidate + end - pos])
                end++;
            while (pos > anchor && candidate > 0 && src[pos - 1] == src[candidate - 1])
            {
                pos--;
                candidate--;
            }
            WriteSequence(dst, src, anchor, pos - anchor, pos - candidate, end - pos);
            pos = anchor = end;
        }
        WriteSequence(dst, src, anchor, src.Length - anchor, 0, 0);
        return dst.ToArray();
    }

    private static void WriteSequence(MemoryStream dst, byte[] src, int literalStart, int literalLength, int offset, int matchLength)
    {
        int matchCode = matchLength == 0 ? 0 : matchLength - MinMatch;
        dst.WriteByte((byte)((Math.Min(literalLength, 15) << 4) | Math.Min(matchCode, 15)));
        if (literalLength >= 15)
            WriteLength(dst, literalLength - 15);
        dst.Write(src, literalStart, literalLength);
        if (matchLength == 0)
            return;
        dst.WriteByte((byte)offset);
        dst.WriteByte((byte)(offset >> 8));
        if (matchCode >= 15)
            WriteLength(dst, matchCode - 15);
    }

    private static void WriteLength(MemoryStream dst, int n)
    {
        for (; n >= 255; n -= 255)
            dst.WriteByte(255);
        dst.WriteByte((byte)n);
    }

    private static int Hash(byte[] b, int i) =>
        (int)((uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24) * 2654435761u >> (32 - HashBits));

    private static bool Equal4(byte[] b, int x, int y) =>
        b[x] == b[y] && b[x + 1] == b[y + 1] && b[x + 2] == b[y + 2] && b[x + 3] == b[y + 3];

    /// <summary>Decodes a block that must expand to exactly <paramref name="length"/> bytes.</summary>
    public static byte[] Decompress(byte[] src, int length)
    {
        byte[] dst = new byte[length];
        int s = 0, d = 0;
        while (true)
        {
            int token = Read(src, ref s);
            int literals = token >> 4;
            if (literals == 15)
                literals += ReadLength(src, ref s);
            if (literals > src.Length - s || literals > length - d)
                throw new InvalidDataException("compressed pixels run past the end");
            Buffer.BlockCopy(src, s, dst, d, literals);
            s += literals;
            d += literals;
            if (s == src.Length)
                break;

            int offset = Read(src, ref s) | Read(src, ref s) << 8;
            int match = token & 15;
            if (match == 15)
                match += ReadLength(src, ref s);
            match += MinMatch;
            if (offset == 0 || offset > d || match > length - d)
                throw new InvalidDataException("compressed pixels are corrupt");
            for (int i = 0; i < match; i++, d++)
                dst[d] = dst[d - offset];
        }
        if (d != length)
            throw new InvalidDataException($"pixels decompress to {d} bytes, expected {length}");
        return dst;
    }

    private static int Read(byte[] src, ref int s)
    {
        if (s >= src.Length)
            throw new InvalidDataException("compressed pixels end too early");
        return src[s++];
    }

    private static int ReadLength(byte[] src, ref int s)
    {
        int n = 0, b;
        do
        {
            b = Read(src, ref s);
            n += b;
        } while (b == 255);
        return n;
    }
}
