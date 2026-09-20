using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Art3m1s.PsvTool.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
    }

    private static void LogTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        Dispatcher.UIThread.Post(() =>
        {
            textBox.CaretIndex = textBox.Text?.Length ?? 0;
            textBox.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault()
                ?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private async void BrowseInputClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync(ViewModel.InputDirectory);
        if (path is not null) ViewModel.InputDirectory = path;
    }
    private async void BrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync(ViewModel.OutputDirectory);
        if (path is not null) ViewModel.OutputDirectory = path;
    }
    private async Task<string?> PickFolderAsync(string suggested)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { AllowMultiple = false, SuggestedStartLocation = Directory.Exists(suggested) ? await StorageProvider.TryGetFolderFromPathAsync(suggested) : null });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
    private async void ScanClick(object? sender, RoutedEventArgs e) => await ViewModel.ScanAsync();
    private async void StartClick(object? sender, RoutedEventArgs e) => await ViewModel.StartAsync();
    private void CancelClick(object? sender, RoutedEventArgs e) => ViewModel.Cancel();
    private void ChineseClick(object? sender, RoutedEventArgs e) => ViewModel.SetLanguage("zh-CN");
    private void EnglishClick(object? sender, RoutedEventArgs e) => ViewModel.SetLanguage("en-US");
    private void ThemeClick(object? sender, RoutedEventArgs e) => ViewModel.ToggleTheme();
    private void Ratio075Click(object? sender, RoutedEventArgs e) => ViewModel.SetRatio(0.75);
    private void Ratio05Click(object? sender, RoutedEventArgs e) => ViewModel.SetRatio(0.5);
    private void Ratio0375Click(object? sender, RoutedEventArgs e) => ViewModel.SetRatio(0.375);
    private void Ratio025Click(object? sender, RoutedEventArgs e) => ViewModel.SetRatio(0.25);
    private async void AboutClick(object? sender, RoutedEventArgs e)
    {
        string appVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        string avalonia = typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(3) ?? "12.1.2";
        string imageSharp = typeof(SixLabors.ImageSharp.Image).Assembly.GetName().Version?.ToString(3) ?? "3.1.12";
        Window dialog = new()
        {
            Title = ViewModel.AboutLabel,
            Width = 620,
            Height = 520,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Margin = new Avalonia.Thickness(26),
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = $"{ViewModel.Title} v{appVersion}", FontSize = 22, FontWeight = Avalonia.Media.FontWeight.Bold },
                        new TextBlock { Text = $".NET 10 · Avalonia {avalonia} · ImageSharp {imageSharp} · FFmpeg n9.0 GPL Full", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        new TextBlock { Text = ViewModel.AboutBody, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                    }
                }
            }
        };
        await dialog.ShowDialog(this);
    }
}
