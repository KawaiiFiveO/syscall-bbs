// Protracker module player: plays .mod files in a desktop window through the game's audio, using the Tracker library.
using System;
using System.IO;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.TrackerPlayer;

public sealed class TrackerPlayerMod : IMod
{
    public void OnLoad(ModContext context)
    {
        ModConfig config = context.Config;
        var settings = new Settings
        {
            Columns = Clamp(config.GetInt("Columns", 80), 50, TextScreen.MaxColumns),
            Rows = Clamp(config.GetInt("Rows", 30), 16, TextScreen.MaxRowsWithMenu),
            ModuleFolder = Path.Combine(context.Directory, config.GetString("ModuleFolder", "modules")),
            // The mod's folder is <game>/mods/<Name>.
            GameFolder = Path.GetDirectoryName(Path.GetDirectoryName(context.Directory.TrimEnd('\\', '/'))),
            Volume = Clamp(config.GetInt("Volume", 100), 0, 100) / 100f,
            Amplification = Clamp(config.GetInt("Amplification", 100), 25, 400) / 100f,
            Version = context.Version,
        };
        Directory.CreateDirectory(settings.ModuleFolder);
        context.Programs.Add(Product.Name, context.Textures.Load("icon.png"), new Player(settings, context.Log, context.Sounds));
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}

internal sealed class Settings
{
    public int Columns;
    public int Rows;
    public string ModuleFolder;
    public string GameFolder;
    public float Volume;
    public float Amplification;
    public string Version;
}
