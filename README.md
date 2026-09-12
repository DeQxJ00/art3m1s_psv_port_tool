# art3m1s_psv_port_tool

[简体中文](README.md) · [English](README.en.md)

[![CI](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/ci.yml/badge.svg)](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/ci.yml)
[![Release](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/release.yml/badge.svg)](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/release.yml)

面向 Artemis 引擎游戏的 PSV 移植辅助工具。它逐个解包物理 PFS、按选择缩小资源，再以相同文件名重新打包；PFS 之外的文件与目录也会复制到输出项目。应用使用 .NET 10、Avalonia 12、C# 14 与 NativeAOT，支持 Windows、Linux 和 macOS。

> 请只处理你有权修改的游戏资源，并先备份原项目。工具不绕过平台签名、加密授权或 DRM。

## UI 截图

![中文深色界面](docs/screenshots/ui-zh-CN-dark.png)

[中文深色](docs/screenshots/ui-zh-CN-dark.png) · [English dark](docs/screenshots/ui-en-US-dark.png) · [中文浅色](docs/screenshots/ui-zh-CN-light.png) · [English light](docs/screenshots/ui-en-US-light.png)

## 下载与平台支持

Release 提供 `win-x64`、`linux-x64` NativeAOT 单文件，以及未签名、未公证的 `osx-x64` / `osx-arm64` `.app.zip`。macOS 首次启动可能需要在“隐私与安全性”中手动允许。视频转换使用发行包内嵌的 FFmpeg/ffprobe 9.0.1 LGPL 构建；开发构建也可通过 `ART3M1S_FFMPEG` 与 `ART3M1S_FFPROBE` 指定工具。

## 使用步骤

1. 选择包含 `xxxxx.pfs` 的游戏根目录与不同的输出目录。
2. 扫描并确认独立 PFS 数量及检测到的分辨率。
3. 设置 Ratio，勾选文本、图片、动画和视频类型。
4. 开始转换；输出目录已存在时需要再次点击确认覆盖。

转换先写入输出目录同级的专用临时目录，全部成功后再替换目标；取消或失败会清理临时结果。

## Ratio 与 PSV 分辨率建议

默认 `0.5`，允许输入 `0 < Ratio ≤ 1`。参考按钮为 `0.75 · 720p`、`0.5 · 1080p`、`0.375 · 2K`、`0.25 · 4K`。以“原游戏分辨率 × Ratio”接近 PSV 的 `960×540`（或 `960×544`）为宜；设置过大会增加显存和内存压力。分辨率从根目录 `system.ini` 的 `[WINDOWS]` 读取；无法读取时可参考解包后的 `image/bg` PNG。

## PFS 文件识别规则

根目录中名称匹配 `xxxxx.pfs` 或 `xxxxx.pfs.000`–`.999` 且文件头为 `pf2`、`pf6`、`pf8` 的文件会被处理。数字允许跳号，也允许多个前缀。每个现有物理文件都是独立归档：不拼接、不补号、不改名，条目顺序、原始路径字节与目录结构保持不变，输出统一为加密 `pf8`。解包会阻止绝对路径及 `..` 路径穿越。

## 资源处理规则

- 文本：INI / TBL / IPT / AST / LUA 的 Artemis 坐标、尺寸与字号按 Ratio 缩放，保留 UTF-8 / Shift_JIS、BOM 和换行；IET 原样复制。
- 图片：PNG 使用 ImageSharp Bicubic，仅缩放，不进行 PNG 优化、调色板压缩、waifu2x 或有损压缩。
- 动画：OGV 使用 FFmpeg Bicubic，保持帧率与音频。
- 视频：WMV / DAT / MP4 / AVI / MPG / MKV 尽量保持原视频编码并复制音频；WMV3 转为 WMV2。
- 字体（可选）：对 TrueType `glyf` 字体按简体中文、日文或繁体中文常用范围削减轮廓，同时始终保留脚本中实际出现的字符、ASCII、常用标点与全角/半角符号；复合字形依赖会递归保留。CFF/CFF2、TTC 与可变字体为避免损坏会原样保留。
- 未勾选类型按字节复制，不执行转换。

并行度默认为 `max(1, CPU 逻辑核心数 - 1)`，视频任务最多同时两个。

## PNG 颜色表与透明度保证

RGB24 输出仍为 RGB24，不增加 Alpha；RGBA、灰度、透明灰度保持颜色类型。索引色保持原 1/2/4/8-bit 位深，原 `PLTE` 与 `tRNS` 块逐字节写回，不修改、重排或删减自带颜色表。Bicubic 结果仅映射回原颜色表并重写像素索引。PNG 坐标文本按 Ratio 缩放，其他元数据尽量保留。

## `system.ini [VITA]`

已有 `[VITA]` 时完全不改。不存在时，无论文本项是否勾选，都在文件末尾追加 PSV 模板：`WIDTH=960`、`HEIGHT=540`、`SIDECUT=0`、`BOOT=system/first.iet`、`FONT_CACHE_SIZE=25165824`。`CHARSET` 优先沿用 `[WINDOWS]`，其次使用文件中首个 `CHARSET`，都没有时使用 `Shift_JIS`。

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
- 自动文本规则面向常见 Artemis 脚本，发布前仍应在真实游戏中检查字幕、点击区域与动画坐标。

## 开源来源、许可证与 FFmpeg 合规

本项目是独立的 C# 重写与适配，整体采用 [GPL-3.0-or-later](LICENSE)。PFS 行为参考 [nextgal/pfs_upk@abdffcb](https://github.com/nextgal/pfs_upk/tree/abdffcbeb3c733ce234aa99ed42b206d13aaed2f)（GPL-3.0）；文本与 Ratio 规则参考 [VisualNovelUpscaler@d755913](https://github.com/hokejyo/VisualNovelUpscaler/tree/d755913eb72f739ad4faea70e689cf933ba54c7f)（MIT）；并行与图片流程参考 [ArtemisTools@3333dea](https://github.com/DeQxJ00/ArtemisTools/tree/3333dea5b27a36c49e45035bf02f5f0a1b0f83e8)（GPL-3.0）。没有移植 waifu2x 逻辑。完整说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

发行版只允许嵌入未启用 `--enable-gpl`、`--enable-nonfree` 的 FFmpeg 9.0.1 LGPL 构建，同时附带构建配置、对应源码包或源码获取方式。详见 [FFmpeg Legal](https://ffmpeg.org/legal.html)。
