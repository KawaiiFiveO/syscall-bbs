using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LastCallBBS.Launcher;

/// <summary>The optional log window. Does nothing until <see cref="Open"/> succeeds.</summary>
internal static class ConsoleWindow
{
    private const ushort Gray = 7, Cyan = 11, Red = 12, Yellow = 14;
    private static readonly object Gate = new();
    private static IntPtr _out = IntPtr.Zero;

    public static bool IsOpen => _out != IntPtr.Zero;

    public static void Open()
    {
        AllocConsole(); // fails harmlessly if this process already has a console
        IntPtr handle = CreateFileW("CONOUT$", GenericRead | GenericWrite, ShareRead | ShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == InvalidHandle)
        {
            Log.Warn("Could not open the console window; logging to the file only.");
            return;
        }
        _out = handle;
        SetConsoleTitleW("Last Call BBS (Modded) - log");

        // Closing a console window or pressing Ctrl+C would kill the game with it.
        // Windows Terminal hosts ignore the menu change, but conhost honours it.
        IntPtr window = GetConsoleWindow();
        if (window != IntPtr.Zero)
            DeleteMenu(GetSystemMenu(window, false), ScClose, MfByCommand);
        SetConsoleCtrlHandler(IntPtr.Zero, true);

        // So anything the game or a mod prints with Console.WriteLine shows up too.
        var writer = new Writer();
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    public static void WriteLine(string level, string source, string message)
    {
        if (!IsOpen)
            return;
        ushort color = level == "ERROR" ? Red : level == "WARN" ? Yellow : Gray;
        lock (Gate)
        {
            Put(color, $"[{level,-5}] ");
            Put(Cyan, $"[{source}] ");
            Put(color, message + "\r\n");
        }
    }

    /// <summary>Keeps the window up after a crash, when its contents matter most.</summary>
    public static void WaitForEnter()
    {
        if (!IsOpen)
            return;
        WriteLine("ERROR", "launcher", "Press Enter to close this window.");
        try { Console.ReadLine(); } catch (IOException) { }
    }

    private static void Put(ushort color, string text)
    {
        SetConsoleTextAttribute(_out, color);
        WriteConsoleW(_out, text, (uint)text.Length, out _, IntPtr.Zero);
    }

    private sealed class Writer : TextWriter
    {
        public override Encoding Encoding => Encoding.Unicode;
        public override void Write(char value) => Write(value.ToString());

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            lock (Gate) Put(Gray, value);
        }
    }

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    private const uint ScClose = 0xF060, MfByCommand = 0;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleTextAttribute(IntPtr console, ushort attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetConsoleTitleW(string title);
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);
    [DllImport("user32.dll")] private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WriteConsoleW(IntPtr console, string text, uint length, out uint written, IntPtr reserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
