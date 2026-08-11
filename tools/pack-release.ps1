<#
.SYNOPSIS
    Builds the release packages: a portable ZIP and a Windows installer.

.DESCRIPTION
    One staging folder feeds both packages, which is the point of doing it in one script: a ZIP and an
    installer assembled separately drift, and the first sign of it is a user reporting a file that is in
    one and not the other.

    Steps, in order, because each depends on the last:

      1. Ask MSBuild for the version. Never passed in - a version typed on the command line is a version
         that can disagree with the binary it names. It is not read out of Directory.Build.props either,
         because the revision is not stored there: it is the commit count, resolved by a target.
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

.PARAMETER SignThumbprint
    SHA1 thumbprint of a code-signing certificate in CurrentUser\My. Everything shippable is signed with it
    before anything is hashed. Omit both signing parameters and the release is unsigned, which is what it has
    always been.

.PARAMETER SignAzureMetadata
    Path to an Azure Artifact Signing (formerly Trusted Signing) metadata .json. Uses that service's signtool
    plug-in instead of a local certificate, which is the route that needs no hardware token.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server. Not optional in practice - see the note on Invoke-CodeSign.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/pack-release.ps1

.EXAMPLE
    # With a certificate in the current user's store
    powershell -File tools/pack-release.ps1 -SignThumbprint 1A2B3C...

.EXAMPLE
    # With Azure Artifact Signing
    powershell -File tools/pack-release.ps1 -SignAzureMetadata signing.json -TimestampUrl http://timestamp.acs.microsoft.com
#>
[CmdletBinding()]
param(
    [switch] $SkipPublish,
    [string] $OutputDirectory,
    [string] $SignThumbprint,
    [string] $SignAzureMetadata,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\PasteJump.App\PasteJump.App.csproj'

$signing = $SignThumbprint -or $SignAzureMetadata

if ($SignThumbprint -and $SignAzureMetadata) {
    throw "Choose one signing method: -SignThumbprint or -SignAzureMetadata, not both."
}

<#
.SYNOPSIS
    Signs files with Authenticode, or explains why it cannot.

.DESCRIPTION
    Called before anything is hashed or zipped, and that ordering is the point rather than a detail: signing
    rewrites the file, so a hash taken first would not match what anyone downloads. It is also why setup.exe is
    signed between ISCC and its own hash.

    /tr with a timestamp server, never /t, and never neither. Without a timestamp countersignature an
    Authenticode signature stops validating the day the certificate expires - so a release signed today would
    start warning in a year or two even though nothing about it changed. /td sha256 sets the timestamp digest;
    omitting it leaves it at SHA1, which modern Windows distrusts.

    signtool is located rather than assumed on PATH: it lives in the Windows SDK, which does not add itself.
    Highest version wins, because the older ones predate some of these switches.
#>
function Invoke-CodeSign([string[]] $paths) {
    if (-not $signing) {
        return
    }

    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object -Property FullName -Descending |
        Select-Object -First 1

    if (-not $signtool) {
        throw "Signing was requested but signtool.exe was not found. Install the Windows SDK, or drop the signing parameters."
    }

    foreach ($path in $paths) {
        Write-Host "  signing $(Split-Path -Leaf $path)"

        $arguments = @('sign', '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256')

        if ($SignThumbprint) {
            $arguments += @('/sha1', $SignThumbprint)
        }
        else {
            # The Azure Artifact Signing plug-in. /dlib names the library and /dmdf the account metadata, which is
            # what replaces a local private key - nothing sensitive is on this machine.
            $arguments += @('/dlib', 'Azure.CodeSigning.Dlib.dll', '/dmdf', (Resolve-Path $SignAzureMetadata).Path)
        }

        $arguments += $path

        $log = & $signtool.FullName @arguments 2>&1

        if ($LASTEXITCODE -ne 0) {
            $log | ForEach-Object { Write-Host "    $_" }
            throw "signtool failed for $path (exit $LASTEXITCODE)."
        }

        # Verified rather than trusted. /pa uses the Authenticode policy - the one Windows itself applies when
        # deciding whether to warn - so a signature that signtool wrote happily but Windows would reject fails
        # here instead of in front of a user. A self-signed certificate fails this on purpose: see the note in
        # the release checklist about what self-signing is and is not good for.
        $verify = & $signtool.FullName verify /pa /q $path 2>&1

        if ($LASTEXITCODE -ne 0) {
            $verify | ForEach-Object { Write-Host "    $_" }
            Write-Warning "$(Split-Path -Leaf $path) is signed but does not verify against the Authenticode policy."
            Write-Warning "Windows will still warn. This is expected for a self-signed certificate."
        }
    }
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\release'
}

# --------------------------------------------------------------- 1. version

# Asked of MSBuild rather than read out of Directory.Build.props with a regex, because the version is no
# longer written there: the revision is the commit count, resolved by a target. Reimplementing "base plus
# git rev-list --count" here would work and then quietly stop agreeing the first time either side changed,
# and the symptom is a .zip whose name disagrees with the exe inside it. Step 5 still verifies the two
# match, which is what would catch this going wrong anyway.

$versionOutput = & dotnet msbuild $project -t:PrintPasteJumpVersion -nologo -v:m

if ($LASTEXITCODE -ne 0) {
    $versionOutput | ForEach-Object { Write-Host "  $_" }
    throw "Could not resolve the version (dotnet msbuild -t:PrintPasteJumpVersion exited $LASTEXITCODE)."
}

$match = [regex]::Match(($versionOutput -join "`n"), 'PasteJumpVersion=([0-9]+(?:\.[0-9]+){3})')

if (-not $match.Success) {
    throw "Could not read the version from the PrintPasteJumpVersion target's output."
}

$version = $match.Groups[1].Value
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
    # build under the right name - in one package and not the other, which is worse than in both. Now that
    # the revision comes from the commit count it also catches a publish made before the last commit.
    $candidateVersion = (Get-Item $candidate).VersionInfo.FileVersion

    if ($candidateVersion -ne $version) {
        throw "$candidate reports $candidateVersion but this build resolves to $version. Publish again without -SkipPublish."
    }
}

# A folder publish that produced only the exe means the single-file properties did not actually come off,
# and the installer would ship a single-file build while claiming to be the fast one.
$folderFileCount = (Get-ChildItem $folderPublishDirectory -Recurse -File).Count

if ($folderFileCount -lt 50) {
    throw "The folder publish has only $folderFileCount files, so it is still a single-file build. Check the -p: overrides."
}

# Signed here: after the version check, so a mismatched build is rejected before anything is signed, and well
# before staging or hashing. Only the two executables, not the folder build's 250 DLLs - SmartScreen and the
# publisher prompt read the exe, the .NET assemblies beside it are Microsoft's and already signed, and each
# signature is a billable call on the Azure route.
if ($signing) {
    Write-Host "  code signing..."
    Invoke-CodeSign @($exe, $folderExe)
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

    # Between ISCC and the hash, for the same reason the executables are signed before staging: signing rewrites
    # the file. Note the exe inside the installer was already signed above, so a signed setup.exe means both the
    # thing you download and the thing it installs carry a signature.
    Invoke-CodeSign @($setup)

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

if (-not $signing) {
    Write-Host "Neither package is code-signed, so Windows will show a SmartScreen warning on first run."
    Write-Host "Pass -SignThumbprint or -SignAzureMetadata to sign. See the release checklist in CLAUDE.md."
}
else {
    $how = if ($SignThumbprint) { "certificate $SignThumbprint" } else { "Azure Artifact Signing" }

    Write-Host "Signed with $how, timestamped by $TimestampUrl."
    Write-Host "A signature names the publisher; it does not silence SmartScreen on its own - reputation accrues"
    Write-Host "as the file is downloaded, so early downloads may still be warned about."
}
