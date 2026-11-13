using System;
using System.Collections.Generic;
using System.IO;

namespace LastCallBBS.ModAPI;

/// <summary>
/// A text program in its own desktop window: a grid of characters with keyboard input. Every
/// method runs on the game's main thread. An exception stops the program until the window is
/// reopened, and is logged.
/// </summary>
public interface IDesktopProgram
{
    /// <summary>The window's title. Read every frame.</summary>
    string Title { get; }

    /// <summary>Grid width in characters, read when the window opens. At most <see cref="TextScreen.MaxColumns"/>.</summary>
    int Columns { get; }

    /// <summary>Grid height in characters, read when the window opens. At most <see cref="TextScreen.MaxRows"/>.</summary>
    int Rows { get; }

    /// <summary>The window opened, with a new blank screen.</summary>
    void Opened(ProgramWindow window);

    /// <summary>Called every frame while the window is shown, after that frame's keys.</summary>
    void Update(ProgramWindow window, float seconds);

    /// <summary>A key was pressed or auto-repeated. Only the top window gets keys.</summary>
    void KeyPressed(ProgramWindow window, KeyPress key);

    /// <summary>The window closed. The screen isn't drawn again.</summary>
    void Closed(ProgramWindow window);
}

/// <summary>
/// Optional, for an <see cref="IDesktopProgram"/>: the window gets a menu in its title bar and a
/// minimize button. The window's frame is 16 pixels taller, so the grid fits at most
/// <see cref="TextScreen.MaxRowsWithMenu"/> rows.
/// </summary>
public interface IProgramMenu
{
    /// <summary>The menu's items, read every frame while the window is open. Actions run on the game's main thread.</summary>
    IReadOnlyList<ProgramMenuItem> MenuItems { get; }
}

/// <summary>An item in a program's menu, or a separator.</summary>
public sealed class ProgramMenuItem
{
    public ProgramMenuItem(string label, Action action, bool enabled = true)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Enabled = enabled;
    }

    private ProgramMenuItem() { }

    /// <summary>A line between groups of items.</summary>
    public static ProgramMenuItem Separator { get; } = new() { IsSeparator = true };

    public string Label { get; }
    public Action Action { get; }

    /// <summary>A disabled item is shown greyed out and does nothing.</summary>
    public bool Enabled { get; }

    public bool IsSeparator { get; private set; }
}

/// <summary>Implemented by the loader. Mods use <see cref="ModPrograms"/> instead.</summary>
public interface IProgramHost
{
    void Add(string modId, IModLog log, string label, ModTexture icon, IDesktopProgram program);
    string DataDirectory(string modId);
}

/// <summary>Adds desktop programs. Needs scope = programs (or full) in mod.ini.</summary>
public sealed class ModPrograms
{
    private readonly IProgramHost _host;
    private readonly string _modId;
    private readonly IModLog _log;
    private readonly bool _allowed;

    public ModPrograms(IProgramHost host, string modId, IModLog log, bool allowed)
    {
        _host = host;
        _modId = modId;
        _log = log;
        _allowed = allowed;
    }

    /// <summary>
    /// Puts an icon on the desktop that opens the program. Call it from OnLoad. The icon is drawn
    /// at its own size; the game's icons are about 64x80. Throws <see cref="ShapeMismatchException"/>
    /// if a game update has broken desktop programs.
    /// </summary>
    public void Add(string label, ModTexture icon, IDesktopProgram program)
    {
        if (!_allowed)
            throw new ModScopeException("Programs need scope = programs in mod.ini");
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("a program needs a label");
        _host.Add(_modId, _log, label, icon ?? throw new ArgumentNullException(nameof(icon)), program ?? throw new ArgumentNullException(nameof(program)));
    }

    /// <summary>
    /// The mod's folder for saved files, next to the player's save: My Games\Last Call BBS\&lt;user&gt;\mods\&lt;id&gt;.
    /// Created on first use. Available once the game is running, not in OnLoad.
    /// </summary>
    public string DataDirectory
    {
        get
        {
            if (!_allowed)
                throw new ModScopeException("DataDirectory needs scope = programs in mod.ini");
            string path = _host.DataDirectory(_modId);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}

/// <summary>One open window of a program.</summary>
public sealed class ProgramWindow
{
    private readonly ModPrograms _programs;

    public ProgramWindow(TextScreen screen, ModPrograms programs)
    {
        Screen = screen;
        _programs = programs;
    }

    /// <summary>The characters shown in the window. Drawn after every <see cref="IDesktopProgram.Update"/>.</summary>
    public TextScreen Screen { get; }

    /// <summary>Same as <see cref="ModPrograms.DataDirectory"/>.</summary>
    public string DataDirectory => _programs.DataDirectory;
}

/// <summary>
/// A grid of characters, each with a foreground and background colour, plus a cursor. Row 0 is
/// the top. Writes outside the grid are clipped. Cells are 9x15 pixels in the desktop font.
/// </summary>
public sealed class TextScreen
{
    public const int MaxColumns = 106;
    public const int MaxRows = 34;

    /// <summary>The most rows for a program with a menu (<see cref="IProgramMenu"/>), whose frame is taller.</summary>
    public const int MaxRowsWithMenu = 33;

    private readonly char[] _chars;
    private readonly TextColor[] _fore;
    private readonly TextColor[] _back;

    public TextScreen(int columns, int rows)
    {
        if (columns < 1 || rows < 1 || columns > MaxColumns || rows > MaxRows)
            throw new ArgumentOutOfRangeException(nameof(columns), $"the grid must be 1x1 to {MaxColumns}x{MaxRows}, got {columns}x{rows}");
        Columns = columns;
        Rows = rows;
        _chars = new char[columns * rows];
        _fore = new TextColor[columns * rows];
        _back = new TextColor[columns * rows];
        Clear();
    }

    public int Columns { get; }
    public int Rows { get; }

    /// <summary>Colour for writes that don't name one.</summary>
    public TextColor Foreground { get; set; } = TextColor.FromRgb(0xD8D8D8);

    /// <summary>Background for writes that don't name one. Also the window's background.</summary>
    public TextColor Background { get; set; } = TextColor.FromRgb(0x101418);

    public int CursorColumn { get; set; }
    public int CursorRow { get; set; }

    /// <summary>Shows a blinking block cursor at <see cref="CursorColumn"/>, <see cref="CursorRow"/>.</summary>
    public bool CursorVisible { get; set; }

    /// <summary>The face of the desktop font the grid is drawn in.</summary>
    public TextFont Font { get; set; } = TextFont.Bold;

    public char GetChar(int column, int row) => Inside(column, row) ? _chars[row * Columns + column] : ' ';
    public TextColor GetForeground(int column, int row) => Inside(column, row) ? _fore[row * Columns + column] : Foreground;
    public TextColor GetBackground(int column, int row) => Inside(column, row) ? _back[row * Columns + column] : Background;

    public void Put(int column, int row, char c) => Put(column, row, c, Foreground, Background);

    public void Put(int column, int row, char c, TextColor foreground, TextColor background)
    {
        if (!Inside(column, row))
            return;
        int i = row * Columns + column;
        _chars[i] = c < ' ' ? ' ' : c;
        _fore[i] = foreground;
        _back[i] = background;
    }

    /// <summary>Writes on one row, without wrapping. Returns the column after the text.</summary>
    public int Write(int column, int row, string text) => Write(column, row, text, Foreground, Background);

    public int Write(int column, int row, string text, TextColor foreground, TextColor background)
    {
        foreach (char c in text ?? "")
            Put(column++, row, c, foreground, background);
        return column;
    }

    /// <summary>Fills a rectangle with one character in the current colours.</summary>
    public void Fill(int column, int row, int width, int height, char c = ' ')
    {
        for (int r = Math.Max(row, 0); r < Math.Min(row + height, Rows); r++)
        for (int col = Math.Max(column, 0); col < Math.Min(column + width, Columns); col++)
            Put(col, r, c);
    }

    /// <summary>Blanks the whole grid in the current colours and moves the cursor to the top left.</summary>
    public void Clear()
    {
        Fill(0, 0, Columns, Rows);
        CursorColumn = 0;
        CursorRow = 0;
    }

    /// <summary>
    /// Moves rows <paramref name="top"/> to <paramref name="bottom"/> - 1 up by <paramref name="lines"/>
    /// (down if negative). Rows uncovered are blanked in the current colours.
    /// </summary>
    public void Scroll(int top, int bottom, int lines)
    {
        top = Math.Max(top, 0);
        bottom = Math.Min(bottom, Rows);
        int height = bottom - top;
        if (height <= 0 || lines == 0)
            return;
        if (Math.Abs(lines) >= height)
        {
            Fill(0, top, Columns, height);
            return;
        }
        int shift = lines * Columns;
        int start = top * Columns, length = (height - Math.Abs(lines)) * Columns;
        if (lines > 0)
        {
            Array.Copy(_chars, start + shift, _chars, start, length);
            Array.Copy(_fore, start + shift, _fore, start, length);
            Array.Copy(_back, start + shift, _back, start, length);
            Fill(0, bottom - lines, Columns, lines);
        }
        else
        {
            Array.Copy(_chars, start, _chars, start - shift, length);
            Array.Copy(_fore, start, _fore, start - shift, length);
            Array.Copy(_back, start, _back, start - shift, length);
            Fill(0, top, Columns, -lines);
        }
    }

    private bool Inside(int column, int row) => column >= 0 && row >= 0 && column < Columns && row < Rows;
}

/// <summary>Faces of the desktop font for a <see cref="TextScreen"/>.</summary>
public enum TextFont
{
    /// <summary>The terminal's face.</summary>
    Bold,

    /// <summary>A thinner face of the same font.</summary>
    Regular,

    /// <summary>The game's monospaced font (font_cpmono): thin, ASCII only. Other characters lose their accents or show as '?'.</summary>
    Mono,

    /// <summary>The game's IBM PC font (font_cga): 8x8, code page 437, with accented letters and box drawing.</summary>
    Cga,
}

/// <summary>An opaque RGB colour.</summary>
public readonly struct TextColor : IEquatable<TextColor>
{
    public TextColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    /// <summary>The colour as 0xRRGGBB.</summary>
    public int Rgb => R << 16 | G << 8 | B;

    /// <summary>From 0xRRGGBB.</summary>
    public static TextColor FromRgb(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public bool Equals(TextColor other) => Rgb == other.Rgb;
    public override bool Equals(object obj) => obj is TextColor other && Equals(other);
    public override int GetHashCode() => Rgb;
    public static bool operator ==(TextColor a, TextColor b) => a.Equals(b);
    public static bool operator !=(TextColor a, TextColor b) => !a.Equals(b);
    public override string ToString() => $"#{Rgb:X6}";
}

public enum ProgramKey
{
    /// <summary>A typed character, in <see cref="KeyPress.Char"/>.</summary>
    Character,
    Enter, Backspace, Delete, Escape, Tab,
    Up, Down, Left, Right, Home, End, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

public readonly struct KeyPress
{
    public KeyPress(ProgramKey key, char c, bool shift, bool control, bool alt)
    {
        Key = key;
        Char = c;
        Shift = shift;
        Control = control;
        Alt = alt;
    }

    public ProgramKey Key { get; }

    /// <summary>The character for <see cref="ProgramKey.Character"/>, otherwise '\0'.</summary>
    public char Char { get; }

    public bool Shift { get; }
    public bool Control { get; }
    public bool Alt { get; }

    public override string ToString() => Key == ProgramKey.Character ? $"'{Char}'" : Key.ToString();
}
