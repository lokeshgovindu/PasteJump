<#
.SYNOPSIS
    Regenerates the screenshots used by docs/help.

.DESCRIPTION
    Runs the UI smoke harness with --shot into a temporary directory and copies the light-theme shots it
    wants into docs/help/images under stable names.

    The harness is the source rather than a screen-capture tool for one reason that matters: it renders each
    window from the real XAML with seeded data, so a screenshot cannot drift from the product the way a
    hand-taken one does. It also means the images are reproducible - run this again after a UI change and the
    help follows.

    Light theme only. The help is a document, and a dark screenshot on a white page reads as a defect rather
    than as a theme.

    The images are checked in, so building the .chm does not require running this: hhc.exe needs the files to
    exist, and a help build should not depend on being able to start WPF.

.PARAMETER Keep
    Leave the temporary capture directory in place, for inspecting shots this script does not copy.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/update-help-images.ps1
#>
[CmdletBinding()]
param(
    [switch] $Keep
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$harness = Join-Path $repoRoot 'tests\PasteJump.UiSmoke'
$imageDirectory = Join-Path $repoRoot 'docs\help\images'

# Source shot name (without the theme prefix) -> name the help refers to. Keeping the mapping here rather
# than referring to harness names in the HTML means a rename in the harness is a one-line change.
#
# WATCH THE NUMBERS. The harness names a settings shot by the tab's index, so inserting a tab renumbers every
# one after it and each of those mappings silently stops matching - the script warns, but the help quietly keeps
# the previous image, which then documents the wrong tab. Adding the Keys tab shifted four of these. If a
# "produced no shot for" warning appears below, this is almost certainly why.
$wanted = [ordered]@{
    'HistoryWindow'             = 'history-window.png'
    'HistoryWindow-Clips'       = 'clips-view.png'
    'SettingsWindow-0-Capture'  = 'settings-capture.png'
    'SettingsWindow-1-History'  = 'settings-history.png'
    'SettingsWindow-2-PasteMode' = 'settings-paste-mode.png'
    'SettingsWindow-3-Keys'     = 'settings-keys.png'
    'SettingsWindow-4-ExcludedApps' = 'settings-excluded-apps.png'
    'SettingsWindow-5-Appearance' = 'settings-appearance.png'
    'SettingsWindow-6-System'   = 'settings-system.png'
    'SettingsWindow-7-Advanced' = 'settings-advanced.png'

    # The System tab again with both halves on a custom folder, which is the only state that shows the path box
    # and its Browse button - a collapsed row is not rendered, so the shot above cannot document it.
    'SettingsWindow-CustomFolder-6-System' = 'settings-custom-folder.png'

    # Reached from Excluded Apps, and worth a picture because what it lists is not obvious from the button.
    'RunningAppPicker'          = 'running-app-picker.png'
    'OverlayWindow'             = 'overlay.png'
    'OverlayWindow-Search'      = 'overlay-search.png'
    'OverlayWindow-DeleteAll'   = 'overlay-delete-all.png'
    'OverlayWindow-KindFilter'  = 'overlay-kind-filter.png'
    'ShortcutHelpWindow'        = 'shortcut-help.png'
    'AboutWindow'               = 'about.png'
    'ImportDialog'              = 'import-dialog.png'
    'ToastWindow'               = 'toast.png'
}

$capture = Join-Path ([System.IO.Path]::GetTempPath()) ("pastejump-help-shots-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $capture | Out-Null

Write-Host "Running the UI smoke harness..."

# Not 2>&1: redirecting a native command's stderr inside PowerShell 5.1 turns each line into an ErrorRecord
# and sets $? to false even on a clean exit.
$output = & dotnet run --project $harness --nologo -- --shot $capture

if ($LASTEXITCODE -ne 0) {
    $output | ForEach-Object { Write-Host "  $_" }
    throw "The UI smoke harness failed (exit $LASTEXITCODE). No images were changed."
}

if (-not (Test-Path $imageDirectory)) {
    New-Item -ItemType Directory -Force -Path $imageDirectory | Out-Null
}

$copied = 0
$missing = @()

foreach ($entry in $wanted.GetEnumerator()) {
    $source = Join-Path $capture ("Light-" + $entry.Key + ".png")

    if (-not (Test-Path $source)) {
        $missing += $entry.Key
        continue
    }

    Copy-Item -Path $source -Destination (Join-Path $imageDirectory $entry.Value) -Force
    $copied++
}

Write-Host "Copied $copied image(s) to $imageDirectory"

# Reported rather than ignored: a shot that stopped being produced means the help now references an image
# that is either stale or absent, and hhc.exe will not tell you which.
if ($missing) {
    Write-Warning "The harness produced no shot for: $($missing -join ', ')"
    Write-Warning "The help still references the previous copy of those images, if any."
}

if ($Keep) {
    Write-Host "Capture kept at $capture"
}
else {
    Remove-Item $capture -Recurse -Force
}

$total = (Get-ChildItem $imageDirectory -Filter *.png | Measure-Object -Property Length -Sum).Sum
Write-Host ("Images total {0:N0} KB. Run tools/build-help.ps1 to fold them into the .chm." -f ($total / 1KB))
