using Art3m1s.PsvTool.App;
using Art3m1s.PsvTool.Core;
using Xunit;

namespace Art3m1s.PsvTool.Ui.Tests;

public sealed class UiTests
{
    [Fact]
    public void ChineseAndEnglishHaveIdenticalResourceKeys() =>
        Assert.Equal(Localizer.ChineseKeys.Order(), Localizer.EnglishKeys.Order());

    [Fact]
    public void ChineseIsTheFirstLaunchDefault()
    {
        Localizer localizer = new();
        MainViewModel viewModel = new(localizer, settings: new MemorySettingsStore());
        Assert.Equal("zh-CN", localizer.Language);
        Assert.Equal("图片（PNG）", localizer["Images"]);
        Assert.Equal("字体削减（TTF / OTF）", viewModel.FontSubsetLabel);
        Assert.Equal("动画（OGV / E-mote PSB）", viewModel.AnimationLabel);
        Assert.Contains("art3m1s-core", viewModel.AboutBody);
        Assert.Equal("项目 GitHub", viewModel.ProjectRepositoryLabel);
        Assert.Equal("https://github.com/DeQxJ00/art3m1s_psv_port_tool", MainWindow.RepositoryUrl);
        Assert.True(viewModel.IgnorePfsVideos);
        Assert.Contains("WMV / DAT / MP4 / AVI / MPG / MKV", viewModel.IgnorePfsVideosLabel);
        Assert.DoesNotContain("OGV", viewModel.IgnorePfsVideosLabel);
    }

    [Fact]
    public void LanguageSwitchUpdatesLongLabels()
    {
        Localizer localizer = new(); MainViewModel viewModel = new(localizer, settings: new MemorySettingsStore());
        localizer.SetLanguage("en-US");
        Assert.Equal("Video (WMV / DAT / MP4 / AVI / MPG / MKV)", viewModel.VideoLabel);
        Assert.Equal("Font subsetting (TTF / OTF)", viewModel.FontSubsetLabel);
        Assert.Equal("Animation (OGV / E-mote PSB)", viewModel.AnimationLabel);
        Assert.Equal("Ignore video inside PFS (WMV / DAT / MP4 / AVI / MPG / MKV)", viewModel.IgnorePfsVideosLabel);
        Assert.Equal("art3m1s PSV Port Tool", viewModel.Title);
        Assert.Equal("Project GitHub", viewModel.ProjectRepositoryLabel);
    }

    [Fact]
    public void LanguageAndThemeArePersistedSeparately()
    {
        MemorySettingsStore store = new(); MainViewModel viewModel = new(settings: store);
        viewModel.SetLanguage("en-US"); viewModel.ToggleTheme();
        Assert.Equal("en-US", store.Value.Language);
        Assert.False(store.Value.IsDark);
    }

    [Fact]
    public async Task ConversionFailureLogShowsExactArchiveFileAndReason()
    {
        string root = Path.Combine(Path.GetTempPath(), "art3m1s-ui-error-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        try
        {
            MainViewModel viewModel = new(converter: new FailingConversionService(), settings: new MemorySettingsStore())
            {
                InputDirectory = input,
                OutputDirectory = Path.Combine(root, "output")
            };

            await viewModel.StartAsync();

            Assert.Contains("PFS 归档: root.pfs.010", viewModel.Log);
            Assert.Contains("错误文件: image\\bg\\broken.png", viewModel.Log);
            Assert.Contains("错误原因: invalid PNG", viewModel.Log);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public AppSettings Value { get; private set; } = new();
        public AppSettings Load() => Value;
        public void Save(AppSettings settings) => Value = settings;
    }

    private sealed class FailingConversionService : IConversionService
    {
        public Task ConvertAsync(ConversionOptions options, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromException(new ConversionItemException("root.pfs.010", "image\\bg\\broken.png", new InvalidDataException("invalid PNG")));
    }
}
