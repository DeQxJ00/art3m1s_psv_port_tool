using System.Text;

namespace Art3m1s.PsvTool.Core;

public interface IConversionService
{
    Task ConvertAsync(
        ConversionOptions options,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ConversionService : IConversionService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ini", ".tbl", ".ipt", ".ast", ".lua" };
    private static readonly HashSet<string> AnimationExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ogv" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wmv", ".dat", ".mp4", ".avi", ".mpg", ".mkv" };

    private readonly IProjectScanner _scanner;
    private readonly IPfsCodec _pfs;
    private readonly IPngProcessor _png;
    private readonly ITextProcessor _text;
    private readonly IVitaIniProcessor _vita;
    private readonly IFfmpegProcessor _ffmpeg;
    private readonly IFontSubsetProcessor _fonts;

    public ConversionService(
        IProjectScanner? scanner = null,
        IPfsCodec? pfs = null,
        IPngProcessor? png = null,
        ITextProcessor? text = null,
        IVitaIniProcessor? vita = null,
        IFfmpegProcessor? ffmpeg = null,
        IFontSubsetProcessor? fonts = null)
    {
        _scanner = scanner ?? new ProjectScanner();
        _pfs = pfs ?? new PfsCodec();
        _png = png ?? new PngProcessor();
        _text = text ?? new ArtemisTextProcessor();
        _vita = vita ?? new VitaIniProcessor();
        _ffmpeg = ffmpeg ?? new FfmpegProcessor();
        _fonts = fonts ?? new FontSubsetProcessor();
    }

    public async Task ConvertAsync(
        ConversionOptions options,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        ScanResult scan = await _scanner.ScanAsync(options.InputDirectory, cancellationToken);
        if (scan.Archives.Count == 0)
            throw new InvalidDataException("No valid PFS archives were found.");
        if (Directory.Exists(options.OutputDirectory) && !options.OverwriteExisting)
            throw new IOException("Output directory already exists.");

        string outputFull = Path.GetFullPath(options.OutputDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string parent = Path.GetDirectoryName(outputFull) ?? throw new InvalidOperationException("Output has no parent directory.");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".{Path.GetFileName(outputFull)}.art3m1s-{Guid.NewGuid():N}");
        string backup = Path.Combine(parent, $".{Path.GetFileName(outputFull)}.backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            progress?.Report(new ConversionProgress(0, "copy"));
            await CopyLooseFilesAsync(options.InputDirectory, staging, scan.Archives, cancellationToken);

            int total = scan.Archives.Count + 1;
            for (int index = 0; index < scan.Archives.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PfsFileInfo archive = scan.Archives[index];
                progress?.Report(new ConversionProgress(100d * index / total, "extract", archive.FileName));
                await ConvertArchiveAsync(archive, staging, options, progress, index, total, cancellationToken);
            }

            progress?.Report(new ConversionProgress(100d * scan.Archives.Count / total, "loose"));
            await ProcessTreeAsync(staging, options, progress, cancellationToken, skipPfs: true,
                progressStart: 100d * scan.Archives.Count / total, progressSpan: 100d / total);
            CommitDirectory(staging, outputFull, backup, options.OverwriteExisting);
            progress?.Report(new ConversionProgress(100, "complete"));
        }
        finally
        {
            SafeDeleteTaskDirectory(staging, parent);
            SafeDeleteTaskDirectory(backup, parent);
        }
    }

    private async Task ConvertArchiveAsync(
        PfsFileInfo archive,
        string staging,
        ConversionOptions options,
        IProgress<ConversionProgress>? progress,
        int archiveIndex,
        int totalUnits,
        CancellationToken cancellationToken)
    {
        string workParent = Path.Combine(staging, ".art3m1s-work");
        string work = Path.Combine(workParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            ExtractedArchive extracted = await _pfs.ExtractAsync(archive.Path, work, options.NameEncoding, cancellationToken);
            await ProcessTreeAsync(work, options, progress, cancellationToken, skipPfs: false,
                progressStart: 100d * (archiveIndex + 0.05) / totalUnits,
                progressSpan: 100d * 0.85 / totalUnits);
            string destination = Path.Combine(staging, archive.FileName);
            string temporary = destination + ".packing";
            progress?.Report(new ConversionProgress(100d * (archiveIndex + 0.9) / totalUnits, "pack", archive.FileName));
            await _pfs.PackPf8Async(extracted, temporary, cancellationToken);
            File.Move(temporary, destination, true);
        }
        finally
        {
            SafeDeleteTaskDirectory(work, workParent);
            if (Directory.Exists(workParent) && !Directory.EnumerateFileSystemEntries(workParent).Any())
                Directory.Delete(workParent);
        }
    }

    private async Task ProcessTreeAsync(
        string root,
        ConversionOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken,
        bool skipPfs,
        double progressStart,
        double progressSpan)
    {
        string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !skipPfs || !IsPfsFileName(Path.GetFileName(path)))
            .ToArray();
        IReadOnlySet<int> usedCodePoints = options.SubsetFonts ? await CollectUsedCodePointsAsync(files, cancellationToken) : new HashSet<int>();
        using SemaphoreSlim videoSlots = new(2);
        int completed = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            MaxDegreeOfParallelism = options.EffectiveParallelism,
            CancellationToken = cancellationToken
        }, async (path, token) =>
        {
            string extension = Path.GetExtension(path);
            if (TextExtensions.Contains(extension) && options.Categories.HasFlag(AssetCategories.Text))
                await _text.ProcessAsync(path, options.Ratio, token);
            else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && options.Categories.HasFlag(AssetCategories.Images))
                await _png.ResizeAsync(path, options.Ratio, token);
            else if (options.SubsetFonts && extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase))
                await _fonts.SubsetAsync(path, options.FontProfile, usedCodePoints, token);
            else if ((AnimationExtensions.Contains(extension) && options.Categories.HasFlag(AssetCategories.Animation)) ||
                     (VideoExtensions.Contains(extension) && options.Categories.HasFlag(AssetCategories.Video)))
            {
                await videoSlots.WaitAsync(token);
                try { await _ffmpeg.ResizeAsync(path, options.Ratio, token); }
                finally { videoSlots.Release(); }
            }

            if (Path.GetFileName(path).Equals("system.ini", StringComparison.OrdinalIgnoreCase))
                await _vita.EnsureVitaSectionAsync(path, token);
            int done = Interlocked.Increment(ref completed);
            double fraction = files.Length == 0 ? 1 : (double)done / files.Length;
            progress?.Report(new ConversionProgress(progressStart + progressSpan * fraction, "resource", Entry: Path.GetRelativePath(root, path)));
        });
    }

    private static async Task<IReadOnlySet<int>> CollectUsedCodePointsAsync(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        HashSet<int> result = [];
        foreach (string path in files.Where(path => TextExtensions.Contains(Path.GetExtension(path))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                string text = TextEncoding.Detect(bytes).Encoding.GetString(bytes);
                foreach (System.Text.Rune rune in text.EnumerateRunes()) result.Add(rune.Value);
            }
            catch (DecoderFallbackException) { }
        }
        return result;
    }

    private static async Task CopyLooseFilesAsync(
        string input,
        string output,
        IReadOnlyList<PfsFileInfo> archives,
        CancellationToken cancellationToken)
    {
        HashSet<string> archivePaths = archives.Select(item => Path.GetFullPath(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in Directory.EnumerateDirectories(input, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(output, Path.GetRelativePath(input, directory)));
        foreach (string file in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (archivePaths.Contains(Path.GetFullPath(file))) continue;
            string target = Path.Combine(output, Path.GetRelativePath(input, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using FileStream source = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            await using FileStream destination = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await source.CopyToAsync(destination, cancellationToken);
        }
    }

    private static void CommitDirectory(string staging, string output, string backup, bool overwrite)
    {
        bool hadOutput = Directory.Exists(output);
        if (hadOutput)
        {
            if (!overwrite) throw new IOException("Output directory already exists.");
            Directory.Move(output, backup);
        }
        try
        {
            Directory.Move(staging, output);
            if (hadOutput) Directory.Delete(backup, true);
        }
        catch
        {
            if (!Directory.Exists(output) && Directory.Exists(backup)) Directory.Move(backup, output);
            throw;
        }
    }

    private static bool IsPfsFileName(string name)
    {
        int marker = name.LastIndexOf(".pfs", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0) return false;
        string suffix = name[(marker + 4)..];
        return suffix.Length == 0 || (suffix.Length == 4 && suffix[0] == '.' && suffix[1..].All(char.IsAsciiDigit));
    }

    private static void SafeDeleteTaskDirectory(string path, string expectedParent)
    {
        if (!Directory.Exists(path)) return;
        string parent = Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).Contains("art3m1s", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(expectedParent).Equals(".art3m1s-work", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove an unexpected temporary directory.");
        Directory.Delete(path, true);
    }
}
