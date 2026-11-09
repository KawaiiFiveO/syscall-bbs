using System.Collections.Generic;
using System.Linq;

namespace LastCallBBS.FoodCourt;

public sealed class SocketInfo
{
    public bool IsOutput;
    public string Name;
}

/// <summary>What a module type stores in a .solution file after its positions.</summary>
public sealed class ModuleData
{
    public int IntCount, BoolCount;

    // Files older than these versions hold an extra int before or after the data.
    public int LegacyLeadBefore, LegacyTrailBefore;

    public static ModuleData None(int legacyLeadBefore = 0) => new() { LegacyLeadBefore = legacyLeadBefore };
    public static ModuleData Bools(int count, int legacyLeadBefore = 0) => new() { BoolCount = count, LegacyLeadBefore = legacyLeadBefore };

    public static ModuleData Ints(int count, int legacyLeadBefore = 0, int legacyTrailBefore = 0) =>
        new() { IntCount = count, LegacyLeadBefore = legacyLeadBefore, LegacyTrailBefore = legacyTrailBefore };
}

public sealed class ModuleInfo
{
    public int Id, Cost;
    public string Name;
    public ModuleData Data;
    public IReadOnlyList<SocketInfo> Sockets;
}

/// <summary>Every module type. Generated from the game's module definitions.</summary>
public static class ModuleCatalog
{
    public const int SequencerId = 11, SequencerRows = 12, SequencerChannels = 4;
    private const int FirstScannerId = 150;
    private const string ScannerPrefix = "ScannerFor";

    public const int CurrentVersion = 1013, MinVersion = 1010;

    public static readonly Dictionary<int, ModuleInfo> ById = new();

    public static ModuleInfo ByName(string name) => ById.Values.FirstOrDefault(m => m.Name == name);

    public static bool LevelExists(int level) =>
        ById.TryGetValue(FirstScannerId + level - 1, out ModuleInfo scanner) && scanner.Name.StartsWith(ScannerPrefix);

    /// <summary>The level's internal name, such as "WineBar". Level ids follow the scanner ids.</summary>
    public static string LevelName(int level) =>
        LevelExists(level) ? ById[FirstScannerId + level - 1].Name.Substring(ScannerPrefix.Length) : "Level" + level;

    /// <summary>The id of a level given its internal name, or null if there's no such level.</summary>
    public static int? LevelId(string name)
    {
        ModuleInfo scanner = ByName(ScannerPrefix + name);
        return scanner == null ? null : scanner.Id - FirstScannerId + 1;
    }

    private static void Add(int id, string name, int cost, ModuleData data, string sockets)
    {
        ById[id] = new ModuleInfo
        {
            Id = id,
            Name = name,
            Cost = cost,
            Data = data,
            Sockets = sockets.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => new SocketInfo { IsOutput = s.StartsWith("out:"), Name = s.Substring(s.IndexOf(':') + 1) }).ToList(),
        };
    }

    static ModuleCatalog()
    {
        Add(10, "MixerComplex", 1, ModuleData.None(), "in:ENABLE in:INPUT_A in:INPUT_B in:INPUT_C out:OUTPUT_A out:OUTPUT_B out:OUTPUT_C");
        Add(11, "Sequencer", 5, ModuleData.Bools(SequencerRows * SequencerChannels, legacyLeadBefore: 1011), "in:START in:STOP out:OUTPUT_A out:OUTPUT_B out:OUTPUT_C out:OUTPUT_D");
        Add(12, "CounterSmall", 3, ModuleData.Ints(2, legacyLeadBefore: 1011), "out:ZERO_FLAG in:TRIGGER_A in:TRIGGER_B");
        Add(13, "CounterLarge", 5, ModuleData.Ints(4, legacyLeadBefore: 1011), "out:ZERO_FLAG out:POSITIVE_FLAG in:TRIGGER_A in:TRIGGER_B in:TRIGGER_C in:TRIGGER_D");
        Add(14, "MixerSimple", 1, ModuleData.None(), "in:INPUT_A in:INPUT_B in:INPUT_C in:INPUT_D out:OUTPUT_A out:OUTPUT_B out:OUTPUT_C out:OUTPUT_D");
        Add(20, "Conveyor", 5, ModuleData.None(), "");
        Add(22, "SolidInputSingle", 0, ModuleData.Ints(1), "in:IN1");
        Add(23, "SolidInputDouble", 0, ModuleData.Ints(1), "in:IN1 in:IN2");
        Add(24, "SolidInputTriple", 0, ModuleData.Ints(1), "in:IN1 in:IN2 in:IN3");
        Add(25, "LiquidInputSingle", 0, ModuleData.Ints(1, legacyTrailBefore: 1013), "in:IN1");
        Add(26, "LiquidInputDouble", 0, ModuleData.Ints(1, legacyTrailBefore: 1013), "in:IN1 in:IN2");
        Add(27, "LiquidInputTriple", 0, ModuleData.Ints(1, legacyTrailBefore: 1013), "in:IN1 in:IN2 in:IN3");
        Add(28, "EnroberInput", 0, ModuleData.Ints(1), "");
        Add(29, "Output", 0, ModuleData.None(), "");
        Add(30, "Sensor", 5, ModuleData.None(), "out:SENSE");
        Add(31, "Router", 10, ModuleData.None(), "in:LEFT in:THRU in:RIGHT");
        Add(32, "Sorter", 10, ModuleData.None(), "out:SENSE in:LEFT in:THRU in:RIGHT");
        Add(33, "Stacker", 20, ModuleData.None(), "out:STACK in:EJECT");
        Add(34, "Trash", 20, ModuleData.None(), "");
        Add(35, "SlicerDoubleVertical", 20, ModuleData.None(), "");
        Add(36, "SlicerTriple", 20, ModuleData.None(), "");
        Add(37, "Spinner", 20, ModuleData.None(), "");
        Add(38, "Espresso", 40, ModuleData.None(), "in:GRIND in:EXTRACT in:STEAM in:EJECT");
        Add(40, "Roller", 20, ModuleData.None(), "");
        Add(41, "Docker", 20, ModuleData.None(), "");
        Add(42, "Flattener", 20, ModuleData.None(), "");
        Add(43, "Painter", 40, ModuleData.Ints(2), "");
        Add(45, "Microwave", 20, ModuleData.None(legacyLeadBefore: 1012), "out:SENSE in:EJECT");
        Add(46, "Grill", 20, ModuleData.None(legacyLeadBefore: 1012), "out:SENSE in:EJECT");
        Add(47, "Fryer", 20, ModuleData.None(legacyLeadBefore: 1012), "out:SENSE in:EJECT");
        Add(48, "ToppingInputVerticalOnly", 0, ModuleData.Ints(1), "in:INPUT");
        Add(49, "FreezerInputSingle", 0, ModuleData.Ints(1), "in:IN1");
        Add(50, "FreezerInputDouble", 0, ModuleData.Ints(1), "in:IN1 in:IN2");
        Add(51, "FreezerInputSeptuple", 0, ModuleData.Ints(1), "in:IN1 in:IN2 in:IN3 in:IN4 in:IN5 in:IN6 in:IN7");
        Add(52, "Animatronic", 40, ModuleData.Bools(1), "in:DANCE in:SING in:GLASSES in:MUSIC_A in:MUSIC_B in:MUSIC_C in:MUSIC_D");
        Add(53, "ToppingInput", 0, ModuleData.Ints(1), "in:INPUT");
        Add(54, "SlicerDoubleHorizontal", 20, ModuleData.None(), "");
        Add(55, "FreezerInputTriple", 0, ModuleData.Ints(1), "in:IN1 in:IN2 in:IN3");
        Add(150, "ScannerForSnackBar", 20, ModuleData.None(), "out:START out:NACHO out:PRETZEL");
        Add(151, "ScannerForHotPockets", 20, ModuleData.None(), "out:START out:POCKET");
        Add(152, "ScannerForIceCream", 20, ModuleData.None(), "out:START out:CHOCO out:VANILLA out:TWIST out:SMALL out:MEDIUM out:LARGE");
        Add(153, "ScannerForFriedChicken", 20, ModuleData.None(), "out:START out:WHOLE out:HALF out:CUTLET out:LEG");
        Add(154, "ScannerForPelmeni", 20, ModuleData.None(), "out:START out:1_PC out:3_PCS out:6_PCS out:10_PCS out:15_PCS out:21_PCS");
        Add(155, "ScannerForChaat", 20, ModuleData.None(), "out:START out:POORI out:PAPDI");
        Add(156, "ScannerForDonuts", 20, ModuleData.None(), "out:START out:ONE out:SIX out:DOZEN out:PLAIN out:CHOCO out:BERRY");
        Add(157, "ScannerForWineBar", 20, ModuleData.None(), "out:START out:RED out:WHITE");
        Add(158, "ScannerForColaWars", 20, ModuleData.None(), "out:START out:COKE out:DIET_C out:PEPSI out:DIET_P");
        Add(159, "ScannerForSouthern", 20, ModuleData.None(), "out:START out:MAC out:SLAW out:GREENS out:BEANS");
        Add(160, "ScannerForWings", 20, ModuleData.None(), "out:START out:3_PCS out:6_PCS out:9_PCS");
        Add(161, "ScannerForFastFood", 20, ModuleData.None(), "out:START out:PLAIN out:CHEESE out:ORANGE out:PURPLE out:POTATO out:ONION");
        Add(162, "ScannerForParisian", 20, ModuleData.None(), "out:START out:DU_JOUR");
        Add(163, "ScannerForBarbecue", 20, ModuleData.None(), "out:START out:ROAST out:RIBS out:200G out:400G out:600G");
        Add(164, "ScannerForCafeteria", 20, ModuleData.None(), "out:START out:MON out:TUES out:WED out:THUR out:FRI");
        Add(165, "ScannerForPizza", 20, ModuleData.None(), "out:START out:MEAT_L out:MEAT_R out:VEGGIE_L out:VEGGIE_R");
        Add(166, "ScannerForCoffee", 20, ModuleData.None(), "out:START out:ESPRE out:DOPPIO out:LATTE out:CAPP out:AMER");
        Add(167, "ScannerForBurgers", 20, ModuleData.None(), "out:START out:SINGLE out:DOUBLE out:TRIPLE out:CHEESE out:PICKLE out:TOMATO");
        Add(168, "ScannerForBreakfast", 20, ModuleData.None(), "out:START out:SOFT out:HARD");
        Add(169, "ScannerForCocktails", 20, ModuleData.None(), "out:START out:WHISKY out:W_SOUR out:HIGHBALL out:COLA");
        Add(170, "ScannerForSushi", 20, ModuleData.None(), "out:START out:T_MAKI out:S_MAKI out:T_NIGIRI out:S_NIGIRI out:SASHIMI out:SOUP");
        Add(200, "PrimaryInputForSnackBar", 0, ModuleData.None(), "out:START out:NACHO out:PRETZEL");
        Add(201, "PrimaryInputForHotPockets", 0, ModuleData.None(), "out:START out:POCKET");
        Add(202, "PrimaryInputForIceCream", 0, ModuleData.None(), "out:START out:CHOCO out:VANILLA out:TWIST out:SMALL out:MEDIUM out:LARGE");
        Add(203, "PrimaryInputForFriedChicken", 0, ModuleData.None(), "out:START out:WHOLE out:HALF out:CUTLET out:LEG");
        Add(204, "PrimaryInputForPelmeni", 0, ModuleData.None(), "out:START out:1_PC out:3_PCS out:6_PCS out:10_PCS out:15_PCS out:21_PCS");
        Add(205, "PrimaryInputForChaat", 0, ModuleData.None(), "out:START out:POORI out:PAPDI");
        Add(206, "PrimaryInputForDonuts", 0, ModuleData.None(), "out:START out:ONE out:SIX out:DOZEN out:PLAIN out:CHOCO out:BERRY");
        Add(207, "PrimaryInputForWineBar", 0, ModuleData.None(), "out:START out:RED out:WHITE");
        Add(208, "PrimaryInputForColaWars", 0, ModuleData.None(), "out:START out:COKE out:DIET_C out:PEPSI out:DIET_P");
        Add(209, "PrimaryInputForSouthern", 0, ModuleData.None(), "out:START out:MAC out:SLAW out:GREENS out:BEANS");
        Add(210, "PrimaryInputForWings", 0, ModuleData.None(), "out:START out:3_PCS out:6_PCS out:9_PCS");
        Add(211, "PrimaryInputForFastFood", 0, ModuleData.None(), "out:START out:PLAIN out:CHEESE out:ORANGE out:PURPLE out:POTATO out:ONION");
        Add(212, "PrimaryInputForParisian", 0, ModuleData.None(), "out:START out:DU_JOUR");
        Add(213, "PrimaryInputForBarbecue", 0, ModuleData.None(), "out:START out:ROAST out:RIBS out:200G out:400G out:600G");
        Add(214, "PrimaryInputForCafeteria", 0, ModuleData.None(), "out:START out:MON out:TUES out:WED out:THUR out:FRI");
        Add(215, "PrimaryInputForPizza", 0, ModuleData.None(), "out:START out:MEAT_L out:MEAT_R out:VEGGIE_L out:VEGGIE_R");
        Add(216, "PrimaryInputForCoffee", 0, ModuleData.None(), "out:START out:ESPRE out:DOPPIO out:LATTE out:CAPP out:AMER");
        Add(217, "PrimaryInputForBurgers", 0, ModuleData.None(), "out:START out:SINGLE out:DOUBLE out:TRIPLE out:CHEESE out:PICKLE out:TOMATO");
        Add(218, "PrimaryInputForBreakfast", 0, ModuleData.None(), "out:START out:SOFT out:HARD");
        Add(219, "PrimaryInputForCocktails", 0, ModuleData.None(), "out:START out:WHISKY out:W_SOUR out:HIGHBALL out:COLA");
        Add(220, "PrimaryInputForSushi", 0, ModuleData.None(), "out:START out:T_MAKI out:S_MAKI out:T_NIGIRI out:S_NIGIRI out:SASHIMI out:SOUP");
    }
}
