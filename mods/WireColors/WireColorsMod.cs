using System;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.WireColors;

/// <summary>
/// Colors for 20th Century Food Court wires. Colors are saved in the solution file, after the
/// wire list, where the unmodded game doesn't look. F5 on the puzzle screen exports a copy
/// without them, and C switches the cost display to the wire count.
/// </summary>
public sealed class WireColorsMod : IMod
{
    public void OnLoad(ModContext context)
    {
        // Without the serializer patch colors would be lost on the next save, so nothing else is applied.
        ColorStore.Apply(context.Game, context.Harmony, context.Log);
        Apply(context, "colored wires", () => WireDraw.Apply(context.Game, context.Harmony, new Recolor(context.Textures), context.Log));
        Apply(context, "wire count display", () => CostDisplay.Apply(context.Game, context.Harmony, context.Log));
        Apply(context, "coloring, export, and wire count keys", () => PuzzleInput.Apply(context.Game, context.Harmony, context));
    }

    private static void Apply(ModContext context, string feature, Action apply)
    {
        try
        {
            apply();
        }
        catch (ShapeMismatchException e)
        {
            context.ReportProblem($"{feature} unavailable: {e.Message}");
        }
    }
}
