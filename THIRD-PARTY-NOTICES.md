# Third-party notices

This project contains original C# implementation informed by the observable formats and workflows below. Source snapshots are pinned for reproducibility; the upstream projects are not bundled as Git submodules.

| Project | Pinned source | License | Use |
|---|---|---|---|
| pfs_upk | https://github.com/nextgal/pfs_upk/tree/abdffcbeb3c733ce234aa99ed42b206d13aaed2f | GPL-3.0 | pf2/pf6/pf8 behavior reference |
| VisualNovelUpscaler | https://github.com/hokejyo/VisualNovelUpscaler/tree/d755913eb72f739ad4faea70e689cf933ba54c7f | MIT | Artemis text/ratio behavior reference; no waifu2x code |
| Avalonia | https://github.com/AvaloniaUI/Avalonia | MIT | Cross-platform UI, version 12.1.2 |
| SixLabors.ImageSharp | https://github.com/SixLabors/ImageSharp | Six Labors Split License 1.0 | PNG decode/resize, version 3.1.12 |
| FFmpeg | https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz | LGPL v2.1+ build | Separate command-line video processor |
| Optris.StaticGraphics.Avalonia.Software | https://www.nuget.org/packages/Optris.StaticGraphics.Avalonia.Software | MIT fork plus upstream licenses | Cross-platform static NativeAOT Skia/HarfBuzz linkage |
| SkiaSharp / Skia | https://github.com/mono/SkiaSharp | MIT and upstream notices | Native graphics implementation, version 3.119.4 |
| HarfBuzzSharp / HarfBuzz | https://github.com/mono/SkiaSharp | MIT and upstream notices | Text shaping implementation, version 8.3.1.3 |
| Inter | https://github.com/rsms/inter | SIL Open Font License 1.1 | Bundled Avalonia UI font |
| .NET Runtime | https://github.com/dotnet/runtime | MIT and third-party notices | NativeAOT runtime components |

The complete GPL-3.0 license is in `LICENSE`. Official release archives contain a `licenses` directory with the applicable runtime license and copyright texts. Reference-project licenses are included for attribution even though their repositories are not bundled. Release assets also include generated SBOM data, the exact FFmpeg `-buildconf` output, an unmodified-source statement, and the corresponding FFmpeg 9.0.1 source archive. FFmpeg distributions must not enable GPL-only or nonfree components.
