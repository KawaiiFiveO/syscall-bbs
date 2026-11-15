namespace Tracker;

/// <summary>Text in module files: Latin-1 on the Amiga, code page 437 on the PC.</summary>
internal static class Charset
{
    private const string CodePage437High =
        "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐" +
        "└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

    /// <summary>The character for a byte; '\0' for 0 and control codes other than tab and line breaks.</summary>
    public static char Decode(byte b, bool pc)
    {
        if (b == 9 || b == 10 || b == 13)
            return (char)b;
        if (b < 32 || b == 127)
            return '\0';
        if (b < 127)
            return (char)b;
        if (pc)
            return CodePage437High[b - 128];
        return b >= 160 ? (char)b : '\0';
    }
}
