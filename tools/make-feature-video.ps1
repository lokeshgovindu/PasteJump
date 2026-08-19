<#
.SYNOPSIS
    Builds a captioned feature tour of PasteJump as an MP4, from screenshots the UI smoke harness renders.

.DESCRIPTION
    The frames are REAL screenshots of the real windows. The harness renders every window and state, so this
    cannot drift from the application the way a hand-recorded video does, and re-running it after a UI change
    produces a current video with no re-recording.

    What this is NOT: a screen recording. The defining feature is a keyboard gesture on a live desktop, and no
    script can hold Ctrl down for a camera. For a live demo of the gesture itself, follow the storyboard in
    docs/video-script.md and record it by hand.

.PARAMETER OutputPath
    Where the .mp4 goes, relative to the repository. Defaults to artifacts/PasteJump-tour.mp4.

.PARAMETER Seconds
    How long each frame is shown.

.PARAMETER KeepFrames
    Leaves the composed PNG frames on disk, which is how you check one before watching a whole video.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = 'artifacts/PasteJump-tour.mp4',
    [double] $Seconds = 3.5,
    [switch] $KeepFrames
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
$shots = Join-Path $env:TEMP 'pastejump-tour-shots'
$frames = Join-Path $env:TEMP 'pastejump-tour-frames'

foreach ($dir in @($shots, $frames)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir | Out-Null
}

# The tour, in order. Each entry is the harness's shot name, a title, and one line saying why the feature exists
# rather than what it is called - a caption reading "History window" teaches nobody anything.
$tour = @(
    @{ Shot = $null; Title = 'PasteJump'; Caption = 'A keyboard-driven clipboard manager for Windows' }
    @{ Shot = 'Light-OverlayWindow-TextFacts'; Title = 'The gesture'; Caption = 'Hold Ctrl, tap V to walk back through your clips, release to paste. No window, no mouse.' }
    @{ Shot = 'Light-OverlayWindow-Image'; Title = 'See what you are pasting'; Caption = 'Pictures, files and text are previewed in the overlay as you step through them.' }
    @{ Shot = 'Light-OverlayWindow-Search'; Title = 'Search without leaving'; Caption = 'Type while the gesture is open to narrow forty clips down to the one you meant.' }
    @{ Shot = 'Light-OverlayWindow-KindFilter'; Title = 'One kind at a time'; Caption = 'K cycles All, Text, Images, Files - and says so, so a short stack is never a mystery.' }
    @{ Shot = 'Light-OverlayWindow-JoinMark'; Title = 'Paste several as one'; Caption = 'Mark clips as you browse; releasing Ctrl pastes them joined, in the order you marked them.' }
    @{ Shot = 'Light-OverlayWindow-DeleteAll'; Title = 'X changes what release does'; Caption = 'Paste, cancel, delete, or delete all - and the destructive one asks before it acts.' }
    @{ Shot = 'Light-OverlayWindow-Minimal'; Title = 'As quiet as you like'; Caption = 'Twelve switches strip the overlay back. What changes the paste can never be hidden.' }
    @{ Shot = 'Light-OverlayWindow-Font'; Title = 'Your font, your size'; Caption = 'Any installed font, 9 to 24 point. The colours come from the theme.' }
    @{ Shot = 'Light-HistoryWindow'; Title = 'Everything you copied'; Caption = 'A searchable archive with full-text search, kept for as long as you choose.' }
    @{ Shot = 'Light-HistoryWindow-Filtered'; Title = 'Filter and act'; Caption = 'Narrow by kind, then right-click a row: copy, pin, delete, or show only that kind.' }
    @{ Shot = 'Light-HistoryWindow-ClipsImage'; Title = 'Look closely'; Caption = 'Zoom, pan, and a 100% that is exactly 1:1 - every pixel as it was captured.' }
    @{ Shot = 'Light-HistoryWindow-Joining'; Title = 'Copy many as one'; Caption = 'Select several rows and Copy becomes Copy Joined, top to bottom as you see them.' }
    @{ Shot = 'Light-SettingsWindow-2-PasteMode'; Title = 'Tuned to your machine'; Caption = 'Trigger key, paste keystroke, and a settle delay per application that needs one.' }
    @{ Shot = 'Light-SettingsWindow-3-Keys'; Title = 'Every key rebindable'; Caption = 'Fourteen paste-mode actions, on the letters you choose - or switched off entirely.' }
    @{ Shot = 'Monokai-HistoryWindow-Monokai'; Title = 'Nineteen themes'; Caption = 'Or write your own: a theme is a JSON file of named colours you can edit and reload.' }
    @{ Shot = 'Light-SettingsWindow-7-Advanced'; Title = 'Nothing hidden'; Caption = 'Every setting in one list, with where it lives and whether you have changed it.' }
    @{ Shot = 'Light-AboutWindow'; Title = 'Free and open source'; Caption = 'github.com/lokeshgovindu/PasteJump' }
)

Write-Host 'Rendering the windows (UI smoke harness)...'
& dotnet run --project (Join-Path $repo 'tests/PasteJump.UiSmoke') -v q --nologo -- --shot $shots | Out-Null

# 1080p, and a shot is never enlarged: an enlarged screenshot is a blurred screenshot, which is a poor
# advertisement for an application whose most recent bug was exactly that.
$width = 1920
$height = 1080
$background = [System.Drawing.Color]::FromArgb(255, 18, 20, 24)
$accent = [System.Drawing.Color]::FromArgb(255, 88, 140, 255)
$muted = [System.Drawing.Color]::FromArgb(255, 170, 178, 190)
$hairline = [System.Drawing.Color]::FromArgb(255, 60, 66, 78)

$titleFont = New-Object System.Drawing.Font('Segoe UI Semibold', 40, [System.Drawing.FontStyle]::Regular)
$captionFont = New-Object System.Drawing.Font('Segoe UI', 22, [System.Drawing.FontStyle]::Regular)
$markFont = New-Object System.Drawing.Font('Segoe UI Semibold', 96, [System.Drawing.FontStyle]::Regular)

$index = 0

foreach ($step in $tour) {
    $index++
    $frame = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($frame)
    $g.Clear($background)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    if ($null -eq $step.Shot) {
        $mark = Join-Path $repo 'src/PasteJump.App/Assets/pastejump-256.png'

        if (Test-Path $mark) {
            $logo = [System.Drawing.Image]::FromFile($mark)
            $g.DrawImage($logo, [int](($width - 200) / 2), 300, 200, 200)
            $logo.Dispose()
        }

        $centre = New-Object System.Drawing.StringFormat
        $centre.Alignment = [System.Drawing.StringAlignment]::Center

        $g.DrawString($step.Title, $markFont, [System.Drawing.Brushes]::White,
            (New-Object System.Drawing.RectangleF 0, 540, $width, 150), $centre)
        $g.DrawString($step.Caption, $captionFont, (New-Object System.Drawing.SolidBrush $muted),
            (New-Object System.Drawing.RectangleF 0, 700, $width, 60), $centre)
    }
    else {
        $path = Join-Path $shots ($step.Shot + '.png')

        if (-not (Test-Path $path)) {
            Write-Warning ('no shot named ' + $step.Shot + ' - skipping')
            $g.Dispose()
            $frame.Dispose()
            continue
        }

        $g.FillRectangle((New-Object System.Drawing.SolidBrush $accent), 96, 92, 76, 5)
        $g.DrawString($step.Title, $titleFont, [System.Drawing.Brushes]::White, 90, 112)
        $g.DrawString($step.Caption, $captionFont, (New-Object System.Drawing.SolidBrush $muted), 92, 178)

        $img = [System.Drawing.Image]::FromFile($path)
        $areaY = 240
        $areaH = 800

        # How a shot is sized, and both branches matter.
        #
        # The overlay is 439x86, and at 1:1 it is a postage stamp lost in a 1080p frame. So a small shot is
        # enlarged by a WHOLE number with nearest-neighbour - pixel doubling, which keeps its text exactly as
        # rendered. A fractional scale or a bicubic one would resample the glyphs into mush, which is precisely
        # the bug this application was reported for; blocky is honest, blurry is not.
        #
        # A shot too large for the frame is reduced with bicubic, where smoothing is what you want.
        $fits = [Math]::Min(1680.0 / $img.Width, $areaH / $img.Height)

        if ($fits -ge 2) {
            $step = [Math]::Min(3, [int][Math]::Floor($fits))
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
            $scale = $step
        }
        else {
            $scale = [Math]::Min(1.0, $fits)
        }

        $w = [int]($img.Width * $scale)
        $h = [int]($img.Height * $scale)
        $x = [int](($width - $w) / 2)
        $y = [int]($areaY + ($areaH - $h) / 2)

        # A hairline, so a light screenshot does not bleed into the dark frame.
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $hairline), $x - 1, $y - 1, $w + 2, $h + 2)
        $g.DrawImage($img, $x, $y, $w, $h)
        $img.Dispose()
    }

    $frame.Save((Join-Path $frames ('{0:D3}.png' -f $index)), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $frame.Dispose()
}

$titleFont.Dispose()
$captionFont.Dispose()
$markFont.Dispose()

# One entry per frame with its duration, which is what the concat demuxer wants. The last file is repeated
# because the demuxer ignores the final duration.
$list = Join-Path $frames 'list.txt'
$pngs = Get-ChildItem $frames -Filter '*.png' | Sort-Object Name
$lines = New-Object System.Collections.Generic.List[string]

foreach ($file in $pngs) {
    $lines.Add("file '" + ($file.FullName -replace '\\', '/') + "'")
    $lines.Add('duration ' + $Seconds)
}

$lines.Add("file '" + ($pngs[-1].FullName -replace '\\', '/') + "'")
# Written without a BOM: PowerShell 5.1's -Encoding utf8 adds one and ffmpeg's concat demuxer rejects the file
# with "Invalid data found when processing input", which says nothing about a byte order mark.
[System.IO.File]::WriteAllLines($list, $lines, (New-Object System.Text.UTF8Encoding $false))

$full = Join-Path $repo $OutputPath
New-Item -ItemType Directory -Force -Path (Split-Path $full) | Out-Null

Write-Host ('Encoding ' + $pngs.Count + ' frames...')
& ffmpeg -y -loglevel error -f concat -safe 0 -i $list -vf 'fps=30,format=yuv420p' -c:v libx264 -preset slow -crf 20 $full

if (-not (Test-Path $full)) { throw 'ffmpeg produced no file' }

$size = [Math]::Round((Get-Item $full).Length / 1MB, 1)
$seconds = & ffprobe -v error -show_entries format=duration -of csv=p=0 $full
Write-Host ('Wrote ' + $OutputPath + ' - ' + $size + ' MB, ' + [Math]::Round([double]$seconds, 1) + 's')

if ($KeepFrames) { Write-Host ('Frames left in ' + $frames) }
else { Remove-Item $frames -Recurse -Force }
