using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Art3m1s.PsvTool.Core;
using Xunit;

namespace Art3m1s.PsvTool.Core.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "art3m1s-tests-" + Guid.NewGuid().ToString("N"));
    public CoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Pf8RoundTripPreservesNamesOrderAndBytes()
    {
        string extracted = Path.Combine(_root, "files"); Directory.CreateDirectory(Path.Combine(extracted, "system"));
        string first = Path.Combine(extracted, "system", "first.iet"); string second = Path.Combine(extracted, "日本語.txt");
        await File.WriteAllBytesAsync(first, [1, 2, 3, 4]); await File.WriteAllTextAsync(second, "内容", Encoding.UTF8);
        ExtractedArchive source = new('8',
        [
            new PfsEntry(Encoding.UTF8.GetBytes("system/first.iet"), "system/first.iet", 0, 4, first),
            new PfsEntry(Encoding.UTF8.GetBytes("日本語.txt"), "日本語.txt", 0, 0, second)
        ]);
        string archive = Path.Combine(_root, "root.pfs.010");
        PfsCodec codec = new(); await codec.PackPf8Async(source, archive);
        ExtractedArchive result = await codec.ExtractAsync(archive, Path.Combine(_root, "out"));
        Assert.Equal(["system/first.iet", "日本語.txt"], result.Entries.Select(item => item.Path));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(result.Entries[0].ExtractedPath));
        Assert.Equal("内容", await File.ReadAllTextAsync(result.Entries[1].ExtractedPath));
    }

    [Fact]
    public async Task ScannerAcceptsSparseIndependentPfsNames()
    {
        PfsCodec codec = new(); string payload = Path.Combine(_root, "a.bin"); await File.WriteAllBytesAsync(payload, [42]);
        ExtractedArchive source = new('8', [new PfsEntry(Encoding.UTF8.GetBytes("a.bin"), "a.bin", 0, 1, payload)]);
        foreach (string name in new[] { "root.pfs", "root.pfs.000", "root.pfs.001", "root.pfs.010", "voice.pfs.999" })
            await codec.PackPf8Async(source, Path.Combine(_root, name));
        await File.WriteAllTextAsync(Path.Combine(_root, "root.pfs.002"), "not pfs");
        ScanResult result = await new ProjectScanner().ScanAsync(_root);
        Assert.Equal(5, result.Archives.Count);
        Assert.Contains(result.Archives, item => item.FileName == "root.pfs.010");
        Assert.DoesNotContain(result.Archives, item => item.FileName == "root.pfs.002");
    }

    [Fact]
    public async Task VitaSectionInheritsWindowsCharsetAndIsIdempotent()
    {
        string path = Path.Combine(_root, "system.ini");
        await File.WriteAllTextAsync(path, "[WINDOWS]\r\nCHARSET = UTF-8\r\nWIDTH = 1920\r\nHEIGHT = 1080\r\n", new UTF8Encoding(false));
        VitaIniProcessor processor = new(); await processor.EnsureVitaSectionAsync(path); byte[] once = await File.ReadAllBytesAsync(path);
        await processor.EnsureVitaSectionAsync(path); byte[] twice = await File.ReadAllBytesAsync(path);
        string text = Encoding.UTF8.GetString(twice);
        Assert.Contains("[VITA]\r\n", text); Assert.Contains("CHARSET = UTF-8", text);
        Assert.Contains("WIDTH = 960", text); Assert.Contains("HEIGHT = 540", text); Assert.Equal(once, twice);
    }

    [Fact]
    public async Task ExistingVitaSectionIsNeverChanged()
    {
        string path = Path.Combine(_root, "system.ini"); byte[] original = Encoding.UTF8.GetBytes("[VITA]\nWIDTH=123\n");
        await File.WriteAllBytesAsync(path, original); await new VitaIniProcessor().EnsureVitaSectionAsync(path);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task IndexedPngKeepsPaletteTransparencyAndBitDepth()
    {
        string path = Path.Combine(_root, "palette.png");
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]; byte[] transparency = [255, 150, 80, 0];
        await File.WriteAllBytesAsync(path, CreateIndexedPng(8, 8, 2, palette, transparency));
        (byte depthBefore, byte[] plteBefore, byte[] trnsBefore) = ReadPngDetails(await File.ReadAllBytesAsync(path));
        await new PngProcessor().ResizeAsync(path, 0.5);
        byte[] result = await File.ReadAllBytesAsync(path); (byte depthAfter, byte[] plteAfter, byte[] trnsAfter) = ReadPngDetails(result);
        Assert.Equal(depthBefore, depthAfter); Assert.Equal(plteBefore, plteAfter); Assert.Equal(trnsBefore, trnsAfter);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(16, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(20, 4)));
    }

    [Fact]
    public async Task ConversionKeepsSparseArchivesIndependentAndCopiesLooseFiles()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string input = Path.Combine(_root, "game");
        string output = Path.Combine(_root, "game-psv");
        Directory.CreateDirectory(input);
        string payload = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(payload, [9, 8, 7]);
        ExtractedArchive source = new('8', [new PfsEntry(Encoding.UTF8.GetBytes("data/payload.bin"), "data/payload.bin", 0, 3, payload)]);
        PfsCodec codec = new();
        await codec.PackPf8Async(source, Path.Combine(input, "root.pfs"));
        await codec.PackPf8Async(source, Path.Combine(input, "root.pfs.010"));
        await File.WriteAllTextAsync(Path.Combine(input, "system.ini"), "[WINDOWS]\nCHARSET=Shift_JIS\nWIDTH=1920\nHEIGHT=1080\n", Encoding.GetEncoding(932));
        await File.WriteAllBytesAsync(Path.Combine(input, "untouched.bin"), [4, 5, 6]);

        await new ConversionService().ConvertAsync(new ConversionOptions(input, output, Categories: AssetCategories.None));

        Assert.True(File.Exists(Path.Combine(output, "root.pfs")));
        Assert.True(File.Exists(Path.Combine(output, "root.pfs.010")));
        Assert.False(File.Exists(Path.Combine(output, "root.pfs.000")));
        Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(Path.Combine(output, "untouched.bin")));
        string ini = Encoding.GetEncoding(932).GetString(await File.ReadAllBytesAsync(Path.Combine(output, "system.ini")));
        Assert.Contains("[VITA]", ini);
        ExtractedArchive rebuilt = await codec.ExtractAsync(Path.Combine(output, "root.pfs.010"), Path.Combine(_root, "verify"));
        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(rebuilt.Entries.Single().ExtractedPath));
    }

    [Fact]
    public async Task TrueTypeFontSubsettingProducesAValidSmallerSfntWhenAFontIsAvailable()
    {
        string? source = new[]
        {
            @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf"
        }.FirstOrDefault(File.Exists);
        if (source is null) return;
        string target = Path.Combine(_root, "font.ttf"); File.Copy(source, target);
        long before = new FileInfo(target).Length;
        bool changed = await new FontSubsetProcessor().SubsetAsync(target, FontSubsetProfile.Japanese, new HashSet<int> { 'A', '日' });
        byte[] result = await File.ReadAllBytesAsync(target);
        Assert.True(changed); Assert.True(result.Length <= before); Assert.True(result.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 1, 0, 0 }) || Encoding.ASCII.GetString(result, 0, 4) == "true");
    }

    private static byte[] CreateIndexedPng(int width, int height, byte bitDepth, byte[] palette, byte[] transparency)
    {
        using MemoryStream result = new(); result.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] ihdr = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height); ihdr[8] = bitDepth; ihdr[9] = 3;
        WriteChunk(result, "IHDR", ihdr); WriteChunk(result, "PLTE", palette); WriteChunk(result, "tRNS", transparency);
        int rowBytes = (width * bitDepth + 7) / 8; byte[] raw = new byte[height * (rowBytes + 1)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        { int value = (x + y) % 4; int offset = y * (rowBytes + 1) + 1 + x / 4; raw[offset] |= (byte)(value << (6 - (x % 4) * 2)); }
        using MemoryStream compressed = new(); using (ZLibStream z = new(compressed, CompressionLevel.SmallestSize, true)) z.Write(raw);
        WriteChunk(result, "IDAT", compressed.ToArray()); WriteChunk(result, "IEND", []); return result.ToArray();
    }

    private static (byte, byte[], byte[]) ReadPngDetails(byte[] png)
    {
        byte depth = png[24]; byte[] palette = [], transparency = []; int offset = 8;
        while (offset < png.Length) { int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset)); string type = Encoding.ASCII.GetString(png, offset + 4, 4); byte[] data = png.AsSpan(offset + 8, length).ToArray(); if (type == "PLTE") palette = data; if (type == "tRNS") transparency = data; offset += 12 + length; }
        return (depth, palette, transparency);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type); Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length); output.Write(length); output.Write(typeBytes); output.Write(data);
        uint crc = 0xffffffff; foreach (byte value in typeBytes.Concat(data)) { crc ^= value; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1)); }
        Span<byte> crcBytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc); output.Write(crcBytes);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
