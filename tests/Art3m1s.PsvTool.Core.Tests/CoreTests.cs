using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Art3m1s.PsvTool.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
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
    public async Task RgbPngStaysRgbAndKeepsAncillaryChunksByteForByte()
    {
        string path = Path.Combine(_root, "rgb.png");
        using (Image<Rgb24> image = new(10, 6, new Rgb24(20, 40, 60)))
        {
            using MemoryStream encoded = new();
            image.Save(encoded, new PngEncoder { ColorType = PngColorType.Rgb });
            byte[] png = encoded.ToArray();
            byte[] customChunk = [0, 0, 14, 196, 0, 0, 14, 196, 1];
            int idatOffset = FindChunkOffset(png, "IDAT");
            using MemoryStream withMetadata = new();
            withMetadata.Write(png.AsSpan(0, idatOffset));
            WriteChunk(withMetadata, "pHYs", customChunk);
            withMetadata.Write(png.AsSpan(idatOffset));
            await File.WriteAllBytesAsync(path, withMetadata.ToArray());
        }

        await new PngProcessor().ResizeAsync(path, 0.5);

        byte[] result = await File.ReadAllBytesAsync(path);
        Assert.Equal(2, result[25]);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(16, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(20, 4)));
        Assert.Equal(new byte[] { 0, 0, 14, 196, 0, 0, 14, 196, 1 }, ReadChunk(result, "pHYs"));
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
    public async Task ConversionResizesPngInsidePfsAndLoosePng()
    {
        string input = Path.Combine(_root, "png-game");
        string output = Path.Combine(_root, "png-game-psv");
        string payloadDirectory = Path.Combine(_root, "png-payload");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(Path.Combine(payloadDirectory, "image", "bg"));

        byte[] png = CreateIndexedPng(8, 8, 2,
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255],
            [255, 150, 80, 0]);
        string archivedPng = Path.Combine(payloadDirectory, "image", "bg", "archived.png");
        await File.WriteAllBytesAsync(archivedPng, png);
        await File.WriteAllBytesAsync(Path.Combine(input, "loose.png"), png);

        ExtractedArchive source = new('8',
        [
            new PfsEntry(Encoding.UTF8.GetBytes("image/bg/archived.png"), "image/bg/archived.png", 0,
                checked((uint)png.Length), archivedPng)
        ]);
        PfsCodec codec = new();
        await codec.PackPf8Async(source, Path.Combine(input, "root.pfs"));

        await new ConversionService().ConvertAsync(new ConversionOptions(input, output, Ratio: 0.5,
            Categories: AssetCategories.Images));

        byte[] looseResult = await File.ReadAllBytesAsync(Path.Combine(output, "loose.png"));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(looseResult.AsSpan(16, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(looseResult.AsSpan(20, 4)));

        ExtractedArchive rebuilt = await codec.ExtractAsync(Path.Combine(output, "root.pfs"), Path.Combine(_root, "png-verify"));
        byte[] archivedResult = await File.ReadAllBytesAsync(rebuilt.Entries.Single().ExtractedPath);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(archivedResult.AsSpan(16, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(archivedResult.AsSpan(20, 4)));
    }

    [Fact]
    public async Task ConversionProcessesLooseAssetsAtLeastThreeFoldersDeep()
    {
        string input = Path.Combine(_root, "nested-game");
        string output = Path.Combine(_root, "nested-game-psv");
        string nestedAssets = Path.Combine(input, "arbitrary-name", "level-two", "level-three");
        Directory.CreateDirectory(nestedAssets);
        await File.WriteAllBytesAsync(Path.Combine(nestedAssets, "intro.dat"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(nestedAssets, "effect.ogv"), [4, 5, 6]);

        string payload = Path.Combine(_root, "required.bin");
        await File.WriteAllBytesAsync(payload, [7]);
        ExtractedArchive archive = new('8', [new PfsEntry(Encoding.UTF8.GetBytes("required.bin"), "required.bin", 0, 1, payload)]);
        await new PfsCodec().PackPf8Async(archive, Path.Combine(input, "root.pfs"));
        RecordingFfmpegProcessor ffmpeg = new();

        await new ConversionService(ffmpeg: ffmpeg).ConvertAsync(new ConversionOptions(input, output, Ratio: 0.5,
            Categories: AssetCategories.Animation | AssetCategories.Video));

        Assert.Equal(2, ffmpeg.Processed.Count);
        Assert.Contains(ffmpeg.Processed, item => item.Path.EndsWith(Path.Combine("arbitrary-name", "level-two", "level-three", "intro.dat"), StringComparison.OrdinalIgnoreCase) && item.ConvertDatToMp4);
        Assert.Contains(ffmpeg.Processed, item => item.Path.EndsWith(Path.Combine("arbitrary-name", "level-two", "level-three", "effect.ogv"), StringComparison.OrdinalIgnoreCase) && !item.ConvertDatToMp4);
        Assert.False(File.Exists(Path.Combine(output, "arbitrary-name", "level-two", "level-three", "intro.dat")));
        Assert.Equal([0x50], await File.ReadAllBytesAsync(Path.Combine(output, "arbitrary-name", "level-two", "level-three", "intro.mp4")));
        Assert.Equal([0x50], await File.ReadAllBytesAsync(Path.Combine(output, "arbitrary-name", "level-two", "level-three", "effect.ogv")));
    }

    [Theory]
    [InlineData(225, 225, 0.5, 112, 112)]
    [InlineData(1919, 1079, 0.75, 1439, 809)]
    [InlineData(1920, 1080, 0.5, 960, 540)]
    public void VideoDimensionsUseVisualNovelUpscalerTruncation(
        int width, int height, double ratio, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight),
            FfmpegProcessor.CalculateScaledDimensions(width, height, ratio));
    }

    [Fact]
    public async Task DatInsidePfsIsRepackedAsSameStemMp4()
    {
        string input = Path.Combine(_root, "pfs-dat-game");
        string output = Path.Combine(_root, "pfs-dat-game-psv");
        Directory.CreateDirectory(input);
        string dat = Path.Combine(_root, "opening.dat");
        await File.WriteAllBytesAsync(dat, [1, 2, 3]);
        ExtractedArchive source = new('8',
        [
            new PfsEntry(Encoding.UTF8.GetBytes("movie/opening.dat"), "movie/opening.dat", 0, 3, dat)
        ]);
        PfsCodec codec = new();
        await codec.PackPf8Async(source, Path.Combine(input, "root.pfs"));

        RecordingFfmpegProcessor ffmpeg = new();
        await new ConversionService(ffmpeg: ffmpeg).ConvertAsync(new ConversionOptions(input, output, Ratio: 0.5,
            Categories: AssetCategories.Video));

        ExtractedArchive rebuilt = await codec.ExtractAsync(Path.Combine(output, "root.pfs"), Path.Combine(_root, "pfs-dat-verify"));
        PfsEntry entry = Assert.Single(rebuilt.Entries);
        Assert.Equal("movie/opening.mp4", entry.Path);
        Assert.Equal([0x50], await File.ReadAllBytesAsync(entry.ExtractedPath));
        Assert.Contains(ffmpeg.Processed, item => item.ConvertDatToMp4);
    }

    [Fact]
    public async Task ArtemisTextRulesMatchVisualNovelUpscalerLineAndSavePathBehavior()
    {
        string ast = Path.Combine(_root, "sample.ast");
        await File.WriteAllTextAsync(ast,
            "astver = 2.0\r\n{\"bg\", ax=640, ay=360, x=-941, x2=1280}\r\nuntouched\r\n",
            new UTF8Encoding(false));
        await new ArtemisTextProcessor().ProcessAsync(ast, 0.5);
        Assert.Equal("astver = 2.0\r\n{\"bg\", ax=320, ay=180, x=-470, x2=640}\runtouched\r\n",
            await File.ReadAllTextAsync(ast));

        string ini = Path.Combine(_root, "visualnovel.ini");
        await File.WriteAllTextAsync(ini,
            "WIDTH = 1920\r\nHEIGHT = 1080\r\nSAVEPATH = savedata\r\n;SAVEPATH = maker\\title\r\n",
            new UTF8Encoding(false));
        await new ArtemisTextProcessor().ProcessAsync(ini, 0.5);
        Assert.Equal("WIDTH = 960\rHEIGHT = 540\r;SAVEPATH = savedata\r\nSAVEPATH = savedataHD\r\n",
            await File.ReadAllTextAsync(ini));
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

    private static int FindChunkOffset(byte[] png, string wanted)
    {
        int offset = 8;
        while (offset <= png.Length - 12)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset)));
            if (Encoding.ASCII.GetString(png, offset + 4, 4) == wanted) return offset;
            offset += length + 12;
        }
        throw new InvalidDataException($"PNG chunk {wanted} was not found.");
    }

    private static byte[] ReadChunk(byte[] png, string wanted)
    {
        int offset = FindChunkOffset(png, wanted);
        int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset)));
        return png.AsSpan(offset + 8, length).ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type); Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length); output.Write(length); output.Write(typeBytes); output.Write(data);
        uint crc = 0xffffffff; foreach (byte value in typeBytes.Concat(data)) { crc ^= value; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1)); }
        Span<byte> crcBytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc); output.Write(crcBytes);
    }

    private sealed class RecordingFfmpegProcessor : IFfmpegProcessor
    {
        public List<(string Path, bool ConvertDatToMp4)> Processed { get; } = [];
        public async Task ResizeAsync(string path, double ratio, bool convertDatToMp4 = false, CancellationToken cancellationToken = default)
        {
            Assert.Equal(0.5, ratio);
            lock (Processed) Processed.Add((path, convertDatToMp4));
            string output = convertDatToMp4 && Path.GetExtension(path).Equals(".dat", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(path, ".mp4") : path;
            await File.WriteAllBytesAsync(output, [0x50], cancellationToken);
            if (!output.Equals(path, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
