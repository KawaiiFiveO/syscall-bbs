using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>
/// Creates game textures from mods' PNG files, using the game's own two steps: TextureLoader's
/// PNG reader (SDL_image, flipped to the game's bottom-up rows, alpha premultiplied) and the
/// renderer's image-to-texture call. Textures from pixels build the image with its
/// (format, width, height, pixels) constructor instead, flipped and premultiplied here.
/// </summary>
internal static class RuntimeTextures
{
    public static readonly ITextureSource Source = new TextureSource();

    private static MethodInfo _readPng, _create;
    private static ConstructorInfo _newImage;
    private static object _rgba;
    private static string _failure;

    /// <summary>Finds the game code. Returns one message if it couldn't be found.</summary>
    public static List<string> Verify()
    {
        try
        {
            Resolve();
            Log.Info("Textures: OK");
            return new List<string>();
        }
        catch (ShapeMismatchException e)
        {
            Log.Error($"Textures: FAILED - {e.Message}");
            return new List<string> { $"textures: {e.Message}" };
        }
    }

    private static void Resolve()
    {
        if (_failure != null)
            throw new ShapeMismatchException(_failure);
        if (_create != null)
            return;
        try
        {
            Assembly game = HookHost.Game;
            Type loader = CodeShape.RequireType(game, "TextureLoader");
            Type texture = CodeShape.RequireType(game, "Texture");

            // (string path, bool premultiply) returning the image itself, not a Maybe of it.
            _readPng = CodeShape.Single(loader.GetMethods(CodeShape.AllDeclared).Where(m => m.IsStatic && !m.ReturnType.IsGenericType &&
                m.ReturnType.IsClass && m.ReturnType != texture &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string), typeof(bool) })), "PNG reader");
            Type image = _readPng.ReturnType;
            _create = CodeShape.Single(game.GetTypes().SelectMany(t => t.GetMethods(CodeShape.AllDeclared)).Where(m => m.IsStatic &&
                m.ReturnType == texture && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { image })), "image-to-texture call");
            _newImage = CodeShape.Single(image.GetConstructors(CodeShape.AllDeclared).Where(c => !c.IsStatic && c.GetParameters().Length == 4 &&
                c.GetParameters()[0].ParameterType.IsEnum && c.GetParameters().Skip(1).Select(p => p.ParameterType).SequenceEqual(new[] { typeof(int), typeof(int), typeof(byte[]) })),
                "image constructor (format, width, height, pixels)");
            Type format = _newImage.GetParameters()[0].ParameterType;
            if (!Enum.IsDefined(format, "RGBA"))
                throw new ShapeMismatchException("the image format enum has no RGBA");
            _rgba = Enum.Parse(format, "RGBA");
        }
        catch (ShapeMismatchException e)
        {
            _failure = e.Message;
            throw;
        }
    }

    private sealed class TextureSource : ITextureSource
    {
        public object Create(string path)
        {
            Resolve();
            try
            {
                object image = _readPng.Invoke(null, new object[] { path, true });
                return _create.Invoke(null, new[] { image });
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        public object Create(int width, int height, byte[] rgba)
        {
            Resolve();
            // The game's images are bottom row first, with premultiplied alpha.
            int stride = width * 4;
            var pixels = new byte[rgba.Length];
            for (int y = 0; y < height; y++)
            {
                int from = y * stride, to = (height - 1 - y) * stride;
                for (int x = 0; x < stride; x += 4)
                {
                    int a = rgba[from + x + 3];
                    pixels[to + x] = (byte)(rgba[from + x] * a / 255);
                    pixels[to + x + 1] = (byte)(rgba[from + x + 1] * a / 255);
                    pixels[to + x + 2] = (byte)(rgba[from + x + 2] * a / 255);
                    pixels[to + x + 3] = (byte)a;
                }
            }
            try
            {
                object image = _newImage.Invoke(new[] { _rgba, width, height, (object)pixels });
                return _create.Invoke(null, new[] { image });
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }
}
