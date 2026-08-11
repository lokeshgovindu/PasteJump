<#
.SYNOPSIS
    Builds the release packages: three ZIPs of the same build, and a Windows installer.

.DESCRIPTION
    One script, because packages assembled separately drift, and the first sign of it is a user reporting a
    file that is in one and not the other. Everything below comes from the same publish of the same commit.

    THREE ZIPS, which is three shapes of one program rather than three products. What separates them is who
    supplies .NET and whether the runtime is unpacked in advance:

      PasteJump-<version>-win-x64.zip           One PasteJump.exe. Nothing to install, nothing beside it,
                                                copies to a USB stick and takes its data with it. About
                                                65 MB, and the default download.
      PasteJump-<version>-win-x64-unpacked.zip  The same build with the runtime already unpacked - ~257
                                                files. Starts about 4x faster warm (see the table below),
                                                because single-file spends that time extracting before a
                                                line of our code runs. For an app that starts at logon and
                                                runs all day, that is the case that matters.
      PasteJump-<version>-win-x64-net10.zip     PasteJump's own files only - 15 files, under 4 MB. Needs the
                                                .NET 10 Desktop Runtime already on the machine; without it
                                                Windows says so and offers the download.

    Measured warm, same store, D: drive:

      single file   1,100-1,145 ms before Compose,  138-140 ms in it,  ~1,260 ms total
      unpacked        171-176 ms before Compose,  112-114 ms in it,    ~286 ms total

    The cold case reverses and honestly so: a first launch after unpacking is slower, because Defender scans
    255 new files instead of one. Do not quote the warm figure alone.

    Steps, in order, because each depends on the last:

      1. Ask MSBuild for the version. Never passed in - a version typed on the command line is a version
         that can disagree with the binary it names. It is not read out of Directory.Build.props either,
         because the revision is not stored there: it is the commit count, resolved by a target.
      2. Publish Release three times, win-x64, and check all three report the version above.
      3. Compile the help, so the .chm in the packages matches this source tree.
      4. Stage each shape with the same documents beside it.
      5. Zip each staging folder, and hash it.
      6. Compile the installer from the unpacked staging folder, and hash that.

    Step 6 is skipped with a warning when Inno Setup is not installed, because the ZIPs are the primary
    artifacts and most of a release is better than none.

.PARAMETER SkipPublish
    Reuse whatever is already under artifacts\publish*. For iterating on the packaging itself - three
    publishes take far longer than everything else here.

.PARAMETER OutputDirectory
    Where the finished packages go. Defaults to artifacts/release.

.PARAMETER SignThumbprint
    SHA1 thumbprint of a code-signing certificate in CurrentUser\My. Everything shippable is signed with it
    before anything is hashed. Omit both signing parameters and the release is unsigned, which is what it has
    always been. tools/sign-local.ps1 -ThumbprintOnly prints a self-signed one, which is good for rehearsing
    this path and for nothing else - see that script's warnings before using it on something you publish.

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
# and the symptom is a .zip whose name disagrees with the exe inside it. Step 2 still verifies the two
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

# --------------------------------------------------------------- 2. publish

# THREE publishes of one commit. The shapes and what each is for are described at the top of this file; what
# follows is only how each is asked for.
#
# Note the direction of the overrides: the csproj turns single-file ON for everyone, so the other two turn it
# back off rather than the single-file one turning it on. That way a plain `dotnet publish` by hand still
# produces the portable exe the README describes, which is the one someone reaching for that command wants.

$shapes = @(
    [ordered]@{
        Key          = 'single'
        Label        = 'single file, portable'
        Suffix       = ''
        PublishDir   = Join-Path $repoRoot 'artifacts\publish'
        Arguments    = @()
        MinimumFiles = 1
        Note         = 'one exe, no prerequisites'
    }
    [ordered]@{
        Key          = 'unpacked'
        Label        = 'unpacked, runtime included'
        Suffix       = '-unpacked'
        PublishDir   = Join-Path $repoRoot 'artifacts\publish-folder'
        Arguments    = @(
            '-p:PublishSingleFile=false',
            '-p:EnableCompressionInSingleFile=false',
            '-p:IncludeNativeLibrariesForSelfExtract=false'
        )
        # Fifty is a long way below the ~257 this really produces, deliberately: the check is here to catch a
        # single-file build wearing this name, not to assert an exact file count that every .NET update moves.
        MinimumFiles = 50
        Note         = 'no prerequisites, fastest warm start'
    }
    [ordered]@{
        Key          = 'net10'
        Label        = 'framework-dependent'
        Suffix       = '-net10'
        PublishDir   = Join-Path $repoRoot 'artifacts\publish-framework'
        Arguments    = @(
            '--self-contained', 'false',
            '-p:PublishSingleFile=false',
            '-p:EnableCompressionInSingleFile=false',
            '-p:IncludeNativeLibrariesForSelfExtract=false'
        )
        MinimumFiles = 5
        Note         = 'needs the .NET 10 Desktop Runtime'
    }
)

function Invoke-Publish([string] $label, [string] $destination, [string[]] $extra) {
    Write-Host "  $label"

    # A stale publish is worse than none: files a later build stopped producing would stay behind and be
    # packaged, and one wrong assembly version is a runtime failure with no clue as to why. This matters most
    # for the two directory shapes, but costs nothing for the single-file one.
    if (Test-Path $destination) {
        Remove-Item $destination -Recurse -Force
    }

    $log = & dotnet publish $project -c Release -o $destination --nologo -v q @extra

    if ($LASTEXITCODE -ne 0) {
        $log | ForEach-Object { Write-Host "    $_" }
        throw "dotnet publish failed for $label (exit $LASTEXITCODE)."
    }
}

if ($SkipPublish) {
    Write-Host "[1/5] Publish skipped; reusing what is already under artifacts\publish*"
}
else {
    Write-Host "[1/5] Publishing Release three times..."

    foreach ($shape in $shapes) {
        Invoke-Publish $shape.Label $shape.PublishDir $shape.Arguments
    }
}

foreach ($shape in $shapes) {
    $shapeExe = Join-Path $shape.PublishDir 'PasteJump.exe'

    if (-not (Test-Path $shapeExe)) {
        throw "No PasteJump.exe at $shapeExe."
    }

    # Every published binary must be the version this script claims to be packaging. Cheap to check, and it
    # catches -SkipPublish being used against a stale publish, which would otherwise ship the wrong build
    # under the right name - in one package and not the others, which is worse than in all of them. Now that
    # the revision comes from the commit count it also catches a publish made before the last commit.
    $shapeVersion = (Get-Item $shapeExe).VersionInfo.FileVersion

    if ($shapeVersion -ne $version) {
        throw "$shapeExe reports $shapeVersion but this build resolves to $version. Publish again without -SkipPublish."
    }

    $shapeFiles = Get-ChildItem $shape.PublishDir -Recurse -File
    $shape.Exe = $shapeExe
    $shape.FileCount = $shapeFiles.Count
    $shape.Bytes = ($shapeFiles | Measure-Object -Property Length -Sum).Sum

    # A publish that came out the wrong shape is the failure this guards, and each shape fails differently:
    # too few files means the single-file properties did not come off and this "unpacked" package would ship a
    # single-file build while claiming to be the fast one.
    if ($shape.FileCount -lt $shape.MinimumFiles) {
        throw "The $($shape.Key) publish has only $($shape.FileCount) files, fewer than the $($shape.MinimumFiles) expected. Check its -p: overrides."
    }

    # And the framework-dependent one fails the other way round: --self-contained false silently ignored
    # would produce a perfectly working 135 MB directory that nobody could tell from the unpacked shape until
    # they looked at its size. coreclr.dll is the runtime itself, so its presence is the tell.
    if ($shape.Key -eq 'net10' -and (Test-Path (Join-Path $shape.PublishDir 'coreclr.dll'))) {
        throw "The framework-dependent publish contains coreclr.dll, so it is self-contained after all. Check --self-contained false."
    }
}

# Signed here: after the version check, so a mismatched build is rejected before anything is signed, and well
# before staging or hashing. Only the executables, not the unpacked shape's 250 DLLs - SmartScreen and the
# publisher prompt read the exe, the .NET assemblies beside it are Microsoft's and already signed, and each
# signature is a billable call on the Azure route.
if ($signing) {
    Write-Host "  code signing..."
    Invoke-CodeSign @($shapes | ForEach-Object { $_.Exe })
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
    # Not fatal on its own, but the packages would silently lose their manual - so it fails here unless a
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

# The documents every package carries. LICENSE is renamed on the way in: the repository file has no
# extension, and both Explorer and Inno's licence page want one.
$documents = @(
    @{ From = $chm; To = 'PasteJump.chm' },
    @{ From = Join-Path $repoRoot 'README.md'; To = 'README.md' },
    @{ From = Join-Path $repoRoot 'LICENSE'; To = 'LICENSE.txt' }
)

foreach ($shape in $shapes) {
    $stageName = "PasteJump-$version-win-x64$($shape.Suffix)"
    $stageDirectory = Join-Path $OutputDirectory "stage\$stageName"

    if (Test-Path $stageDirectory) {
        Remove-Item $stageDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null

    # The single-file shape stages its one executable; the other two stage the whole publish. Written as a
    # test on the shape rather than on the file count, so an empty directory fails later as a missing exe
    # instead of quietly staging nothing.
    if ($shape.Key -eq 'single') {
        Copy-Item $shape.Exe (Join-Path $stageDirectory 'PasteJump.exe')
    }
    else {
        Copy-Item "$($shape.PublishDir)\*" $stageDirectory -Recurse -Force
    }

    foreach ($document in $documents) {
        Copy-Item $document.From (Join-Path $stageDirectory $document.To)
    }

    $shape.StageName = $stageName
    $shape.StageDirectory = $stageDirectory

    $staged = Get-ChildItem $stageDirectory -Recurse -File

    Write-Host ("  {0,-28} {1,4} files  {2,7:N1} MB" -f $stageName, $staged.Count,
        (($staged | Measure-Object -Property Length -Sum).Sum / 1MB))
}

# --------------------------------------------------------------- 5. zip

Write-Host "[4/5] Zipping..."

function Write-Hash([string] $path) {
    $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = Split-Path -Leaf $path

    # sha256sum's own format, two spaces between, so `sha256sum -c` verifies it unchanged.
    Set-Content -Path "$path.sha256" -Value "$hash  $name" -Encoding ascii -NoNewline

    return $hash
}

foreach ($shape in $shapes) {
    $zip = Join-Path $OutputDirectory "$($shape.StageName).zip"

    if (Test-Path $zip) {
        Remove-Item $zip -Force
    }

    # The staging folder itself is compressed, not its contents, so each archive expands into a named folder
    # rather than scattering files - four of them for one shape and 260 for another - into whatever directory
    # it was opened in.
    Compress-Archive -Path $shape.StageDirectory -DestinationPath $zip -CompressionLevel Optimal

    $shape.Zip = $zip
    $shape.ZipHash = Write-Hash $zip

    Write-Host ("  {0,-44} {1,7:N1} MB" -f (Split-Path -Leaf $zip), ((Get-Item $zip).Length / 1MB))
}

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
$setupHash = $null

if (-not $iscc) {
    Write-Warning "Inno Setup not found, so no installer was built. The ZIPs above are complete."
    Write-Warning "Install it with:  winget install --id JRSoftware.InnoSetup"
}
else {
    $script = Join-Path $repoRoot 'packaging\PasteJump.iss'

    # The unpacked shape's staging folder, which is also what that ZIP contains - one directory feeding both,
    # so an installed copy and an unzipped one cannot differ. This is what makes setup.exe deploy the fast
    # build while the default download stays one file.
    $unpackedStage = ($shapes | Where-Object { $_.Key -eq 'unpacked' }).StageDirectory

    $isccLog = & $iscc `
        "/DAppVersion=$version" `
        "/DStageDir=$unpackedStage" `
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
Write-Host ""

foreach ($shape in $shapes) {
    $item = Get-Item $shape.Zip

    Write-Host ("  {0,-44} {1,7:N1} MB  {2}" -f $item.Name, ($item.Length / 1MB), $shape.Note)
}

if ($setup) {
    $setupItem = Get-Item $setup
    Write-Host ("  {0,-44} {1,7:N1} MB  {2}" -f $setupItem.Name, ($setupItem.Length / 1MB), 'installs the unpacked shape')
}

Write-Host ""
Write-Host "SHA256"

foreach ($shape in $shapes) {
    Write-Host "  $($shape.ZipHash)  $(Split-Path -Leaf $shape.Zip)"
}

if ($setup) {
    Write-Host "  $setupHash  $(Split-Path -Leaf $setup)"
}

Write-Host ""

if (-not $signing) {
    Write-Host "Nothing here is code-signed, so Windows will show a SmartScreen warning on first run."
    Write-Host "Pass -SignThumbprint or -SignAzureMetadata to sign. See the release checklist in CLAUDE.md."
}
else {
    $how = if ($SignThumbprint) { "certificate $SignThumbprint" } else { "Azure Artifact Signing" }

    Write-Host "Signed with $how, timestamped by $TimestampUrl."
    Write-Host "A signature names the publisher; it does not silence SmartScreen on its own - reputation accrues"
    Write-Host "as the file is downloaded, so early downloads may still be warned about."
}
