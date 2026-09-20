# art3m1s_psv_port_tool

[简体中文](README.md) · [English](README.en.md)

[![CI](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/ci.yml/badge.svg)](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/ci.yml)
[![Release](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/release.yml/badge.svg)](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/release.yml)

面向 Artemis 引擎游戏的 PSV 移植辅助工具。它逐个解包物理 PFS、按选择缩小资源，再以相同文件名重新打包；根目录下 PFS 之外任意名称的文件夹也会完整递归复制，并按文件扩展名转换其中资源（至少覆盖 3 层，实际不限制深度）。应用使用 .NET 10、Avalonia 12、C# 14 与 NativeAOT，支持 Windows、Linux 和 macOS。

> 请只处理你有权修改的游戏资源，并先备份原项目。工具不绕过平台签名、加密授权或 DRM。

## UI 截图

![中文深色界面](docs/screenshots/ui-zh-CN-dark.png)

[中文深色](docs/screenshots/ui-zh-CN-dark.png) · [English dark](docs/screenshots/ui-en-US-dark.png) · [中文浅色](docs/screenshots/ui-zh-CN-light.png) · [English light](docs/screenshots/ui-en-US-light.png)

## 下载与平台支持

Release 提供 `win-x64`、`linux-x64` NativeAOT 单文件，以及未签名、未公证的 `osx-x64` / `osx-arm64` `.app.zip`。macOS 首次启动可能需要在“隐私与安全性”中手动允许。FFmpeg/ffprobe 9.0.1 GPL Full 可执行文件位于发行包的 `tools` 目录，不嵌入主程序；构建包含 x264、libtheora、libogg 与 zlib，不包含 nonfree 组件。开发构建也可通过 `ART3M1S_FFMPEG` 与 `ART3M1S_FFPROBE` 指定工具。

## 使用步骤

1. 选择包含 `xxxxx.pfs` 的游戏根目录与不同的输出目录。
2. 扫描并确认独立 PFS 数量及检测到的分辨率。
3. 设置 Ratio，勾选文本、图片、动画和视频类型。
4. 开始转换；输出目录已存在时需要再次点击确认覆盖。

转换先写入输出目录同级的专用临时目录，全部成功后再替换目标；取消或失败会清理临时结果。

## Ratio 与 PSV 分辨率建议

默认 `0.5`，允许输入 `0 < Ratio ≤ 1`。参考按钮为 `0.75 · 720p`、`0.5 · 1080p`、`0.375 · 2K`、`0.25 · 4K`。以“原游戏分辨率 × Ratio”接近 PSV 的 `960×540`（或 `960×544`）为宜；设置过大会增加显存和内存压力。分辨率从根目录 `system.ini` 的 `[WINDOWS]` 读取；无法读取时可参考解包后的 `image/bg` PNG。

## 资源处理规则

- 文本：INI / TBL / IPT / AST / LUA 严格复刻 VisualNovelUpscaler 的 Artemis 匹配、取整、编码与输出行为；IET 原样复制。E-mote 会额外同步缩放 TBL 姿态表中的 X/Y 偏移与画布宽高、AST 坐标以及 LUA 的固定 `mulpos()` 坐标；人物缩放倍率、动作、表情、口型采样和资源名保持不变。
- 图片：PNG 使用 ImageSharp 的 Alpha 预乘高质量 Bicubic，仅缩放，不进行 PNG 优化、调色板压缩、waifu2x 或有损压缩。
- 动画：OGV 使用 FFmpeg Bicubic；目标宽高与 VisualNovelUpscaler 一样分别按 `int(原尺寸 × Ratio)` 截断，保持帧率与音频。Artemis／E-mote 动态立绘 PSB（v1–v4）会解析内嵌 `RGBA8` / `DXT5` atlas，以 Bicubic 按 Ratio 缩小并重建资源表，同时缩放 texture 尺寸、裁切尺寸、icon 矩形、origin、screenSize、动作坐标、偏移、运动路径与空白网格域；角度、动作时间、缩放倍率、曲线和参数范围保持不变。PSB 内外及 PFS 内均使用相同逻辑。为控制峰值内存，PSB 固定逐个处理。
- 视频：默认勾选“忽略 PFS 内的视频（WMV / DAT / MP4 / AVI / MPG / MKV）”，这些 PFS 条目保持原文件名和原始字节，不交给 FFmpeg；OGV 不在此忽略范围内，仍按动画规则处理。取消勾选后可处理 PFS 内受支持的视频，但 PFS 内 DAT 仍因可能是字体缓存等普通数据而原样保留。PFS 外散装目录中的 WMV / DAT / MP4 / AVI / MPG / MKV 视频统一输出为同名 MP4（H.264 Main@3.1、AAC）；除 DAT 固定为 960×544 外，其余格式使用 Ratio 尺寸截断规则。无法检测到视频流的普通数据 DAT 原样保留。
- 字体（可选）：支持 TTF 与 OTF。TrueType `glyf` 字体使用内置保守削减器，CFF OpenType 字体使用 HarfBuzz 子集器；按简体中文、日文或繁体中文常用范围裁剪，同时始终保留脚本中实际出现的字符、ASCII、常用标点与全角/半角符号。TTC 为避免损坏会原样保留。
- 未勾选类型按字节复制，不执行转换。

并行度默认为 `max(1, CPU 逻辑核心数 - 1)`；OGV 动画固定逐个单线程转换，其他视频任务最多同时两个。

## PNG 颜色表与透明度保证

RGB24 输出仍为 RGB24，不增加 Alpha；RGBA、灰度、透明灰度保持颜色类型。索引色保持原 1/2/4/8-bit 位深，原 `PLTE` 与 `tRNS` 块逐字节写回，不修改、重排或删减自带颜色表。Bicubic 结果仅映射回原颜色表并重写像素索引。PNG 坐标文本按 Ratio 缩放；除图像尺寸、像素数据和坐标文本外，其余 PNG 块逐字节保留。

## 构建、测试和 GitHub Actions

需要 .NET SDK 10.0.303：

```powershell
dotnet restore art3m1s_psv_port_tool.slnx
dotnet test art3m1s_psv_port_tool.slnx -c Release
dotnet publish src/Art3m1s.PsvTool.App -c Release -r win-x64
./scripts/generate-screenshots.ps1
```

`ci.yml` 执行 Release 编译、测试、格式、截图基线与多平台 NativeAOT 检查；`release.yml` 在 `v*` 标签或手动触发时构建四个平台并发布校验和、SBOM、许可与 FFmpeg 构建信息。

## 已知限制

- macOS 首版不签名、不公证。
- 无法由容器和原编码支持的音视频流会报告 FFmpeg 原始诊断并停止，不会静默降级。
- pf2/pf6 可读取，但重新打包统一输出 pf8；超 4 GiB 的单个 PFS 条目不支持。
- 当前 PSB 重建支持明文正文以及可自动推导密钥的加密头；加密正文、外置纹理和 `RGBA8` / `DXT5` 以外的 atlas 格式会明确报错，不会静默复制成“已转换”。DXT5 会重新编码，属于有损块压缩。
- 自动文本规则面向常见 Artemis 脚本，发布前仍应在真实游戏中检查字幕、点击区域与动画坐标。

## Credits

感谢以下开源项目。

| 项目 | 许可证 | 在本项目中的用途 | 来源 |
|---|---|---|---|
| **pfs_upk** | GPL-3.0 | pf2 / pf6 / pf8 格式及打包、解包行为参考 | [nextgal/pfs_upk@abdffcb](https://github.com/nextgal/pfs_upk/tree/abdffcbeb3c733ce234aa99ed42b206d13aaed2f) |
| **art3m1s-core** | MPL-2.0 | E-mote PSB v1–v4 结构、加密头、对象及资源表解析参考 | [Alphaly2K/art3m1s-core@0c06f37](https://github.com/Alphaly2K/art3m1s-core/tree/0c06f37160961c9ff75d4937d5e6bb0500d0bef9) |
| **VisualNovelUpscaler** | MIT | Artemis 文本坐标、尺寸与 Ratio 处理规则参考； | [hokejyo/VisualNovelUpscaler@d755913](https://github.com/hokejyo/VisualNovelUpscaler/tree/d755913eb72f739ad4faea70e689cf933ba54c7f) |
| **Avalonia** | MIT | 跨平台桌面 UI（12.1.2） | [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia) |
| **SixLabors.ImageSharp** | Six Labors Split License 1.0 | PNG 解码与 Bicubic 缩放（3.1.12） | [SixLabors/ImageSharp](https://github.com/SixLabors/ImageSharp) |
| **HarfBuzz** | Old MIT | CFF OpenType 字体削减（8.3.1） | [harfbuzz/harfbuzz](https://github.com/harfbuzz/harfbuzz) |
| **Optris.StaticGraphics.Avalonia.Software** | MIT fork 及上游组件许可证 | NativeAOT 静态 Skia / HarfBuzz 图形后端 | [NuGet](https://www.nuget.org/packages/Optris.StaticGraphics.Avalonia.Software) |
| **FFmpeg / ffprobe** | GPL-2.0-or-later Full 构建 | 动画与视频探测、缩放及转码（9.0.1） | [FFmpeg 9.0.1 源码](https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz) |
| **x264** | GPL-2.0 | H.264 编码 | [VideoLAN/x264](https://code.videolan.org/videolan/x264) |
| **libtheora** | BSD-3-Clause | Theora 编码 | [Xiph.Org/libtheora](https://github.com/xiph/theora) |
| **libogg** | BSD-3-Clause | Ogg 容器 | [Xiph.Org/libogg](https://github.com/xiph/ogg) |
| **zlib** | Zlib | PNG 帧序列压缩 | [zlib](https://zlib.net/) |
