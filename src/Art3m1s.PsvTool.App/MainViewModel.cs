using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Art3m1s.PsvTool.Core;
using Avalonia;
using Avalonia.Styling;

namespace Art3m1s.PsvTool.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ILocalizer _localizer;
    private readonly IProjectScanner _scanner;
    private readonly IConversionService _converter;
    private readonly ISettingsStore _settings;
    private CancellationTokenSource? _conversionCancellation;
    private string _inputDirectory = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _ratio = "0.5";
    private int? _width;
    private int? _height;
    private int _archiveCount;
    private bool _isBusy;
    private bool _overwriteArmed;
    private double _progress;
    private string _status = string.Empty;
    private string _log = string.Empty;
    private bool _text = true, _images = true, _animation = true, _video = true;
    private bool _subsetFonts;
    private int _fontProfile;
    private int _selectedParallel;
    private int _selectedEncoding;

    public MainViewModel(ILocalizer? localizer = null, IProjectScanner? scanner = null, IConversionService? converter = null, ISettingsStore? settings = null)
    {
        _localizer = localizer ?? new Localizer();
        _scanner = scanner ?? new ProjectScanner();
        _converter = converter ?? new ConversionService();
        _settings = settings ?? new LocalSettingsStore();
        AppSettings saved = _settings.Load();
        IsDark = saved.IsDark;
        _localizer.SetLanguage(saved.Language);
        if (Application.Current is not null)
            Application.Current.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        _localizer.LanguageChanged += (_, _) => RaiseAllLocalized();
        _status = L("Ready");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string InputDirectory { get => _inputDirectory; set { Set(ref _inputDirectory, value); _overwriteArmed = false; } }
    public string OutputDirectory { get => _outputDirectory; set { Set(ref _outputDirectory, value); _overwriteArmed = false; } }
    public string Ratio { get => _ratio; set { if (Set(ref _ratio, value)) OnPropertyChanged(nameof(TargetResolution)); } }
    public bool ProcessText { get => _text; set => Set(ref _text, value); }
    public bool ProcessImages { get => _images; set => Set(ref _images, value); }
    public bool ProcessAnimation { get => _animation; set => Set(ref _animation, value); }
    public bool ProcessVideo { get => _video; set => Set(ref _video, value); }
    public bool SubsetFonts { get => _subsetFonts; set => Set(ref _subsetFonts, value); }
    public int FontProfile { get => _fontProfile; set => Set(ref _fontProfile, value); }
    public int SelectedParallel { get => _selectedParallel; set => Set(ref _selectedParallel, value); }
    public int SelectedEncoding { get => _selectedEncoding; set => Set(ref _selectedEncoding, value); }
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !IsBusy;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Log { get => _log; private set => Set(ref _log, value); }
    public string OriginalResolution => _width is > 0 && _height is > 0 ? $"{_width} × {_height}" : L("Unknown");
    public string TargetResolution => _width is > 0 && _height is > 0 && TryRatio(out double ratio)
        ? $"{Math.Max(1, (int)(_width.Value * ratio))} × {Math.Max(1, (int)(_height.Value * ratio))}" : L("Unknown");
    public string ScanSummary => _archiveCount == 0 ? L("ScanEmpty") : $"{_archiveCount} {L("Archives")}";
    public string Language => _localizer.Language;
    public bool IsDark { get; private set; } = true;

    public string Title => L("Title"); public string Subtitle => L("Subtitle"); public string ProjectLabel => L("Project");
    public string InputLabel => L("Input"); public string OutputLabel => L("Output"); public string BrowseLabel => L("Browse");
    public string ScanLabel => L("Scan"); public string RatioLabel => L("Ratio"); public string RatioHelp => L("RatioHelp");
    public string OriginalLabel => L("Original"); public string TargetLabel => L("Target"); public string TypesLabel => L("Types");
    public string TextLabel => L("Text"); public string ImagesLabel => L("Images"); public string AnimationLabel => L("Animation");
    public string VideoLabel => L("Video"); public string ModeLabel => L("Mode"); public string ModeHelp => L("ModeHelp");
    public string FontSubsetLabel => L("FontSubset"); public string FontSubsetHelp => L("FontSubsetHelp");
    public IReadOnlyList<string> FontProfiles => [L("Simplified"), L("Japanese"), L("Traditional")];
    public IReadOnlyList<string> ParallelChoices => [L("Auto"), .. Enumerable.Range(1, Math.Max(2, Environment.ProcessorCount)).Select(value => value.ToString(CultureInfo.InvariantCulture))];
    public IReadOnlyList<string> EncodingChoices => [$"{L("Auto")} · UTF-8 / Shift_JIS", "UTF-8", "Shift_JIS"];
    public string AdvancedLabel => L("Advanced"); public string ParallelLabel => L("Parallel"); public string AutoLabel => L("Auto");
    public string EncodingLabel => L("Encoding"); public string LogLabel => L("Log"); public string StartLabel => L("Start");
    public string CancelLabel => L("Cancel"); public string AboutLabel => L("About"); public string AboutBody => L("AboutBody"); public string ThemeLabel => L("Theme"); public string ThemeValue => L(IsDark ? "Dark" : "Light");

    public void SetRatio(double ratio) => Ratio = ratio.ToString("0.###", CultureInfo.InvariantCulture);
    public void SetLanguage(string language)
    {
        _localizer.SetLanguage(language);
        SaveSettings();
    }
    public void ToggleTheme()
    {
        IsDark = !IsDark;
        if (Application.Current is not null) Application.Current.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        OnPropertyChanged(nameof(IsDark)); OnPropertyChanged(nameof(ThemeValue));
        SaveSettings();
    }

    public async Task ScanAsync()
    {
        if (!Directory.Exists(InputDirectory)) { Status = L("InvalidPaths"); return; }
        IsBusy = true; Status = L("Scanning");
        try
        {
            ScanResult result = await _scanner.ScanAsync(InputDirectory);
            _archiveCount = result.Archives.Count; _width = result.Width; _height = result.Height;
            Status = _archiveCount == 0 ? L("NoPfs") : ScanSummary;
            Log = string.Join(Environment.NewLine, result.Archives.Select(item => $"{item.FileName} · pf{item.Version} · {item.Length:N0} B"));
            OnPropertyChanged(nameof(ScanSummary)); OnPropertyChanged(nameof(OriginalResolution)); OnPropertyChanged(nameof(TargetResolution));
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    public async Task StartAsync()
    {
        if (!Directory.Exists(InputDirectory) || string.IsNullOrWhiteSpace(OutputDirectory) || !TryRatio(out double ratio))
        { Status = L("InvalidPaths"); return; }
        if (Directory.Exists(OutputDirectory) && !_overwriteArmed)
        { _overwriteArmed = true; Status = L("Overwrite"); return; }

        AssetCategories categories = AssetCategories.None;
        if (ProcessText) categories |= AssetCategories.Text; if (ProcessImages) categories |= AssetCategories.Images;
        if (ProcessAnimation) categories |= AssetCategories.Animation; if (ProcessVideo) categories |= AssetCategories.Video;
        _conversionCancellation = new CancellationTokenSource(); IsBusy = true; Progress = 0; Log = string.Empty;
        string? activeArchive = null;
        string? activeEntry = null;
        try
        {
            Progress<ConversionProgress> reporter = new(value =>
            {
                activeArchive = value.Archive;
                activeEntry = value.Entry;
                Progress = value.Percent; Status = LocalizeStage(value.Stage);
                if (!string.IsNullOrWhiteSpace(value.Entry)) Log += value.Entry + Environment.NewLine;
            });
            await _converter.ConvertAsync(new ConversionOptions(InputDirectory, OutputDirectory, ratio, categories, SelectedParallel, (PfsNameEncoding)SelectedEncoding, _overwriteArmed, SubsetFonts, (FontSubsetProfile)FontProfile), reporter, _conversionCancellation.Token);
            Status = L("Finished");
        }
        catch (OperationCanceledException) { Status = L("Cancel"); }
        catch (ConversionItemException ex)
        {
            Status = $"{L("Failed")}: {ex.Message}";
            AppendFailure(ex.Archive ?? activeArchive, ex.Entry ?? activeEntry, ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            Status = $"{L("Failed")}: {ex.Message}";
            AppendFailure(activeArchive, activeEntry, ex.Message);
        }
        finally { _conversionCancellation.Dispose(); _conversionCancellation = null; IsBusy = false; _overwriteArmed = false; }
    }

    public void Cancel() => _conversionCancellation?.Cancel();
    public void ConfigureScreenshotDemo()
    {
        InputDirectory = OperatingSystem.IsWindows() ? @"D:\Games\ArtemisDemo" : "/games/ArtemisDemo";
        OutputDirectory = OperatingSystem.IsWindows() ? @"D:\Games\ArtemisDemo-PSV" : "/games/ArtemisDemo-PSV";
        _width = 1920; _height = 1080; _archiveCount = 5; Ratio = "0.5"; Progress = 68;
        SubsetFonts = true; FontProfile = 0;
        Status = string.Format(CultureInfo.CurrentUICulture, L("DemoProgress"), "root.pfs.010", 68);
        Log = "root.pfs\nroot.pfs.000\nroot.pfs.001\nroot.pfs.010\nroot.pfs.011";
        OnPropertyChanged(nameof(ScanSummary)); OnPropertyChanged(nameof(OriginalResolution)); OnPropertyChanged(nameof(TargetResolution));
    }
    private bool TryRatio(out double ratio) => double.TryParse(Ratio, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio) && ratio is > 0 and <= 1;
    private string L(string key) => _localizer[key];
    private string LocalizeStage(string stage) => stage switch
    {
        "copy" => L("StageCopy"),
        "extract" => L("StageExtract"),
        "pack" => L("StagePack"),
        "loose" => L("StageLoose"),
        "resource" => L("StageResource"),
        "complete" => L("StageComplete"),
        _ => stage
    };
    private void AppendFailure(string? archive, string? entry, string reason)
    {
        List<string> details = [$"--- {L("FailureDetails")} ---"];
        if (!string.IsNullOrWhiteSpace(archive)) details.Add($"{L("ErrorArchive")}: {archive}");
        if (!string.IsNullOrWhiteSpace(entry)) details.Add($"{L("ErrorFile")}: {entry}");
        details.Add($"{L("ErrorReason")}: {reason}");
        if (!string.IsNullOrEmpty(Log) && !Log.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            Log += Environment.NewLine;
        Log += string.Join(Environment.NewLine, details) + Environment.NewLine;
    }
    private void SaveSettings()
    {
        try { _settings.Save(new AppSettings(Language, IsDark)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void RaiseAllLocalized()
    {
        foreach (string property in new[] { nameof(Title), nameof(Subtitle), nameof(ProjectLabel), nameof(InputLabel), nameof(OutputLabel), nameof(BrowseLabel), nameof(ScanLabel), nameof(RatioLabel), nameof(RatioHelp), nameof(OriginalLabel), nameof(TargetLabel), nameof(TypesLabel), nameof(TextLabel), nameof(ImagesLabel), nameof(AnimationLabel), nameof(VideoLabel), nameof(FontSubsetLabel), nameof(FontSubsetHelp), nameof(FontProfiles), nameof(ParallelChoices), nameof(ModeLabel), nameof(ModeHelp), nameof(AdvancedLabel), nameof(ParallelLabel), nameof(AutoLabel), nameof(EncodingLabel), nameof(LogLabel), nameof(StartLabel), nameof(CancelLabel), nameof(AboutLabel), nameof(AboutBody), nameof(ThemeLabel), nameof(ThemeValue), nameof(ScanSummary), nameof(OriginalResolution), nameof(TargetResolution), nameof(Language) }) OnPropertyChanged(property);
    }
}
