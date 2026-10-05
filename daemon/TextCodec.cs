using System.Globalization;
using System.Text;

namespace SpotifyRadio;

/// <summary>
/// Converts text for the game. X-Ray fonts are single-byte, so by default everything is
/// reduced to plain ASCII (accents stripped, Cyrillic transliterated).
/// </summary>
public sealed class TextCodec
{
    private static readonly Dictionary<char, string> Map = BuildMap();
    private readonly bool _ascii;

    public Encoding Wire { get; }

    /// <summary>How text typed in the game is decoded. The game sends its ANSI code page (Cyrillic = 1251).</summary>
    public Encoding Incoming { get; }

    public TextCodec(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var n = (name ?? "").Trim().ToLowerInvariant();
        if (n is "" or "ascii")
        {
            _ascii = true;
            Wire = Encoding.Latin1;
            Incoming = Encoding.GetEncoding(1251);
            return;
        }
        else if (n is "utf-8" or "utf8")
        {
            Wire = new UTF8Encoding(false);
        }
        else
        {
            Wire = Encoding.GetEncoding(n);
        }
        Incoming = Wire;
    }

    /// <summary>Makes a value safe to put in one tab-separated field.</summary>
    public string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in _ascii ? s.Normalize(NormalizationForm.FormD) : s)
        {
            if (ch == '\t' || ch == '\r' || ch == '\n' || char.IsControl(ch)) { sb.Append(' '); continue; }
            if (!_ascii || ch < 128) { sb.Append(ch); continue; }
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsSurrogate(ch)) continue;
            sb.Append(Map.TryGetValue(ch, out var rep) ? rep : "?");
        }
        return sb.ToString().Trim();
    }

    private static Dictionary<char, string> BuildMap()
    {
        var m = new Dictionary<char, string>();
        var lower = new (char, string)[]
        {
            ('а', "a"), ('б', "b"), ('в', "v"), ('г', "g"), ('д', "d"), ('е', "e"), ('ж', "zh"),
            ('з', "z"), ('и', "i"), ('й', "y"), ('к', "k"), ('л', "l"), ('м', "m"), ('н', "n"), ('о', "o"),
            ('п', "p"), ('р', "r"), ('с', "s"), ('т', "t"), ('у', "u"), ('ф', "f"), ('х', "kh"), ('ц', "ts"),
            ('ч', "ch"), ('ш', "sh"), ('щ', "shch"), ('ъ', ""), ('ы', "y"), ('ь', ""), ('э', "e"), ('ю', "yu"),
            ('я', "ya"), ('і', "i"), ('є', "ye"), ('ґ', "g"),
            ('ß', "ss"), ('æ', "ae"), ('ø', "o"), ('đ', "d"), ('ł', "l"), ('þ', "th"), ('ð', "d"), ('œ', "oe"), ('ı', "i"),
        };
        foreach (var (c, r) in lower)
        {
            m[c] = r;
            var up = char.ToUpperInvariant(c);
            if (up != c) m[up] = r.Length == 0 ? "" : char.ToUpperInvariant(r[0]) + r[1..];
        }
        foreach (var c in "‘’‚′") m[c] = "'";
        foreach (var c in "“”„«»″") m[c] = "\"";
        foreach (var c in "‐‑‒–—―−") m[c] = "-";
        m['…'] = "...";
        m[' '] = " ";
        m['×'] = "x";
        m['•'] = "*";
        m['·'] = "*";
        return m;
    }
}
