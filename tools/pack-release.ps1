<#
.SYNOPSIS
    Builds the release packages: a portable ZIP and a Windows installer.

.DESCRIPTION
    One staging folder feeds both packages, which is the point of doing it in one script: a ZIP and an
    installer assembled separately drift, and the first sign of it is a user reporting a file that is in
    one and not the other.

    Steps, in order, because each depends on the last:

      1. Read the version from Directory.Build.props. Never passed in - a version typed on the command
         line is a version that can disagree with the binary it names.
      2. Publish Release, self-contained, single-file, win-x64.
      3. Compile the help, so the .chm in the package matches this source tree.
      4. Stage exe + chm + README + LICENSE.txt.
      5. Zip the staging folder, and hash it.
      6. Compile the installer from the same staging folder, and hash that.

    Step 6 is skipped with a warning when Inno Setup is not installed, because the ZIP is the primary
    artifact and half a release is better than none.

.PARAMETER SkipPublish
    Reuse whatever is already in artifacts/publish. For iterating on the packaging itself - a publish
    takes far longer than everything else here.

.PARAMETER OutputDirectory
    Where the finished packages go. Defaults to artifacts/release.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/pack-release.ps1
#>
[CmdletBinding()]
param(
    [switch] $SkipPublish,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsFile = Join-Path $repoRoot 'Directory.Build.props'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\release'
}

# --------------------------------------------------------------- 1. version

$props = Get-Content $propsFile -Raw
$match = [regex]::Match($props, '<PasteJumpVersion>([^<]+)</PasteJumpVersion>')

if (-not $match.Success) {
    throw "Could not read <PasteJumpVersion> from $propsFile."
}

$version = $match.Groups[1].Value.Trim()
Write-Host "PasteJump $version" -ForegroundColor Cyan

$stageName = "PasteJump-$version-win-x64"
$stageDirectory = Join-Path $OutputDirectory "stage\$stageName"
$installerStageDirectory = Join-Path $OutputDirectory 'stage-installer'

$publishDirectory = Join-Path $repoRoot 'artifacts\publish'
$folderPublishDirectory = Join-Path $repoRoot 'artifacts\publish-folder'

# --------------------------------------------------------------- 2. publish

# TWO publishes, deliberately, because the two packages want opposite things.
#
#   Single file, for the ZIP. Portability is the whole point of that download: one executable that can be
#     dropped on a USB stick, with data\ appearing beside it.
#
#   A folder, for the installer. Single-file costs about a second on every launch before a line of our
#     code runs - measured 1,100-1,145 ms pre-Compose against 228 ms for a folder build - and it buys
#     nothing once an installer is putting files in a directory for you. Someone who ran a setup program
#     does not care that the directory has 200 files in it; they do notice a second of nothing happening
#     after they press the shortcut.
#
# The cost is disk: roughly 143 MB installed against 65 MB. That is the trade being made on purpose.

$project = Join-Path $repoRoot 'src\PasteJump.App\PasteJump.App.csproj'

function Invoke-Publish([string] $label, [string] $destination, [string[]] $extra) {
    Write-Host "  $label"

    $log = & dotnet publish $project -c Release -o $destination --nologo -v q @extra

    if ($LASTEXITCODE -ne 0) {
        $log | ForEach-Object { Write-Host "    $_" }
        throw "dotnet publish failed for $label (exit $LASTEXITCODE)."
    }
}

if ($SkipPublish) {
    Write-Host "[1/5] Publish skipped; reusing artifacts\publish and artifacts\publish-folder"
}
else {
    Write-Host "[1/5] Publishing Release twice..."

    Invoke-Publish 'single file, for the ZIP' $publishDirectory @()

    # The csproj turns single-file on for everyone, so it is turned back off here rather than the other way
    # round: the default publish - what someone gets from `dotnet publish` by hand - should stay the
    # portable one described in the README. Compression and native-library extraction only mean anything
    # inside a bundle, so both go with it.
    if (Test-Path $folderPublishDirectory) {
        # A stale folder publish is worse than none: files that a later build stopped producing would stay
        # behind and be packaged, and a single wrong assembly version is a runtime failure with no clue.
        Remove-Item $folderPublishDirectory -Recurse -Force
    }

    Invoke-Publish 'folder, for the installer' $folderPublishDirectory @(
        '-p:PublishSingleFile=false',
        '-p:EnableCompressionInSingleFile=false',
        '-p:IncludeNativeLibrariesForSelfExtract=false'
    )
}

$exe = Join-Path $publishDirectory 'PasteJump.exe'
$folderExe = Join-Path $folderPublishDirectory 'PasteJump.exe'

foreach ($candidate in @($exe, $folderExe)) {
    if (-not (Test-Path $candidate)) {
        throw "No PasteJump.exe at $candidate."
    }

    # Both published binaries must be the version this script claims to be packaging. Cheap to check, and
    # it catches -SkipPublish being used against a stale publish, which would otherwise ship the wrong
    # build under the right name - in one package and not the other, which is worse than in both.
    $candidateVersion = (Get-Item $candidate).VersionInfo.FileVersion

    if ($candidateVersion -ne $version) {
        throw "$candidate reports $candidateVersion but Directory.Build.props says $version. Publish again without -SkipPublish."
    }
}

# A folder publish that produced only the exe means the single-file properties did not actually come off,
# and the installer would ship a single-file build while claiming to be the fast one.
$folderFileCount = (Get-ChildItem $folderPublishDirectory -Recurse -File).Count

if ($folderFileCount -lt 50) {
    throw "The folder publish has only $folderFileCount files, so it is still a single-file build. Check the -p: overrides."
}

# --------------------------------------------------------------- 3. help

Write-Host "[2/5] Compiling the help..."

$helpScript = Join-Path $PSScriptRoot 'build-help.ps1'
$chm = Join-Path $repoRoot 'artifacts\help\PasteJump.chm'

try {
    # 6>$null swallows the help script's Write-Host output. It is useful when run on its own and pure
    # noise in the middle of a release: hhc prints two dozen blank lines and a graphics count that lies.
    & $helpScript 6>$null | Out-Null
}
catch {
    # Not fatal on its own, but the package would silently lose its manual - so it fails here unless a
    # previously built .chm is lying around to use.
    if (Test-Path $chm) {
        Write-Warning "The help did not rebuild ($($_.Exception.Message)). Packaging the existing $chm."
    }
    else {
        throw "The help could not be built and there is no existing .chm to package: $($_.Exception.Message)"
    }
}

if (-not (Test-Path $chm)) {
    throw "No PasteJump.chm at $chm."
}

# --------------------------------------------------------------- 4. stage

Write-Host "[3/5] Staging..."

# The documents both packages carry. LICENSE is renamed on the way in: the repository file has no
# extension, and both Explorer and Inno's licence page want one.
$documents = @(
    @{ From = $chm; To = 'PasteJump.chm' },
    @{ From = Join-Path $repoRoot 'README.md'; To = 'README.md' },
    @{ From = Join-Path $repoRoot 'LICENSE'; To = 'LICENSE.txt' }
)

# The ZIP: one executable and the documents.
if (Test-Path $stageDirectory) {
    Remove-Item $stageDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
Copy-Item $exe (Join-Path $stageDirectory 'PasteJump.exe')

foreach ($document in $documents) {
    Copy-Item $document.From (Join-Path $stageDirectory $document.To)
}

# The installer: the whole folder publish, plus the same documents. Staged rather than pointing the
# installer at artifacts\publish-folder directly, so both packages are assembled from a directory this
# script controls and the .iss needs one Source line rather than a list that can fall behind the build.
if (Test-Path $installerStageDirectory) {
    Remove-Item $installerStageDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $installerStageDirectory | Out-Null
Copy-Item "$folderPublishDirectory\*" $installerStageDirectory -Recurse -Force

foreach ($document in $documents) {
    Copy-Item $document.From (Join-Path $installerStageDirectory $document.To)
}

$installerBytes = (Get-ChildItem $installerStageDirectory -Recurse -File | Measure-Object -Property Length -Sum).Sum

Write-Host ("  ZIP payload:       1 exe + {0} documents" -f $documents.Count)
Write-Host ("  installer payload: {0} files, {1:N0} MB on disk once installed" -f
    (Get-ChildItem $installerStageDirectory -Recurse -File).Count, ($installerBytes / 1MB))

# --------------------------------------------------------------- 5. zip

Write-Host "[4/5] Zipping..."

$zip = Join-Path $OutputDirectory "$stageName.zip"

if (Test-Path $zip) {
    Remove-Item $zip -Force
}

# The staging folder itself is compressed, not its contents, so the archive expands into a named folder
# rather than scattering four files into whatever directory it was opened in.
Compress-Archive -Path $stageDirectory -DestinationPath $zip -CompressionLevel Optimal

function Write-Hash([string] $path) {
    $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = Split-Path -Leaf $path

    # sha256sum's own format, two spaces between, so `sha256sum -c` verifies it unchanged.
    Set-Content -Path "$path.sha256" -Value "$hash  $name" -Encoding ascii -NoNewline

    return $hash
}

$zipHash = Write-Hash $zip

# --------------------------------------------------------------- 6. installer

Write-Host "[5/5] Compiling the installer..."

$innoCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$iscc = $innoCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { $iscc = $onPath.Source }
}

$setup = $null

if (-not $iscc) {
    Write-Warning "Inno Setup not found, so no installer was built. The ZIP above is complete."
    Write-Warning "Install it with:  winget install --id JRSoftware.InnoSetup"
}
else {
    $script = Join-Path $repoRoot 'packaging\PasteJump.iss'

    # The installer staging folder, not the ZIP's: this is what makes setup.exe deploy the fast folder
    # build while the ZIP stays one file.
    $isccLog = & $iscc `
        "/DAppVersion=$version" `
        "/DStageDir=$installerStageDirectory" `
        "/DOutputDir=$OutputDirectory" `
        "/DRepoRoot=$repoRoot" `
        $script

    if ($LASTEXITCODE -ne 0) {
        $isccLog | ForEach-Object { Write-Host "  $_" }
        throw "ISCC failed (exit $LASTEXITCODE)."
    }

    $setup = Join-Path $OutputDirectory "PasteJump-$version-setup.exe"

    if (-not (Test-Path $setup)) {
        throw "ISCC reported success but produced no $setup."
    }

    $setupHash = Write-Hash $setup
}

# --------------------------------------------------------------- summary

Write-Host ""
Write-Host "Packages in $OutputDirectory" -ForegroundColor Green

foreach ($file in @($zip, $setup) | Where-Object { $_ }) {
    $item = Get-Item $file
    Write-Host ("  {0,-44} {1,8:N1} MB" -f $item.Name, ($item.Length / 1MB))
}

Write-Host ""
Write-Host "SHA256"
Write-Host "  $zipHash  $(Split-Path -Leaf $zip)"

if ($setup) {
    Write-Host "  $setupHash  $(Split-Path -Leaf $setup)"
}

Write-Host ""
Write-Host "Neither package is code-signed, so Windows will show a SmartScreen warning on first run."
