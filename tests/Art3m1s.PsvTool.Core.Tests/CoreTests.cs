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
    public async Task Gray8MaskStaysEightBitGrayscaleWithoutAlpha()
    {
        string path = Path.Combine(_root, "mask-gray8.png");
        using (Image<L8> image = new(12, 8))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<L8> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++) row[x] = new L8(checked((byte)(x * 17 + y * 3)));
                }
            });
            await image.SaveAsync(path, new PngEncoder
            {
                ColorType = PngColorType.Grayscale,
                BitDepth = PngBitDepth.Bit8
            });
        }

        await new PngProcessor().ResizeAsync(path, 0.5);

        byte[] result = await File.ReadAllBytesAsync(path);
        Assert.Equal(8, result[24]);
        Assert.Equal(0, result[25]);
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(16, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(20, 4)));
        using Image<L8> decoded = await Image.LoadAsync<L8>(path);
        Assert.Equal(new Size(6, 4), decoded.Size);
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
        await File.WriteAllBytesAsync(Path.Combine(nestedAssets, "opening.wmv"), [7, 8, 9]);
        await File.WriteAllBytesAsync(Path.Combine(nestedAssets, "effect.ogv"), [4, 5, 6]);

        string payload = Path.Combine(_root, "required.bin");
        await File.WriteAllBytesAsync(payload, [7]);
        ExtractedArchive archive = new('8', [new PfsEntry(Encoding.UTF8.GetBytes("required.bin"), "required.bin", 0, 1, payload)]);
        await new PfsCodec().PackPf8Async(archive, Path.Combine(input, "root.pfs"));
        RecordingFfmpegProcessor ffmpeg = new();

        await new ConversionService(ffmpeg: ffmpeg).ConvertAsync(new ConversionOptions(input, output, Ratio: 0.5,
            Categories: AssetCategories.Animation | AssetCategories.Video));

        Assert.Equal(3, ffmpeg.Processed.Count);
        Assert.Contains(ffmpeg.Processed, item => item.Path.EndsWith(Path.Combine("arbitrary-name", "level-two", "level-three", "intro.dat"), StringComparison.OrdinalIgnoreCase) && item.ConvertToH264Mp4);
        Assert.Contains(ffmpeg.Processed, item => item.Path.EndsWith(Path.Combine("arbitrary-name", "level-two", "level-three", "opening.wmv"), StringComparison.OrdinalIgnoreCase) && item.ConvertToH264Mp4);
        Assert.Contains(ffmpeg.Processed, item => item.Path.EndsWith(Path.Combine("arbitrary-name", "level-two", "level-three", "effect.ogv"), StringComparison.OrdinalIgnoreCase) && !item.ConvertToH264Mp4);
        Assert.False(File.Exists(Path.Combine(output, "arbitrary-name", "level-two", "level-three", "intro.dat")));
        Assert.False(File.Exists(Path.Combine(output, "arbitrary-name", "level-two", "level-three", "opening.wmv")));
        Assert.Equal([0x50], await File.ReadAllBytesAsync(Path.Combine(output, "arbitrary-name", "level-two", "level-three", "intro.mp4")));
        Assert.Equal([0x50], await File.ReadAllBytesAsync(Path.Combine(output, "arbitrary-name", "level-two", "level-three", "opening.mp4")));
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

    [Theory]
    [InlineData("yuv420p", "yuv420p")]
    [InlineData("yuv422p", "yuv422p")]
    [InlineData("yuv444p", "yuv444p")]
    [InlineData("", "yuv420p")]
    [InlineData("rgb24", "yuv420p")]
    public void TheoraPixelFormatIsPreservedWhenSupported(string input, string expected) =>
        Assert.Equal(expected, FfmpegProcessor.NormalizeTheoraPixelFormat(input));

    [Fact]
    public async Task DatInsidePfsIsPreservedWithOriginalNameAndBytes()
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
        Assert.Equal("movie/opening.dat", entry.Path);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(entry.ExtractedPath));
        Assert.Empty(ffmpeg.Processed);
    }

    [Fact]
    public async Task PfsVideosAreIgnoredByDefaultWhileLooseVideosAreStillConverted()
    {
        string input = Path.Combine(_root, "pfs-video-game");
        string output = Path.Combine(_root, "pfs-video-game-psv");
        Directory.CreateDirectory(input);
        string payload = Path.Combine(_root, "protected-video.bin");
        byte[] original = [0xee, 0x8d, 0xdd, 0x05];
        await File.WriteAllBytesAsync(payload, original);
        string[] extensions = [".wmv", ".dat", ".mp4", ".avi", ".mpg", ".mkv"];
        ExtractedArchive source = new('8', extensions.Select(extension =>
            new PfsEntry(Encoding.UTF8.GetBytes($"movie/mb/opening{extension}"),
                $"movie/mb/opening{extension}", 0, (uint)original.Length, payload)).ToArray());
        PfsCodec codec = new();
        await codec.PackPf8Async(source, Path.Combine(input, "root.pfs"));
        await File.WriteAllBytesAsync(Path.Combine(input, "loose.mp4"), [1, 2, 3]);

        RecordingFfmpegProcessor ffmpeg = new();
        await new ConversionService(ffmpeg: ffmpeg).ConvertAsync(new ConversionOptions(input, output,
            Ratio: 0.5, Categories: AssetCategories.Animation | AssetCategories.Video));

        Assert.Single(ffmpeg.Processed);
        Assert.EndsWith("loose.mp4", ffmpeg.Processed[0].Path, StringComparison.OrdinalIgnoreCase);
        ExtractedArchive rebuilt = await codec.ExtractAsync(Path.Combine(output, "root.pfs"),
            Path.Combine(_root, "pfs-video-verify"));
        Assert.Equal(extensions.Length, rebuilt.Entries.Count);
        foreach (PfsEntry entry in rebuilt.Entries)
            Assert.Equal(original, await File.ReadAllBytesAsync(entry.ExtractedPath));
    }

    [Fact]
    public async Task OggVideoInsidePfsIsStillProcessedAsAnimation()
    {
        string input = Path.Combine(_root, "pfs-ogv-game");
        string output = Path.Combine(_root, "pfs-ogv-game-psv");
        Directory.CreateDirectory(input);
        string ogv = Path.Combine(_root, "opening.ogv");
        await File.WriteAllBytesAsync(ogv, [1, 2, 3]);
        ExtractedArchive source = new('8',
        [
            new PfsEntry(Encoding.UTF8.GetBytes("movie/opening.ogv"), "movie/opening.ogv", 0, 3, ogv)
        ]);
        PfsCodec codec = new();
        await codec.PackPf8Async(source, Path.Combine(input, "root.pfs"));

        RecordingFfmpegProcessor ffmpeg = new();
        await new ConversionService(ffmpeg: ffmpeg).ConvertAsync(new ConversionOptions(input, output,
            Ratio: 0.5, Categories: AssetCategories.Animation));

        Assert.Single(ffmpeg.Processed);
        Assert.EndsWith("opening.ogv", ffmpeg.Processed[0].Path, StringComparison.OrdinalIgnoreCase);
        Assert.False(ffmpeg.Processed[0].ConvertToH264Mp4);
    }

    [Fact]
    public async Task OgvAnimationsRunOneAtATime()
    {
        string input = Path.Combine(_root, "ogv-serial-game");
        string output = Path.Combine(_root, "ogv-serial-game-psv");
        Directory.CreateDirectory(input);
        string payload = Path.Combine(_root, "required-for-ogv.bin");
        await File.WriteAllBytesAsync(payload, [1]);
        await new PfsCodec().PackPf8Async(new ExtractedArchive('8',
            [new PfsEntry(Encoding.UTF8.GetBytes("required.bin"), "required.bin", 0, 1, payload)]),
            Path.Combine(input, "root.pfs"));
        foreach (string name in new[] { "a.ogv", "b.ogv", "c.ogv" })
            await File.WriteAllBytesAsync(Path.Combine(input, name), [1, 2, 3]);
        ConcurrencyRecordingFfmpegProcessor ffmpeg = new();

        await new ConversionService(ffmpeg: ffmpeg).ConvertAsync(new ConversionOptions(input, output,
            Ratio: 0.5, Categories: AssetCategories.Animation, MaxParallelism: 8));

        Assert.Equal(3, ffmpeg.Processed);
        Assert.Equal(1, ffmpeg.MaxConcurrent);
    }

    [Fact]
    public async Task NonVideoDatIsKeptByteForByteWhenProbeRejectsIt()
    {
        string dat = Path.Combine(_root, "index.dat");
        byte[] original = [0x41, 0x52, 0x54, 0x45, 0x4d, 0x49, 0x53];
        await File.WriteAllBytesAsync(dat, original);

        FfmpegProcessor processor = new(ffmpeg: "dotnet", ffprobe: "dotnet");
        await processor.ResizeAsync(dat, 0.75, convertToH264Mp4: true);

        Assert.Equal(original, await File.ReadAllBytesAsync(dat));
        Assert.False(File.Exists(Path.ChangeExtension(dat, ".mp4")));
    }

    [Fact]
    public async Task CompletedFfmpegOutputIsRetriedWhenWindowsTemporarilyLocksIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        string source = Path.Combine(_root, ".opening.transcoded.ogv");
        string destination = Path.Combine(_root, "opening.ogv");
        await File.WriteAllBytesAsync(source, [4, 5, 6]);
        await File.WriteAllBytesAsync(destination, [1, 2, 3]);

        using FileStream temporaryLock = new(source, FileMode.Open, FileAccess.Read, FileShare.None);
        Task move = FfmpegProcessor.MoveReplacingWithRetryAsync(source, destination, CancellationToken.None);
        // Hold the file longer than the previous retry window to cover antivirus/indexer
        // locks observed after real FFmpeg Theora output on Windows.
        await Task.Delay(2500);
        temporaryLock.Dispose();
        await move;

        Assert.False(File.Exists(source));
        Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task NewMp4FallsBackToCopyWhenScannerDeniesRename()
    {
        if (!OperatingSystem.IsWindows()) return;
        string source = Path.Combine(_root, ".opening.transcoded.mp4");
        string destination = Path.Combine(_root, "opening.mp4");
        byte[] payload = Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(source, payload);

        using FileStream scannerLock = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task move = FfmpegProcessor.MoveReplacingWithRetryAsync(
            source, destination, overwrite: false, CancellationToken.None);
        await Task.Delay(150);
        scannerLock.Dispose();
        await move;

        Assert.False(File.Exists(source));
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
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
    public async Task ArtemisTextRulesScaleEmoteGeometryWithoutChangingBehaviorValues()
    {
        string table = Path.Combine(_root, "emote.tbl");
        await File.WriteAllTextAsync(table,
            "init = {\r\n"
            + "\tgame_scale = { 1280, 720, },\r\n"
            + "\tsystem = {\r\n\t\tgame_width = 1280,\r\n\t\tgame_height = 720,\r\n\t},\r\n"
            + "\temote = {\r\n"
            + "\t\tbase = { [\"かずは\"] = { \"kaz\", 1, 1, }, },\r\n"
            + "\t\tkaz = {\r\n"
            + "\t\t\tfa = { 0.33, -380, 95, 1920, 2048, },\r\n"
            + "\t\t\tno = { 0.59, -292, -125, 1920, 2048, },\r\n"
            + "\t\t},\r\n\t},\r\n"
            + "\temote_autoexec = \"待機\",\r\n}\r\n"
            + "unrelated = { 1, -10, 20, 30, 40, }\r\n",
            new UTF8Encoding(false));

        await new ArtemisTextProcessor().ProcessAsync(table, 0.75);

        string output = await File.ReadAllTextAsync(table);
        Assert.Contains("\tgame_scale = { 960, 540, },", output, StringComparison.Ordinal);
        Assert.Contains("\t\tgame_width = 960,", output, StringComparison.Ordinal);
        Assert.Contains("\t\tgame_height = 540,", output, StringComparison.Ordinal);
        Assert.Contains("fa = { 0.33, -285, 71, 1440, 1536, },", output, StringComparison.Ordinal);
        Assert.Contains("no = { 0.59, -219, -93, 1440, 1536, },", output, StringComparison.Ordinal);
        Assert.Contains("[\"かずは\"] = { \"kaz\", 1, 1, },", output, StringComparison.Ordinal);
        Assert.Contains("emote_autoexec = \"待機\"", output, StringComparison.Ordinal);
        Assert.Contains("unrelated = { 1, -10, 20, 30, 40, }", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtemisTextRulesScaleLiteralMulposGeometry()
    {
        string lua = Path.Combine(_root, "e-mote.lua");
        await File.WriteAllTextAsync(lua,
            "local y = mulpos(80)\r\n"
            + "local x = mulpos(-40)\r\n"
            + "local dynamic = mulpos(v[2])\r\n"
            + "lyc2{ width=\"16\", height=\"4\" }\r\n"
            + "em:setScale(0.33, 0, 0)\r\n",
            new UTF8Encoding(false));

        await new ArtemisTextProcessor().ProcessAsync(lua, 0.75);

        string output = await File.ReadAllTextAsync(lua);
        Assert.Contains("local y = mulpos(60)", output, StringComparison.Ordinal);
        Assert.Contains("local x = mulpos(-30)", output, StringComparison.Ordinal);
        Assert.Contains("local dynamic = mulpos(v[2])", output, StringComparison.Ordinal);
        Assert.Contains("lyc2{ width=\"12\", height=\"3\" }", output, StringComparison.Ordinal);
        Assert.Contains("em:setScale(0.33, 0, 0)", output, StringComparison.Ordinal);
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

    [Fact]
    public async Task CffOpenTypeSubsettingProducesAValidSmallerOtfWhenAFontIsAvailable()
    {
        string? source = new[]
        {
            @"C:\Windows\Fonts\SourceHanSansJP-Bold.otf",
            @"C:\Windows\Fonts\A-OTF-FolkPro-Medium.otf",
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.otf",
            "/System/Library/Fonts/Supplemental/Arial Unicode.ttf"
        }.FirstOrDefault(path => File.Exists(path) && File.ReadAllBytes(path).AsSpan().StartsWith("OTTO"u8));
        if (source is null) return;
        string target = Path.Combine(_root, "font.otf");
        File.Copy(source, target);
        long before = new FileInfo(target).Length;

        bool changed = await new FontSubsetProcessor().SubsetAsync(target,
            FontSubsetProfile.SimplifiedChinese, new HashSet<int> { 'A', '中' });

        byte[] result = await File.ReadAllBytesAsync(target);
        Assert.True(changed);
        Assert.True(result.AsSpan().StartsWith("OTTO"u8));
        Assert.True(result.Length < before);
        Assert.True(HarfBuzzSubsetter.Subset(result, new HashSet<int> { 'A', '中' }).Length > 0);
    }

    [Fact]
    public async Task ConversionDiscoversBothTtfAndOtfFonts()
    {
        string input = Path.Combine(_root, "font-discovery-game");
        string output = Path.Combine(_root, "font-discovery-game-psv");
        Directory.CreateDirectory(input);
        string payload = Path.Combine(_root, "required-for-fonts.bin");
        await File.WriteAllBytesAsync(payload, [1]);
        await new PfsCodec().PackPf8Async(new ExtractedArchive('8',
            [new PfsEntry(Encoding.UTF8.GetBytes("required.bin"), "required.bin", 0, 1, payload)]),
            Path.Combine(input, "root.pfs"));
        await File.WriteAllBytesAsync(Path.Combine(input, "body.ttf"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(input, "title.otf"), [2]);
        RecordingFontSubsetProcessor fonts = new();

        await new ConversionService(fonts: fonts).ConvertAsync(new ConversionOptions(input, output,
            Categories: AssetCategories.None, SubsetFonts: true));

        Assert.Equal(2, fonts.Paths.Count);
        Assert.Contains(fonts.Paths, path => path.EndsWith("body.ttf", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fonts.Paths, path => path.EndsWith("title.otf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PsbInspectorValidatesPlainEmoteResourceTables()
    {
        string path = Path.Combine(_root, "character.psb");
        await File.WriteAllBytesAsync(path, CreateMinimalPsbV3());

        PsbInspection result = await new PsbProcessor().InspectAsync(path);

        Assert.Equal((ushort)3, result.Version);
        Assert.Equal(0, result.ResourceCount);
        Assert.Equal(0, result.ExtraResourceCount);
        Assert.False(result.BodyEncrypted);
    }

    [Fact]
    public async Task EmotePsbInsidePfsIsDispatchedToPsbProcessor()
    {
        string input = Path.Combine(_root, "psb-game");
        string output = Path.Combine(_root, "psb-game-psv");
        Directory.CreateDirectory(input);
        byte[] original = CreateMinimalPsbV3();
        string psb = Path.Combine(_root, "character.psb");
        await File.WriteAllBytesAsync(psb, original);
        PfsCodec codec = new();
        await codec.PackPf8Async(new ExtractedArchive('8',
        [
            new PfsEntry(Encoding.UTF8.GetBytes("image/fg/character.psb"),
                "image/fg/character.psb", 0, (uint)original.Length, psb)
        ]), Path.Combine(input, "root.pfs"));

        RecordingPsbProcessor psbProcessor = new();
        await new ConversionService(psb: psbProcessor).ConvertAsync(new ConversionOptions(input, output,
            Ratio: 0.5, Categories: AssetCategories.Animation));

        Assert.Single(psbProcessor.Paths);
        Assert.Equal(0.5, psbProcessor.Paths[0].Ratio);
        ExtractedArchive rebuilt = await codec.ExtractAsync(Path.Combine(output, "root.pfs"),
            Path.Combine(_root, "psb-verify"));
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(rebuilt.Entries).ExtractedPath));
    }

    private static byte[] CreateMinimalPsbV3()
    {
        byte[] result = new byte[57];
        "PSB\0"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), 44);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), 44); // names
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 47); // strings
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), 56); // string data
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), 50); // resource offsets
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28), 53); // resource lengths
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32), 56); // resource data
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36), 56); // root entry
        uint checksum = Adler32ForTest(result.AsSpan(8, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40), checksum);
        new byte[] { 0x0d, 0, 0x0c }.CopyTo(result, 44);
        new byte[] { 0x0d, 0, 0x0c }.CopyTo(result, 47);
        new byte[] { 0x0d, 0, 0x0c }.CopyTo(result, 50);
        new byte[] { 0x0d, 0, 0x0c }.CopyTo(result, 53);
        result[56] = 0x21;
        return result;
    }

    private static uint Adler32ForTest(ReadOnlySpan<byte> bytes)
    {
        const uint modulus = 65_521;
        uint a = 1, b = 0;
        foreach (byte value in bytes) { a = (a + value) % modulus; b = (b + a) % modulus; }
        return (b << 16) | a;
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
        public List<(string Path, bool ConvertToH264Mp4)> Processed { get; } = [];
        public async Task ResizeAsync(string path, double ratio, bool convertToH264Mp4 = false, CancellationToken cancellationToken = default)
        {
            Assert.Equal(0.5, ratio);
            lock (Processed) Processed.Add((path, convertToH264Mp4));
            string output = convertToH264Mp4
                ? Path.ChangeExtension(path, ".mp4") : path;
            await File.WriteAllBytesAsync(output, [0x50], cancellationToken);
            if (!output.Equals(path, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        }
    }

    private sealed class RecordingPsbProcessor : IPsbProcessor
    {
        public List<(string Path, double Ratio)> Paths { get; } = [];
        public Task<PsbInspection> InspectAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PsbInspection(3, 0, 0, 0, false));
        public Task ResizeAsync(string path, double ratio, CancellationToken cancellationToken = default)
        {
            Paths.Add((path, ratio));
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrencyRecordingFfmpegProcessor : IFfmpegProcessor
    {
        private int _concurrent;
        public int Processed;
        public int MaxConcurrent;

        public async Task ResizeAsync(string path, double ratio, bool convertToH264Mp4 = false,
            CancellationToken cancellationToken = default)
        {
            int current = Interlocked.Increment(ref _concurrent);
            int observed;
            do
            {
                observed = Volatile.Read(ref MaxConcurrent);
                if (current <= observed) break;
            } while (Interlocked.CompareExchange(ref MaxConcurrent, current, observed) != observed);
            try { await Task.Delay(75, cancellationToken); Interlocked.Increment(ref Processed); }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
    }

    private sealed class RecordingFontSubsetProcessor : IFontSubsetProcessor
    {
        public List<string> Paths { get; } = [];

        public Task<bool> SubsetAsync(string path, FontSubsetProfile profile, IReadOnlySet<int> usedCodePoints,
            CancellationToken cancellationToken = default)
        {
            lock (Paths) Paths.Add(path);
            return Task.FromResult(true);
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
