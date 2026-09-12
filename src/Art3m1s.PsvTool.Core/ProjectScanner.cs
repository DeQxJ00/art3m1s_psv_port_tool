using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Art3m1s.PsvTool.Core;

public interface IProjectScanner
{
    Task<ScanResult> ScanAsync(string inputDirectory, CancellationToken cancellationToken = default);
}

public sealed partial class ProjectScanner : IProjectScanner
{
    public async Task<ScanResult> ScanAsync(string inputDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException(inputDirectory);

        List<PfsFileInfo> archives = [];
        foreach (string file in Directory.EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(IsPfsName)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using FileStream stream = File.OpenRead(file);
            byte[] header = new byte[3];
            if (await stream.ReadAsync(header, cancellationToken) != header.Length ||
                header[0] != (byte)'p' || header[1] != (byte)'f' || header[2] is not ((byte)'2' or (byte)'6' or (byte)'8'))
                continue;
            archives.Add(new PfsFileInfo(file, Path.GetFileName(file), (char)header[2], stream.Length));
        }

        string systemIni = Path.Combine(inputDirectory, "system.ini");
        (int? width, int? height) = File.Exists(systemIni)
            ? await ReadResolutionAsync(systemIni, cancellationToken)
            : (null, null);

        return new ScanResult(archives, File.Exists(systemIni), width, height);
    }

    private static bool IsPfsName(string path) => PfsNameRegex().IsMatch(Path.GetFileName(path));

    private static async Task<(int?, int?)> ReadResolutionAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        string text = TextEncoding.Detect(bytes).Encoding.GetString(bytes);
        Match section = WindowsSectionRegex().Match(text);
        string source = section.Success ? section.Groups[1].Value : text;
        Match width = WidthRegex().Match(source);
        Match height = HeightRegex().Match(source);
        return (width.Success ? int.Parse(width.Groups[1].Value) : null,
            height.Success ? int.Parse(height.Groups[1].Value) : null);
    }

    [GeneratedRegex(@"^.+\.pfs(?:\.\d{3})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PfsNameRegex();

    [GeneratedRegex(@"(?ims)^\s*\[WINDOWS\]\s*(.*?)(?=^\s*\[|\z)")]
    private static partial Regex WindowsSectionRegex();

    [GeneratedRegex(@"(?im)^\s*WIDTH\s*=\s*(\d+)")]
    private static partial Regex WidthRegex();

    [GeneratedRegex(@"(?im)^\s*HEIGHT\s*=\s*(\d+)")]
    private static partial Regex HeightRegex();
}

internal static class TextEncoding
{
    static TextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static (Encoding Encoding, bool HasBom) Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true, true), true);
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return (new UTF8Encoding(false, true), false);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), false);
        }
    }
}
