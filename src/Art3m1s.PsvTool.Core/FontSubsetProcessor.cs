using System.Buffers.Binary;
using System.Text;

namespace Art3m1s.PsvTool.Core;

public interface IFontSubsetProcessor
{
    Task<bool> SubsetAsync(string path, FontSubsetProfile profile, IReadOnlySet<int> usedCodePoints, CancellationToken cancellationToken = default);
}

/// <summary>
/// Conservatively strips unused TrueType glyf outlines while retaining glyph IDs and layout tables.
/// Keeping IDs avoids rewriting GSUB/GPOS and is particularly suitable for Artemis bitmap-facing TTF fonts.
/// CFF/CFF2, TTC and variable fonts are deliberately left unchanged.
/// </summary>
public sealed class FontSubsetProcessor : IFontSubsetProcessor
{
    public async Task<bool> SubsetAsync(string path, FontSubsetProfile profile, IReadOnlySet<int> usedCodePoints, CancellationToken cancellationToken = default)
    {
        byte[] font = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!SfntFont.TryParse(font, out SfntFont? parsed) || parsed is null || parsed.Tables.ContainsKey("CFF ") || parsed.Tables.ContainsKey("CFF2") || parsed.Tables.ContainsKey("fvar"))
            return false;

        HashSet<int> codePoints = FontCharacterSets.Create(profile);
        codePoints.UnionWith(usedCodePoints);
        HashSet<ushort> glyphs = [0];
        foreach (int codePoint in codePoints)
            if (parsed.TryMapCodePoint(codePoint, out ushort glyph)) glyphs.Add(glyph);
        parsed.AddCompositeDependencies(glyphs);

        byte[] subset = parsed.BuildWithGlyphs(glyphs);
        string temporary = path + ".subset-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, subset, cancellationToken);
            File.Move(temporary, path, true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal static class FontCharacterSets
{
    private static readonly Lazy<HashSet<int>> Simplified = new(() => FromDoubleByteEncoding(936, 0xA1, 0xF7, 0xA1, 0xFE));
    private static readonly Lazy<HashSet<int>> JapaneseSet = new(() => FromDoubleByteEncoding(932, 0x81, 0xFC, 0x40, 0xFC));
    private static readonly Lazy<HashSet<int>> Traditional = new(() => FromDoubleByteEncoding(950, 0x81, 0xFE, 0x40, 0xFE));

    public static HashSet<int> Create(FontSubsetProfile profile)
    {
        HashSet<int> result = new(profile switch
        {
            FontSubsetProfile.Japanese => JapaneseSet.Value,
            FontSubsetProfile.TraditionalChinese => Traditional.Value,
            _ => Simplified.Value
        });
        AddRange(result, 0x20, 0x7E);
        AddRange(result, 0xA0, 0xFF);
        AddRange(result, 0x2000, 0x206F);
        AddRange(result, 0x3000, 0x303F);
        AddRange(result, 0xFF01, 0xFF9F);
        return result;
    }

    private static HashSet<int> FromDoubleByteEncoding(int codePage, int leadStart, int leadEnd, int trailStart, int trailEnd)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        HashSet<int> result = [];
        Span<byte> pair = stackalloc byte[2];
        for (int lead = leadStart; lead <= leadEnd; lead++)
            for (int trail = trailStart; trail <= trailEnd; trail++)
            {
                pair[0] = (byte)lead; pair[1] = (byte)trail;
                try
                {
                    string text = encoding.GetString(pair);
                    foreach (Rune rune in text.EnumerateRunes()) result.Add(rune.Value);
                }
                catch (DecoderFallbackException) { }
            }
        return result;
    }

    private static void AddRange(HashSet<int> set, int start, int end)
    { for (int value = start; value <= end; value++) set.Add(value); }
}

internal sealed class SfntFont
{
    internal sealed record Table(string Tag, byte[] Data);
    private readonly uint _scalerType;
    public Dictionary<string, Table> Tables { get; }
    private readonly ushort _glyphCount;
    private readonly bool _longLoca;
    private readonly uint[] _locations;

    private SfntFont(uint scalerType, Dictionary<string, Table> tables, ushort glyphCount, bool longLoca, uint[] locations)
    { _scalerType = scalerType; Tables = tables; _glyphCount = glyphCount; _longLoca = longLoca; _locations = locations; }

    public static bool TryParse(byte[] bytes, out SfntFont? font)
    {
        font = null;
        if (bytes.Length < 12) return false;
        uint scaler = ReadU32(bytes, 0);
        if (scaler is not (0x00010000 or 0x74727565)) return false;
        ushort count = ReadU16(bytes, 4);
        if (12 + count * 16 > bytes.Length) return false;
        Dictionary<string, Table> tables = new(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int record = 12 + i * 16;
            string tag = Encoding.ASCII.GetString(bytes, record, 4);
            uint offset = ReadU32(bytes, record + 8), length = ReadU32(bytes, record + 12);
            if ((ulong)offset + length > (ulong)bytes.Length || length > int.MaxValue) return false;
            tables[tag] = new Table(tag, bytes.AsSpan((int)offset, (int)length).ToArray());
        }
        if (!tables.TryGetValue("head", out Table? head) || head.Data.Length < 54 ||
            !tables.TryGetValue("maxp", out Table? maxp) || maxp.Data.Length < 6 ||
            !tables.TryGetValue("loca", out Table? loca) || !tables.ContainsKey("glyf") || !tables.ContainsKey("cmap")) return false;
        ushort glyphCount = ReadU16(maxp.Data, 4); bool longLoca = ReadI16(head.Data, 50) != 0;
        int entrySize = longLoca ? 4 : 2;
        if (loca.Data.Length < (glyphCount + 1) * entrySize) return false;
        uint[] locations = new uint[glyphCount + 1];
        for (int i = 0; i < locations.Length; i++) locations[i] = longLoca ? ReadU32(loca.Data, i * 4) : (uint)ReadU16(loca.Data, i * 2) * 2;
        if (!tables.TryGetValue("glyf", out Table? glyf) || locations.Any(value => value > glyf.Data.Length)) return false;
        font = new SfntFont(scaler, tables, glyphCount, longLoca, locations); return true;
    }

    public bool TryMapCodePoint(int codePoint, out ushort glyph)
    {
        glyph = 0;
        byte[] cmap = Tables["cmap"].Data;
        if (cmap.Length < 4) return false;
        ushort tableCount = ReadU16(cmap, 2);
        List<int> offsets = [];
        for (int i = 0; i < tableCount && 4 + i * 8 + 8 <= cmap.Length; i++)
        {
            uint offset = ReadU32(cmap, 4 + i * 8 + 4);
            if (offset < cmap.Length) offsets.Add((int)offset);
        }
        foreach (int offset in offsets.OrderByDescending(value => ReadU16(cmap, value) == 12))
        {
            ushort format = ReadU16(cmap, offset);
            if (format == 12 && TryMapFormat12(cmap, offset, codePoint, out glyph) || format == 4 && codePoint <= ushort.MaxValue && TryMapFormat4(cmap, offset, (ushort)codePoint, out glyph))
                return glyph != 0;
        }
        return false;
    }

    public void AddCompositeDependencies(HashSet<ushort> glyphs)
    {
        byte[] data = Tables["glyf"].Data;
        Queue<ushort> pending = new(glyphs);
        while (pending.TryDequeue(out ushort glyph))
        {
            if (glyph >= _glyphCount) continue;
            int start = (int)_locations[glyph], end = (int)_locations[glyph + 1];
            if (end - start < 10 || ReadI16(data, start) >= 0) continue;
            int cursor = start + 10; ushort flags;
            do
            {
                if (cursor + 4 > end) break;
                flags = ReadU16(data, cursor); ushort component = ReadU16(data, cursor + 2); cursor += 4;
                if (glyphs.Add(component)) pending.Enqueue(component);
                cursor += (flags & 0x0001) != 0 ? 4 : 2;
                if ((flags & 0x0008) != 0) cursor += 2;
                else if ((flags & 0x0040) != 0) cursor += 4;
                else if ((flags & 0x0080) != 0) cursor += 8;
            } while ((flags & 0x0020) != 0);
        }
    }

    public byte[] BuildWithGlyphs(HashSet<ushort> glyphs)
    {
        byte[] oldGlyf = Tables["glyf"].Data;
        using MemoryStream glyf = new(); uint[] locations = new uint[_glyphCount + 1];
        for (int glyphIndex = 0; glyphIndex < _glyphCount; glyphIndex++)
        {
            ushort glyph = (ushort)glyphIndex;
            locations[glyph] = checked((uint)glyf.Length);
            if (!glyphs.Contains(glyph)) continue;
            int start = (int)_locations[glyph], length = checked((int)(_locations[glyph + 1] - _locations[glyph]));
            glyf.Write(oldGlyf, start, length);
            while (glyf.Length % (_longLoca ? 4 : 2) != 0) glyf.WriteByte(0);
        }
        locations[_glyphCount] = checked((uint)glyf.Length);
        if (!_longLoca && locations[^1] / 2 > ushort.MaxValue) throw new InvalidDataException("Subset loca table overflow.");
        byte[] loca = new byte[locations.Length * (_longLoca ? 4 : 2)];
        for (int i = 0; i < locations.Length; i++)
            if (_longLoca) WriteU32(loca, i * 4, locations[i]); else WriteU16(loca, i * 2, checked((ushort)(locations[i] / 2)));
        Dictionary<string, Table> replacement = Tables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        replacement["glyf"] = new Table("glyf", glyf.ToArray()); replacement["loca"] = new Table("loca", loca);
        replacement["head"].Data.AsSpan(8, 4).Clear();
        return BuildSfnt(_scalerType, replacement.Values.OrderBy(table => table.Tag, StringComparer.Ordinal).ToArray());
    }

    private static byte[] BuildSfnt(uint scaler, Table[] tables)
    {
        int count = tables.Length, selector = count == 0 ? 0 : (int)Math.Floor(Math.Log2(count));
        int directorySize = 12 + count * 16, total = directorySize + tables.Sum(table => (table.Data.Length + 3) & ~3);
        byte[] output = new byte[total]; WriteU32(output, 0, scaler); WriteU16(output, 4, (ushort)count);
        ushort searchRange = (ushort)(16 * (1 << selector)); WriteU16(output, 6, searchRange); WriteU16(output, 8, (ushort)selector); WriteU16(output, 10, (ushort)(count * 16 - searchRange));
        int dataOffset = directorySize, headOffset = -1;
        for (int i = 0; i < count; i++)
        {
            Table table = tables[i]; int record = 12 + i * 16; Encoding.ASCII.GetBytes(table.Tag, output.AsSpan(record, 4));
            WriteU32(output, record + 4, Checksum(table.Data)); WriteU32(output, record + 8, (uint)dataOffset); WriteU32(output, record + 12, (uint)table.Data.Length);
            table.Data.CopyTo(output, dataOffset); if (table.Tag == "head") headOffset = dataOffset; dataOffset += (table.Data.Length + 3) & ~3;
        }
        if (headOffset >= 0) WriteU32(output, headOffset + 8, unchecked(0xB1B0AFBAu - Checksum(output)));
        return output;
    }

    private static bool TryMapFormat12(byte[] data, int offset, int codePoint, out ushort glyph)
    {
        glyph = 0; if (offset + 16 > data.Length) return false; uint count = ReadU32(data, offset + 12);
        for (uint i = 0; i < count; i++) { int group = checked(offset + 16 + (int)i * 12); if (group + 12 > data.Length) return false; uint start = ReadU32(data, group), end = ReadU32(data, group + 4); if ((uint)codePoint >= start && (uint)codePoint <= end) { uint value = ReadU32(data, group + 8) + (uint)codePoint - start; if (value <= ushort.MaxValue) glyph = (ushort)value; return true; } }
        return false;
    }

    private static bool TryMapFormat4(byte[] data, int offset, ushort codePoint, out ushort glyph)
    {
        glyph = 0; if (offset + 16 > data.Length) return false; int length = ReadU16(data, offset + 2); if (offset + length > data.Length) return false;
        int segments = ReadU16(data, offset + 6) / 2, ends = offset + 14, starts = ends + segments * 2 + 2, deltas = starts + segments * 2, ranges = deltas + segments * 2;
        for (int i = 0; i < segments; i++) if (codePoint >= ReadU16(data, starts + i * 2) && codePoint <= ReadU16(data, ends + i * 2))
        { short delta = ReadI16(data, deltas + i * 2); ushort range = ReadU16(data, ranges + i * 2); if (range == 0) glyph = (ushort)(codePoint + delta); else { int location = ranges + i * 2 + range + 2 * (codePoint - ReadU16(data, starts + i * 2)); if (location + 2 > offset + length) return false; ushort value = ReadU16(data, location); glyph = value == 0 ? (ushort)0 : (ushort)(value + delta); } return true; }
        return false;
    }

    private static uint Checksum(byte[] bytes)
    {
        uint sum = 0;
        byte[] word = new byte[4];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            Array.Clear(word);
            bytes.AsSpan(i, Math.Min(4, bytes.Length - i)).CopyTo(word);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(word));
        }
        return sum;
    }
    private static ushort ReadU16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    private static short ReadI16(byte[] data, int offset) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset, 2));
    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static void WriteU16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), value);
    private static void WriteU32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
}
