using System;
using System.Collections.Generic;
using System.IO;

namespace LastCallBBS.ModAPI;

/// <summary>Implemented by the loader. Mods use <see cref="ModTextures"/> instead.</summary>
public interface ITextureSource
{
    /// <summary>Loads a PNG into the game and returns the game's texture object.</summary>
    object Create(string path);
}

/// <summary>Loads PNG files from the mod's folder as game textures.</summary>
public sealed class ModTextures
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly string _folder;
    private readonly ITextureSource _source;
    private readonly bool _full;
    private readonly Dictionary<string, ModTexture> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public ModTextures(string modFolder, ITextureSource source, bool full)
    {
        _folder = Path.GetFullPath(modFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _source = source;
        _full = full;
    }

    /// <summary>
    /// Checks a PNG in the mod's folder and reads its size. The game texture is created the first
    /// time it's used. Loading the same file again returns the same texture. Throws if the file is
    /// missing, outside the mod's folder, or not a PNG.
    /// </summary>
    public ModTexture Load(string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(_folder, relativePath));
        if (!path.StartsWith(_folder, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{relativePath} is outside the mod's folder");
        if (_loaded.TryGetValue(path, out ModTexture texture))
            return texture;
        if (!File.Exists(path))
            throw new FileNotFoundException($"{relativePath} not found in the mod's folder", path);

        // The width and height are the first fields of the IHDR chunk, which must come first.
        byte[] header = new byte[24];
        using (FileStream file = File.OpenRead(path))
        {
            if (file.Read(header, 0, header.Length) != header.Length)
                throw new InvalidDataException($"{relativePath} is not a PNG");
        }
        for (int i = 0; i < PngSignature.Length; i++)
        {
            if (header[i] != PngSignature[i])
                throw new InvalidDataException($"{relativePath} is not a PNG");
        }
        if (header[12] != 'I' || header[13] != 'H' || header[14] != 'D' || header[15] != 'R')
            throw new InvalidDataException($"{relativePath} is not a PNG");

        texture = new ModTexture(path, BigEndian(header, 16), BigEndian(header, 20), _source, _full);
        _loaded[path] = texture;
        return texture;
    }

    private static int BigEndian(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];
}

/// <summary>A PNG from the mod's folder. It's loaded with premultiplied alpha, as the game loads its own images.</summary>
public sealed class ModTexture
{
    private readonly ITextureSource _source;
    private readonly bool _full;
    private object _game;

    internal ModTexture(string path, int width, int height, ITextureSource source, bool full)
    {
        Path = path;
        Width = width;
        Height = height;
        _source = source;
        _full = full;
    }

    /// <summary>The full path of the PNG file.</summary>
    public string Path { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// The game's Texture object, created the first time this is read. Read it only from the game's
    /// main thread once the game is running, such as in a hook handler, not in OnLoad. Only
    /// available with scope = full.
    /// </summary>
    public object GameTexture
    {
        get
        {
            if (!_full)
                throw new ModScopeException("GameTexture needs scope = full in mod.ini");
            return _game ??= _source.Create(Path);
        }
    }
}
