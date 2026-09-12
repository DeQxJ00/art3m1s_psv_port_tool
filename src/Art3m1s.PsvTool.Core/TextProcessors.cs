using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Art3m1s.PsvTool.Core;

public interface ITextProcessor
{
    Task ProcessAsync(string path, double ratio, CancellationToken cancellationToken = default);
}

public interface IVitaIniProcessor
{
    Task EnsureVitaSectionAsync(string path, CancellationToken cancellationToken = default);
}

public sealed partial class ArtemisTextProcessor : ITextProcessor
{
    public async Task ProcessAsync(string path, double ratio, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        EncodedText encoded = EncodedText.Decode(bytes);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        string output = extension switch
        {
            ".ini" => ScaleIni(encoded.Text, ratio),
            ".tbl" => ScaleTbl(encoded.Text, ratio),
            ".ipt" => ScaleIpt(encoded.Text, ratio),
            ".ast" => ScaleAst(encoded.Text, ratio),
            ".lua" => ScaleLua(encoded.Text, ratio),
            _ => encoded.Text
        };
        if (!ReferenceEquals(output, encoded.Text) && output != encoded.Text)
            await File.WriteAllBytesAsync(path, encoded.Encode(output), cancellationToken);
    }

    private static string ScaleIni(string text, double ratio)
    {
        StringBuilder result = new(text.Length);
        bool inVita = false;
        foreach (string line in SplitLines(text))
        {
            Match section = SectionRegex().Match(line);
            if (section.Success)
                inVita = section.Groups[1].Value.Equals("VITA", StringComparison.OrdinalIgnoreCase);
            result.Append(inVita ? line : IniSizeRegex().Replace(line, match => ScaleMatch(match, ratio)));
        }
        return result.ToString();
    }

    private static string ScaleTbl(string text, double ratio)
    {
        StringBuilder result = new(text.Length);
        foreach (string line in SplitLines(text))
        {
            string scaled = TblBraceRegex().Replace(line, match =>
                match.Groups[1].Value + IntegerRegex().Replace(match.Groups[2].Value, number => ScaleNumber(number.Value, ratio)) + match.Groups[3].Value);
            scaled = TblFieldRegex().Replace(scaled, match => ScaleMatch(match, ratio));
            scaled = TblClipRegex().Replace(scaled, match =>
                match.Groups[1].Value + ScaleCommaList(match.Groups[2].Value, ratio) + match.Groups[3].Value);
            result.Append(scaled);
        }
        return result.ToString();
    }

    private static string ScaleIpt(string text, double ratio)
    {
        StringBuilder result = new(text.Length);
        foreach (string line in SplitLines(text))
        {
            string scaled = IptFieldRegex().Replace(line, match => ScaleMatch(match, ratio));
            scaled = QuotedCommaRegex().Replace(scaled, match =>
                match.Groups[1].Value + ScaleCommaList(match.Groups[2].Value, ratio) + match.Groups[3].Value);
            result.Append(scaled);
        }
        return result.ToString();
    }

    private static string ScaleAst(string text, double ratio) =>
        AstFieldRegex().Replace(text, match => ScaleMatch(match, ratio));

    private static string ScaleLua(string text, double ratio) =>
        LuaFieldRegex().Replace(text, match => ScaleMatch(match, ratio));

    private static string ScaleMatch(Match match, double ratio) =>
        match.Groups[1].Value + ScaleNumber(match.Groups[2].Value, ratio);

    private static string ScaleNumber(string value, double ratio) =>
        ((int)(int.Parse(value, CultureInfo.InvariantCulture) * ratio)).ToString(CultureInfo.InvariantCulture);

    private static string ScaleCommaList(string value, double ratio)
    {
        string[] fields = value.Split(',');
        for (int i = 0; i < fields.Length; i++)
        {
            string trimmed = fields[i].Trim();
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                fields[i] = fields[i].Replace(trimmed, ((int)(number * ratio)).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        return string.Join(',', fields);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            int end = i + 1;
            if (text[i] == '\r' && end < text.Length && text[end] == '\n') end++;
            yield return text[start..end];
            i = end - 1;
            start = end;
        }
        if (start < text.Length) yield return text[start..];
    }

    [GeneratedRegex(@"^\s*\[([^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SectionRegex();
    [GeneratedRegex(@"(?im)^(\s*(?:WIDTH|HEIGHT)\s*=\s*)(-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex IniSizeRegex();
    [GeneratedRegex(@"(?i)(^\s*(?:game_scale|game_wasmbar|fontsize|line_size|line_window|line_back|line_scroll|line_name01|line_name02)\s*\{)(.*?)(\})", RegexOptions.CultureInvariant)]
    private static partial Regex TblBraceRegex();
    [GeneratedRegex(@"(?i)(\b(?:x|y|w|h|r|cx|cy|cw|ch|fx|fy|fw|fh|left|top|size|width|height|spacetop|spacemiddle|spacebottom|kerning|rubysize|game_width|game_height)\s*[:=,]\s*)(-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TblFieldRegex();
    [GeneratedRegex("(?i)(\\b(?:clip|clip_a|clip_c|clip_d)\\s*[:=]\\s*\")(.*?)(\")", RegexOptions.CultureInvariant)]
    private static partial Regex TblClipRegex();
    [GeneratedRegex(@"(?i)(\b(?:x|y|w|h|ax|ay)\s*[:=,]\s*)(-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex IptFieldRegex();
    [GeneratedRegex("(\")(.*?,.*?)(\")", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedCommaRegex();
    [GeneratedRegex(@"(?i)(\b(?:mx|my|ax|ay|bx|by|x|y|x2|y2)\s*[:=,]\s*)(-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex AstFieldRegex();
    [GeneratedRegex(@"(?i)(\b(?:width|height|left|top|x|y)\s*=\s*)(-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LuaFieldRegex();
    [GeneratedRegex(@"-?\d+", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerRegex();
}

public sealed partial class VitaIniProcessor : IVitaIniProcessor
{
    public async Task EnsureVitaSectionAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        EncodedText encoded = EncodedText.Decode(bytes);
        if (VitaSectionRegex().IsMatch(encoded.Text))
            return;

        string charset = FindCharset(WindowsSectionRegex().Match(encoded.Text).Groups[1].Value)
            ?? FindCharset(encoded.Text)
            ?? "Shift_JIS";
        string separator = encoded.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string prefix = encoded.Text.Length == 0 || encoded.Text.EndsWith('\n') || encoded.Text.EndsWith('\r') ? string.Empty : separator;
        string block = VitaTemplate.Replace("\n", separator, StringComparison.Ordinal).Replace("{CHARSET}", charset, StringComparison.Ordinal);
        await File.WriteAllBytesAsync(path, encoded.Encode(encoded.Text + prefix + block), cancellationToken);
    }

    private static string? FindCharset(string text)
    {
        Match match = CharsetRegex().Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private const string VitaTemplate = """
        [VITA]

        ; 文字コード
        CHARSET = {CHARSET}

        ; ステージの幅
        WIDTH = 960
        ; ステージの高さ
        HEIGHT = 540

        ; ステージと液晶パネルの縦横比が一致しない場合に
        ; はみ出した部分をカットしてフィットさせるか否か
        SIDECUT = 0

        ; 最初に読み込むスクリプト
        BOOT = system/first.iet

        ; フォントキャッシュ（本文とバックログのパフォーマンス要チェック）
        ;FONT_CACHE_SIZE = 50331648
        ;FONT_CACHE_SIZE = 33554432
        FONT_CACHE_SIZE = 25165824
        ;FONT_CACHE_SIZE = 16777216
        ;FONT_CACHE_SIZE = 14680064
        ;FONT_CACHE_SIZE = 67108864
        """;

    [GeneratedRegex(@"(?im)^\s*\[VITA\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex VitaSectionRegex();
    [GeneratedRegex(@"(?ims)^\s*\[WINDOWS\]\s*(.*?)(?=^\s*\[|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsSectionRegex();
    [GeneratedRegex(@"(?im)^\s*CHARSET\s*=\s*([^;\r\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CharsetRegex();
}

internal sealed record EncodedText(string Text, Encoding Encoding, byte[] Preamble)
{
    public static EncodedText Decode(byte[] bytes)
    {
        (Encoding encoding, bool hasBom) = TextEncoding.Detect(bytes);
        byte[] preamble = hasBom ? encoding.GetPreamble() : [];
        int offset = hasBom ? preamble.Length : 0;
        return new EncodedText(encoding.GetString(bytes, offset, bytes.Length - offset), encoding, preamble);
    }

    public byte[] Encode(string text)
    {
        byte[] body = Encoding.GetBytes(text);
        if (Preamble.Length == 0) return body;
        byte[] result = new byte[Preamble.Length + body.Length];
        Preamble.CopyTo(result, 0);
        body.CopyTo(result, Preamble.Length);
        return result;
    }
}
