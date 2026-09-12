using Art3m1s.PsvTool.App;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;

AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .WithInterFont()
    .SetupWithoutStarting();

string output = args.Length == 0 ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../docs/screenshots")) : Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
foreach ((string language, bool dark, string fileName) in new[]
{
    ("zh-CN", true, "ui-zh-CN-dark.png"), ("en-US", true, "ui-en-US-dark.png"),
    ("zh-CN", false, "ui-zh-CN-light.png"), ("en-US", false, "ui-en-US-light.png")
})
{
    Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    MainWindow window = new(new MainViewModel(settings: new ScreenshotSettingsStore(language, dark))) { Width = 1180, Height = 1000, Position = new PixelPoint(0, 0) };
    window.ViewModel.SetLanguage(language); window.ViewModel.ConfigureScreenshotDemo(); window.Show(); Dispatcher.UIThread.RunJobs();
    using Avalonia.Media.Imaging.Bitmap bitmap = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The headless window did not render.");
    bitmap.Save(Path.Combine(output, fileName), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); window.Close(); Dispatcher.UIThread.RunJobs();
}

sealed class ScreenshotSettingsStore : ISettingsStore
{
    private readonly AppSettings _settings;
    public ScreenshotSettingsStore(string language, bool isDark) => _settings = new AppSettings(language, isDark);
    public AppSettings Load() => _settings;
    public void Save(AppSettings settings) { }
}
