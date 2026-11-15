using LastCallBBS.ModAPI;

namespace LastCallBBS.Mods.TrackerPlayer;

/// <summary>A color scheme.</summary>
internal sealed class Theme
{
    public string Name;
    public TextColor Back, Text, Dim, Bar, BarText, Highlight, HighlightText;
    public TextColor Note, Sample, Effect;
    public TextColor Low, Mid, High, Error;
    public TextColor Button, ButtonText, ButtonHover;

    /// <summary>A toggle that is on, such as LOOP.</summary>
    public TextColor Active, ActiveText;

    private static TextColor C(int rgb) => TextColor.FromRgb(rgb);

    public static readonly Theme[] All =
    {
        new()
        {
            Name = "Slate",
            Back = C(0x182028), Text = C(0xC8D0D8), Dim = C(0x5A6878), Bar = C(0x4A5A70), BarText = C(0xF0F4F8),
            Highlight = C(0x34465C), HighlightText = C(0xFFFFFF),
            Note = C(0xF0F0F0), Sample = C(0x80C8FF), Effect = C(0xFFC860),
            Low = C(0x60D070), Mid = C(0xE8D050), High = C(0xE85848), Error = C(0xFF8070),
            Button = C(0x34465C), ButtonText = C(0xE0E8F0), ButtonHover = C(0x506C8C),
            Active = C(0x70C8F0), ActiveText = C(0x0C1820),
        },
        new()
        {
            Name = "Workbench",
            Back = C(0x0055AA), Text = C(0xFFFFFF), Dim = C(0x7FA8D4), Bar = C(0xFFFFFF), BarText = C(0x0055AA),
            Highlight = C(0xFF8800), HighlightText = C(0x000000),
            Note = C(0xFFFFFF), Sample = C(0xFFBB66), Effect = C(0xD8E8FF),
            Low = C(0xFFFFFF), Mid = C(0xFF8800), High = C(0x000000), Error = C(0xFFBB66),
            Button = C(0x003F80), ButtonText = C(0xFFFFFF), ButtonHover = C(0x2A70C0),
            Active = C(0xFF8800), ActiveText = C(0x000000),
        },
        new()
        {
            Name = "Amber",
            Back = C(0x100A00), Text = C(0xFFB000), Dim = C(0x7A5400), Bar = C(0xFFB000), BarText = C(0x100A00),
            Highlight = C(0x4A3200), HighlightText = C(0xFFD060),
            Note = C(0xFFC840), Sample = C(0xE09800), Effect = C(0xFFD880),
            Low = C(0xB07800), Mid = C(0xFFB000), High = C(0xFFE0A0), Error = C(0xFFE0A0),
            Button = C(0x3A2800), ButtonText = C(0xFFB000), ButtonHover = C(0x5C4000),
            Active = C(0xFFB000), ActiveText = C(0x100A00),
        },
        new()
        {
            Name = "Phosphor",
            Back = C(0x001008), Text = C(0x33FF66), Dim = C(0x167A32), Bar = C(0x33FF66), BarText = C(0x001008),
            Highlight = C(0x0C4A20), HighlightText = C(0xA0FFB8),
            Note = C(0x66FF88), Sample = C(0x22CC55), Effect = C(0xB0FFC4),
            Low = C(0x1FA043), Mid = C(0x33FF66), High = C(0xC8FFD6), Error = C(0xC8FFD6),
            Button = C(0x06381A), ButtonText = C(0x33FF66), ButtonHover = C(0x0E5A28),
            Active = C(0x33FF66), ActiveText = C(0x001008),
        },
        new()
        {
            Name = "Daylight",
            Back = C(0xE8E4DC), Text = C(0x283038), Dim = C(0x9098A0), Bar = C(0x506070), BarText = C(0xF4F2EE),
            Highlight = C(0xC0CCD8), HighlightText = C(0x101418),
            Note = C(0x101418), Sample = C(0x1060B0), Effect = C(0xA05000),
            Low = C(0x309040), Mid = C(0xC09000), High = C(0xC03020), Error = C(0xB03020),
            Button = C(0xCCC6BA), ButtonText = C(0x283038), ButtonHover = C(0xB0BCC8),
            Active = C(0x506070), ActiveText = C(0xF4F2EE),
        },
    };

    public static Theme Find(string name)
    {
        foreach (Theme t in All)
        {
            if (t.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return All[0];
    }
}
