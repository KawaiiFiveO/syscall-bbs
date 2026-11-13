using System;
using Yomiel;

namespace LastCallBBS.Mods.YomielPlayer;

/// <summary>
/// The period features: an emulated machine speed, text revealed a line or a character at a time,
/// and the disk taking a moment to save and restore.
/// </summary>
internal sealed class Pacer : ZPacer
{
    private const double DiskSeconds = 1.2;
    private const double PleaseWaitSeconds = 1;

    private readonly Action<string> playDiskSound;
    private ZSession session;
    private double emulated;
    private double revealCredit;
    private double stalled;
    private Action diskAction;
    private double diskTimer;

    /// <param name="playDiskSound">Plays a game sound, or does nothing when disk sounds are off.</param>
    public Pacer(int speed, int lines, int baud, Action<string> playDiskSound)
    {
        Speed = speed;
        Lines = lines;
        Baud = baud;
        this.playDiskSound = playDiskSound;
    }

    /// <summary>Z-machine instructions per second, or 0 for full speed.</summary>
    public int Speed { get; private set; }

    /// <summary>Lines per second, or 0.</summary>
    public int Lines { get; private set; }

    /// <summary>Bits per second, 10 to a character, or 0.</summary>
    public int Baud { get; private set; }

    private bool TextPaced => Lines > 0 || Baud > 0;

    /// <summary>The story has been busy for a while: show "Please wait...".</summary>
    public bool PleaseWait => stalled >= PleaseWaitSeconds;

    /// <summary>Lines are written but not all shown yet.</summary>
    public bool LinesBehind => session != null && TextPaced && session.Screen.ShownLines < session.Screen.LowerLineCount;

    /// <summary>Some written text isn't shown yet, including the line being written.</summary>
    private bool TextBehind => TextPaced && session.Screen.ShownLines <= session.Screen.LowerLineCount;

    /// <summary>The emulated machine hasn't reached the real one yet.</summary>
    private bool Behind => Speed > 0 && emulated < session.Machine.InstructionCount;

    /// <summary>Changes the speed, emulating from now rather than from the start of the story.</summary>
    public void SetSpeed(int speed)
    {
        Speed = speed;
        emulated = session?.Machine.InstructionCount ?? 0;
    }

    public void SetText(int lines, int baud)
    {
        Lines = lines;
        Baud = baud;
        revealCredit = 0;
    }

    public override void Start(ZSession session)
    {
        this.session = session;
        emulated = 0;
        stalled = 0;
        diskAction = null;
        revealCredit = 0;
        // Reveal from what's on the screen now, so the story's first text is paced too.
        session.Screen.ShownLines = TextPaced ? session.Screen.LowerLineCount + 1 : long.MaxValue;
        session.Screen.ShownColumns = 0;
    }

    public override void BeforeReveal(ZSession session, double seconds)
    {
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
        emulated = Math.Min(emulated + seconds * Speed, session.Machine.InstructionCount);
    }

    // At an emulated speed, output appears once the emulated machine has reached it.
    public override bool MayReveal(ZSession session, ZOutput item) => Speed == 0 || item.InstructionCount <= emulated;

    public override void AfterReveal(ZSession session, double seconds, bool revealed)
    {
        revealed |= RevealText(seconds);
        stalled = Speed > 0 && !session.ShowingMore && !revealed && (Behind || diskAction != null) ? stalled + seconds : 0;
    }

    public override bool HoldInput(ZSession session) => diskAction != null || Behind || TextBehind;

    // [MORE] shows once its page has been revealed.
    public override bool HoldMore(ZSession session) => LinesBehind;

    /// <summary>The disk sound, and at an emulated speed, the disk takes a moment.</summary>
    public override void DiskOperation(ZSession session, bool skip, Action complete)
    {
        if (!skip)
            playDiskSound("desktop/load_s");
        if (Speed == 0 || skip)
        {
            complete();
            return;
        }
        diskAction = complete;
        diskTimer = DiskSeconds;
    }

    /// <summary>
    /// Paced text: output is written to the screen at once, and the screen shows it a line or a
    /// character at a time, so each line is already wrapped. The line being written is revealed
    /// only up to its last space until no more output is waiting, because a word still being
    /// written could wrap.
    /// </summary>
    private bool RevealText(double seconds)
    {
        ZScreen screen = session.Screen;
        if (!TextPaced)
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
        bool drained = !session.OutputPending;
        if (Lines > 0)
        {
            screen.ShownColumns = 0;
            revealCredit = Math.Min(revealCredit + seconds * Lines, 2);
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
            double perSecond = Baud / 10.0;
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
}
