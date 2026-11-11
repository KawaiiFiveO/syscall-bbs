// Example desktop program: type on a page, which is kept between sessions.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.Typewriter;

public sealed class TypewriterMod : IMod
{
    public void OnLoad(ModContext context)
    {
        var program = new Typewriter(context.Config.GetInt("Columns", 80), context.Config.GetInt("Rows", 25));
        context.Programs.Add("Typewriter", context.Textures.Load("icon.png"), program);
    }
}

internal sealed class Typewriter : IDesktopProgram
{
    private static readonly TextColor Paper = TextColor.FromRgb(0xE8E0C8);
    private static readonly TextColor Ink = TextColor.FromRgb(0x202838);
    private static readonly TextColor Bar = TextColor.FromRgb(0x384858);
    private static readonly TextColor BarText = TextColor.FromRgb(0xE8E0C8);

    private List<string> _lines = new() { "" };

    public Typewriter(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public string Title => "Typewriter";
    public int Columns { get; }
    public int Rows { get; }

    private int Width => Columns - 2;

    public void Opened(ProgramWindow window)
    {
        string page = PagePath(window);
        if (File.Exists(page))
            _lines = File.ReadAllLines(page).ToList();
        if (_lines.Count == 0)
            _lines.Add("");
    }

    public void Closed(ProgramWindow window) => File.WriteAllLines(PagePath(window), _lines);

    public void KeyPressed(ProgramWindow window, KeyPress key)
    {
        switch (key.Key)
        {
            case ProgramKey.Character:
                if (_lines[_lines.Count - 1].Length >= Width)
                    _lines.Add("");
                _lines[_lines.Count - 1] += key.Char;
                break;
            case ProgramKey.Enter:
                _lines.Add("");
                break;
            case ProgramKey.Backspace when _lines[_lines.Count - 1].Length > 0:
                string line = _lines[_lines.Count - 1];
                _lines[_lines.Count - 1] = line.Substring(0, line.Length - 1);
                break;
            case ProgramKey.Backspace when _lines.Count > 1:
                _lines.RemoveAt(_lines.Count - 1);
                break;
            case ProgramKey.F1:
                _lines = new List<string> { "" };
                break;
        }
    }

    public void Update(ProgramWindow window, float seconds)
    {
        TextScreen screen = window.Screen;
        screen.Foreground = Ink;
        screen.Background = Paper;
        screen.Clear();

        // The page shows its last lines, between a title bar and a status bar.
        int height = Rows - 2;
        List<string> shown = _lines.Skip(System.Math.Max(0, _lines.Count - height)).ToList();
        for (int i = 0; i < shown.Count; i++)
            screen.Write(1, 1 + i, shown[i]);

        DrawBar(screen, 0, " TYPEWRITER");
        DrawBar(screen, Rows - 1, $" {_lines.Count} line(s)   F1 new page");
        screen.CursorColumn = 1 + shown[shown.Count - 1].Length;
        screen.CursorRow = shown.Count;
        screen.CursorVisible = true;
    }

    private void DrawBar(TextScreen screen, int row, string text)
    {
        screen.Write(0, row, text.PadRight(Columns), BarText, Bar);
    }

    private static string PagePath(ProgramWindow window) => Path.Combine(window.DataDirectory, "page.txt");
}
