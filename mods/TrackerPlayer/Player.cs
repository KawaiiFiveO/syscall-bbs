using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LastCallBBS.ModAPI;
using Tracker;

namespace LastCallBBS.Mods.TrackerPlayer;

/// <summary>The desktop program: a module list, a pattern view of the module playing, and an About page.</summary>
internal sealed class Player : IDesktopProgram, IProgramMenu, IProgramMouse
{
    private const int CellWidth = 11; // " C-3 01 C20"
    private const int PatternLeft = 3; // after the row numbers
    private static readonly string[] Extensions = { ".mod", ".m15", ".stk", ".nst" };

    private enum Page { List, Pattern, Text, About }

    /// <summary>A clickable area drawn this frame.</summary>
    private readonly struct Button
    {
        public Button(int column, int row, int width, Action left, Action right)
        {
            Column = column;
            Row = row;
            Width = width;
            Left = left;
            Right = right;
        }

        public int Column { get; }
        public int Row { get; }
        public int Width { get; }
        public Action Left { get; }
        public Action Right { get; }
        public bool Contains(int column, int row) => row == Row && column >= Column && column < Column + Width;
    }

    private readonly Settings _settings;
    private readonly IModLog _log;
    private readonly ModSounds _sounds;
    private readonly List<Button> _buttons = new();
    private Preferences _prefs = new();
    private string _dataDirectory;
    private ProgramWindow _window;
    private Page _page = Page.List, _pageBeforeAbout;
    private ProgramMenuItem[] _menu;

    private List<string> _files = new();
    private List<long> _sizes = new();
    private float _sinceScan;
    private int _selected, _listTop;
    private string _message;

    private Module _module;
    private string _modulePath, _moduleName;
    private TimeSpan _duration;
    private ModulePlayer _player;
    private ModuleStream _source;
    private AudioStream _stream;
    private PlayView _view;
    private int _firstChannel;

    public Player(Settings settings, IModLog log, ModSounds sounds)
    {
        _settings = settings;
        _log = log;
        _sounds = sounds;
    }

    public string Title => _page == Page.Pattern && _module != null ? $"{Product.Name} - {_moduleName}" : Product.Name;
    public int Columns => _settings.Columns;
    public int Rows => _settings.Rows;

    private Theme T => Theme.Find(_prefs.Theme);

    // ---- Menu ----

    public IReadOnlyList<ProgramMenuItem> MenuItems => _menu ??= BuildMenu();

    // The panel shows labels up to about 20 characters, has no submenus and must fit the screen.
    // Colors and fonts are checklists under a heading; the other choices are one item each that
    // steps to the next value. Headings are ordinary items that do nothing: the game draws disabled
    // items dim, at half opacity.
    private ProgramMenuItem[] BuildMenu()
    {
        ProgramMenuItem Item(string label, Action action) => new(label, () => { action(); Changed(); });
        static ProgramMenuItem Heading(string label) => new("── " + label + " ──", () => { });
        ProgramMenuItem Check(bool on, string label, Action action) => Item((on ? "[X] " : "[  ] ") + label, action);
        var items = new List<ProgramMenuItem>
        {
            _page == Page.List
                ? new ProgramMenuItem("Refresh List", Scan)
                : new ProgramMenuItem("Module List", ShowList),
        };
        if (_module != null)
            items.Add(new ProgramMenuItem(_page == Page.Text ? "Pattern View" : "Module Text", ToggleText));
        items.Add(new ProgramMenuItem("Open Module Folder", OpenFolder));
        items.Add(ProgramMenuItem.Separator);
        items.Add(Check(_prefs.Loop, "Loop Song", () => _prefs.Loop = !_prefs.Loop));
        items.Add(Item("Mute Music: " + MusicLabels[(int)_prefs.GameMusic], () => _prefs.GameMusic = (GameMusic)(((int)_prefs.GameMusic + 1) % MusicLabels.Length)));
        items.Add(Item("Filter: " + FilterLabel(_prefs.Model), () => _prefs.Model = Next(Filters, _prefs.Model)));
        items.Add(Item("Stereo: " + (_prefs.Separation == 0 ? "Mono" : _prefs.Separation + "%"), () => _prefs.Separation = Next(Separations, _prefs.Separation)));
        items.Add(Heading("COLORS"));
        foreach (Theme theme in Theme.All)
            items.Add(Check(T == theme, theme.Name, () => _prefs.Theme = theme.Name));
        items.Add(Heading("FONT"));
        foreach (TextFont font in Fonts)
            items.Add(Check(_prefs.Font == font, font == TextFont.Cga ? "CGA" : font.ToString(), () => _prefs.Font = font));
        items.Add(ProgramMenuItem.Separator);
        items.Add(new ProgramMenuItem("About " + Product.Name, ShowAbout));
        return items.ToArray();
    }

    private static readonly string[] MusicLabels = { "None", "Desktop", "All" };
    private static readonly AmigaModel[] Filters = { AmigaModel.A500, AmigaModel.A1200, AmigaModel.None };
    private static readonly int[] Separations = { 0, 20, 50, 100 };
    private static readonly TextFont[] Fonts = { TextFont.Bold, TextFont.Regular, TextFont.Mono, TextFont.Cga };

    private static string FilterLabel(AmigaModel model) => model == AmigaModel.A500 ? "Amiga 500" : model == AmigaModel.A1200 ? "Amiga 1200" : "None";

    private static T2 Next<T2>(T2[] values, T2 current) => values[(Array.IndexOf(values, current) + 1) % values.Length];

    /// <summary>Preferences changed: apply, save and rebuild the menu.</summary>
    private void Changed()
    {
        ApplyPreferences();
        SavePreferences();
        _menu = null;
    }

    private void GoTo(Page page)
    {
        _page = page;
        _menu = null;
    }

    private void ShowList() => GoTo(Page.List);

    private void ShowAbout()
    {
        if (_page != Page.About)
            _pageBeforeAbout = _page;
        GoTo(Page.About);
    }

    // ---- Window ----

    public void Opened(ProgramWindow window)
    {
        _window = window;
        _dataDirectory = window.DataDirectory;
        _prefs = Preferences.Load(PreferencesPath);
        _menu = null;
        Scan();
    }

    public void Closed(ProgramWindow window)
    {
        StopStream();
        _module = null;
        _player = null;
        GoTo(Page.List);
    }

    public void Update(ProgramWindow window, float seconds)
    {
        _window = window;
        window.GameMusic = _prefs.GameMusic;
        TextScreen screen = window.Screen;
        screen.Font = _prefs.Font;
        screen.Foreground = T.Text;
        screen.Background = T.Back;
        screen.CursorVisible = false;
        screen.Clear();
        _buttons.Clear();
        if (_page == Page.List && (_sinceScan += seconds) >= ScanSeconds)
            Scan();
        switch (_page)
        {
            case Page.List: DrawList(screen); break;
            case Page.Pattern: DrawPattern(screen); break;
            case Page.Text: DrawText(screen); break;
            default: DrawAbout(screen); break;
        }
    }

    // ---- Input ----

    public void KeyPressed(ProgramWindow window, KeyPress key)
    {
        switch (_page)
        {
            case Page.About:
                GoTo(_pageBeforeAbout);
                break;
            case Page.List:
                ListKey(key);
                break;
            case Page.Text:
                TextKey(key);
                break;
            default:
                PatternKey(key);
                break;
        }
    }

    public void MouseClicked(ProgramWindow window, MouseClick click)
    {
        if (_page == Page.About)
        {
            GoTo(_pageBeforeAbout);
            return;
        }
        foreach (Button b in _buttons)
        {
            if (!b.Contains(click.Column, click.Row))
                continue;
            if (click.Button == MouseButton.Left)
                b.Left?.Invoke();
            else if (click.Button == MouseButton.Right)
                (b.Right ?? b.Left)?.Invoke();
            return;
        }
    }

    public void MouseWheel(ProgramWindow window, int column, int row, int notches)
    {
        if (_page == Page.List)
            Select(_selected - notches * 3);
        else if (_page == Page.Pattern)
            ScrollChannels(-notches);
        else if (_page == Page.Text)
            _textTop -= notches * 3;
    }

    // ---- Module list ----

    // Files can be added, removed or renamed at any time; the list is rescanned while shown.
    private const float ScanSeconds = 2f;

    private void Scan()
    {
        string selected = _selected < _files.Count ? _files[_selected] : null;
        var files = new List<string>();
        var sizes = new List<long>();
        try
        {
            if (Directory.Exists(_settings.ModuleFolder))
            {
                foreach (string f in Directory.EnumerateFiles(_settings.ModuleFolder, "*", SearchOption.AllDirectories)
                    .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()) || Path.GetFileName(f).StartsWith("mod.", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        sizes.Add(new FileInfo(f).Length);
                        files.Add(f);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        // Removed since it was listed.
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // The folder changed while being read; the next scan catches up.
        }
        _files = files;
        _sizes = sizes;
        _sinceScan = 0f;
        int kept = selected == null ? -1 : _files.IndexOf(selected);
        Select(kept >= 0 ? kept : _selected);
    }

    private void Select(int index) => _selected = Math.Max(0, Math.Min(index, _files.Count - 1));

    private int ListRows => Rows - 5;

    private void ListKey(KeyPress key)
    {
        switch (key.Key)
        {
            case ProgramKey.Up: Select(_selected - 1); break;
            case ProgramKey.Down: Select(_selected + 1); break;
            case ProgramKey.PageUp: Select(_selected - ListRows); break;
            case ProgramKey.PageDown: Select(_selected + ListRows); break;
            case ProgramKey.Home: Select(0); break;
            case ProgramKey.End: Select(_files.Count - 1); break;
            case ProgramKey.F5: Scan(); break;
            case ProgramKey.Enter: PlaySelected(); break;
            case ProgramKey.Escape when _module != null: GoTo(Page.Pattern); break;
        }
    }

    private void PlaySelected()
    {
        if (_files.Count > 0)
            Load(_files[_selected]);
    }

    private void DrawList(TextScreen screen)
    {
        Header(screen, Product.Name, $"v{_settings.Version}");
        screen.Write(1, 1, "Modules in " + ShownFolder(Columns - 13), T.Dim, T.Back);
        if (_files.Count == 0)
        {
            screen.Write(2, 3, "No modules found. Put .mod files in the folder above,", T.Text, T.Back);
            screen.Write(2, 4, "or choose Open Module Folder.", T.Text, T.Back);
        }
        int top = 2, rows = ListRows;
        if (_selected < _listTop)
            _listTop = _selected;
        if (_selected >= _listTop + rows)
            _listTop = _selected - rows + 1;
        _listTop = Math.Max(0, Math.Min(_listTop, Math.Max(0, _files.Count - rows)));
        for (int i = 0; i < rows && _listTop + i < _files.Count; i++)
        {
            int index = _listTop + i;
            bool selected = index == _selected;
            bool hover = Hovered(0, top + i, Columns);
            TextColor back = selected ? T.Highlight : hover ? T.Button : T.Back;
            TextColor fore = selected ? T.HighlightText : T.Text;
            screen.Write(0, top + i, new string(' ', Columns), fore, back);
            bool playing = _module != null && _files[index] == _modulePath;
            string size = $"{(_sizes[index] + 1023) / 1024} KB";
            screen.Write(1, top + i, (playing ? "> " : "  ") + Shorten(RelativeName(_files[index]), Columns - size.Length - 6), fore, back);
            screen.Write(Columns - size.Length - 2, top + i, size, selected ? fore : T.Dim, back);
            _buttons.Add(new Button(0, top + i, Columns, () =>
            {
                if (_selected == index)
                    PlaySelected();
                else
                    _selected = index;
            }, null));
        }
        if (_files.Count > rows)
            ScrollMarks(screen, top, rows, _listTop, _files.Count);

        int y = Rows - 3, x = 1;
        x += DrawButton(screen, x, y, "PLAY", PlaySelected, _files.Count > 0) + 1;
        x += DrawButton(screen, x, y, "REFRESH", Scan) + 1;
        x += DrawButton(screen, x, y, "OPEN FOLDER", OpenFolder) + 1;
        if (_module != null)
            DrawButton(screen, x, y, "BACK TO PLAYER", () => GoTo(Page.Pattern));
        DrawButton(screen, Columns - 8, y, "ABOUT", ShowAbout);
        if (_message != null)
            screen.Write(1, Rows - 2, Shorten(_message, Columns - 2), T.Error, T.Back);
        Footer(screen, "Click or Enter: play   F5: refresh" + (_module != null ? "   Esc: back to player" : ""));
    }

    /// <summary>Up and down marks at the right edge when a list scrolls.</summary>
    private void ScrollMarks(TextScreen screen, int top, int rows, int first, int count)
    {
        if (first > 0)
            screen.Put(Columns - 1, top, '▲', T.Dim, T.Back);
        if (first + rows < count)
            screen.Put(Columns - 1, top + rows - 1, '▼', T.Dim, T.Back);
    }

    private void Load(string path)
    {
        Module module;
        try
        {
            module = Module.Load(path);
        }
        catch (Exception e) when (e is ModuleFormatException || e is IOException || e is UnauthorizedAccessException)
        {
            _message = $"{Path.GetFileName(path)}: {e.Message}";
            Scan();
            return;
        }
        StopStream();
        _message = null;
        _module = module;
        _modulePath = path;
        _moduleName = module.Title.Length > 0 ? module.Title : Path.GetFileNameWithoutExtension(path);
        _duration = SongLength.Measure(module);
        _player = new ModulePlayer(module, ModSounds.SampleRate);
        _view = _player.CreateView();
        _firstChannel = 0;
        _textLines = TextLines(module);
        _textTop = 0;
        ApplyPreferences();
        GoTo(Page.Pattern);
        StartStream();
    }

    // ---- Playback ----

    private void StartStream()
    {
        _source = new ModuleStream(_player);
        _stream = _sounds.Play(_source, _settings.Volume);
        if (_stream == null)
            _message = "Audio streams don't work on this build of the game. Details are in the log.";
    }

    private void StopStream()
    {
        _stream?.Stop();
        _stream = null;
        _source = null;
    }

    private bool Playing => _stream != null && _stream.IsPlaying;
    private bool Paused => Playing && _source.Paused;

    private void PlayOrPause()
    {
        if (!Playing)
        {
            _player.Restart();
            StartStream();
        }
        else
        {
            _source.Paused = !_source.Paused;
        }
    }

    private void Stop()
    {
        StopStream();
        _player.Restart();
    }

    private void Restart()
    {
        _player.Restart();
        if (!Playing)
            StartStream();
        else
            _source.Paused = false;
    }

    private void Seek(int delta)
    {
        _player.Seek(_player.Position + delta);
        if (!Playing)
            StartStream();
    }

    private void ApplyPreferences()
    {
        if (_player == null)
            return;
        _player.Loop = _prefs.Loop;
        _player.Model = _prefs.Model;
        _player.StereoSeparation = _prefs.Separation / 100f;
        _player.Gain = _settings.Amplification;
    }

    // ---- Pattern view ----

    // All channels if they fit with a column to spare; otherwise as many as fit, with a scrollbar.
    private int VisibleChannels => PatternLeft + _module.Channels * CellWidth < Columns
        ? _module.Channels
        : Math.Max(1, (Columns - PatternLeft - 1) / CellWidth);

    private bool ChannelsScroll => VisibleChannels < _module.Channels;

    private void ScrollChannels(int delta) =>
        _firstChannel = Math.Max(0, Math.Min(_module.Channels - VisibleChannels, _firstChannel + delta));

    private void ToggleMute(int channel) => _player.SetMuted(channel, !_player.IsMuted(channel));

    /// <summary>Hears only this channel, or everything again if it already is the only one.</summary>
    private void Solo(int channel)
    {
        bool alreadySolo = Enumerable.Range(0, _module.Channels).All(c => _player.IsMuted(c) == (c != channel));
        for (int c = 0; c < _module.Channels; c++)
            _player.SetMuted(c, !alreadySolo && c != channel);
    }

    private void PatternKey(KeyPress key)
    {
        int visible = VisibleChannels;
        switch (key.Key)
        {
            case ProgramKey.Escape: ShowList(); return;
            case ProgramKey.Left: Seek(-1); return;
            case ProgramKey.Right: Seek(1); return;
            case ProgramKey.Home: Restart(); return;
            case ProgramKey.Tab: ScrollChannels(key.Shift ? -visible : visible); return;
            case ProgramKey.Character: break;
            default: return;
        }
        char c = char.ToLowerInvariant(key.Char);
        if (c == ' ')
            PlayOrPause();
        else if (c == 's')
            Stop();
        else if (c == 'l')
        {
            _prefs.Loop = !_prefs.Loop;
            Changed();
        }
        else if (c == 't')
            ToggleText();
        else if (c == '[')
            ScrollChannels(-1);
        else if (c == ']')
            ScrollChannels(1);
        else if (c >= '1' && c <= '9' || c == '0')
        {
            int channel = _firstChannel + (c == '0' ? 9 : c - '1');
            if (channel < _module.Channels && channel < _firstChannel + visible)
                ToggleMute(channel);
        }
    }

    private void DrawPattern(TextScreen screen)
    {
        // What is being heard lags what has been rendered by the game's buffer.
        long heard = Math.Max(0, _player.FramesRendered - ModSounds.BufferFrames);
        bool haveView = _player.GetView(heard, _view);
        int position = haveView ? _view.Position : 0;
        int pattern = haveView ? _view.Pattern : _module.Orders[0];
        int row = haveView ? _view.Row : 0;
        TimeSpan elapsed = haveView ? _view.Elapsed : TimeSpan.Zero;

        string format = _module.Tag.Length == 0 ? "15 SAMPLES" : _module.Tag.Contains("CH") ? _module.Tag : $"{_module.Tag} {_module.Channels}CH";
        Header(screen, _moduleName, format);
        string info = $"POS {position:D3}/{_module.SongLength:D3}  PAT {pattern:D2}  ROW {row:D2}  " +
                      $"BPM {(haveView ? _view.Tempo : _module.InitialTempo):D3}  SPD {(haveView ? _view.Speed : 6):D2}  {Clock(elapsed)} / {Clock(_duration)}";
        screen.Write(1, 1, Shorten(info, Columns - 10), T.Text, T.Back);
        string state = !Playing ? "STOPPED" : Paused ? "PAUSED" : "PLAYING";
        screen.Write(Columns - state.Length - 1, 1, state, !Playing ? T.Dim : Paused ? T.Mid : T.Low, T.Back);

        // Transport.
        int y = 3, x = 1; // a blank row above and below the buttons
        x += DrawButton(screen, x, y, "|<", Restart) + 1;
        x += DrawButton(screen, x, y, "<<", () => Seek(-1)) + 1;
        x += DrawButton(screen, x, y, !Playing || Paused ? " PLAY" : "PAUSE", PlayOrPause) + 1;
        x += DrawButton(screen, x, y, ">>", () => Seek(1)) + 1;
        x += DrawButton(screen, x, y, "STOP", Stop, Playing) + 2;
        DrawButton(screen, x, y, "LOOP", () => { _prefs.Loop = !_prefs.Loop; Changed(); }, active: _prefs.Loop);
        DrawButton(screen, Columns - 14, y, "TEXT", ToggleText);
        DrawButton(screen, Columns - 7, y, "LIST", ShowList);

        int visible = VisibleChannels;
        _firstChannel = Math.Max(0, Math.Min(_firstChannel, _module.Channels - visible));
        DrawChannelHeaders(screen, 5, visible, haveView);

        // Pattern rows, the current row in the middle.
        int top = 6, bottom = Rows - (ChannelsScroll ? 2 : 1);
        int middle = top + (bottom - top) / 2;
        for (int line = top; line < bottom; line++)
        {
            int r = row + (line - middle);
            bool current = line == middle;
            TextColor back = current ? T.Highlight : T.Back;
            if (current)
                screen.Write(0, line, new string(' ', Columns), T.Text, back);
            if (r < 0 || r >= Tracker.Module.Rows)
                continue;
            screen.Write(1, line, r.ToString("D2"), current ? T.HighlightText : T.Dim, back);
            for (int v = 0; v < visible; v++)
            {
                int channel = _firstChannel + v;
                DrawNote(screen, PatternLeft + v * CellWidth + 1, line, _module.GetNote(pattern, r, channel), back, current, _player.IsMuted(channel));
            }
        }
        if (ChannelsScroll)
            DrawChannelScrollbar(screen, Rows - 2, visible);
        Footer(screen, ChannelsScroll ? "Space play  S stop  Left/Right position  1-0 mute  [ ] channels  Esc list" : "Space play  S stop  Left/Right position  1-0 mute  L loop  Esc list");
    }

    private void DrawChannelHeaders(TextScreen screen, int y, int visible, bool haveView)
    {
        for (int v = 0; v < visible; v++)
        {
            int channel = _firstChannel + v;
            int x = PatternLeft + v * CellWidth;
            bool hover = Hovered(x, y, CellWidth);
            TextColor back = hover ? T.Button : T.Back;
            screen.Write(x, y, new string(' ', CellWidth), T.Text, back);
            _buttons.Add(new Button(x, y, CellWidth, () => ToggleMute(channel), () => Solo(channel)));
            if (_player.IsMuted(channel))
            {
                screen.Write(x + 1, y, $"{channel + 1:D2} MUTED", T.High, back);
                continue;
            }
            screen.Write(x + 1, y, $"{channel + 1:D2}", T.Text, back);
            float level = haveView ? _view.Channels[channel].Level : 0f;
            int bars = (int)Math.Round(Math.Min(1f, level * 1.5f) * 7);
            for (int b = 0; b < 7; b++)
                screen.Put(x + 4 + b, y, b < bars ? '█' : '·', b < bars ? (b < 4 ? T.Low : b < 6 ? T.Mid : T.High) : T.Dim, back);
        }
    }

    /// <summary>Arrows and a track under the pattern; click the track to jump.</summary>
    private void DrawChannelScrollbar(TextScreen screen, int y, int visible)
    {
        int channels = _module.Channels;
        int trackLeft = PatternLeft + 1, trackWidth = visible * CellWidth - 2;
        DrawButton(screen, PatternLeft - 2, y, "◄", () => ScrollChannels(-1), _firstChannel > 0, pad: false);
        DrawButton(screen, trackLeft + trackWidth, y, "►", () => ScrollChannels(1), _firstChannel + visible < channels, pad: false);
        int maxFirst = channels - visible;
        int thumbWidth = Math.Max(2, trackWidth * visible / channels);
        int thumbLeft = (trackWidth - thumbWidth) * _firstChannel / maxFirst;
        for (int i = 0; i < trackWidth; i++)
        {
            bool thumb = i >= thumbLeft && i < thumbLeft + thumbWidth;
            screen.Put(trackLeft + i, y, thumb ? '█' : '░', thumb ? T.Text : T.Dim, T.Back);
            int target = (int)Math.Round((double)i * maxFirst / Math.Max(1, trackWidth - 1));
            _buttons.Add(new Button(trackLeft + i, y, 1, () => ScrollChannels(target - _firstChannel), null));
        }
        string range = $"{_firstChannel + 1}-{_firstChannel + visible}/{channels}";
        screen.Write(trackLeft + trackWidth + 2, y, range, T.Dim, T.Back);
    }

    private void DrawNote(TextScreen screen, int x, int y, Note note, TextColor back, bool current, bool muted)
    {
        TextColor Pick(bool empty, TextColor color) => current ? T.HighlightText : muted || empty ? T.Dim : color;
        screen.Write(x, y, note.NoteName, Pick(note.Period == 0, T.Note), back);
        screen.Write(x + 4, y, note.Sample.ToString("X2"), Pick(note.Sample == 0, T.Sample), back);
        screen.Write(x + 7, y, $"{note.Effect:X1}{note.Parameter:X2}", Pick(note.Effect == 0 && note.Parameter == 0, T.Effect), back);
    }

    // ---- Module text ----

    private List<(string Text, int Sample)> _textLines = new();
    private int _textTop;

    private void ToggleText() => GoTo(_page == Page.Text ? Page.Pattern : Page.Text);

    /// <summary>Sample names in order (where messages usually are), then any text after the samples. Sample 0 is plain text, -1 a heading.</summary>
    private List<(string Text, int Sample)> TextLines(Module module)
    {
        var lines = new List<(string, int)> { ("Samples", -1) };
        int last = Array.FindLastIndex(module.Samples, s => s.Name.Length > 0 || s.Length > 0);
        for (int i = 0; i <= last; i++)
            lines.Add((module.Samples[i].Name, i + 1));
        if (module.Comment.Length > 0)
        {
            lines.Add(("", 0));
            lines.Add(("Text after the samples", -1));
            foreach (string line in module.Comment.Split('\n'))
            {
                foreach (string part in line.Length <= Columns - 4 ? new[] { line } : Chunks(line, Columns - 4))
                    lines.Add((part, 0));
            }
        }
        return lines;
    }

    private int TextRows => Rows - 5;

    private void TextKey(KeyPress key)
    {
        switch (key.Key)
        {
            case ProgramKey.Up: _textTop--; break;
            case ProgramKey.Down: _textTop++; break;
            case ProgramKey.PageUp: _textTop -= TextRows; break;
            case ProgramKey.PageDown: _textTop += TextRows; break;
            case ProgramKey.Home: _textTop = 0; break;
            case ProgramKey.End: _textTop = _textLines.Count; break;
            case ProgramKey.Escape: GoTo(Page.Pattern); break;
            case ProgramKey.Character when char.ToLowerInvariant(key.Char) == 't': GoTo(Page.Pattern); break;
            case ProgramKey.Character when key.Char == ' ': PlayOrPause(); break;
            case ProgramKey.Character when char.ToLowerInvariant(key.Char) == 's': Stop(); break;
        }
    }

    private void DrawText(TextScreen screen)
    {
        Header(screen, _moduleName, "TEXT");
        int x = 1;
        x += DrawButton(screen, x, 2, "PATTERN", ToggleText) + 1;
        x += DrawButton(screen, x, 2, !Playing || Paused ? " PLAY" : "PAUSE", PlayOrPause) + 1;
        DrawButton(screen, x, 2, "STOP", Stop, Playing);

        // Samples heard now are highlighted.
        var playing = new HashSet<int>();
        if (Playing && _player.GetView(Math.Max(0, _player.FramesRendered - ModSounds.BufferFrames), _view))
        {
            foreach (ChannelView ch in _view.Channels)
            {
                if (ch.Level > 0.01f)
                    playing.Add(ch.Sample);
            }
        }

        int top = 4, rows = TextRows;
        _textTop = Math.Max(0, Math.Min(_textTop, _textLines.Count - rows));
        for (int i = 0; i < rows && _textTop + i < _textLines.Count; i++)
        {
            (string text, int sample) = _textLines[_textTop + i];
            int y = top + i;
            if (sample < 0)
            {
                screen.Write(1, y, text, T.Dim, T.Back);
                continue;
            }
            if (sample == 0)
            {
                screen.Write(2, y, text, T.Text, T.Back);
                continue;
            }
            bool heard = playing.Contains(sample);
            Sample s = _module.Samples[sample - 1];
            screen.Write(1, y, heard ? "►" : " ", T.Low, T.Back);
            screen.Write(2, y, sample.ToString("X2"), T.Sample, T.Back);
            screen.Write(5, y, text, heard ? T.Low : T.Text, T.Back);
            if (s.Length > 0 && Columns >= 52)
            {
                string info = $"{s.Length,6}  vol {s.Volume,2}{(s.Loops ? "  loop" : "      ")}";
                screen.Write(Columns - info.Length - 2, y, info, T.Dim, T.Back);
            }
        }
        if (_textLines.Count > rows)
            ScrollMarks(screen, top, rows, _textTop, _textLines.Count);
        Footer(screen, "Up/Down scroll   T or Esc pattern view   Space play/pause   S stop");
    }

    // ---- About ----

    private void DrawAbout(TextScreen screen)
    {
        Header(screen, $"{Product.Name.ToUpperInvariant()} {_settings.Version}", "");
        int row = 2;
        void Line(string text = "", TextColor? color = null)
        {
            // Paths break anywhere; prose breaks between words.
            IEnumerable<string> parts = text.Length <= Columns - 4 ? new[] { text } : color.HasValue ? Chunks(text, Columns - 4) : Words(text, Columns - 4);
            foreach (string part in parts)
            {
                if (row < Rows - 1)
                    screen.Write(2, row++, part, color ?? T.Text, T.Back);
            }
        }
        Line($"{Product.Name} - Protracker module player by onehalf");
        Line("Plays .mod files from Protracker, NoiseTracker, SoundTracker, etc.");
        Line();
        Line("Module folder:");
        Line(_settings.ModuleFolder, T.Dim);
        Line();
        Line("Preferences:");
        Line(PreferencesPath ?? "", T.Dim);
        Line();
        void Key(string key, string action)
        {
            if (row >= Rows - 1)
                return;
            screen.Write(4, row, key, T.Sample, T.Back);
            screen.Write(16, row++, action, T.Text, T.Back);
        }
        Line("Keys:");
        Key("Space", "Play or pause");
        Key("S", "Stop");
        Key("Home", "Restart");
        Key("Left/Right", "Previous or next position");
        Key("T", "Module text");
        Key("1-0", "Mute a channel");
        Key("[ ]", "Scroll channels");
        Key("Tab", "Page through channels");
        Key("L", "Loop song");
        Key("Esc", "Module list");
        Line();
        Line("Mouse: click a channel to mute, right-click to solo");
        Footer(screen, "Press any key or click to return.");
    }

    // ---- Helpers ----

    private bool Hovered(int column, int row, int width) =>
        _window != null && _window.MouseRow == row && _window.MouseColumn >= column && _window.MouseColumn < column + width;

    /// <summary>Draws a button and registers its click. Returns its width.</summary>
    private int DrawButton(TextScreen screen, int x, int y, string label, Action action, bool enabled = true, bool active = false, bool pad = true)
    {
        string text = pad ? " " + label + " " : label;
        bool hover = enabled && Hovered(x, y, text.Length);
        // Disabled buttons are flat: dim text on the window's background.
        TextColor back = !enabled ? T.Back : active ? T.Active : hover ? T.ButtonHover : T.Button;
        TextColor fore = !enabled ? T.Dim : active ? T.ActiveText : T.ButtonText;
        screen.Write(x, y, text, fore, back);
        if (enabled)
            _buttons.Add(new Button(x, y, text.Length, action, null));
        return text.Length;
    }

    private void Header(TextScreen screen, string left, string right)
    {
        screen.Write(0, 0, new string(' ', Columns), T.BarText, T.Bar);
        screen.Write(1, 0, Shorten(left, Columns - right.Length - 4), T.BarText, T.Bar);
        if (right.Length > 0)
            screen.Write(Columns - right.Length - 1, 0, right, T.BarText, T.Bar);
    }

    private void Footer(TextScreen screen, string text)
    {
        screen.Write(0, Rows - 1, new string(' ', Columns), T.BarText, T.Bar);
        screen.Write(1, Rows - 1, Shorten(text, Columns - 2), T.BarText, T.Bar);
    }

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    private static string Shorten(string text, int width) =>
        width <= 0 ? "" : text.Length <= width ? text : text.Substring(0, Math.Max(0, width - 3)) + "...";

    private static IEnumerable<string> Words(string text, int width)
    {
        var line = new StringBuilder();
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }
        yield return line.ToString();
    }

    private static IEnumerable<string> Chunks(string text, int width)
    {
        for (int i = 0; i < text.Length; i += width)
            yield return text.Substring(i, Math.Min(width, text.Length - i));
    }

    /// <summary>The module folder relative to the game's folder when inside it, cut from the left to fit.</summary>
    private string ShownFolder(int width)
    {
        string folder = _settings.ModuleFolder;
        if (!string.IsNullOrEmpty(_settings.GameFolder))
        {
            string game = _settings.GameFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (folder.StartsWith(game, StringComparison.OrdinalIgnoreCase))
                folder = folder.Substring(game.Length);
        }
        folder = folder.Replace('\\', '/').TrimEnd('/') + "/";
        return folder.Length <= width ? folder : "..." + folder.Substring(folder.Length - Math.Max(0, width - 3));
    }

    private string RelativeName(string path)
    {
        string folder = _settings.ModuleFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? path.Substring(folder.Length).Replace('\\', '/') : Path.GetFileName(path);
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.ModuleFolder);
            Process.Start(new ProcessStartInfo { FileName = _settings.ModuleFolder, UseShellExecute = true });
        }
        catch (Exception e)
        {
            _log.Warn($"Couldn't open the module folder: {e.Message}");
        }
    }

    private string PreferencesPath => _dataDirectory == null ? null : Path.Combine(_dataDirectory, "preferences.ini");

    private void SavePreferences()
    {
        if (_dataDirectory == null)
            return;
        try
        {
            _prefs.Save(PreferencesPath);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            _log.Warn($"Couldn't save preferences: {e.Message}");
        }
    }
}

/// <summary>Player settings kept in &lt;data&gt;/preferences.ini.</summary>
internal sealed class Preferences
{
    public bool Loop;
    public GameMusic GameMusic = GameMusic.MutedOnDesktop;
    public AmigaModel Model = AmigaModel.A500;
    public int Separation = 20;
    public TextFont Font = TextFont.Bold;
    public string Theme = TrackerPlayer.Theme.All[0].Name;

    public static Preferences Load(string path)
    {
        var prefs = new Preferences();
        if (path == null || !File.Exists(path))
            return prefs;
        foreach (string line in File.ReadAllLines(path))
        {
            int eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
            bool on = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            switch (key)
            {
                case "Loop": prefs.Loop = on; break;
                case "GameMusic": Enum.TryParse(value, true, out prefs.GameMusic); break;
                case "Model": Enum.TryParse(value, true, out prefs.Model); break;
                case "Separation" when int.TryParse(value, out int s): prefs.Separation = Math.Max(0, Math.Min(100, s)); break;
                case "Font": Enum.TryParse(value, true, out prefs.Font); break;
                case "Theme": prefs.Theme = TrackerPlayer.Theme.Find(value).Name; break;
            }
        }
        return prefs;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path,
            $"Loop = {(Loop ? "true" : "false")}\nGameMusic = {GameMusic}\n" +
            $"Model = {Model}\nSeparation = {Separation}\nFont = {Font}\nTheme = {Theme}\n");
    }
}
