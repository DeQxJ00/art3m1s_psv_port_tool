param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [string]$FfmpegSourceArchive,

    [string]$FfmpegVersion = '9.0.1'
)

$ErrorActionPreference = 'Stop'
$publishPath = [IO.Path]::GetFullPath($PublishDirectory)
$sourcePath = [IO.Path]::GetFullPath($FfmpegSourceArchive)
$licensePath = Join-Path $publishPath 'licenses'
$nugetPath = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget/packages'
} else {
    [IO.Path]::GetFullPath($env:NUGET_PACKAGES)
}

New-Item -ItemType Directory -Path $licensePath -Force | Out-Null

function Copy-PackageLicense {
    param([string]$Package, [string]$Version, [string]$SourceName, [string]$DestinationName)
    $source = Join-Path $nugetPath "$Package/$Version/$SourceName"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required package license was not found: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $licensePath $DestinationName) -Force
}

function Save-PinnedLicense {
    param([string]$Uri, [string]$DestinationName)
    Invoke-WebRequest -Uri $Uri -OutFile (Join-Path $licensePath $DestinationName)
}

Copy-PackageLicense 'sixlabors.imagesharp' '3.1.12' 'LICENSE' 'ImageSharp-LICENSE.txt'
Copy-PackageLicense 'skiasharp' '3.119.4' 'LICENSE.txt' 'SkiaSharp-MIT.txt'
Copy-PackageLicense 'harfbuzzsharp' '8.3.1.3' 'LICENSE.txt' 'HarfBuzzSharp-MIT.txt'
$harfBuzzNativePackage = if ($RuntimeIdentifier.StartsWith('win-')) {
    'harfbuzzsharp.nativeassets.win32'
} elseif ($RuntimeIdentifier.StartsWith('linux-')) {
    'harfbuzzsharp.nativeassets.linux'
} elseif ($RuntimeIdentifier.StartsWith('osx-')) {
    'harfbuzzsharp.nativeassets.macos'
} else {
    throw "Unsupported runtime identifier for HarfBuzz notices: $RuntimeIdentifier"
}
Copy-PackageLicense $harfBuzzNativePackage '8.3.1.3' 'LICENSE.txt' 'HarfBuzzSharp-NativeAssets-MIT.txt'
Copy-PackageLicense $harfBuzzNativePackage '8.3.1.3' 'THIRD-PARTY-NOTICES.txt' 'HarfBuzzSharp-NativeAssets-THIRD-PARTY-NOTICES.txt'

$runtimeRoot = Join-Path $nugetPath "microsoft.netcore.app.runtime.$RuntimeIdentifier"
$runtimePackage = Get-ChildItem -LiteralPath $runtimeRoot -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if ($null -eq $runtimePackage) {
    throw "The .NET runtime pack was not found for $RuntimeIdentifier."
}
Copy-Item -LiteralPath (Join-Path $runtimePackage.FullName 'LICENSE.TXT') -Destination (Join-Path $licensePath 'dotnet-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $runtimePackage.FullName 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licensePath 'dotnet-THIRD-PARTY-NOTICES.txt') -Force

Save-PinnedLicense 'https://raw.githubusercontent.com/AvaloniaUI/Avalonia/d3c867a9e2de379249b03dbeb3495bd7f076a81a/licence.md' 'Avalonia-MIT.md'
Save-PinnedLicense 'https://raw.githubusercontent.com/Optris/Optris.StaticGraphics.Avalonia/f47e14e204da8f2f177f6366740a3eda78d05d51/LICENSE' 'Optris-MIT.txt'
Save-PinnedLicense 'https://raw.githubusercontent.com/Optris/Optris.StaticGraphics.Avalonia/f47e14e204da8f2f177f6366740a3eda78d05d51/NOTICE.md' 'Optris-NOTICE.md'
Save-PinnedLicense 'https://raw.githubusercontent.com/rsms/inter/v4.1/LICENSE.txt' 'Inter-OFL-1.1.txt'
Save-PinnedLicense 'https://raw.githubusercontent.com/nextgal/pfs_upk/abdffcbeb3c733ce234aa99ed42b206d13aaed2f/LICENSE' 'pfs_upk-GPL-3.0.txt'
Save-PinnedLicense 'https://raw.githubusercontent.com/Alphaly2K/art3m1s-core/0c06f37160961c9ff75d4937d5e6bb0500d0bef9/LICENSE' 'art3m1s-core-MPL-2.0.txt'
Save-PinnedLicense 'https://raw.githubusercontent.com/hokejyo/VisualNovelUpscaler/d755913eb72f739ad4faea70e689cf933ba54c7f/LICENSE' 'VisualNovelUpscaler-MIT.txt'
Save-PinnedLicense 'https://raw.githubusercontent.com/xiph/ogg/v1.3.6/COPYING' 'libogg-BSD-3-Clause.txt'
Save-PinnedLicense 'https://raw.githubusercontent.com/xiph/theora/8e4808736e9c181b971306cc3f05df9e61354004/COPYING' 'libtheora-BSD-3-Clause.txt'
Save-PinnedLicense 'https://code.videolan.org/videolan/x264/-/raw/b35605ace3ddf7c1a5d67a2eb553f034aef41d55/COPYING' 'x264-GPL-2.0.txt'
Save-PinnedLicense 'https://raw.githubusercontent.com/madler/zlib/v1.3.2/LICENSE' 'zlib-LICENSE.txt'

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "FFmpeg source archive was not found: $sourcePath"
}
$extractPath = Join-Path $licensePath '.ffmpeg-license-extract'
New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
try {
    & tar -xf $sourcePath -C $extractPath "ffmpeg-$FfmpegVersion/LICENSE.md" "ffmpeg-$FfmpegVersion/COPYING.GPLv2"
    if ($LASTEXITCODE -ne 0) { throw "Unable to extract FFmpeg license files (tar exit code $LASTEXITCODE)." }
    Copy-Item -LiteralPath (Join-Path $extractPath "ffmpeg-$FfmpegVersion/LICENSE.md") -Destination (Join-Path $licensePath 'FFmpeg-LICENSE.md') -Force
    Copy-Item -LiteralPath (Join-Path $extractPath "ffmpeg-$FfmpegVersion/COPYING.GPLv2") -Destination (Join-Path $licensePath 'FFmpeg-COPYING.GPLv2') -Force
} finally {
    if (Test-Path -LiteralPath $extractPath) {
        Remove-Item -LiteralPath $extractPath -Recurse -Force
    }
}

@"
FFmpeg $FfmpegVersion source changes

The official ffmpeg-$FfmpegVersion.tar.xz release archive is built without source modifications.
The GPL Full build links the pinned x264, libtheora, libogg, and zlib sources listed on the Release page.
Configure and compiler options are recorded in FFMPEG-BUILD-CONFIG.txt.
Source archive SHA-256: CF38E0E28C7E5605942C4A77755349B0145804A397AF37EB1FB4C77CB237F635
"@ | Set-Content -LiteralPath (Join-Path $licensePath 'FFmpeg-CHANGES.txt') -Encoding utf8NoBOM

@"
Third-party licenses bundled with art3m1s_psv_port_tool

This directory contains the license and copyright notices for runtime dependencies and credited reference projects.
The application itself is licensed under GPL-3.0-or-later; see LICENSE in the package root.
The exact FFmpeg 9.0.1, x264, libtheora, libogg, and zlib source archives and SHA-256 values are published as assets on the same GitHub Release page.
"@ | Set-Content -LiteralPath (Join-Path $licensePath 'README.txt') -Encoding utf8NoBOM

Write-Host "Collected third-party licenses in $licensePath"
