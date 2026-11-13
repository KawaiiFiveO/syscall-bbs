using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LastCallBBS.ModAPI;
using Yomiel;

namespace LastCallBBS.Mods.YomielPlayer;

/// <summary>
/// The program: a story list, then the story. A <see cref="ZSession"/> plays the story; this class
/// adds the menu choices (font, speed, text, sounds), keeps preferences and draws.
/// </summary>
internal sealed class Player : IDesktopProgram, IProgramMenu
{
    private static readonly TextColor DefaultFore = TextColor.FromRgb(0xD8D8D8);
    private static readonly TextColor DefaultBack = TextColor.FromRgb(0x101418);
    private static readonly TextColor BarFore = TextColor.FromRgb(0x101418);
    private static readonly TextColor BarBack = TextColor.FromRgb(0x9CC8A8);
    private static readonly TextColor Dim = TextColor.FromRgb(0x707880);
    private static readonly TextColor Warning = TextColor.FromRgb(0xE08070);

    private readonly Settings settings;
    private readonly IModLog log;
    private string dataRoot;

    // Story list
    private List<StoryFile> stories = new();
    private int selected;
    private string listMessage;

    // Playing
    private StoryFile story;
    private ZSession session;
    private Pacer pacer;

    private static readonly (TextFont Font, string Name, string Label)[] Fonts =
    {
        (TextFont.Regular, "regular", "Regular"),
        (TextFont.Bold, "bold", "Bold"),
        (TextFont.Mono, "mono", "Mono"),
        (TextFont.Cga, "cga", "CGA"),
    };

    // Emulated machine speeds, in Z-machine instructions per second. 0 is full speed.
    private static readonly (int Speed, string Label)[] Speeds =
    {
        (0, "Full"),
        (200_000, "200,000 ops/s"),
        (50_000, "50,000 ops/s"),
        (15_000, "15,000 ops/s"),
    };

    // How text appears: at once, a line at a time (lines per second, as on an IBM PC), or a
    // character at a time (baud, as over a modem).
    private static readonly (string Name, string Label, int Lines, int Baud)[] TextModes =
    {
        ("instant", "Instant", 0, 0),
        ("lines30", "30 lines/s", 30, 0),
        ("lines10", "10 lines/s", 10, 0),
        ("baud9600", "9600 baud", 0, 9600),
        ("baud2400", "2400 baud", 0, 2400),
        ("baud1200", "1200 baud", 0, 1200),
    };

    private ProgramMenuItem[] listMenu;
    private ProgramMenuItem[] storyMenu;
    private TextFont font = TextFont.Regular;
    // The speed and text mode in use, and the ones in preferences.ini that stories without their own settings use.
    private int speed;
    private int textMode;
    private int globalSpeed;
    private int globalTextMode;
    private bool storyOwnSettings;
    private bool about;

    private readonly ModSounds sounds;
    private bool diskSounds = true;
    private bool beeps = true;

    public Player(Settings settings, IModLog log, ModSounds sounds)
    {
        this.settings = settings;
        this.log = log;
        this.sounds = sounds;
        BuildMenus();
    }

    public IReadOnlyList<ProgramMenuItem> MenuItems => session == null ? listMenu : storyMenu;

    // The panel shows labels up to about 20 characters and has no submenus, so each group gets a
    // heading, which also divides it from the group above. Headings are ordinary items that do
    // nothing: the game draws disabled items dim, in its menu shadow colour at half opacity. Rebuilt
    // only when a choice changes, since the desktop reads the menu every frame.
    private void BuildMenus()
    {
        static ProgramMenuItem Heading(string label) => new("── " + label + " ──", () => { });
        static ProgramMenuItem Check(bool on, string label, Action action) => new((on ? "[X] " : "[  ] ") + label, action);
        var choices = new List<ProgramMenuItem> { Heading("FONT") };
        choices.AddRange(Fonts.Select(f => Check(f.Font == font, f.Label, () => ChooseFont(f.Font))));
        choices.Add(Heading("SPEED"));
        choices.AddRange(Speeds.Select(s => Check(s.Speed == speed, s.Label, () => ChooseSpeed(s.Speed))));
        choices.Add(Heading("TEXT"));
        choices.AddRange(TextModes.Select((m, i) => Check(i == textMode, m.Label, () => ChooseTextMode(i))));
        choices.Add(Heading("SOUND"));
        choices.Add(Check(diskSounds, "Disk Sounds", () => { diskSounds = !diskSounds; SavePreferences(); }));
        choices.Add(Check(beeps, "Beeps", () => { beeps = !beeps; SavePreferences(); }));
        var aboutItem = new ProgramMenuItem("About Yomiel", () => about = true);
        var openFolder = new ProgramMenuItem("Open Story Folder", OpenStoryFolder);

        listMenu = new[] { new ProgramMenuItem("Refresh List", () => { ShowList(); listMessage = null; }), openFolder }
            .Concat(choices).Concat(new[] { ProgramMenuItem.Separator, aboutItem }).ToArray();
        storyMenu = new[] { new ProgramMenuItem("Story List", LeaveStory), openFolder }
            .Concat(choices)
            .Concat(new[] { ProgramMenuItem.Separator, Check(storyOwnSettings, "This Story Only", ToggleStoryOwnSettings), ProgramMenuItem.Separator, aboutItem })
            .ToArray();
    }

    private void ChooseFont(TextFont chosen)
    {
        font = chosen;
        SavePreferences();
    }

    private void ChooseSpeed(int chosen)
    {
        speed = chosen;
        pacer?.SetSpeed(chosen);
        SaveChoice();
    }

    private void ChooseTextMode(int chosen)
    {
        textMode = chosen;
        pacer?.SetText(TextModes[chosen].Lines, TextModes[chosen].Baud);
        SaveChoice();
    }

    /// <summary>Speed and text go to the story's own settings when it has them, otherwise to everyone's.</summary>
    private void SaveChoice()
    {
        if (story != null && storyOwnSettings)
        {
            SaveStorySettings();
            BuildMenus();
            return;
        }
        globalSpeed = speed;
        globalTextMode = textMode;
        SavePreferences();
    }

    private void ToggleStoryOwnSettings()
    {
        if (story == null)
            return;
        storyOwnSettings = !storyOwnSettings;
        if (storyOwnSettings)
        {
            SaveStorySettings();
        }
        else
        {
            try
            {
                File.Delete(StorySettingsFile);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                log.Warn($"Couldn't remove {story.Name}'s settings: {e.Message}");
            }
            UseSpeedAndText(globalSpeed, globalTextMode);
        }
        BuildMenus();
    }

    private void UseSpeedAndText(int newSpeed, int newTextMode)
    {
        if (newSpeed != speed)
            pacer?.SetSpeed(newSpeed);
        speed = newSpeed;
        textMode = newTextMode;
        pacer?.SetText(TextModes[newTextMode].Lines, TextModes[newTextMode].Baud);
    }

    private string PreferencesFile => Path.Combine(dataRoot, "preferences.ini");
    private string StorySettingsFile => Path.Combine(story.DataFolder, "preferences.ini");

    private static string SpeedValue(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private int ReadSpeed(ModConfig config, int fallback)
    {
        int value = config.GetInt("Speed", fallback);
        return Speeds.Any(x => x.Speed == value) ? value : fallback;
    }

    private static int ReadTextMode(ModConfig config, int fallback)
    {
        string name = config.GetString("Text", null);
        if (name == null)
        {
            // Before baud rates, the text setting was Scrolling = 0, 30 or 10 (lines per second).
            int lines = config.GetInt("Scrolling", -1);
            int byLines = Array.FindIndex(TextModes, m => m.Baud == 0 && m.Lines == lines);
            return byLines >= 0 ? byLines : fallback;
        }
        // 300 baud was dropped; 1200 is the slowest left.
        if (string.Equals(name, "baud300", StringComparison.OrdinalIgnoreCase))
            name = "baud1200";
        int index = Array.FindIndex(TextModes, m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : fallback;
    }

    private void SavePreferences()
    {
        BuildMenus();
        TryWriteLines(PreferencesFile, "Font = " + Fonts.First(f => f.Font == font).Name,
            "Speed = " + SpeedValue(globalSpeed), "Text = " + TextModes[globalTextMode].Name, "Sound = " + (diskSounds ? "on" : "off"), "Beeps = " + (beeps ? "on" : "off"));
    }

    private void SaveStorySettings()
    {
        Directory.CreateDirectory(story.DataFolder);
        TryWriteLines(StorySettingsFile, "Speed = " + SpeedValue(speed), "Text = " + TextModes[textMode].Name);
    }

    private void TryWriteLines(string path, params string[] lines)
    {
        try
        {
            File.WriteAllLines(path, new[] { "# Chosen in Yomiel's menu." }.Concat(lines));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            log.Warn($"Couldn't save {path}: {e.Message}");
        }
    }

    private void LoadPreferences()
    {
        ModConfig saved = ModConfig.Load(PreferencesFile, log.Warn);
        // Before preferences.ini, the font and speed were kept in font.txt and speed.txt.
        string fontName = saved.GetString("Font", ReadText(Path.Combine(dataRoot, "font.txt")) ?? "regular");
        int fontIndex = Array.FindIndex(Fonts, f => string.Equals(f.Name, fontName, StringComparison.OrdinalIgnoreCase));
        font = fontIndex >= 0 ? Fonts[fontIndex].Font : TextFont.Regular;
        int oldSpeed = int.TryParse(ReadText(Path.Combine(dataRoot, "speed.txt")), out int s) ? s : 0;
        globalSpeed = ReadSpeed(saved, Speeds.Any(x => x.Speed == oldSpeed) ? oldSpeed : 0);
        globalTextMode = ReadTextMode(saved, 0);
        diskSounds = saved.GetBool("Sound", true);
        beeps = saved.GetBool("Beeps", true);
        speed = globalSpeed;
        textMode = globalTextMode;
        bool upgrade = !File.Exists(PreferencesFile) || !saved.Contains("Text") || !saved.Contains("Sound") || !saved.Contains("Beeps");
        if (upgrade)
        {
            SavePreferences();
            foreach (string old in new[] { "font.txt", "speed.txt" })
            {
                try { File.Delete(Path.Combine(dataRoot, old)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
        }
        BuildMenus();
    }

    /// <summary>A story with its own preferences.ini uses its speed and text; others use everyone's.</summary>
    private void LoadStorySettings()
    {
        storyOwnSettings = File.Exists(StorySettingsFile);
        if (storyOwnSettings)
        {
            ModConfig own = ModConfig.Load(StorySettingsFile, log.Warn);
            UseSpeedAndText(ReadSpeed(own, globalSpeed), ReadTextMode(own, globalTextMode));
        }
        else
        {
            UseSpeedAndText(globalSpeed, globalTextMode);
        }
        BuildMenus();
    }

    /// <summary>Keeps the story's place, as closing the window does, and shows the list.</summary>
    private void LeaveStory()
    {
        if (session == null)
            return;
        Suspend();
        ReturnToList();
    }

    private void OpenStoryFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.StoryFolder);
            Process.Start(new ProcessStartInfo { FileName = settings.StoryFolder, UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception)
        {
            log.Warn($"Couldn't open {settings.StoryFolder}: {e.Message}");
        }
    }

    public string Title => story == null ? "Yomiel" : "Yomiel - " + story.Name;
    public int Columns => settings.Columns;
    public int Rows => settings.Rows;

    // ---- Window lifecycle ----

    public void Opened(ProgramWindow window)
    {
        dataRoot = window.DataDirectory;
        LoadPreferences();
        window.Screen.Font = font;
        ShowList();
    }

    public void Closed(ProgramWindow window)
    {
        if (session != null)
            Suspend();
        StopStory();
    }

    private void ShowList()
    {
        stories = StoryFile.Find(settings.StoryFolder, dataRoot);
        string last = ReadText(Path.Combine(dataRoot, "last.txt"));
        selected = Math.Max(0, stories.FindIndex(s => string.Equals(Path.GetFileName(s.Path), last, StringComparison.OrdinalIgnoreCase)));
    }

    private void StartStory(StoryFile file, bool fresh)
    {
        listMessage = null;
        if (!file.Supported)
        {
            listMessage = $"{file.Name} is a version {file.Version} story. Yomiel plays versions 5, 7 and 8.";
            return;
        }
        var options = new ZMachineOptions { ScreenColumns = Columns, ScreenRows = Rows };
        ZScreen screen = null;
        var storage = new ZStoryFolder(file.DataFolder);
        ZMachine machine;
        try
        {
            byte[] bytes = File.ReadAllBytes(file.Path);
            ZSuspended suspended = fresh ? null : storage.ReadSuspended();
            if (suspended != null)
            {
                machine = ZMachine.Resume(bytes, suspended.State, options);
                // The window may have changed size since, in config.ini.
                screen = ZScreen.FromContents(suspended.Screen, settings.Scrollback);
                screen?.Resize(Columns, Rows);
            }
            else
            {
                machine = new ZMachine(bytes, options);
            }
            Directory.CreateDirectory(file.DataFolder);
            File.WriteAllText(Path.Combine(dataRoot, "last.txt"), Path.GetFileName(file.Path));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
        {
            listMessage = $"Can't open {file.Name}: {e.Message}";
            return;
        }

        screen ??= new ZScreen(Columns, Rows, settings.Scrollback);
        story = file;
        // The game's disk sounds for loading a program: short, medium or long.
        PlayDiskSound(file.Size < 128 * 1024 ? "desktop/load_s" : file.Size < 384 * 1024 ? "desktop/load_m" : "desktop/load_l");
        LoadStorySettings();
        pacer = new Pacer(speed, TextModes[textMode].Lines, TextModes[textMode].Baud, PlayDiskSound);
        session = new ZSession(machine, screen, new ZSessionOptions
        {
            Storage = storage,
            Pacer = pacer,
            MorePrompts = settings.MorePrompts,
            EndText = "[The story has ended. Press any key to return to the list.]",
            ErrorText = "[Yomiel stopped the story: {0}. Press any key to return to the list.]",
            Sound = Beep,
            Warning = message => log.Warn($"{file.Name}: {message}"),
            Error = message => log.Error($"{file.Name}: {message}"),
        });
        BuildMenus();
    }

    private void StopStory()
    {
        storyOwnSettings = false;
        speed = globalSpeed;
        textMode = globalTextMode;
        session?.Close();
        session = null;
        pacer = null;
        story = null;
        BuildMenus();
    }

    private void ReturnToList()
    {
        StopStory();
        ShowList();
    }

    /// <summary>Saves the story so the next session continues where this one stopped.</summary>
    private void Suspend()
    {
        try
        {
            session.Suspend();
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
        {
            log.Warn($"Couldn't keep {story.Name} for next time: {e.Message}");
        }
    }

    // ---- Every frame ----

    public void Update(ProgramWindow window, float seconds)
    {
        window.Screen.Font = font;
        if (about)
        {
            DrawAbout(window.Screen);
            return;
        }
        if (session == null)
        {
            DrawList(window.Screen);
            return;
        }
        session.Update(seconds);
        Draw(window.Screen);
    }

    // ---- Keys ----

    public void KeyPressed(ProgramWindow window, KeyPress key)
    {
        if (about)
        {
            about = false;
            return;
        }
        if (session == null)
        {
            ListKey(key);
            return;
        }
        // ProgramKey and ZPlayerKey list the same keys in the same order.
        session.KeyPressed(new ZKeyPress((ZPlayerKey)(int)key.Key, key.Char));
        if (session.Dismissed)
            ReturnToList();
    }

    // ---- Sounds ----

    // The story's beeps: high is the organizer's beep, low is Food Court's error buzz. Other sound
    // effects are Blorb resources, which Yomiel doesn't load.
    private void Beep(int number)
    {
        if (beeps && (number == 1 || number == 2))
            sounds.PlayGameSound(number == 1 ? "organizer/beep" : "milwaukee/error");
    }

    private void PlayDiskSound(string name)
    {
        if (diskSounds)
            sounds.PlayGameSound(name);
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Drawing ----

    private void Draw(TextScreen target)
    {
        ZCell[] cells = session.Screen.Render(session.Scroll);
        target.Background = DefaultBack;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                ZCell cell = cells[row * Columns + column];
                TextColor fore = Color(cell.Foreground, DefaultFore);
                TextColor back = Color(cell.Background, DefaultBack);
                if ((cell.Style & ZTextStyle.Reverse) != 0)
                    (fore, back) = (back, fore);
                if ((cell.Style & ZTextStyle.Bold) != 0)
                    fore = Brighten(fore);
                target.Put(column, row, cell.Char, fore, back);
            }
        }

        if (session.ShowingMore && !pacer.LinesBehind)
            target.Write(0, Rows - 1, "[MORE]", BarFore, BarBack);
        if (pacer.PleaseWait && session.Scroll == 0)
        {
            const string wait = " Please wait... ";
            target.Write(Columns - wait.Length, Rows - 1, wait, BarFore, BarBack);
        }
        if (session.Scroll > 0)
        {
            string note = $" {session.Scroll} lines back - Page Down returns ";
            target.Write(Columns - note.Length, Rows - 1, note, BarFore, BarBack);
        }

        target.CursorVisible = session.AwaitingPlayer && session.Screen.CursorRow >= 0;
        target.CursorColumn = Math.Max(0, session.Screen.CursorColumn);
        target.CursorRow = Math.Max(0, session.Screen.CursorRow);
    }

    private static TextColor Color(ZColor color, TextColor fallback) =>
        color.IsDefault ? fallback : TextColor.FromRgb(color.ToRgb24());

    private static TextColor Brighten(TextColor c) =>
        new((byte)(c.R + (255 - c.R) / 3), (byte)(c.G + (255 - c.G) / 3), (byte)(c.B + (255 - c.B) / 3));

    // ---- Story list ----

    private void ListKey(KeyPress key)
    {
        switch (key.Key)
        {
            case ProgramKey.Up when selected > 0:
                selected--;
                break;
            case ProgramKey.Down when selected < stories.Count - 1:
                selected++;
                break;
            case ProgramKey.Enter when stories.Count > 0:
                StartStory(stories[selected], fresh: false);
                break;
            case ProgramKey.F2 when stories.Count > 0:
                StartStory(stories[selected], fresh: true);
                break;
            case ProgramKey.F5:
                ShowList();
                listMessage = null;
                break;
        }
    }

    private void DrawList(TextScreen target)
    {
        target.Foreground = DefaultFore;
        target.Background = DefaultBack;
        target.Clear();
        target.CursorVisible = false;
        target.Write(0, 0, " YOMIEL   Z-machine interpreter".PadRight(Columns), BarFore, BarBack);

        int row = 2;
        if (stories.Count == 0)
        {
            target.Write(2, row++, "No story files found. Put .z5 or .z8 files in:");
            row++;
            foreach (string part in Chunks(settings.StoryFolder, Columns - 4))
                target.Write(2, row++, part, Dim, DefaultBack);
            row++;
            target.Write(2, row, "Then press F5.");
        }
        else
        {
            int visible = Rows - 6;
            int first = Math.Max(0, Math.Min(selected - visible / 2, stories.Count - visible));
            for (int i = first; i < Math.Min(stories.Count, first + visible); i++)
            {
                StoryFile s = stories[i];
                string state = !s.Supported ? "not supported" : s.CanContinue ? "continues" : "";
                string name = s.Name.Length > Columns - 34 ? s.Name.Substring(0, Columns - 34) : s.Name;
                string line = $" {name.PadRight(Columns - 34)} v{s.Version}  {s.Size / 1024,5} KB  {state,-13}";
                bool chosen = i == selected;
                TextColor fore = chosen ? BarFore : s.Supported ? DefaultFore : Dim;
                TextColor back = chosen ? BarBack : DefaultBack;
                target.Write(1, row++, line.PadRight(Columns - 2), fore, back);
            }
        }

        if (listMessage != null)
            target.Write(1, Rows - 3, listMessage.Length > Columns - 2 ? listMessage.Substring(0, Columns - 2) : listMessage, Warning, DefaultBack);
        string help = stories.Count > 0 ? " Enter play or continue   F2 start over   F5 refresh" : " F5 refresh";
        target.Write(0, Rows - 1, help.PadRight(Columns), BarFore, BarBack);
    }

    private void DrawAbout(TextScreen target)
    {
        target.Foreground = DefaultFore;
        target.Background = DefaultBack;
        target.Clear();
        target.CursorVisible = false;
        target.Write(0, 0, $" YOMIEL {settings.Version}".PadRight(Columns), BarFore, BarBack);
        int row = 2;
        void Line(string text = "", TextColor? color = null)
        {
            // Paths break anywhere; prose breaks between words.
            IEnumerable<string> parts = text.Length == 0 ? new[] { "" } : color.HasValue ? Chunks(text, Columns - 4) : Words(text, Columns - 4);
            foreach (string part in parts)
                target.Write(2, row++, part, color ?? DefaultFore, DefaultBack);
        }
        Line("Yomiel - Z-machine interpreter by onehalf");
        Line("Plays Infocom-format story files (.z5, .z7, .z8)");
        Line();
        Line("Story folder:");
        Line(settings.StoryFolder, Dim);
        Line();
        Line("Saves, transcripts and recordings:");
        Line(dataRoot ?? "", Dim);
        Line();
        Line("Keys in the story list:  Enter play or continue   F2 start over   F5 refresh");
        Line("Keys in a story:         Page Up/Down scroll back   Up/Down earlier commands");
        Line();
        Line("Sessions are saved automatically. More settings in config.ini.");
        target.Write(0, Rows - 1, " Press any key to return.".PadRight(Columns), BarFore, BarBack);
    }

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
}
