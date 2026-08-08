# Generates the application icon.
#
# Design notes, because the previous placeholder failed in ways worth not repeating:
#
#   * It used a flat #2563EB tile. That colour has a relative luminance around 0.15, so against a
#     near-black taskbar the tile read as a dark blob with a smudge in it. The gradient here runs
#     lighter (#5B93F8 -> #1D4ED8) so the mark separates from a dark background, and a translucent
#     white inner rim keeps the tile edge crisp against dark chrome without looking outlined on light.
#
#   * Its glyph was two chevrons about 4% of the icon height apart. At 16 and 20 px - which is what
#     the taskbar and Alt+Tab actually ask for - that gap is under a pixel, so the two strokes merged
#     into one thick illegible mass. The glyph is now two offset cards: two shapes, separated by a
#     gap that stays at least one full pixel at 16 px, which also says "several clips" rather than
#     "download".
#
# Run with Windows PowerShell 5.1, which has System.Drawing available.

#   * -Disabled renders the same geometry with the colour drained out, for the tray icon shown while
#     PasteJump is disabled. Greyscale rather than translucent: Windows' own convention for an inactive
#     icon is desaturation, and a semi-transparent tray icon reads as a rendering fault against some
#     taskbar colours rather than as a state.

#   * -Paused is amber AND swaps the two cards for two pause bars, which is belt and braces on purpose.
#     A badge or a corner dot - the obvious way to mark a state - is what does not work here: at 16 px,
#     the size the tray actually asks for, a badge is about five pixels across and its detail
#     anti-aliases into a smudge, leaving paused indistinguishable from disabled. Hue is the one signal
#     that survives at that size, so it carries the state; the glyph change is there so it also survives
#     for anyone who cannot separate amber from blue, and in a greyscale rendering. Same reasoning as the
#     note above about the original chevrons fusing into one mass.

#   * -PngPath writes a single large PNG as well as the .ico. Needed because a multi-frame .ico is the
#     wrong source for anything that renders the mark at a size Windows did not ask for: WPF's icon
#     decoder picks a frame for you, and without a requested decode size it can pick a small one and
#     scale it up. A one-frame PNG removes the choice. It is also the file to reach for outside the app -
#     a README, a release page, a store listing.

[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\src\PasteJump.App\Assets\pastejump.ico'),
    [string] $PreviewPath,
    [string] $PngPath,
    [int] $PngSize = 256,
    [switch] $Disabled,
    [switch] $Paused
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ($Disabled -and $Paused) {
    throw 'Pass -Disabled or -Paused, not both: they are two different tray states and each needs its own file.'
}

# Tile gradient. The disabled pair is the enabled pair converted to its luminance, so the mark keeps its
# shape and tonal structure and only loses its colour.
if ($Disabled) {
    $tileTop = [System.Drawing.Color]::FromArgb(255, 142, 142, 146)
    $tileBottom = [System.Drawing.Color]::FromArgb(255, 79, 79, 84)
    $cardColour = [System.Drawing.Color]::FromArgb(255, 236, 236, 238)
}
elseif ($Paused) {
    # Amber, and lighter than the blue rather than darker: this has to separate from #1D4ED8 at 16 px on a
    # near-black taskbar, where two similarly dark tiles would read as the same icon.
    $tileTop = [System.Drawing.Color]::FromArgb(255, 251, 191, 36)
    $tileBottom = [System.Drawing.Color]::FromArgb(255, 217, 119, 6)
    $cardColour = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
}
else {
    $tileTop = [System.Drawing.Color]::FromArgb(255, 91, 147, 248)
    $tileBottom = [System.Drawing.Color]::FromArgb(255, 29, 78, 216)
    $cardColour = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
}

function New-RoundedPath {
    param(
        [single] $X, [single] $Y, [single] $W, [single] $H, [single] $Radius
    )

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single]([Math]::Min($Radius, [Math]::Min($W, $H) / 2) * 2)

    if ($d -le 0.5) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF($X, $Y, $W, $H)))
        return $path
    }

    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc(($X + $W - $d), $Y, $d, $d, 270, 90)
    $path.AddArc(($X + $W - $d), ($Y + $H - $d), $d, $d, 0, 90)
    $path.AddArc($X, ($Y + $H - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    return $path
}

function New-PasteJumpBitmap {
    param([int] $Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$Size

    # --- tile -----------------------------------------------------------------
    # Only a hairline inset. The taskbar already pads its icons, so insetting further just throws
    # away pixels that the 16 px frame cannot spare.
    $inset = [single]([Math]::Max(0.5, $s * 0.035))
    $tileW = $s - (2 * $inset)
    $radius = [single]($s * 0.235)

    $tile = New-RoundedPath -X $inset -Y $inset -W $tileW -H $tileW -Radius $radius

    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, $inset)),
        (New-Object System.Drawing.PointF(0, ($inset + $tileW))),
        $script:tileTop,
        $script:tileBottom)

    $g.FillPath($gradient, $tile)

    # Inner rim: reads as a lit edge on dark chrome, near-invisible on light. Skipped under 24 px,
    # where a sub-pixel translucent stroke only muddies the corners.
    if ($Size -ge 24) {
        $rimPen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(70, 255, 255, 255),
            [single]([Math]::Max(1.0, $s * 0.018)))
        $g.DrawPath($rimPen, $tile)
        $rimPen.Dispose()
    }

    # --- glyph: two pause bars, for the paused tray state ---------------------
    # Sized so the gap between the bars stays at least one whole pixel at 16 px, the same floor the cards
    # below are built to. 0.10 * 16 = 1.6 px of gap and 0.13 * 16 = 2.1 px of bar.
    if ($script:Paused) {
        $barW = [single]($s * 0.13)
        $barH = [single]($s * 0.42)
        $barGap = [single]([Math]::Max(1.0, $s * 0.10))
        $barR = [single]($s * 0.03)

        $barsW = [single](2 * $barW + $barGap)
        $barX = [single](($s - $barsW) / 2)
        $barY = [single](($s - $barH) / 2)

        $barBrush = New-Object System.Drawing.SolidBrush($script:cardColour)

        foreach ($i in 0, 1) {
            $bar = New-RoundedPath `
                -X ($barX + $i * ($barW + $barGap)) -Y $barY `
                -W $barW -H $barH -Radius $barR
            $g.FillPath($barBrush, $bar)
            $bar.Dispose()
        }

        $barBrush.Dispose()
        $gradient.Dispose()
        $tile.Dispose()
        $g.Dispose()

        return $bmp
    }

    # --- glyph: two offset cards ---------------------------------------------
    # Both cards are solid white and the gap between them is a *filled* shape in the tile colour,
    # not a stroked outline. That distinction is the whole trick: a hairline stroke anti-aliases to
    # a grey haze below about 24 px and the two cards fuse into one blob, which is exactly how the
    # previous icon failed. A filled carve-out is at least one whole pixel wide at every size.
    $cardW = [single]($s * 0.44)
    $cardH = [single]($s * 0.325)
    $cardR = [single]($s * 0.07)

    $offset = [single]([Math]::Max(1.5, $s * 0.105))
    $gap = [single]([Math]::Max(1.0, $s * 0.035))

    $centreX = [single](($s - $cardW) / 2)
    $centreY = [single](($s - $cardH) / 2)

    $backX = [single]($centreX - $offset / 2)
    $backY = [single]($centreY - $offset / 2)
    $frontX = [single]($centreX + $offset / 2)
    $frontY = [single]($centreY + $offset / 2)

    $white = New-Object System.Drawing.SolidBrush($script:cardColour)

    # 1. The back card.
    $backPath = New-RoundedPath -X $backX -Y $backY -W $cardW -H $cardH -Radius $cardR
    $g.FillPath($white, $backPath)

    # 2. Carve a tile-coloured gap where the front card is about to go. Filled with the same gradient
    #    so it reads as the tile showing through rather than as a grey line.
    $carvePath = New-RoundedPath `
        -X ($frontX - $gap) -Y ($frontY - $gap) `
        -W ($cardW + 2 * $gap) -H ($cardH + 2 * $gap) `
        -Radius ($cardR + $gap)
    $g.FillPath($gradient, $carvePath)

    # 3. The front card, sitting inside that gap.
    $frontPath = New-RoundedPath -X $frontX -Y $frontY -W $cardW -H $cardH -Radius $cardR
    $g.FillPath($white, $frontPath)

    $white.Dispose()
    $backPath.Dispose()
    $carvePath.Dispose()
    $frontPath.Dispose()
    $gradient.Dispose()
    $tile.Dispose()
    $g.Dispose()

    return $bmp
}

# Assemble a multi-resolution ICO with PNG-compressed frames (supported since Vista).
# Doing it by hand rather than via Icon.FromHandle, which can only emit a single size.
#
# 20, 24 and 40 are included because Windows genuinely asks for them: 20 for some Alt+Tab and
# jump-list contexts, 24 for the tray at 125% scaling, 40 for the taskbar at 150%. Omitting them
# leaves Windows to downscale 32 or 48, which is exactly where a thin glyph turns to mush.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()

foreach ($size in $sizes) {
    $bmp = New-PasteJumpBitmap -Size $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $bmp.Dispose()
}

$outDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$fs = [System.IO.File]::Create($OutputPath)
$bw = New-Object System.IO.BinaryWriter($fs)

try {
    # ICONDIR
    $bw.Write([UInt16]0)                 # reserved
    $bw.Write([UInt16]1)                 # type: icon
    $bw.Write([UInt16]$frames.Count)

    # ICONDIRENTRY records. Data begins after the directory.
    $offset = 6 + (16 * $frames.Count)

    foreach ($frame in $frames) {
        # 256 is encoded as 0 in the single width/height bytes.
        $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }

        $bw.Write([Byte]$dim)            # width
        $bw.Write([Byte]$dim)            # height
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

Write-Output "Wrote $OutputPath ($((Get-Item $OutputPath).Length) bytes, $($frames.Count) frames)"

# Large single-frame PNG. Drawn at the requested size rather than upscaled from an icon frame, so it is
# genuinely that resolution.
if ($PngPath) {
    $pngDir = Split-Path -Parent $PngPath
    if ($pngDir -and -not (Test-Path $pngDir)) { New-Item -ItemType Directory -Path $pngDir -Force | Out-Null }

    $big = New-PasteJumpBitmap -Size $PngSize
    $big.Save($PngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $big.Dispose()

    Write-Output "Wrote $PngPath ($((Get-Item $PngPath).Length) bytes, ${PngSize}x${PngSize})"
}

# Optional contact sheet, for checking the small sizes against both taskbar colours rather than
# judging the 256 px frame and hoping.
if ($PreviewPath) {
    $strip = New-Object System.Drawing.Bitmap(520, 200)
    $pg = [System.Drawing.Graphics]::FromImage($strip)

    $darkBg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
    $lightBg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 243, 243, 243))
    $pg.FillRectangle($darkBg, 0, 0, 520, 100)
    $pg.FillRectangle($lightBg, 0, 100, 520, 100)

    foreach ($row in 0, 1) {
        $x = 20
        foreach ($size in @(16, 20, 24, 32, 40, 48, 64)) {
            $bmp = New-PasteJumpBitmap -Size $size
            $pg.DrawImage($bmp, $x, ($row * 100 + 50 - [int]($size / 2)), $size, $size)
            $bmp.Dispose()
            $x += $size + 18
        }
    }

    $pg.Dispose()
    $darkBg.Dispose()
    $lightBg.Dispose()
    $strip.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $strip.Dispose()

    Write-Output "Wrote preview $PreviewPath"
}
