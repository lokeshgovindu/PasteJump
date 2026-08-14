<#
.SYNOPSIS
    Compiles docs/help into artifacts/help/PasteJump.chm.

.DESCRIPTION
    Wraps hhc.exe, the HTML Help compiler from HTML Help Workshop. Three things about it are worth knowing
    before debugging a failure here, because none of them behave like a normal build tool:

      - It returns exit code 1 on SUCCESS and 0 on failure. That is not a typo; it is documented behaviour
        and the reason this script parses the log instead of trusting $LASTEXITCODE.
      - It writes the .chm next to the .hhp, ignoring any output path. The file is moved afterwards, which
        is also what keeps build output out of the source tree.
      - It is a 32-bit tool that ships outside the SDKs, so it is looked for in its own install locations
        rather than expected on PATH.
      - Its "Graphics" count reports 0 even when images ARE compiled in, because that counter only covers
        images it discovered by scanning topics rather than ones listed in [FILES]. Judge inclusion by the
        output size, or by decompiling with `hh.exe -decompile <dir> <chm>`. Do not chase the zero.

    Not part of `dotnet build`. The help is a separate deliverable, and making every build depend on an
    optional external tool would break the build on a machine that does not have it.

.PARAMETER OutputDirectory
    Where to put the finished .chm. Defaults to artifacts/help.

.PARAMETER Show
    Open the compiled file in the help viewer when it is done.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/build-help.ps1 -Show
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [switch] $Show
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is unreliable in a parameter default under -File, so the paths are resolved in the body -
# the same reason generate-icon.ps1 takes its paths explicitly.
$repoRoot = Split-Path -Parent $PSScriptRoot
$helpSource = Join-Path $repoRoot 'docs\help'
$project = Join-Path $helpSource 'pastejump.hhp'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\help'
}

if (-not (Test-Path $project)) {
    throw "Help project not found: $project"
}

$candidates = @(
    "${env:ProgramFiles(x86)}\HTML Help Workshop\hhc.exe",
    "$env:ProgramFiles\HTML Help Workshop\hhc.exe",
    "$env:LOCALAPPDATA\Programs\HTML Help Workshop\hhc.exe"
)

$compiler = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $compiler) {
    $onPath = Get-Command hhc.exe -ErrorAction SilentlyContinue
    if ($onPath) { $compiler = $onPath.Source }
}

if (-not $compiler) {
    throw @"
hhc.exe not found. Install HTML Help Workshop (htmlhelp.exe from Microsoft), or put hhc.exe on PATH.
Looked in:
  $($candidates -join "`n  ")
"@
}

Write-Host "Compiler : $compiler"
Write-Host "Project  : $project"

# The compiler resolves [FILES] relative to its working directory, not to the .hhp.
Push-Location $helpSource

try {
    $log = & $compiler 'pastejump.hhp' 2>&1
    $log | ForEach-Object { Write-Host "  $_" }
}
finally {
    Pop-Location
}

$built = Join-Path $helpSource 'PasteJump.chm'

# The real success test. hhc's exit code is inverted and its log is the only trustworthy signal, so this
# checks that the file exists and that the log did not report topics it could not compile.
if (-not (Test-Path $built)) {
    throw "Compilation produced no .chm. See the log above."
}

$errorLines = $log | Where-Object { $_ -match 'HHC\d+' -or $_ -match 'error' }

if ($errorLines) {
    Write-Warning "The compiler reported problems:"
    $errorLines | ForEach-Object { Write-Warning "  $_" }
}

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}

$final = Join-Path $OutputDirectory 'PasteJump.chm'
Move-Item -Path $built -Destination $final -Force

$size = [math]::Round((Get-Item $final).Length / 1KB, 1)
Write-Host ""
Write-Host "Wrote $final ($size KB)" -ForegroundColor Green

# The GitHub-readable copy, regenerated from the same HTML this .chm was just compiled from - so the
# two cannot describe different programs. Here rather than left to be remembered: docs/manual is
# generated output that happens to be committed, and CI fails the build when it is stale
# (generate-markdown-help.py --check), which is a fine safety net and a poor workflow.
Write-Host ""
Write-Host "Regenerating the Markdown manual..."

$markdown = Join-Path $PSScriptRoot 'generate-markdown-help.py'
& py -3 $markdown

if ($LASTEXITCODE -ne 0) {
    # A warning, not a throw: the .chm above is built and valid, and refusing to finish would throw
    # away the expensive half of this script over the cheap half.
    Write-Warning "The Markdown manual could not be regenerated (exit $LASTEXITCODE). Run tools/generate-markdown-help.py by hand."
}

if ($Show) {
    # hh.exe, not Invoke-Item: a .chm opened from a path Windows considers untrusted shows blank pages
    # rather than an error, and going through the viewer directly is one less thing to misread.
    Start-Process -FilePath "$env:WINDIR\hh.exe" -ArgumentList $final
}
