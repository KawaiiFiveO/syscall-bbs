using System.Linq;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.HookLogger;

/// <summary>Development aid: prints every hook as it fires, to check them in actual play.</summary>
public sealed class HookLoggerMod : IMod
{
    private const int FramesPerReport = 600;
    private int _frames;

    public void OnLoad(ModContext context)
    {
        IModLog log = context.Log;
        ModHooks hooks = context.Hooks;

        hooks.Game.Update.Subscribe(() =>
        {
            if (++_frames % FramesPerReport == 1)
                log.Info($"Game.Update: frame {_frames}");
        });
        hooks.Desktop.WindowOpened.Subscribe(type => log.Info($"Desktop.WindowOpened: {type}"));
        hooks.Story.EntryUnlocked.Subscribe(entry => log.Info($"Story.EntryUnlocked: #{entry.Index} \"{entry.Title}\""));
        hooks.Terminal.DownloadStarted.Subscribe(program => log.Info($"Terminal.DownloadStarted: {program}"));
        hooks.Terminal.DownloadCompleted.Subscribe(program => log.Info($"Terminal.DownloadCompleted: {program}"));
        hooks.Terminal.Connected.Subscribe(() => log.Info("Terminal.Connected"));
        hooks.FoodCourt.PuzzleOpened.Subscribe(level => log.Info($"FoodCourt.PuzzleOpened: {level}"));
        hooks.FoodCourt.TickCompleted.Subscribe(tick =>
        {
            string signals = string.Join(" ", tick.Signals.Select(s => $"{tick.Modules[s.Module]}#{s.Module}.{s.Socket}"));
            string products = string.Join(" ", tick.Products.Select(p => $"({p.FromX},{p.FromY})>({p.ToX},{p.ToY})"));
            log.Info($"FoodCourt.TickCompleted: tick {tick.Tick}, signals [{signals}], products [{products}]");
        });
    }
}
