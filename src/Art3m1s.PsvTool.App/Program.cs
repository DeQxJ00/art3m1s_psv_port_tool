using Avalonia;
using Art3m1s.PsvTool.Core;
using Optris.StaticGraphics;
using System.Text;

namespace Art3m1s.PsvTool.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.Ordinal))
            return RunSelfTestAsync().GetAwaiter().GetResult();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
        .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
        .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Software] })
        .WithOptrisStaticGraphics()
        .WithInterFont()
        .LogToTrace();

    private static async Task<int> RunSelfTestAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "art3m1s-native-selftest-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input"), output = Path.Combine(root, "output"), payload = Path.Combine(root, "payload.bin");
        try
        {
            Directory.CreateDirectory(input);
            await File.WriteAllBytesAsync(payload, [1, 3, 3, 7]);
            PfsCodec codec = new();
            ExtractedArchive archive = new('8', [new PfsEntry(Encoding.UTF8.GetBytes("data/payload.bin"), "data/payload.bin", 0, 4, payload)]);
            await codec.PackPf8Async(archive, Path.Combine(input, "root.pfs"));
            await File.WriteAllTextAsync(Path.Combine(input, "system.ini"), "[WINDOWS]\nCHARSET=UTF-8\nWIDTH=1920\nHEIGHT=1080\n");
            await new ConversionService().ConvertAsync(new ConversionOptions(input, output, Categories: AssetCategories.None));
            if (!File.Exists(Path.Combine(output, "root.pfs")) || !File.ReadAllText(Path.Combine(output, "system.ini")).Contains("[VITA]", StringComparison.Ordinal)) return 2;
            return 0;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
