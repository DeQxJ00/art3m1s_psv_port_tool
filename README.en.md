# art3m1s_psv_port_tool

[简体中文](README.md) · [English](README.en.md)

[![CI](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/ci.yml/badge.svg)](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/ci.yml)
[![Release](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/release.yml/badge.svg)](https://github.com/DeQxJ00/art3m1s_psv_port_tool/actions/workflows/release.yml)

A PSV porting assistant for Artemis-engine games. It extracts each physical PFS independently, resizes selected assets, and rebuilds it under the exact same file name. Loose files and directories are copied into the output project as well. The app uses .NET 10, Avalonia 12, C# 14, and NativeAOT on Windows, Linux, and macOS.

> Only process game assets you are authorized to modify, and keep a backup. This tool does not bypass platform signing, licensing encryption, or DRM.

## UI screenshots

![English dark UI](docs/screenshots/ui-en-US-dark.png)

[中文深色](docs/screenshots/ui-zh-CN-dark.png) · [English dark](docs/screenshots/ui-en-US-dark.png) · [中文浅色](docs/screenshots/ui-zh-CN-light.png) · [English light](docs/screenshots/ui-en-US-light.png)

## Downloads and platform support

Releases provide NativeAOT single files for `win-x64` and `linux-x64`, plus unsigned and unnotarized `.app.zip` bundles for `osx-x64` and `osx-arm64`. macOS may require manual approval under Privacy & Security. Video conversion uses embedded FFmpeg/ffprobe 9.0.1 LGPL builds; development builds may use `ART3M1S_FFMPEG` and `ART3M1S_FFPROBE`.

## Usage

1. Select a game root containing `xxxxx.pfs` and a different output directory.
2. Scan and verify the independent PFS count and detected resolution.
3. Set Ratio and select Text, Images, Animation, and Video.
4. Start conversion. If output exists, click Start once more to confirm replacement.

The converter writes to a dedicated sibling staging directory and replaces the destination only after every operation succeeds. Cancellation or failure removes temporary output.

## Ratio and PSV resolution guidance

The default is `0.5`; valid input is `0 < Ratio ≤ 1`. Presets are `0.75 · 720p`, `0.5 · 1080p`, `0.375 · 2K`, and `0.25 · 4K`. Aim for “game resolution × Ratio” near PSV `960×540` (or `960×544`). Larger images can exhaust PSV memory. Resolution is read from `[WINDOWS]` in root `system.ini`; otherwise inspect a PNG under extracted `image/bg`.

## PFS recognition

Top-level files named `xxxxx.pfs` or `xxxxx.pfs.000`–`.999` with a `pf2`, `pf6`, or `pf8` header are accepted. Numeric suffixes may be sparse and prefixes may differ. Every existing physical file is an independent archive: files are never concatenated, missing numbers are never created, and names are never changed. Entry order, raw path bytes, and directory structure are retained; rebuilt archives use encrypted pf8. Absolute and `..` traversal paths are rejected.

## Text, image, animation, and video rules

- Text: scales common Artemis coordinates, sizes, and font values in INI / TBL / IPT / AST / LUA while preserving UTF-8 / Shift_JIS, BOM, and line endings. IET is copied unchanged.
- Images: ImageSharp Bicubic PNG resize only. No PNG optimization, palette compression, waifu2x, or lossy compression.
- Animation: Bicubic OGV resize through FFmpeg, retaining frame rate and audio.
- Video: WMV / DAT / MP4 / AVI / MPG / MKV retain the original video codec where encodable and copy audio; WMV3 becomes WMV2.
- Fonts (optional): trims TrueType `glyf` outlines to common Simplified Chinese, Japanese, or Traditional Chinese sets while always retaining characters actually used by scripts, ASCII, common punctuation, and full/half-width symbols. Composite dependencies are retained recursively. CFF/CFF2, TTC, and variable fonts remain unchanged for safety.
- Unselected asset types are copied byte-for-byte.

Default parallelism is `max(1, logical CPU count - 1)`, with at most two simultaneous video jobs.

## PNG palette and transparency guarantees

RGB24 stays RGB24 without Alpha; RGBA, grayscale, and transparent grayscale retain their color type. Indexed PNGs keep their original 1/2/4/8-bit depth. Original `PLTE` and `tRNS` chunks are written back byte-for-byte—the built-in palette is never edited, reordered, or reduced. Bicubic pixels are mapped back to the original palette and only pixel indices change. PNG coordinate text is scaled; other metadata is retained where possible.

## `system.ini [VITA]`

An existing `[VITA]` section is left completely untouched. If absent, the PSV template is appended even when Text is unchecked: `WIDTH=960`, `HEIGHT=540`, `SIDECUT=0`, `BOOT=system/first.iet`, and `FONT_CACHE_SIZE=25165824`. `CHARSET` comes from `[WINDOWS]`, then the first existing `CHARSET`, then defaults to `Shift_JIS`.

## Build, tests, and GitHub Actions

.NET SDK 10.0.303 is required:

```powershell
dotnet restore art3m1s_psv_port_tool.slnx
dotnet test art3m1s_psv_port_tool.slnx -c Release
dotnet publish src/Art3m1s.PsvTool.App -c Release -r win-x64
./scripts/generate-screenshots.ps1
```

`ci.yml` checks Release builds, tests, formatting, screenshot baselines, and multi-platform NativeAOT. `release.yml` builds four targets for `v*` tags or manual runs, then publishes checksums, SBOM, licenses, and FFmpeg build/source information.

## Known limitations

- The first macOS release is unsigned and unnotarized.
- Unsupported video container/codec combinations stop with the original FFmpeg diagnostic instead of silently degrading.
- pf2/pf6 can be read, while rebuilt files are pf8. Individual PFS entries over 4 GiB are unsupported.
- Automatic text rules target common Artemis scripts; verify subtitles, hit areas, and animation coordinates in the actual game.

## Sources, licenses, and FFmpeg compliance

This is an independent C# rewrite/adaptation licensed [GPL-3.0-or-later](LICENSE). PFS behavior references [nextgal/pfs_upk@abdffcb](https://github.com/nextgal/pfs_upk/tree/abdffcbeb3c733ce234aa99ed42b206d13aaed2f) (GPL-3.0); text and Ratio behavior references [VisualNovelUpscaler@d755913](https://github.com/hokejyo/VisualNovelUpscaler/tree/d755913eb72f739ad4faea70e689cf933ba54c7f) (MIT); image and concurrency behavior references [ArtemisTools@3333dea](https://github.com/DeQxJ00/ArtemisTools/tree/3333dea5b27a36c49e45035bf02f5f0a1b0f83e8) (GPL-3.0). No waifu2x implementation is included. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Release builds may only embed FFmpeg 9.0.1 LGPL builds made without `--enable-gpl` or `--enable-nonfree`; build configuration and corresponding source or source offer ship alongside the binaries. See [FFmpeg Legal](https://ffmpeg.org/legal.html).
