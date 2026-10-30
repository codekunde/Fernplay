using System.Globalization;
using System.Text;

namespace Fernplay.Core.Skins;

/// <summary>
/// The 5x6 pixel font in TEXT.BMP (title display, kbps, kHz): a grid of 5x6 cells, three rows.
/// Letters are capitals only; accents are dropped ("é" draws as "E"); anything unknown draws as a space.
/// </summary>
public static class SkinFont
{
    public const int CharWidth = 5, CharHeight = 6;

    private static readonly Dictionary<char, (int Row, int Col)> Map = Build();

    private static Dictionary<char, (int, int)> Build()
    {
        var m = new Dictionary<char, (int, int)>();
        for (int i = 0; i < 26; i++) m[(char)('a' + i)] = (0, i);
        m['"'] = (0, 26); m['@'] = (0, 27); m[' '] = (0, 30);
        for (int i = 0; i < 10; i++) m[(char)('0' + i)] = (1, i);
        const string row1 = "….:()-'!_+\\/[]^&%,=$#";
        for (int i = 0; i < row1.Length; i++) m[row1[i]] = (1, 10 + i);
        m['Å'] = (2, 0); m['Ö'] = (2, 1); m['Ä'] = (2, 2); m['?'] = (2, 3); m['*'] = (2, 4);
        // Stand-ins the sheet has no cell for.
        m['<'] = m['{'] = (1, 22); m['>'] = m['}'] = (1, 23); m[';'] = (1, 12); m['`'] = (1, 16); m['|'] = (1, 21);
        return m;
    }

    /// <summary>The sheet cell (row, column) for one character.</summary>
    public static (int Row, int Col) Cell(char c)
    {
        if (Map.TryGetValue(c, out var cell)) return cell;
        char lower = char.ToLowerInvariant(c);
        if (Map.TryGetValue(lower, out cell)) return cell;
        if (Map.TryGetValue(char.ToUpperInvariant(c), out cell)) return cell; // å → Å
        var plain = c.ToString().Normalize(NormalizationForm.FormD);
        foreach (char p in plain)
            if (CharUnicodeInfo.GetUnicodeCategory(p) != UnicodeCategory.NonSpacingMark && Map.TryGetValue(char.ToLowerInvariant(p), out cell)) return cell;
        return Map[' '];
    }

    /// <summary>The TEXT sprite rectangle for one character.</summary>
    public static Sprite Glyph(char c)
    {
        var (row, col) = Cell(c);
        return new Sprite("TEXT", col * CharWidth, row * CharHeight, CharWidth, CharHeight);
    }
}
