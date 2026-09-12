# Third-party notices

This project contains original C# implementation informed by the observable formats and workflows below. Source snapshots are pinned for reproducibility; the upstream projects are not bundled as Git submodules.

| Project | Pinned source | License | Use |
|---|---|---|---|
| pfs_upk | https://github.com/nextgal/pfs_upk/tree/abdffcbeb3c733ce234aa99ed42b206d13aaed2f | GPL-3.0 | pf2/pf6/pf8 behavior reference |
| VisualNovelUpscaler | https://github.com/hokejyo/VisualNovelUpscaler/tree/d755913eb72f739ad4faea70e689cf933ba54c7f | MIT | Artemis text/ratio behavior reference; no waifu2x code |
| ArtemisTools | https://github.com/DeQxJ00/ArtemisTools/tree/3333dea5b27a36c49e45035bf02f5f0a1b0f83e8 | GPL-3.0 | resize and bounded-concurrency behavior reference |
| Avalonia | https://github.com/AvaloniaUI/Avalonia | MIT | Cross-platform UI, version 12.1.2 |
| SixLabors.ImageSharp | https://github.com/SixLabors/ImageSharp | Six Labors Split License 1.0 | PNG decode/resize, version 3.1.12 |
| FFmpeg | https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz | LGPL v2.1+ build | Separate command-line video processor |
| Optris.StaticGraphics.Avalonia.Software | https://www.nuget.org/packages/Optris.StaticGraphics.Avalonia.Software | MIT fork plus upstream licenses | Cross-platform static NativeAOT Skia/HarfBuzz linkage |

The complete GPL-3.0 license is in `LICENSE`. NuGet package licenses remain under their respective terms. Release archives include generated package/SBOM data and the exact FFmpeg `-buildconf` output. FFmpeg distributions must not enable GPL-only or nonfree components and must provide the corresponding FFmpeg 9.0.1 source under LGPL requirements.
