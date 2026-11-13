// Z-machine interpreter program: plays .z5 and .z8 story files in a desktop window, using Yomiel.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LastCallBBS.ModAPI;
using Yomiel;

namespace LastCallBBS.Mods.YomielPlayer;

public sealed class YomielPlayerMod : IMod
{
    public void OnLoad(ModContext context)
    {
        ModConfig config = context.Config;
        var settings = new Settings
        {
            Columns = Clamp(config.GetInt("Columns", 96), 40, TextScreen.MaxColumns),
            Rows = Clamp(config.GetInt("Rows", 30), 10, TextScreen.MaxRowsWithMenu),
            StoryFolder = Path.Combine(context.Directory, config.GetString("StoryFolder", "stories")),
            Scrollback = Clamp(config.GetInt("Scrollback", 1000), 100, 100_000),
            MorePrompts = config.GetBool("MorePrompts", true),
            Version = context.Version,
        };
        Directory.CreateDirectory(settings.StoryFolder);
        context.Programs.Add("Yomiel", context.Textures.Load("icon.png"), new Player(settings, context.Log, context.Sounds));
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}

internal sealed class Settings
{
    public int Columns;
    public int Rows;
    public string StoryFolder;
    public int Scrollback;
    public bool MorePrompts;
    public string Version;
}

/// <summary>A story file in the story folder, and its folder for saves.</summary>
internal sealed class StoryFile
{
    public static readonly string[] Extensions = { ".z5", ".z7", ".z8" };

    public string Path;
    public string Name;
    public int Version;
    public long Size;
    public string DataFolder;

    public bool Supported => ZMachine.IsSupportedVersion(Version);
    public bool CanContinue => new ZStoryFolder(DataFolder).HasSuspended;

    public static List<StoryFile> Find(string folder, string dataRoot)
    {
        var stories = new List<StoryFile>();
        if (!Directory.Exists(folder))
            return stories;
        foreach (string path in Directory.GetFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (!Extensions.Contains(extension) && extension != ".z3" && extension != ".z4" && extension != ".z6")
                continue;
            int version = 0;
            try
            {
                using FileStream stream = File.OpenRead(path);
                version = stream.ReadByte();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            stories.Add(new StoryFile
            {
                Path = path,
                Name = name,
                Version = version,
                Size = new FileInfo(path).Length,
                DataFolder = System.IO.Path.Combine(dataRoot, ZStoryFolder.SafeName(System.IO.Path.GetFileName(path))),
            });
        }
        return stories;
    }
}
