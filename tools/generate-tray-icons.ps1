# Builds the two notification-area icons from the monochrome source glyphs.
#
# Why two, and why not just reuse the application icon:
#
#   The notification area is the one place in Windows whose background colour changes underneath you.
#   The taskbar follows SystemUsesLightTheme, so a single icon is guaranteed to be wrong half the
#   time - which is what "the icon is not at all looking on dark taskbar" was about. The convention
#   there is monochrome line art supplied once per taskbar colour, and Clipjog picks between them at
#   runtime. The coloured tile stays as the executable/Alt+Tab icon, where a single icon is correct.
#
# Source glyphs are named for their STROKE colour, and the outputs for the TASKBAR they belong on -
# they are deliberately crossed over, and conflating the two is an easy way to ship an invisible icon:
#
#   tray-glyph-dark-strokes.png   (dark ink) -> tray-on-light.ico
#   tray-glyph-light-strokes.png  (light ink) -> tray-on-dark.ico
#
# Run with Windows PowerShell 5.1, which has System.Drawing available.

[CmdletBinding()]
param(
    [string] $AssetsPath,
    [string] $PreviewPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $AssetsPath) {
    throw 'Pass -AssetsPath, e.g. src\Clipjog.App\Assets. ($PSScriptRoot is not reliable in a param default under -File.)'
}

# 16 is the base tray size, 20/24 cover 125% and 150% scaling, 32 is the source resolution.
# Supplying them explicitly matters more here than for the app icon: the shell would otherwise
# downscale 32 to 16 with a plain box filter, and 1px line art does not survive that.
$sizes = @(16, 20, 24, 32)

function New-ScaledFrame {
    param([System.Drawing.Image] $Source, [int] $Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)

    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        if ($Size -eq $Source.Width) {
            # Exact size: copy the pixels rather than resampling them, so the original crisp strokes
            # survive untouched.
            $g.DrawImageUnscaled($Source, 0, 0)
        }
        else {
            $g.DrawImage($Source, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)))
        }
    }
    finally {
        $g.Dispose()
    }

    return $bmp
}

function Write-Ico {
    param([string] $SourcePng, [string] $OutputIco)

    $source = [System.Drawing.Image]::FromFile($SourcePng)
    $frames = @()

    try {
        foreach ($size in $sizes) {
            $frame = New-ScaledFrame -Source $source -Size $size
            $stream = New-Object System.IO.MemoryStream
            $frame.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += , @{ Size = $size; Bytes = $stream.ToArray() }
            $stream.Dispose()
            $frame.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }

    $fs = [System.IO.File]::Create($OutputIco)
    $bw = New-Object System.IO.BinaryWriter($fs)

    try {
        $bw.Write([UInt16]0)                 # reserved
        $bw.Write([UInt16]1)                 # type: icon
        $bw.Write([UInt16]$frames.Count)

        $offset = 6 + (16 * $frames.Count)

        foreach ($frame in $frames) {
            $bw.Write([Byte]$frame.Size)     # width
            $bw.Write([Byte]$frame.Size)     # height
            $bw.Write([Byte]0)               # palette count
            $bw.Write([Byte]0)               # reserved
            $bw.Write([UInt16]1)             # colour planes
            $bw.Write([UInt16]32)            # bits per pixel
            $bw.Write([UInt32]$frame.Bytes.Length)
            $bw.Write([UInt32]$offset)

            $offset += $frame.Bytes.Length
        }

        foreach ($frame in $frames) {
            $bw.Write($frame.Bytes)
        }
    }
    finally {
        $bw.Dispose()
        $fs.Dispose()
    }

    Write-Output "Wrote $OutputIco ($((Get-Item $OutputIco).Length) bytes, $($frames.Count) frames)"
}

$pairs = @(
    @{ Png = 'tray-glyph-dark-strokes.png'; Ico = 'tray-on-light.ico' }
    @{ Png = 'tray-glyph-light-strokes.png'; Ico = 'tray-on-dark.ico' }
)

foreach ($pair in $pairs) {
    $png = Join-Path $AssetsPath $pair.Png

    if (-not (Test-Path $png)) {
        throw "Source glyph not found: $png"
    }

    Write-Ico -SourcePng $png -OutputIco (Join-Path $AssetsPath $pair.Ico)
}

# Contact sheet: each glyph shown on the taskbar colour it is meant for, at real tray sizes. The
# point is to catch a glyph that has gone invisible or mushy, which the 32 px source never shows.
if ($PreviewPath) {
    $strip = New-Object System.Drawing.Bitmap(360, 160)
    $pg = [System.Drawing.Graphics]::FromImage($strip)

    $rows = @(
        @{ Png = 'tray-glyph-light-strokes.png'; Back = [System.Drawing.Color]::FromArgb(255, 25, 25, 25) }
        @{ Png = 'tray-glyph-dark-strokes.png'; Back = [System.Drawing.Color]::FromArgb(255, 243, 243, 243) }
    )

    $y = 0

    foreach ($row in $rows) {
        $brush = New-Object System.Drawing.SolidBrush($row.Back)
        $pg.FillRectangle($brush, 0, $y, 360, 80)
        $brush.Dispose()

        $source = [System.Drawing.Image]::FromFile((Join-Path $AssetsPath $row.Png))
        $x = 24

        foreach ($size in $sizes) {
            $frame = New-ScaledFrame -Source $source -Size $size
            $pg.DrawImageUnscaled($frame, $x, ($y + 40 - [int]($size / 2)))
            $frame.Dispose()
            $x += $size + 32
        }

        $source.Dispose()
        $y += 80
    }

    $pg.Dispose()
    $strip.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $strip.Dispose()

    Write-Output "Wrote preview $PreviewPath"
}
