<#
.SYNOPSIS
    Builds the native Win32 gesture spike and deploys it to its own folder.

.DESCRIPTION
    Deliberately NOT into the PasteJump folder. deploy-dev.ps1 removes every top-level file there that is not
    on its keep list, so a spike dropped beside the application would be deleted by the next deploy - and a
    foreign executable in the application's own directory is confusing besides.

    The spike is a diagnostic, not a second product: text clips in memory, the real gesture, no settings and
    no database. It refuses to start while PasteJump is running, because two managers both swallowing Ctrl+V
    fight over it rather than coexisting.

.PARAMETER Destination
    Where to put it. Defaults to a sibling of the development deployment.

.PARAMETER Sweep
    Run the per-application sweep once after deploying, through a scheduled task - the sweep needs foreground
    rights that a plain shell does not have. Refused unless PasteJump is closed.
#>
[CmdletBinding()]
param(
    [string] $Destination = 'D:\Lokesh\DoNotMove\PasteJump-Native',
    [switch] $Sweep
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repoRoot 'tests\PasteJump.NativeGestureSpike\build.cmd'
$exe = Join-Path $repoRoot 'artifacts\native-spike\pjnative.exe'

Write-Host 'Building the native spike...'
& cmd /c "`"$build`"" | Out-Null

if (-not (Test-Path $exe)) {
    throw "Build produced no $exe. Run $build directly to see the compiler output."
}

if (-not (Test-Path $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

Copy-Item $exe $Destination -Force
Copy-Item (Join-Path $repoRoot 'tests\PasteJump.NativeGestureSpike\README.md') $Destination -Force

# A double-clickable sweep, since the interesting mode is the one that needs a scheduled task. Written here
# rather than kept in the repository because it has to carry the deployed path.
$deployedExe = Join-Path $Destination 'pjnative.exe'

@"
@echo off
rem Runs the per-application sweep. A scheduled task, not a shell: focusing another application's window
rem needs foreground rights a background process does not have.
echo Close PasteJump first - two managers both swallowing Ctrl+V fight over it.
echo.
pause
schtasks /Create /TN PJNativeSweep /TR "%~dp0pjnative.exe --sweep" /SC ONCE /ST 23:59 /IT /F >nul
schtasks /Run /TN PJNativeSweep >nul
echo Sweeping - watch for the overlay appearing over each window...
timeout /t 45 >nul
schtasks /Delete /TN PJNativeSweep /F >nul
echo.
echo Done. The sweep printed its table into its own console window.
pause
"@ | Set-Content -Path (Join-Path $Destination 'run-sweep.cmd') -Encoding ascii

$landed = Get-ChildItem $Destination -File

Write-Host ("Deployed to {0}: {1} files, {2:N0} KB" -f
    $Destination, $landed.Count, (($landed | Measure-Object -Property Length -Sum).Sum / 1KB)) -ForegroundColor Green

Write-Host ''
Write-Host 'Resident mode - use the gesture yourself:'
Write-Host "  $deployedExe"
Write-Host 'Per-application sweep:'
Write-Host "  $Destination\run-sweep.cmd"
Write-Host ''
Write-Host 'Both refuse to start while PasteJump is running. Exit it from the tray first.'

if ($Sweep) {
    if (Get-Process PasteJump -ErrorAction SilentlyContinue) {
        Write-Warning 'PasteJump is running, so the sweep would be a fight rather than a test. Skipped.'
        return
    }

    $out = Join-Path $env:TEMP 'pjnative-sweep.txt'
    $wrap = Join-Path $env:TEMP 'pjnative-sweep.cmd'

    if (Test-Path $out) { Remove-Item $out -Force }

    "@echo off`r`ncd /d `"$Destination`"`r`npjnative.exe --sweep > `"$out`" 2>&1" |
        Set-Content -Path $wrap -Encoding ascii

    schtasks /Create /TN PJNativeSweep /TR $wrap /SC ONCE /ST 23:59 /IT /F | Out-Null
    schtasks /Run /TN PJNativeSweep | Out-Null

    $deadline = (Get-Date).AddSeconds(180)

    do {
        Start-Sleep -Seconds 5
        $status = schtasks /Query /TN PJNativeSweep /FO LIST 2>$null | Select-String 'Status:'
    } while ($status -and $status.ToString() -match 'Running' -and (Get-Date) -lt $deadline)

    schtasks /Delete /TN PJNativeSweep /F | Out-Null

    if (Test-Path $out) {
        Write-Host ''
        Get-Content $out
    }
}
