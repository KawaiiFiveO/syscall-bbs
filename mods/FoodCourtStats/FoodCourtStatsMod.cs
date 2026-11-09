using System;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.FoodCourtStats;

/// <summary>
/// Example of a mod limited to one minigame. It logs how long each order took, and how busy the
/// floor got. Set ShowEveryTick = true in config.ini for a line per tick.
/// </summary>
public sealed class FoodCourtStatsMod : IMod
{
    private IModLog _log;
    private bool _showEveryTick;
    private int _lastTick = -1, _peakProducts, _peakSignals;

    public void OnLoad(ModContext context)
    {
        _log = context.Log;
        _showEveryTick = context.Config.GetBool("ShowEveryTick");
        context.Hooks.FoodCourt.PuzzleOpened.Subscribe(OnPuzzleOpened);
        context.Hooks.FoodCourt.TickCompleted.Subscribe(OnTick);
    }

    private void OnPuzzleOpened(string level)
    {
        FinishOrder();
        _log.Info($"Opened {level}");
    }

    private void OnTick(FoodCourtTick tick)
    {
        // There's no "order finished" event, but tick numbers restart with each order.
        // The last order of a run is therefore only reported once something else starts.
        if (tick.Tick <= _lastTick)
            FinishOrder();

        _lastTick = tick.Tick;
        _peakProducts = Math.Max(_peakProducts, tick.Products.Count);
        _peakSignals = Math.Max(_peakSignals, tick.Signals.Count);

        if (_showEveryTick)
            _log.Info($"Tick {tick.Tick}: {tick.Products.Count} products, {tick.Signals.Count} signals on");
    }

    private void FinishOrder()
    {
        if (_lastTick >= 0)
            _log.Info($"Order ran {_lastTick} ticks, peak {_peakProducts} products and {_peakSignals} signals");
        _lastTick = -1;
        _peakProducts = 0;
        _peakSignals = 0;
    }
}
