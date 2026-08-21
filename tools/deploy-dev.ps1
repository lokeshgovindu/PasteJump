<#
.SYNOPSIS
    Deploys a development build over the copy you actually run, and keeps its data.

.DESCRIPTION
    The framework-dependent shape, deliberately: 15 files rather than the 257 a self-contained folder build
    leaves in the root, and about 4 MB to copy rather than 135 MB. .NET comes from the machine, which for a
    development box is a given - `dotnet --list-runtimes` will say so.

    Why not the other two shapes. Single-file is one file, which is tidier still, but it costs about a second
    on every launch before our first line runs - a poor trade for a build that gets replaced several times an
    hour, and it makes the exe 65 MB to copy. The unpacked shape starts just as fast as this one and is what
    the installer ships, but it is the 257 files this script exists to stop putting there.

    THE DATA FOLDER IS NEVER TOUCHED. data\ holds the clip database, the blobs and PasteJump.json, and it is
    the one thing here that cannot be rebuilt - so the removal below works from an explicit list of what a
    publish produces rather than "delete everything and copy in". Documents that were placed by hand are kept
    for the same reason.

    Two mechanical things this gets right that a Copy-Item by hand does not: the running instance is stopped
    first (it holds its own exe open, and a stale exe with new DLLs beside it fails at load with no clue why),
    and the version of what lands is checked against what MSBuild says this tree is, which catches a publish
    made before the last commit - the revision is the commit count, so that is easy to do by accident.

.PARAMETER Destination
    Where the copy you run lives. Must already contain a PasteJump.exe, so this cannot be pointed at an
    arbitrary directory and start deleting.

.PARAMETER Sign
    Also sign the deployed exe with the self-signed development certificate. See tools/sign-local.ps1 for what
    that signature is and is not worth.

.PARAMETER NoStart
    Leave PasteJump stopped afterwards.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/deploy-dev.ps1
#>
[CmdletBinding()]
param(
    [string] $Destination = 'D:\Lokesh\DoNotMove\PasteJump',
    [switch] $Sign,
    [switch] $NoStart
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\PasteJump.App\PasteJump.App.csproj'
$publishDirectory = Join-Path $repoRoot 'artifacts\publish-framework'

if (-not (Test-Path (Join-Path $Destination 'PasteJump.exe'))) {
    throw "$Destination has no PasteJump.exe, so it is not a PasteJump deployment. Refusing to touch it."
}

# Kept whatever happens: the data folder because it is irreplaceable, the documents because they were put
# there by hand and no publish will bring them back.
$keepDirectories = @('data')
$keepFiles = @('PasteJump.chm', 'README.md', 'LICENSE.txt', 'data-location.json')

# --------------------------------------------------------------- publish

Write-Host "Publishing framework-dependent..."

if (Test-Path $publishDirectory) {
    # A stale publish would leave behind files a later build stopped producing, and one wrong assembly
    # version is a load failure with nothing useful in the message.
    Remove-Item $publishDirectory -Recurse -Force
}

$log = & dotnet publish $project -c Release -o $publishDirectory --nologo -v q `
    --self-contained false `
    -p:PublishSingleFile=false `
    -p:EnableCompressionInSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=false

if ($LASTEXITCODE -ne 0) {
    $log | ForEach-Object { Write-Host "  $_" }
    throw "dotnet publish failed (exit $LASTEXITCODE)."
}

# Same check pack-release.ps1 makes, and for the same reason: --self-contained false silently ignored gives a
# working 135 MB directory that nothing distinguishes from a self-contained one until you read its size.
if (Test-Path (Join-Path $publishDirectory 'coreclr.dll')) {
    throw "The publish contains coreclr.dll, so it is self-contained. Check --self-contained false."
}

$published = Get-ChildItem $publishDirectory -Recurse -File
$publishedExe = Join-Path $publishDirectory 'PasteJump.exe'

# Asked of MSBuild rather than recomputed here - the revision is the commit count, resolved by a target, and
# a second implementation of that would drift.
$versionOutput = & dotnet msbuild $project -t:PrintPasteJumpVersion -nologo -v:m
$match = [regex]::Match(($versionOutput -join "`n"), 'PasteJumpVersion=([0-9]+(?:\.[0-9]+){3})')

if (-not $match.Success) {
    throw "Could not read the version from the PrintPasteJumpVersion target's output."
}

$version = $match.Groups[1].Value
$publishedVersion = (Get-Item $publishedExe).VersionInfo.FileVersion

if ($publishedVersion -ne $version) {
    throw "The publish reports $publishedVersion but this tree resolves to $version."
}

Write-Host ("  PasteJump {0}: {1} files, {2:N1} MB" -f $version, $published.Count,
    (($published | Measure-Object -Property Length -Sum).Sum / 1MB))

# --------------------------------------------------------------- stop

$running = Get-Process PasteJump -ErrorAction SilentlyContinue

# Whether the way is clear. Nothing to stop counts as stopped.
$stopped = $true

if ($running) {
    Write-Host "Stopping the running copy..."
    $stopped = $false

    # Three ways, in order of politeness, because one of them has to work: this script must never leave the
    # machine with nothing deployed and nothing running just because the copy in the way is privileged.
    #
    #   1. Stop-Process. Fails on an ELEVATED copy from an ordinary prompt, and PasteJump legitimately runs
    #      elevated where endpoint security intercepts keyboard input above medium integrity.
    #   2. Ask it to leave. A registered window message crosses the integrity boundary, provided the running
    #      copy is new enough to have opted in - see SingleInstanceSignal. It also shuts down cleanly, saving
    #      settings and closing the database, which Stop-Process does not.
    #   3. Rename the files out of the way. Windows lets a running executable and its loaded assemblies be
    #      RENAMED even while they cannot be deleted, so the new build still lands and takes effect the next
    #      time PasteJump starts.
    $stopped = $false

    try {
        $running | Stop-Process -Force -ErrorAction Stop
        Start-Sleep -Milliseconds 800
        $stopped = $true
    }
    catch {
        Write-Host '  Stop-Process was refused - that copy is elevated. Asking it to exit instead...'

        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PasteJumpExitRequest
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    public static bool Send()
    {
        uint message = RegisterWindowMessage("PasteJump.RequestExit");

        if (message == 0)
        {
            return false;
        }

        // HWND_MESSAGE, and by window TITLE: the class name is unique per instance by design, so the title
        // is the only stable handle on a message-only window.
        IntPtr target = FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, "PasteJump.SingleInstance.9F2C41A6");

        return target != IntPtr.Zero && PostMessage(target, message, IntPtr.Zero, IntPtr.Zero);
    }
}
'@ -ErrorAction SilentlyContinue

        if ([PasteJumpExitRequest]::Send()) {
            for ($waited = 0; $waited -lt 10; $waited++) {
                Start-Sleep -Seconds 1

                if (-not (Get-Process -Id $running.Id -ErrorAction SilentlyContinue)) {
                    $stopped = $true
                    Write-Host "  it exited after $($waited + 1)s"
                    break
                }
            }
        }

        if (-not $stopped) {
            # Old builds do not understand the request, so this is the normal path until one deploy has
            # replaced them. Renaming is not a workaround for a broken deploy - it IS a deploy, and the only
            # thing it costs is that the running copy stays on the old build until it next starts.
            Write-Host '  It is still running, so the old files will be renamed out of the way instead.'
            Write-Host '  The new build lands now and takes effect the next time PasteJump starts.' -ForegroundColor DarkYellow
        }
    }
}

# Left over from a previous rename-in-place deploy. Cleared first, and best-effort: whatever still cannot be
# deleted is simply still in use, and will go on the next run.
foreach ($orphan in @(Get-ChildItem $Destination -File -Filter '*.replaced-*' -ErrorAction SilentlyContinue)) {
    try { Remove-Item -LiteralPath $orphan.FullName -Force -ErrorAction Stop } catch { }
}

# --------------------------------------------------------------- clear

# Everything at the top level that is not on the keep list. Note it does NOT walk into data\, so a file in
# there that happens to share a name with an assembly is safe.
$stale = @(Get-ChildItem $Destination -File | Where-Object { $keepFiles -notcontains $_.Name })
$staleDirectories = @(Get-ChildItem $Destination -Directory | Where-Object { $keepDirectories -notcontains $_.Name })

if ($stale.Count -or $staleDirectories.Count) {
    $staleBytes = ($stale | Measure-Object -Property Length -Sum).Sum
    $staleBytes += ($staleDirectories | ForEach-Object { Get-ChildItem $_.FullName -Recurse -File } |
        Measure-Object -Property Length -Sum).Sum

    Write-Host ("Removing the previous build: {0} files and {1} folder(s), {2:N1} MB" -f
        $stale.Count, $staleDirectories.Count, ($staleBytes / 1MB))

    foreach ($item in @($stale) + @($staleDirectories)) {
        try {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop
        }
        catch {
            # Held open by the copy still running. Windows allows a RENAME of a running executable and of
            # loaded assemblies even when it refuses a delete, which is what lets the new build land anyway.
            # The stamp keeps successive deploys from colliding on the same name.
            $parked = "{0}.replaced-{1}" -f $item.FullName, (Get-Date -Format 'HHmmss')

            try {
                Rename-Item -LiteralPath $item.FullName -NewName (Split-Path $parked -Leaf) -ErrorAction Stop
            }
            catch {
                Write-Warning "  could not remove or rename $($item.Name): $($_.Exception.Message)"
            }
        }
    }
}

# --------------------------------------------------------------- copy

Copy-Item "$publishDirectory\*" $Destination -Recurse -Force

if ($Sign) {
    # Never fatal, and that is the lesson from the time it was. Signing happens AFTER the running copy has been
    # stopped and the new files copied - it has to, since signtool rewrites the exe - so a failure here used to abort
    # the script with the application shut down and no restart. The timestamp server being briefly unreachable left
    # the machine with no PasteJump at all, over a signature that is only a development convenience.
    #
    # So: try it, then try it without the timestamp, then carry on regardless. The one thing that must always happen
    # is the restart below.
    $signer = Join-Path $PSScriptRoot 'sign-local.ps1'
    $exe = Join-Path $Destination 'PasteJump.exe'

    try
    {
        & $signer -Path $exe | Out-Null
        Write-Host "Signed with the development certificate."
    }
    catch
    {
        Write-Warning "Signing failed ($($_.Exception.Message.Split([Environment]::NewLine)[0]))."

        try
        {
            # Usually the timestamp server, which needs the network. A signature with no countersignature stops
            # validating when the certificate expires - irrelevant for a build that lives for an hour.
            & $signer -Path $exe -NoTimestamp | Out-Null
            Write-Warning "Signed WITHOUT a timestamp. Fine for a development build, never for a release."
        }
        catch
        {
            Write-Warning "Left unsigned. Properties will show no Digital Signatures tab."
        }
    }
}

# The manual, if one has been built and is newer than the deployed copy.
#
# It is on the keep list above - deliberately, since it is not build output and a redeploy must not delete it - but
# keeping it also meant it was never REFRESHED, so Help opened a .chm from whenever it was last built by hand. That
# is how a reader came to be looking at a five-day-old manual, hunting for a page added after it was compiled, with
# nothing on screen to say the file was stale. Every page carries its version now, and this keeps the deployed copy
# current.
$freshHelp = Join-Path $repoRoot 'artifacts\help\PasteJump.chm'
$deployedHelp = Join-Path $Destination 'PasteJump.chm'

if (Test-Path $freshHelp) {
    $isNewer = -not (Test-Path $deployedHelp) -or
        (Get-Item $freshHelp).LastWriteTimeUtc -gt (Get-Item $deployedHelp).LastWriteTimeUtc

    if ($isNewer) {
        Copy-Item $freshHelp $deployedHelp -Force
        Write-Host ("Refreshed PasteJump.chm ({0:N0} KB)" -f ((Get-Item $deployedHelp).Length / 1KB))
    }
}
else {
    Write-Host 'No built manual in artifacts\help - run tools/build-help.ps1 to refresh the deployed one.' -ForegroundColor DarkGray
}

$landed = Get-ChildItem $Destination -File -Recurse | Where-Object { $_.FullName -notmatch '\\data\\' }

Write-Host ("Deployed to {0}: {1} files, {2:N1} MB (data\ untouched)" -f
    $Destination, $landed.Count, (($landed | Measure-Object -Property Length -Sum).Sum / 1MB)) -ForegroundColor Green

if (-not $NoStart -and -not $stopped) {
    Write-Host ''
    Write-Host 'The previous copy is STILL RUNNING on the old build, so a new one has NOT been started.' -ForegroundColor DarkYellow
    Write-Host 'Starting one would achieve nothing: it finds the single-instance mutex held and exits - and'  -ForegroundColor DarkYellow
    Write-Host 'if the running copy is elevated it cannot even be reached to be told, so the new copy sits'  -ForegroundColor DarkYellow
    Write-Host 'on a modal dialog instead. The files are in place: exit PasteJump from its tray icon and'     -ForegroundColor DarkYellow
    Write-Host 'start it again to pick up this build.'                                                       -ForegroundColor DarkYellow
}
elseif (-not $NoStart) {
    # Started through the elevated logon task when one exists, and that is not a nicety. On a machine where
    # endpoint security intercepts a browser's keyboard input above medium integrity, PasteJump only works
    # elevated - and starting the exe directly from an ordinary prompt silently hands back a medium-integrity
    # copy, so the gesture stops working in that application again with nothing to say why. Deploying must not
    # quietly undo the one thing that fixed it.
    #
    # schtasks /Run on a task the user owns starts it with the privileges the task was registered with, which
    # is how tools\install-elevated-task.ps1 sets it up.
    $elevatedTask = 'PasteJump (elevated)'
    $haveTask = $null -ne (Get-ScheduledTask -TaskName $elevatedTask -ErrorAction SilentlyContinue)

    if ($haveTask) {
        Write-Host "Starting through '$elevatedTask' so the restarted copy keeps its privileges..."
        schtasks /Run /TN $elevatedTask | Out-Null
    }
    else {
        Start-Process (Join-Path $Destination 'PasteJump.exe')
    }

    Start-Sleep -Seconds 3

    $started = Get-Process PasteJump -ErrorAction SilentlyContinue

    if (-not $started) {
        throw "PasteJump did not stay running. If the .NET 10 Desktop Runtime is missing, this shape cannot start - use the self-contained one."
    }

    Write-Host "Running: pid $($started.Id)"

    if (-not $haveTask) {
        Write-Host ''
        Write-Host 'Started NOT elevated. If the gesture has to work in an application whose input is'         -ForegroundColor DarkYellow
        Write-Host 'intercepted above medium integrity - a browser under endpoint DLP - register the task once:' -ForegroundColor DarkYellow
        Write-Host '  powershell -ExecutionPolicy Bypass -File tools\install-elevated-task.ps1'                -ForegroundColor DarkYellow
        Write-Host 'after which this script will start it elevated on every deploy.'                            -ForegroundColor DarkYellow
    }
}
