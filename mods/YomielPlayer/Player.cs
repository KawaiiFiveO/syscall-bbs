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
/// The program: a story list, then the story. The machine runs at full speed and queues its
/// output; the display reveals it, stopping at [MORE], and takes input once it has caught up.
/// </summary>
internal sealed class Player : IDesktopProgram, IProgramMenu
{
    private const int RunMilliseconds = 8;
    private const int MaxTypeAhead = 64;

    private static readonly TextColor DefaultFore = TextColor.FromRgb(0xD8D8D8);
    private static readonly TextColor DefaultBack = TextColor.FromRgb(0x101418);
    private static readonly TextColor BarFore = TextColor.FromRgb(0x101418);
    private static readonly TextColor BarBack = TextColor.FromRgb(0x9CC8A8);
    private static readonly TextColor Dim = TextColor.FromRgb(0x707880);
    private static readonly TextColor Warning = TextColor.FromRgb(0xE08070);

    private enum FilePrompt { None, Save, Restore }

    private readonly Settings settings;
    private readonly IModLog log;
    private string dataRoot;

    // Story list
    private List<StoryFile> stories = new();
    private int selected;
    private string listMessage;

    // Playing
    private StoryFile story;
    private ZMachine machine;
    private ZScreen screen;
    private readonly Queue<ZOutput> display = new();
    private ZOutput textItem;
    private int textOffset;
    private bool more;
    private long moreMark;
    private bool ended;
    private int scroll;

    // Input
    private ZLineRequest activeLine;
    private readonly StringBuilder edit = new();
    private int cursor;
    private string takenBack;
    private readonly List<string> history = new();
    private int historyIndex;
    private float timer;
    private readonly Queue<KeyPress> typeAhead = new();
    private FilePrompt filePrompt;
    private string lastSaveName = "save";

    // Streams
    private TextWriter transcript;
    private TextWriter record;
    private readonly Queue<string> replay = new();

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
        ("baud300", "300 baud", 0, 300),
    };

    private const float DiskSeconds = 1.2f;
    private const float PleaseWaitSeconds = 1f;

    private ProgramMenuItem[] listMenu;
    private ProgramMenuItem[] storyMenu;
    private TextFont font = TextFont.Regular;
    // The speed and text mode in use, and the ones in preferences.ini that stories without their own settings use.
    private int speed;
    private int textMode;
    private int globalSpeed;
    private int globalTextMode;
    private bool storyOwnSettings;
    private double revealCredit;
    private double emulated;
    private float stalled;
    private Action diskAction;
    private float diskTimer;
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

    public IReadOnlyList<ProgramMenuItem> MenuItems => machine == null ? listMenu : storyMenu;

    // The panel shows labels up to about 20 characters and has no submenus, so each group gets a
    // heading. Headings are ordinary items that do nothing: the game draws disabled items dim, in
    // its menu shadow colour at half opacity. Rebuilt only when a choice changes, since the desktop
    // reads the menu every frame.
    private void BuildMenus()
    {
        static ProgramMenuItem Heading(string label) => new("── " + label + " ──", () => { });
        static ProgramMenuItem Check(bool on, string label, Action action) => new((on ? "[X] " : "[  ] ") + label, action);
        var choices = new List<ProgramMenuItem> { ProgramMenuItem.Separator, Heading("FONT") };
        choices.AddRange(Fonts.Select(f => Check(f.Font == font, f.Label, () => ChooseFont(f.Font))));
        choices.Add(ProgramMenuItem.Separator);
        choices.Add(Heading("SPEED"));
        choices.AddRange(Speeds.Select(s => Check(s.Speed == speed, s.Label, () => ChooseSpeed(s.Speed))));
        choices.Add(ProgramMenuItem.Separator);
        choices.Add(Heading("TEXT"));
        choices.AddRange(TextModes.Select((m, i) => Check(i == textMode, m.Label, () => ChooseTextMode(i))));
        choices.Add(ProgramMenuItem.Separator);
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

    private bool TextPaced => TextModes[textMode].Lines > 0 || TextModes[textMode].Baud > 0;

    private void ChooseFont(TextFont chosen)
    {
        font = chosen;
        SavePreferences();
    }

    private void ChooseSpeed(int chosen)
    {
        speed = chosen;
        // Start emulating from now, not from the start of the story.
        emulated = machine?.InstructionCount ?? 0;
        SaveChoice();
    }

    private void ChooseTextMode(int chosen)
    {
        textMode = chosen;
        revealCredit = 0;
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
            emulated = machine?.InstructionCount ?? 0;
        speed = newSpeed;
        textMode = newTextMode;
        revealCredit = 0;
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
        if (machine == null)
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

    private int LowerRows => Rows - screen.UpperHeight;

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
        if (machine != null)
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
        var newScreen = new ZScreen(Columns, Rows, settings.Scrollback);
        ZMachine newMachine;
        try
        {
            byte[] bytes = File.ReadAllBytes(file.Path);
            if (!fresh && file.CanContinue)
            {
                newMachine = ZMachine.Resume(bytes, File.ReadAllBytes(file.SuspendedState), options);
                if (File.Exists(file.SuspendedScreen))
                    newScreen.LoadContents(File.ReadAllBytes(file.SuspendedScreen));
            }
            else
            {
                newMachine = new ZMachine(bytes, options);
            }
            Directory.CreateDirectory(file.DataFolder);
            File.WriteAllText(Path.Combine(dataRoot, "last.txt"), Path.GetFileName(file.Path));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
        {
            listMessage = $"Can't open {file.Name}: {e.Message}";
            return;
        }

        story = file;
        // The game's disk sounds for loading a program: short, medium or long.
        PlayDiskSound(file.Size < 128 * 1024 ? "desktop/load_s" : file.Size < 384 * 1024 ? "desktop/load_m" : "desktop/load_l");
        machine = newMachine;
        screen = newScreen;
        display.Clear();
        textItem = null;
        more = false;
        moreMark = screen.LowerLineCount;
        ended = false;
        scroll = 0;
        activeLine = null;
        filePrompt = FilePrompt.None;
        typeAhead.Clear();
        history.Clear();
        replay.Clear();
        lastSaveName = "save";
        LoadStorySettings();
        emulated = 0;
        stalled = 0;
        diskAction = null;
        revealCredit = 0;
        // Reveal from what's on the screen now, so the story's first text is paced too.
        screen.ShownLines = TextPaced ? screen.LowerLineCount + 1 : long.MaxValue;
        screen.ShownColumns = 0;
    }

    private void StopStory()
    {
        storyOwnSettings = false;
        speed = globalSpeed;
        textMode = globalTextMode;
        BuildMenus();
        transcript?.Dispose();
        transcript = null;
        record?.Dispose();
        record = null;
        story = null;
        machine = null;
        screen = null;
        display.Clear();
        textItem = null;
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
            // Finish anything the story is waiting on that a closed window can't answer.
            if (machine.Status == ZStatus.WaitingForSave)
                machine.CompleteSave(false);
            else if (machine.Status == ZStatus.WaitingForRestore)
                machine.CompleteRestore(null);
            for (int i = 0; i < 100 && machine.Status == ZStatus.Running; i++)
                machine.Run();
            while (machine.TryGetOutput(out ZOutput item))
                Collect(item);
            if (textItem != null)
                screen.Write(textItem.Text.Substring(textOffset));
            while (display.Count > 0)
                screen.Apply(display.Dequeue());
            screen.Input = null;
            if (takenBack != null && activeLine != null)
                screen.Write(takenBack);

            if (machine.Status is ZStatus.WaitingForLine or ZStatus.WaitingForKey or ZStatus.Running)
            {
                File.WriteAllBytes(story.SuspendedState, machine.SaveState());
                File.WriteAllBytes(story.SuspendedScreen, screen.SaveContents());
            }
            else
            {
                DeleteSuspended();
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
        {
            log.Warn($"Couldn't keep {story.Name} for next time: {e.Message}");
        }
    }

    private void DeleteSuspended()
    {
        try
        {
            File.Delete(story.SuspendedState);
            File.Delete(story.SuspendedScreen);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
        if (machine == null)
        {
            DrawList(window.Screen);
            return;
        }

        if (machine.Status == ZStatus.Running)
        {
            Stopwatch clock = Stopwatch.StartNew();
            do
                machine.Run(20_000);
            while (machine.Status == ZStatus.Running && clock.ElapsedMilliseconds < RunMilliseconds);
        }
        while (machine.TryGetOutput(out ZOutput item))
            Collect(item);

        if (diskAction != null)
        {
            diskTimer -= seconds;
            if (diskTimer <= 0)
            {
                Action action = diskAction;
                diskAction = null;
                action();
            }
        }

        // The emulated machine can't get ahead of the real one, and gains nothing while it waits.
        emulated = Math.Min(emulated + seconds * (double)speed, machine.InstructionCount);
        bool revealed = Reveal() | RevealText(seconds);
        stalled = speed > 0 && !more && !revealed && (Behind || diskAction != null) ? stalled + seconds : 0;
        if (InputReady)
            Answer(seconds);
        Draw(window.Screen);
    }

    /// <summary>
    /// Paced text: output is written to the screen at once, and the screen shows it a line or a
    /// character at a time, so each line is already wrapped. The line being written is revealed
    /// only up to its last space until no more output is waiting, because a word still being
    /// written could wrap.
    /// </summary>
    private bool RevealText(float seconds)
    {
        (string _, string _, int lines, int baud) = TextModes[textMode];
        if (lines == 0 && baud == 0)
        {
            screen.ShownLines = long.MaxValue;
            screen.ShownColumns = 0;
            return false;
        }
        long beforeLines = screen.ShownLines;
        int beforeColumns = screen.ShownColumns;
        if (screen.ShownLines > screen.LowerLineCount + 1)
        {
            screen.ShownLines = screen.LowerLineCount + 1;
            screen.ShownColumns = 0;
        }
        bool drained = textItem == null && display.Count == 0;
        if (lines > 0)
        {
            screen.ShownColumns = 0;
            revealCredit = Math.Min(revealCredit + seconds * lines, 2);
            while (screen.ShownLines < screen.LowerLineCount && revealCredit >= 1)
            {
                screen.ShownLines++;
                revealCredit--;
            }
            if (screen.ShownLines == screen.LowerLineCount && drained)
                screen.ShownLines++;
        }
        else
        {
            // A character on the line is 10 bits: a start bit, 8 data bits and a stop bit.
            double perSecond = baud / 10.0;
            revealCredit = Math.Min(revealCredit + seconds * perSecond, Math.Max(2, perSecond / 20));
            while (revealCredit >= 1 && screen.ShownLines <= screen.LowerLineCount)
            {
                bool last = screen.ShownLines == screen.LowerLineCount;
                if (screen.ShownColumns < screen.LineLength(screen.ShownLines, wholeWords: last && !drained))
                {
                    screen.ShownColumns++;
                    revealCredit--;
                }
                else if (!last)
                {
                    // The line break.
                    screen.ShownLines++;
                    screen.ShownColumns = 0;
                    revealCredit--;
                }
                else
                {
                    if (drained)
                    {
                        screen.ShownLines++;
                        screen.ShownColumns = 0;
                    }
                    break;
                }
            }
        }
        return screen.ShownLines != beforeLines || screen.ShownColumns != beforeColumns;
    }

    /// <summary>Lines are written but not all shown yet.</summary>
    private bool LinesBehind => TextPaced && screen.ShownLines < screen.LowerLineCount;

    /// <summary>Some written text isn't shown yet, including the line being written.</summary>
    private bool TextBehind => TextPaced && screen.ShownLines <= screen.LowerLineCount;

    /// <summary>The emulated machine hasn't reached the real one yet.</summary>
    private bool Behind => speed > 0 && emulated < machine.InstructionCount;

    /// <summary>Handles file output at once and queues screen output for display.</summary>
    private void Collect(ZOutput item)
    {
        switch (item.Kind)
        {
            case ZOutputKind.Transcript:
                transcript?.Write(item.Text.Replace("\n", Environment.NewLine));
                break;
            case ZOutputKind.CommandRecord:
                record?.WriteLine(item.Text);
                break;
            case ZOutputKind.OutputStream:
                OpenOrCloseStream(item.Value);
                break;
            case ZOutputKind.InputStream:
                replay.Clear();
                if (item.Value == 1)
                {
                    string commands = Path.Combine(story.DataFolder, "commands.txt");
                    if (File.Exists(commands))
                        foreach (string line in File.ReadAllLines(commands))
                            replay.Enqueue(line);
                }
                break;
            case ZOutputKind.Warning:
                log.Warn($"{story.Name}: {item.Text}");
                break;
            default:
                display.Enqueue(item);
                break;
        }
    }

    private void OpenOrCloseStream(int stream)
    {
        try
        {
            switch (stream)
            {
                case 2:
                    transcript ??= new StreamWriter(Path.Combine(story.DataFolder, "transcript.txt"), true);
                    break;
                case -2:
                    transcript?.Dispose();
                    transcript = null;
                    break;
                case 4:
                    record ??= new StreamWriter(Path.Combine(story.DataFolder, "commands.txt"), true);
                    break;
                case -4:
                    record?.Dispose();
                    record = null;
                    break;
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            log.Warn($"Couldn't open a file for output stream {stream}: {e.Message}");
        }
    }

    /// <summary>Moves queued output onto the screen, stopping when a screenful has arrived.</summary>
    /// <summary>Returns whether anything was shown.</summary>
    private bool Reveal()
    {
        bool shown = false;
        while (!more)
        {
            if (textItem != null)
            {
                bool echo = textItem.Kind == ZOutputKind.InputEcho;
                string text = textItem.Text;
                while (textOffset < text.Length)
                {
                    if (!echo && settings.MorePrompts && screen.Window == 0 && screen.LowerLineCount - moreMark >= LowerRows - 1)
                    {
                        more = true;
                        return shown;
                    }
                    screen.Write(text[textOffset++]);
                    shown = true;
                }
                if (echo)
                    moreMark = screen.LowerLineCount;
                textItem = null;
            }
            // At an emulated speed, output appears once the emulated machine has reached it.
            if (display.Count == 0 || speed > 0 && display.Peek().InstructionCount > emulated)
                return shown;
            ZOutput item = display.Dequeue();
            shown = true;
            if (item.Kind is ZOutputKind.Text or ZOutputKind.InputEcho)
            {
                textItem = item;
                textOffset = 0;
                continue;
            }
            if (item.Kind == ZOutputKind.Sound)
                Beep(item.Value);
            screen.Apply(item);
            if (item.Kind == ZOutputKind.EraseWindow && item.Value != 1 || item.Kind == ZOutputKind.Restarted)
                moreMark = screen.LowerLineCount;
        }
        return shown;
    }

    private bool InputReady => machine != null && !more && textItem == null && display.Count == 0 && diskAction == null && !Behind && !TextBehind;

    /// <summary>The display has caught up: start or continue whatever the machine is waiting for.</summary>
    private void Answer(float seconds)
    {
        switch (machine.Status)
        {
            case ZStatus.WaitingForLine:
                if (activeLine != machine.LineRequest)
                    BeginLine();
                if (replay.Count > 0)
                {
                    edit.Clear().Append(replay.Dequeue());
                    SubmitLine(ZKey.Enter);
                    return;
                }
                Tick(seconds, machine.LineRequest.TimeoutTenths);
                break;
            case ZStatus.WaitingForKey:
                Tick(seconds, machine.KeyRequest.TimeoutTenths);
                break;
            case ZStatus.WaitingForSave:
                if (filePrompt == FilePrompt.None)
                    BeginSave();
                break;
            case ZStatus.WaitingForRestore:
                if (filePrompt == FilePrompt.None)
                    BeginRestore();
                break;
            case ZStatus.Halted:
                End("[The story has ended. Press any key to return to the list.]");
                return;
            case ZStatus.Error:
                if (!ended)
                    log.Error($"{story.Name}: {machine.ErrorMessage}");
                End($"[Yomiel stopped the story: {machine.ErrorMessage}. Press any key to return to the list.]");
                return;
            default:
                return;
        }
        while (typeAhead.Count > 0 && InputReady && IsWaitingForPlayer)
            HandleKey(typeAhead.Dequeue());
    }

    private bool IsWaitingForPlayer =>
        machine.Status == ZStatus.WaitingForKey || machine.Status == ZStatus.WaitingForLine && activeLine == machine.LineRequest || filePrompt != FilePrompt.None;

    private void Tick(float seconds, int tenths)
    {
        if (tenths <= 0)
            return;
        timer += seconds;
        if (timer < tenths / 10f)
            return;
        timer = 0;
        machine.Interrupt(machine.Status == ZStatus.WaitingForLine ? edit.ToString() : null);
    }

    private void End(string message)
    {
        if (ended)
            return;
        ended = true;
        DeleteSuspended();
        screen.Window = 0;
        screen.Input = null;
        screen.Write("\n\n" + message);
    }

    // ---- Line input ----

    private void BeginLine()
    {
        activeLine = machine.LineRequest;
        string initial = activeLine.InitialText;
        takenBack = initial.Length > 0 && screen.TakeBack(initial) ? initial : null;
        edit.Clear().Append(initial);
        cursor = edit.Length;
        historyIndex = history.Count;
        timer = 0;
        screen.Input = edit.ToString();
        screen.InputCursor = cursor;
    }

    private void SubmitLine(ZKey terminator)
    {
        string line = edit.ToString();
        screen.Input = null;
        // The story printed the pre-loaded text; put it back where it was before the echo follows it.
        if (takenBack != null && line.StartsWith(takenBack, StringComparison.Ordinal))
            screen.Write(takenBack);
        takenBack = null;
        if (terminator == ZKey.Enter && line.Trim().Length > 0 && (history.Count == 0 || history[history.Count - 1] != line))
            history.Add(line);
        timer = 0;
        machine.SubmitLine(line, terminator);
    }

    private int MaxLength => filePrompt != FilePrompt.None ? 60 : activeLine?.MaxLength ?? 0;

    /// <summary>Edits the line being typed. Returns false for keys that don't edit.</summary>
    private bool EditLine(KeyPress key)
    {
        switch (key.Key)
        {
            case ProgramKey.Character when key.Char >= ' ':
                if (edit.Length < MaxLength)
                    edit.Insert(cursor++, key.Char);
                break;
            case ProgramKey.Backspace when cursor > 0:
                edit.Remove(--cursor, 1);
                break;
            case ProgramKey.Delete when cursor < edit.Length:
                edit.Remove(cursor, 1);
                break;
            case ProgramKey.Left when cursor > 0:
                cursor--;
                break;
            case ProgramKey.Right when cursor < edit.Length:
                cursor++;
                break;
            case ProgramKey.Home:
                cursor = 0;
                break;
            case ProgramKey.End:
                cursor = edit.Length;
                break;
            case ProgramKey.Up when filePrompt == FilePrompt.None && historyIndex > 0:
                edit.Clear().Append(history[--historyIndex]);
                cursor = edit.Length;
                break;
            case ProgramKey.Down when filePrompt == FilePrompt.None && historyIndex < history.Count:
                historyIndex++;
                edit.Clear().Append(historyIndex < history.Count ? history[historyIndex] : "");
                cursor = edit.Length;
                break;
            default:
                return false;
        }
        screen.Input = edit.ToString();
        screen.InputCursor = cursor;
        return true;
    }

    // ---- Keys ----

    public void KeyPressed(ProgramWindow window, KeyPress key)
    {
        if (about)
        {
            about = false;
            return;
        }
        if (machine == null)
        {
            ListKey(key);
            return;
        }
        if (key.Key == ProgramKey.PageUp || key.Key == ProgramKey.PageDown)
        {
            int page = Math.Max(1, LowerRows - 1);
            scroll = Math.Max(0, Math.Min(screen.ScrollbackAvailable, scroll + (key.Key == ProgramKey.PageUp ? page : -page)));
            return;
        }
        scroll = 0;
        if (ended)
        {
            ReturnToList();
            return;
        }
        if (more)
        {
            // [MORE] shows once its page has been revealed.
            if (!LinesBehind)
            {
                more = false;
                moreMark = screen.LowerLineCount;
            }
            return;
        }
        if (!InputReady || !IsWaitingForPlayer)
        {
            if (typeAhead.Count < MaxTypeAhead)
                typeAhead.Enqueue(key);
            return;
        }
        HandleKey(key);
    }

    private void HandleKey(KeyPress key)
    {
        if (filePrompt != FilePrompt.None)
        {
            if (key.Key == ProgramKey.Enter)
                FinishFilePrompt(cancelled: false);
            else if (key.Key == ProgramKey.Escape)
                FinishFilePrompt(cancelled: true);
            else
                EditLine(key);
            return;
        }

        if (machine.Status == ZStatus.WaitingForKey)
        {
            moreMark = screen.LowerLineCount;
            timer = 0;
            if (key.Key == ProgramKey.Character)
                machine.SubmitKey(key.Char);
            else if (ToZKey(key.Key) is ZKey z)
                machine.SubmitKey(z);
            return;
        }

        if (machine.Status != ZStatus.WaitingForLine)
            return;
        ZKey? terminator = ToZKey(key.Key);
        if (key.Key == ProgramKey.Enter)
        {
            SubmitLine(ZKey.Enter);
        }
        else if (terminator is ZKey t && t != ZKey.Delete && t != ZKey.Escape && activeLine.IsTerminator(t))
        {
            SubmitLine(t);
        }
        else if (key.Key == ProgramKey.Escape)
        {
            edit.Clear();
            cursor = 0;
            screen.Input = "";
            screen.InputCursor = 0;
        }
        else
        {
            EditLine(key);
        }
    }

    private static ZKey? ToZKey(ProgramKey key) => key switch
    {
        ProgramKey.Enter => ZKey.Enter,
        ProgramKey.Backspace => ZKey.Delete,
        ProgramKey.Delete => ZKey.Delete,
        ProgramKey.Escape => ZKey.Escape,
        ProgramKey.Up => ZKey.Up,
        ProgramKey.Down => ZKey.Down,
        ProgramKey.Left => ZKey.Left,
        ProgramKey.Right => ZKey.Right,
        >= ProgramKey.F1 and <= ProgramKey.F12 => ZKey.F1 + (key - ProgramKey.F1),
        _ => null,
    };

    // ---- Saving and restoring ----

    private string SaveFile(string name) => Path.Combine(story.DataFolder, StoryFile.SafeName(name) + ".qzl");

    private List<string> SaveNames() => Directory.Exists(story.DataFolder)
        ? Directory.GetFiles(story.DataFolder, "*.qzl")
            .Where(f => !string.Equals(f, story.SuspendedState, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(Path.GetFileNameWithoutExtension).ToList()
        : new List<string>();

    private void BeginSave()
    {
        ZSaveRequest request = machine.SaveRequest;
        if (request.IsAuxiliary)
        {
            machine.CompleteSave(TryWrite(Path.Combine(story.DataFolder, "data", StoryFile.SafeName(request.SuggestedName ?? "data")), request.Data));
            return;
        }
        StartFilePrompt(FilePrompt.Save, $"\nSave as [{lastSaveName}]: ");
    }

    private void BeginRestore()
    {
        ZRestoreRequest request = machine.RestoreRequest;
        if (request.IsAuxiliary)
        {
            string path = Path.Combine(story.DataFolder, "data", StoryFile.SafeName(request.SuggestedName ?? "data"));
            machine.CompleteRestore(TryRead(path));
            return;
        }
        List<string> saves = SaveNames();
        if (saves.Count == 0)
        {
            screen.Write("\n[There are no saved games.]\n");
            machine.CompleteRestore(null);
            return;
        }
        if (!saves.Contains(lastSaveName, StringComparer.OrdinalIgnoreCase))
            lastSaveName = saves[0];
        StartFilePrompt(FilePrompt.Restore, $"\nSaved games: {string.Join(", ", saves)}\nRestore which [{lastSaveName}]: ");
    }

    private void StartFilePrompt(FilePrompt kind, string prompt)
    {
        filePrompt = kind;
        screen.Write(prompt);
        edit.Clear();
        cursor = 0;
        screen.Input = "";
        screen.InputCursor = 0;
    }

    private void FinishFilePrompt(bool cancelled)
    {
        FilePrompt kind = filePrompt;
        filePrompt = FilePrompt.None;
        string name = edit.ToString().Trim();
        screen.Input = null;
        screen.Write(cancelled ? "\n" : name + "\n");
        moreMark = screen.LowerLineCount;
        if (name.Length == 0)
            name = lastSaveName;
        name = StoryFile.SafeName(name);

        if (kind == FilePrompt.Save)
        {
            bool saved = !cancelled && TryWrite(SaveFile(name), machine.SaveRequest.Data);
            if (saved)
                lastSaveName = name;
            AfterDisk(cancelled, () => machine.CompleteSave(saved));
            return;
        }

        byte[] data = cancelled ? null : TryRead(SaveFile(name));
        if (!cancelled && data == null)
            screen.Write($"[There is no saved game called {name}.]\n");
        if (data != null)
            lastSaveName = name;
        AfterDisk(cancelled || data == null, () =>
        {
            if (!machine.CompleteRestore(data) && data != null)
                screen.Write($"[{machine.RestoreError}.]\n");
        });
    }

    /// <summary>At an emulated speed, the disk takes a moment.</summary>
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

    private void AfterDisk(bool skip, Action action)
    {
        if (!skip)
            PlayDiskSound("desktop/load_s");
        if (speed == 0 || skip)
        {
            action();
            return;
        }
        diskAction = action;
        diskTimer = DiskSeconds;
    }

    private bool TryWrite(string path, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
            return true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            screen.Write($"[Couldn't save: {e.Message}]\n");
            return false;
        }
    }

    private static byte[] TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return null;
        }
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
        ZCell[] cells = screen.Render(scroll);
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

        if (more && !LinesBehind)
            target.Write(0, Rows - 1, "[MORE]", BarFore, BarBack);
        if (stalled >= PleaseWaitSeconds && scroll == 0)
        {
            const string wait = " Please wait... ";
            target.Write(Columns - wait.Length, Rows - 1, wait, BarFore, BarBack);
        }
        if (scroll > 0)
        {
            string note = $" {scroll} lines back - Page Down returns ";
            target.Write(Columns - note.Length, Rows - 1, note, BarFore, BarBack);
        }

        bool typing = InputReady && scroll == 0 && !ended && (filePrompt != FilePrompt.None
            || machine.Status == ZStatus.WaitingForLine || machine.Status == ZStatus.WaitingForKey);
        target.CursorVisible = typing && screen.CursorRow >= 0;
        target.CursorColumn = Math.Max(0, screen.CursorColumn);
        target.CursorRow = Math.Max(0, screen.CursorRow);
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
        Line("A Z-machine interpreter for Last Call BBS.");
        Line("Plays Infocom-format story files (.z5, .z7, .z8). Story files are not included.");
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
        Line("Closing the window keeps the story's place. Settings are in the mod's config.ini.");
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
