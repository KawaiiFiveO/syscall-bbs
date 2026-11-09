using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

internal sealed class Settings
{
    public bool ShowConsole = true;
    public bool Achievements = true;
    public bool RichPresence = true;
    public bool LocalScoreList = true;
    public string SaveFolder = "";
    public string GameDirectory = "";

    private ModConfig _ini = ModConfig.Load("");

    /// <summary>True if the ini has "AllowUnscoped.&lt;id&gt; = true" for this mod.</summary>
    public bool AllowsUnscoped(string modId) => _ini.GetBool("AllowUnscoped." + modId);

    public static Settings Load(string path)
    {
        var s = new Settings();
        if (!File.Exists(path))
        {
            Log.Info($"No settings file at {path}; using defaults.");
            return s;
        }

        ModConfig ini = ModConfig.Load(path, Log.Error);
        s._ini = ini;
        s.ShowConsole = ini.GetBool("Console", s.ShowConsole);
        s.Achievements = ini.GetBool(nameof(Achievements), s.Achievements);
        s.RichPresence = ini.GetBool(nameof(RichPresence), s.RichPresence);
        s.LocalScoreList = ini.GetBool(nameof(LocalScoreList), s.LocalScoreList);
        s.SaveFolder = ini.GetString(nameof(SaveFolder), s.SaveFolder);
        s.GameDirectory = ini.GetString(nameof(GameDirectory), s.GameDirectory);

        Log.Info($"Settings: Console={s.ShowConsole}, Achievements={s.Achievements}, RichPresence={s.RichPresence}, " +
                 $"LocalScoreList={s.LocalScoreList}, SaveFolder=\"{s.SaveFolder}\", GameDirectory=\"{s.GameDirectory}\"");
        return s;
    }
}

internal static class Log
{
    private const string Launcher = "launcher";
    private static readonly object Gate = new();
    private static StreamWriter _w;

    // Lines logged before we know whether a console will open.
    private static List<(string Level, string Source, string Message)> _early = new();

    public static void Open(string path)
    {
        try { _w = new StreamWriter(path, append: false) { AutoFlush = true }; }
        catch { _w = null; } // logging is best-effort
        Info($"Last Call BBS mod launcher, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    public static void Info(string msg) => Write("INFO", Launcher, msg);
    public static void Warn(string msg) => Write("WARN", Launcher, msg);
    public static void Error(string msg) => Write("ERROR", Launcher, msg);
    public static void Write(string level, string source, string msg)
    {
        lock (Gate)
        {
            try { _w?.WriteLine($"[{level,-5}] [{source}] {msg}"); } catch { }
            if (_early != null)
                _early.Add((level, source, msg));
            else
                ConsoleWindow.WriteLine(level, source, msg);
        }
    }

    /// <summary>Call once the console has opened, or been skipped. Shows the lines logged so far.</summary>
    public static void ConsoleReady()
    {
        lock (Gate)
        {
            if (_early == null)
                return;
            foreach (var (level, source, message) in _early)
                ConsoleWindow.WriteLine(level, source, message);
            _early = null;
        }
    }

    public static void Flush()
    {
        lock (Gate) _w?.Flush();
    }

    public static void Close()
    {
        lock (Gate)
        {
            _w?.Dispose();
            _w = null;
        }
    }
}

internal static class Dialog
{
    private const uint MB_OK = 0x0, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONWARNING = 0x30;
    private const int IDYES = 6;
    private const string Title = "Last Call BBS (Modded)";

    public static void Error(string text) => MessageBoxW(IntPtr.Zero, text, Title, MB_OK | MB_ICONERROR);

    public static bool YesNo(string text) =>
        MessageBoxW(IntPtr.Zero, text, Title, MB_YESNO | MB_ICONWARNING) == IDYES;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
