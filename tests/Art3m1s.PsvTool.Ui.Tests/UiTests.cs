using Art3m1s.PsvTool.App;
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
        Assert.Equal("zh-CN", localizer.Language);
        Assert.Equal("图片（PNG）", localizer["Images"]);
    }

    [Fact]
    public void LanguageSwitchUpdatesLongLabels()
    {
        Localizer localizer = new(); MainViewModel viewModel = new(localizer, settings: new MemorySettingsStore());
        localizer.SetLanguage("en-US");
        Assert.Equal("Video (WMV / DAT / MP4 / AVI / MPG / MKV)", viewModel.VideoLabel);
        Assert.Equal("art3m1s PSV Port Tool", viewModel.Title);
    }

    [Fact]
    public void LanguageAndThemeArePersistedSeparately()
    {
        MemorySettingsStore store = new(); MainViewModel viewModel = new(settings: store);
        viewModel.SetLanguage("en-US"); viewModel.ToggleTheme();
        Assert.Equal("en-US", store.Value.Language);
        Assert.False(store.Value.IsDark);
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public AppSettings Value { get; private set; } = new();
        public AppSettings Load() => Value;
        public void Save(AppSettings settings) => Value = settings;
    }
}
