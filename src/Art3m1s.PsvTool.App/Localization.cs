using System.Globalization;

namespace Art3m1s.PsvTool.App;

public interface ILocalizer
{
    string Language { get; }
    string this[string key] { get; }
    void SetLanguage(string language);
    event EventHandler? LanguageChanged;
}

public sealed class Localizer : ILocalizer
{
    private static readonly IReadOnlyDictionary<string, string> Zh = new Dictionary<string, string>
    {
        ["Title"] = "art3m1s PSV 移植工具",
        ["Subtitle"] = "Artemis 游戏资源缩小与 PFS 重打包",
        ["Project"] = "项目",
        ["Input"] = "输入目录",
        ["Output"] = "输出目录",
        ["Browse"] = "浏览…",
        ["Scan"] = "扫描项目",
        ["ScanEmpty"] = "等待扫描 PFS 文件",
        ["Ratio"] = "缩小比例",
        ["RatioHelp"] = "按游戏分辨率设置；游戏分辨率 × Ratio 接近 PSV 960×540（或 960×544）最佳。设置过大可能导致 PSV 内存不足。",
        ["Original"] = "原始分辨率",
        ["Target"] = "预计输出",
        ["Unknown"] = "未知",
        ["Types"] = "处理类型",
        ["Text"] = "文本（INI / TBL / IPT / AST / LUA）",
        ["Images"] = "图片（PNG）",
        ["Animation"] = "动画（OGV / E-mote PSB）",
        ["Video"] = "视频（WMV / DAT / MP4 / AVI / MPG / MKV）",
        ["FontSubset"] = "字体削减（TTF / OTF）",
        ["FontSubsetHelp"] = "保留脚本实际字符、常用符号及所选语言常用字；支持 TrueType 与 CFF OpenType，TTC 原样保留。",
        ["Simplified"] = "简体中文",
        ["Japanese"] = "日文",
        ["Traditional"] = "繁体中文",
        ["Mode"] = "Mode 1：仅缩放",
        ["ModeHelp"] = "图片使用 Alpha 预乘的高质量 Bicubic 缩小；不执行 PNG 优化、调色板压缩或 waifu2x。",
        ["Advanced"] = "高级设置",
        ["Parallel"] = "并行任务",
        ["Auto"] = "自动",
        ["Encoding"] = "PFS 路径编码",
        ["IgnorePfsVideos"] = "忽略 PFS 内的视频（WMV / DAT / MP4 / AVI / MPG / MKV）",
        ["Log"] = "运行日志",
        ["Ready"] = "准备就绪",
        ["Start"] = "开始转换",
        ["Cancel"] = "取消",
        ["About"] = "关于",
        ["AboutBody"] = "本程序采用 GPL-3.0-or-later，不提供任何担保。\n\nCredits\npfs_upk · GPL-3.0 · PFS 格式与行为参考\nart3m1s-core · MPL-2.0 · E-mote PSB 结构、加密头与资源表参考\nVisualNovelUpscaler · MIT · Artemis 文本与 Ratio 规则参考\nAvalonia 12.1.2 · MIT · 跨平台 UI\nSixLabors.ImageSharp 3.1.12 · Six Labors Split License 1.0 · PNG 处理\nHarfBuzz 8.3.1 · Old MIT · CFF OpenType 字体削减\nOptris.StaticGraphics.Avalonia.Software 3.119.4.1 · MIT 及上游许可 · NativeAOT 图形后端\nFFmpeg / ffprobe n9.0 · GPL Full 构建 · 独立程序\nBtbN/FFmpeg-Builds · GPL-3.0 · Windows/Linux 静态二进制\ndav1d · BSD-2-Clause · AV1 软件解码\nx264 · GPL-2.0 · H.264 编码\nlibtheora · BSD-3-Clause · Theora 编码\nlibogg · BSD-3-Clause · Ogg 容器\nzlib · Zlib · PNG 帧序列压缩\n\nFull 构建位于发行包 tools 目录，不嵌入主程序且不包含 nonfree 组件。正式发布包的 licenses 目录包含第三方许可证；对应源码、构建配置及校验信息随 Release 提供。",
        ["Theme"] = "主题",
        ["Dark"] = "深色",
        ["Light"] = "浅色",
        ["Archives"] = "个独立 PFS",
        ["InvalidPaths"] = "请选择有效且不同的输入、输出目录。",
        ["Overwrite"] = "输出目录已存在；再次点击开始将覆盖。",
        ["Finished"] = "转换完成",
        ["Failed"] = "转换失败",
        ["FailureDetails"] = "失败详情",
        ["ErrorArchive"] = "PFS 归档",
        ["ErrorFile"] = "错误文件",
        ["ErrorReason"] = "错误原因",
        ["Scanning"] = "正在扫描…",
        ["NoPfs"] = "未找到有效 PFS。",
        ["StageCopy"] = "正在复制项目文件…",
        ["StageExtract"] = "正在解包 PFS…",
        ["StagePack"] = "正在重新打包 PFS…",
        ["StageLoose"] = "正在处理散装资源…",
        ["StageResource"] = "正在转换资源…",
        ["StageComplete"] = "转换完成",
        ["DemoProgress"] = "正在转换 {0} · {1}%"
    };

    private static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
    {
        ["Title"] = "art3m1s PSV Port Tool",
        ["Subtitle"] = "Resize Artemis game assets and rebuild independent PFS archives",
        ["Project"] = "Project",
        ["Input"] = "Input folder",
        ["Output"] = "Output folder",
        ["Browse"] = "Browse…",
        ["Scan"] = "Scan project",
        ["ScanEmpty"] = "Waiting to scan PFS files",
        ["Ratio"] = "Resize ratio",
        ["RatioHelp"] = "Set from the game resolution. Game resolution × Ratio should approach PSV 960×540 (or 960×544). Larger output may exceed PSV memory.",
        ["Original"] = "Original resolution",
        ["Target"] = "Estimated output",
        ["Unknown"] = "Unknown",
        ["Types"] = "Asset types",
        ["Text"] = "Text (INI / TBL / IPT / AST / LUA)",
        ["Images"] = "Images (PNG)",
        ["Animation"] = "Animation (OGV / E-mote PSB)",
        ["Video"] = "Video (WMV / DAT / MP4 / AVI / MPG / MKV)",
        ["FontSubset"] = "Font subsetting (TTF / OTF)",
        ["FontSubsetHelp"] = "Keeps characters used by scripts, common symbols, and the selected language set. TrueType and CFF OpenType are supported; TTC stays unchanged.",
        ["Simplified"] = "Simplified Chinese",
        ["Japanese"] = "Japanese",
        ["Traditional"] = "Traditional Chinese",
        ["Mode"] = "Mode 1: Resize only",
        ["ModeHelp"] = "Images use high-quality alpha-premultiplied Bicubic resizing; no PNG optimization, palette compression, or waifu2x.",
        ["Advanced"] = "Advanced",
        ["Parallel"] = "Parallel tasks",
        ["Auto"] = "Auto",
        ["Encoding"] = "PFS path encoding",
        ["IgnorePfsVideos"] = "Ignore video inside PFS (WMV / DAT / MP4 / AVI / MPG / MKV)",
        ["Log"] = "Run log",
        ["Ready"] = "Ready",
        ["Start"] = "Start conversion",
        ["Cancel"] = "Cancel",
        ["About"] = "About",
        ["AboutBody"] = "This program is GPL-3.0-or-later and comes with no warranty.\n\nCredits\npfs_upk · GPL-3.0 · PFS format and behavior reference\nart3m1s-core · MPL-2.0 · E-mote PSB structure, encrypted-header, and resource-table reference\nVisualNovelUpscaler · MIT · Artemis text and Ratio rule reference\nAvalonia 12.1.2 · MIT · Cross-platform UI\nSixLabors.ImageSharp 3.1.12 · Six Labors Split License 1.0 · PNG processing\nHarfBuzz 8.3.1 · Old MIT · CFF OpenType font subsetting\nOptris.StaticGraphics.Avalonia.Software 3.119.4.1 · MIT and upstream licenses · NativeAOT graphics backend\nFFmpeg / ffprobe n9.0 · GPL Full build · Standalone programs\nBtbN/FFmpeg-Builds · GPL-3.0 · Windows/Linux static binaries\ndav1d · BSD-2-Clause · Software AV1 decoding\nx264 · GPL-2.0 · H.264 encoding\nlibtheora · BSD-3-Clause · Theora encoding\nlibogg · BSD-3-Clause · Ogg container\nzlib · Zlib · PNG frame-sequence compression\n\nThe Full build is shipped in the release package's tools directory, is not embedded in the app, and contains no nonfree components. Official packages include third-party licenses; corresponding sources, build configuration, and checksums are supplied with each Release.",
        ["Theme"] = "Theme",
        ["Dark"] = "Dark",
        ["Light"] = "Light",
        ["Archives"] = "independent PFS files",
        ["InvalidPaths"] = "Choose valid, different input and output folders.",
        ["Overwrite"] = "Output exists; click Start again to confirm replacement.",
        ["Finished"] = "Conversion complete",
        ["Failed"] = "Conversion failed",
        ["FailureDetails"] = "Failure details",
        ["ErrorArchive"] = "PFS archive",
        ["ErrorFile"] = "Failed file",
        ["ErrorReason"] = "Error",
        ["Scanning"] = "Scanning…",
        ["NoPfs"] = "No valid PFS archives found.",
        ["StageCopy"] = "Copying project files…",
        ["StageExtract"] = "Extracting PFS…",
        ["StagePack"] = "Rebuilding PFS…",
        ["StageLoose"] = "Processing loose assets…",
        ["StageResource"] = "Converting assets…",
        ["StageComplete"] = "Conversion complete",
        ["DemoProgress"] = "Converting {0} · {1}%"
    };

    public string Language { get; private set; } = "zh-CN";
    public string this[string key] => (Language == "en-US" ? En : Zh).TryGetValue(key, out string? value) ? value : key;
    public event EventHandler? LanguageChanged;

    public void SetLanguage(string language)
    {
        string normalized = language.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
        if (normalized == Language) return;
        Language = normalized;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(normalized);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static IEnumerable<string> ChineseKeys => Zh.Keys;
    public static IEnumerable<string> EnglishKeys => En.Keys;
}
