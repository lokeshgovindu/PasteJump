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

if ($running) {
    Write-Host "Stopping the running copy..."

    # Stop-Process rather than CloseMainWindow: this application has no main window, so there is nothing to
    # send a close message to.
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
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
        Remove-Item -LiteralPath $item.FullName -Recurse -Force
    }
}

# --------------------------------------------------------------- copy

Copy-Item "$publishDirectory\*" $Destination -Recurse -Force

if ($Sign) {
    & (Join-Path $PSScriptRoot 'sign-local.ps1') -Path (Join-Path $Destination 'PasteJump.exe') | Out-Null
    Write-Host "Signed with the development certificate."
}

$landed = Get-ChildItem $Destination -File -Recurse | Where-Object { $_.FullName -notmatch '\\data\\' }

Write-Host ("Deployed to {0}: {1} files, {2:N1} MB (data\ untouched)" -f
    $Destination, $landed.Count, (($landed | Measure-Object -Property Length -Sum).Sum / 1MB)) -ForegroundColor Green

if (-not $NoStart) {
    Start-Process (Join-Path $Destination 'PasteJump.exe')
    Start-Sleep -Seconds 3

    $started = Get-Process PasteJump -ErrorAction SilentlyContinue

    if (-not $started) {
        throw "PasteJump did not stay running. If the .NET 10 Desktop Runtime is missing, this shape cannot start - use the self-contained one."
    }

    Write-Host "Running: pid $($started.Id)"
}
