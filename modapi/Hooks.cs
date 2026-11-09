using System;
using System.Collections.Generic;

namespace LastCallBBS.ModAPI;

/// <summary>Implemented by the loader. Mods use the typed hooks on <see cref="ModHooks"/> instead.</summary>
public interface IHookSource
{
    void Subscribe<T>(string hook, Action<T> handler);
}

/// <summary>
/// An event in the game. Handlers run on the game's main thread. Subscribing throws
/// <see cref="ShapeMismatchException"/> if a game update has broken this hook.
/// A handler that keeps throwing is removed.
/// </summary>
public sealed class Hook
{
    private readonly IHookSource _source;
    private readonly string _name;

    public Hook(IHookSource source, string name)
    {
        _source = source;
        _name = name;
    }

    public void Subscribe(Action handler) => _source.Subscribe<object>(_name, _ => handler());
}

/// <summary>An event in the game that carries data. See <see cref="Hook"/>.</summary>
public sealed class Hook<T>
{
    private readonly IHookSource _source;
    private readonly string _name;

    public Hook(IHookSource source, string name)
    {
        _source = source;
        _name = name;
    }

    public void Subscribe(Action<T> handler) => _source.Subscribe(_name, handler);
}

/// <summary>Every hook, grouped by the part of the game it belongs to.</summary>
public sealed class ModHooks
{
    public ModHooks(IHookSource source)
    {
        Game = new GameHooks(source);
        Terminal = new TerminalHooks(source);
        Desktop = new DesktopHooks(source);
        Story = new StoryHooks(source);
        FoodCourt = new FoodCourtHooks(source);
    }

    public GameHooks Game { get; }
    public TerminalHooks Terminal { get; }
    public DesktopHooks Desktop { get; }
    public StoryHooks Story { get; }
    public FoodCourtHooks FoodCourt { get; }
}

public sealed class GameHooks
{
    public GameHooks(IHookSource source) => Update = new Hook(source, "Game.Update");

    /// <summary>Runs once per frame, after the active screen has updated.</summary>
    public Hook Update { get; }
}

public sealed class TerminalHooks
{
    public TerminalHooks(IHookSource source)
    {
        DownloadStarted = new Hook<string>(source, "Terminal.DownloadStarted");
        DownloadCompleted = new Hook<string>(source, "Terminal.DownloadCompleted");
        Connected = new Hook(source, "Terminal.Connected");
    }

    /// <summary>The player started a download. Carries the program's internal name, such as "Milwaukee".</summary>
    public Hook<string> DownloadStarted { get; }

    /// <summary>A download finished. Carries the program's internal name.</summary>
    public Hook<string> DownloadCompleted { get; }

    /// <summary>The dial-in to Last Call BBS finished.</summary>
    public Hook Connected { get; }
}

public sealed class DesktopHooks
{
    public DesktopHooks(IHookSource source) => WindowOpened = new Hook<string>(source, "Desktop.WindowOpened");

    /// <summary>A desktop window opened. Carries its type name, such as "Terminal.MainWindow".</summary>
    public Hook<string> WindowOpened { get; }
}

public sealed class StoryHooks
{
    public StoryHooks(IHookSource source) => EntryUnlocked = new Hook<StoryEntry>(source, "Story.EntryUnlocked");

    /// <summary>A story entry became available. Entries unlocked before this session don't fire.</summary>
    public Hook<StoryEntry> EntryUnlocked { get; }
}

public sealed class StoryEntry
{
    public StoryEntry(int index, string title)
    {
        Index = index;
        Title = title;
    }

    public int Index { get; }
    public string Title { get; }
}

/// <summary>Read-only events from 20th Century Food Court.</summary>
public sealed class FoodCourtHooks
{
    public FoodCourtHooks(IHookSource source)
    {
        PuzzleOpened = new Hook<string>(source, "FoodCourt.PuzzleOpened");
        TickCompleted = new Hook<FoodCourtTick>(source, "FoodCourt.TickCompleted");
    }

    /// <summary>A puzzle screen opened. Carries the level's internal name, such as "WineBar".</summary>
    public Hook<string> PuzzleOpened { get; }

    /// <summary>The simulation advanced one tick. Tick numbers restart for each order.</summary>
    public Hook<FoodCourtTick> TickCompleted { get; }
}

/// <summary>A copy of the simulation's state. Changing it has no effect on the game.</summary>
public sealed class FoodCourtTick
{
    public FoodCourtTick(int tick, IReadOnlyList<string> modules, IReadOnlyList<FoodCourtSignal> signals, IReadOnlyList<FoodCourtProduct> products)
    {
        Tick = tick;
        Modules = modules;
        Signals = signals;
        Products = products;
    }

    public int Tick { get; }

    /// <summary>Each module's type name. A module's position here is its index in the save file.</summary>
    public IReadOnlyList<string> Modules { get; }

    /// <summary>Every socket that is on this tick.</summary>
    public IReadOnlyList<FoodCourtSignal> Signals { get; }

    public IReadOnlyList<FoodCourtProduct> Products { get; }
}

public readonly struct FoodCourtSignal
{
    public FoodCourtSignal(int module, int socket)
    {
        Module = module;
        Socket = socket;
    }

    /// <summary>Index into <see cref="FoodCourtTick.Modules"/>.</summary>
    public int Module { get; }
    public int Socket { get; }
}

public readonly struct FoodCourtProduct
{
    public FoodCourtProduct(int fromX, int fromY, int toX, int toY)
    {
        FromX = fromX;
        FromY = fromY;
        ToX = toX;
        ToY = toY;
    }

    public int FromX { get; }
    public int FromY { get; }
    public int ToX { get; }
    public int ToY { get; }
}
