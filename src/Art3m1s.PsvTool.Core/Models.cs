namespace Art3m1s.PsvTool.Core;

[Flags]
public enum AssetCategories
{
    None = 0,
    Text = 1,
    Images = 2,
    Animation = 4,
    Video = 8,
    All = Text | Images | Animation | Video
}

public enum PfsNameEncoding
{
    Auto,
    Utf8,
    ShiftJis
}

public enum FontSubsetProfile
{
    SimplifiedChinese,
    Japanese,
    TraditionalChinese
}

public sealed record ConversionOptions(
    string InputDirectory,
    string OutputDirectory,
    double Ratio = 0.5,
    AssetCategories Categories = AssetCategories.All,
    int MaxParallelism = 0,
    PfsNameEncoding NameEncoding = PfsNameEncoding.Auto,
    bool OverwriteExisting = false,
    bool SubsetFonts = false,
    FontSubsetProfile FontProfile = FontSubsetProfile.SimplifiedChinese)
{
    public int EffectiveParallelism => MaxParallelism > 0
        ? MaxParallelism
        : Math.Max(1, Environment.ProcessorCount - 1);

    public void Validate()
    {
        if (!Directory.Exists(InputDirectory))
            throw new DirectoryNotFoundException(InputDirectory);
        if (Ratio is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Ratio), "Ratio must be greater than 0 and no greater than 1.");
        if (Path.GetFullPath(InputDirectory).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(OutputDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output directories must be different.");

        string input = Path.GetFullPath(InputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string output = Path.GetFullPath(OutputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (output.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output directory cannot be inside the input directory.");
        if (input.StartsWith(output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input directory cannot be inside the output directory.");
    }
}

public sealed record PfsFileInfo(string Path, string FileName, char Version, long Length);

public sealed record ScanResult(
    IReadOnlyList<PfsFileInfo> Archives,
    bool HasSystemIni,
    int? Width,
    int? Height)
{
    public bool HasResolution => Width > 0 && Height > 0;
}

public sealed record ConversionProgress(
    double Percent,
    string Stage,
    string? Archive = null,
    string? Entry = null);

public sealed record PfsEntry(
    byte[] RawName,
    string Path,
    uint Offset,
    uint Size,
    string ExtractedPath);

public sealed record ExtractedArchive(char SourceVersion, IReadOnlyList<PfsEntry> Entries);
