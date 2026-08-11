<#
.SYNOPSIS
    Signs a local build with a self-signed development certificate, creating the certificate on first use.

.DESCRIPTION
    For seeing a Digital Signatures tab on a development build, and for exercising the signing path in
    tools/pack-release.ps1 without buying a certificate. It is NOT a substitute for one.

    What a self-signed signature does and does not do, because the difference matters more here than anywhere
    else in this repository:

      It DOES     put a Digital Signatures tab on the file, name the publisher, cover the bytes with a hash so
                  tampering is detectable, and carry an RFC 3161 timestamp.
      It does NOT validate anywhere but on a machine that has chosen to trust the certificate. Everywhere
                  else Explorer reports "the signature is invalid or corrupt" or "a certificate chain
                  terminated in a root certificate which is not trusted", and SmartScreen is unmoved.

    So this is a development convenience. Signing a release with it would be worse than shipping unsigned: an
    unsigned file is merely unknown, while one carrying a signature that fails to validate looks tampered with.
    That is why -SelfSigned is deliberately absent from pack-release.ps1 - once the certificate exists here,
    pass its thumbprint to that script explicitly if you want to rehearse the release path.

    The certificate is created in CurrentUser\My, needs no administrator, and is reused on later runs: a new
    one per run would mean a different publisher identity each time and a store full of near-duplicates. It is
    found by subject rather than by a thumbprint written down somewhere, since the thumbprint changes whenever
    the certificate is regenerated.

    Making Windows accept the signature means installing the certificate as a trusted root, which this script
    deliberately does not do - see the closing note it prints. Trusting a root is a decision about the machine,
    it prompts with a security warning that cannot be answered from a script, and the certificate could then
    vouch for anything else signed with it.

.PARAMETER Path
    Files to sign. Defaults to the deployed development copy, which is the one whose properties you would open.

.PARAMETER ThumbprintOnly
    Create or find the certificate, print its thumbprint, sign nothing. This is how to get a value for
    pack-release.ps1 -SignThumbprint.

.PARAMETER NoTimestamp
    Skip the timestamp countersignature. Only for a machine with no network - a signature with no timestamp
    stops validating the day the certificate expires.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/sign-local.ps1

.EXAMPLE
    powershell -File tools/sign-local.ps1 -Path artifacts/publish/PasteJump.exe

.EXAMPLE
    # Rehearse the release path with the same certificate
    $t = powershell -File tools/sign-local.ps1 -ThumbprintOnly
    powershell -File tools/pack-release.ps1 -SignThumbprint $t
#>
[CmdletBinding()]
param(
    [string[]] $Path,
    [switch] $ThumbprintOnly,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $NoTimestamp
)

$ErrorActionPreference = 'Stop'

# Subject, and it is chosen rather than incidental. CN is what Explorer shows as "Name of signer", so it is the
# real publisher name - a CN of "Test" would make the tab prove nothing about what the field will say. The OU
# carries the warning instead, where anyone reading the details will meet it.
$subject = 'CN=Lokesh Govindu, OU=PasteJump development build (self-signed)'
$friendlyName = 'PasteJump development signing'

function Get-DevelopmentCertificate {
    # Not expiring within the month, so a run does not quietly produce a signature that is already dying, and
    # HasPrivateKey because a public-only copy imported from somewhere else cannot sign.
    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -eq $subject -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date).AddDays(30) -and
            ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' })
        } |
        Sort-Object -Property NotAfter -Descending |
        Select-Object -First 1

    if ($existing) {
        Write-Host "Reusing certificate $($existing.Thumbprint) (expires $($existing.NotAfter.ToString('yyyy-MM-dd')))"
        return $existing
    }

    Write-Host "Creating a self-signed code-signing certificate in CurrentUser\My..."

    # Three years: long enough not to be a recurring chore, short enough that a forgotten development
    # certificate does not linger for a decade. SHA256 because SHA1 signatures are distrusted outright.
    $created = New-SelfSignedCertificate `
        -Subject $subject `
        -FriendlyName $friendlyName `
        -Type CodeSigningCert `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(3)

    Write-Host "Created $($created.Thumbprint)"

    return $created
}

function Get-SignTool {
    # Same lookup as pack-release.ps1: signtool lives in the Windows SDK, which does not put itself on PATH,
    # and the highest version wins because older copies predate /tr.
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object -Property FullName -Descending |
        Select-Object -First 1

    if (-not $signtool) {
        throw "signtool.exe was not found. Install the Windows SDK (it ships with Visual Studio's C++ workload)."
    }

    return $signtool.FullName
}

$certificate = Get-DevelopmentCertificate

if ($ThumbprintOnly) {
    # The only thing written to the success stream, so the caller can capture it with $t = ...
    Write-Output $certificate.Thumbprint
    exit 0
}

if (-not $Path) {
    $Path = @('D:\Lokesh\DoNotMove\PasteJump\PasteJump.exe')
    Write-Host "No -Path given, so signing the deployed development copy:"
}

$signtool = Get-SignTool

foreach ($file in $Path) {
    if (-not (Test-Path $file)) {
        throw "Nothing to sign at $file."
    }

    $resolved = (Resolve-Path $file).Path
    $name = Split-Path -Leaf $resolved

    Write-Host ""
    Write-Host "Signing $name"

    $arguments = @('sign', '/fd', 'sha256', '/sha1', $certificate.Thumbprint)

    if (-not $NoTimestamp) {
        $arguments += @('/tr', $TimestampUrl, '/td', 'sha256')
    }

    $arguments += $resolved

    $log = & $signtool @arguments 2>&1

    if ($LASTEXITCODE -ne 0) {
        $log | ForEach-Object { Write-Host "  $_" }

        # A locked file is the failure that actually happens here, and the message signtool gives for it is
        # about access rather than about the running process - so it is worth naming the cause.
        throw "signtool failed for $name (exit $LASTEXITCODE). If it is running, close it first: signing rewrites the file."
    }

    # Reported from Windows' own API rather than from signtool, because this is the verdict the properties
    # dialog will show. Expect NotSignedByTrustedRoot or UnknownError: that is what self-signed means, and
    # printing it here is the difference between a known limitation and a mystery later.
    $status = Get-AuthenticodeSignature $resolved

    Write-Host "  signer:    $($status.SignerCertificate.Subject)"
    Write-Host "  status:    $($status.Status)"
    Write-Host "  timestamp: $(if ($status.TimeStamperCertificate) { 'countersigned' } else { 'none' })"
}

Write-Host ""
Write-Host "Done. The file now has a Digital Signatures tab, showing Lokesh Govindu as the signer." -ForegroundColor Green
Write-Host ""
Write-Host "Windows will report the signature as not trusted, because nothing vouches for the certificate. To"
Write-Host "make it validate ON THIS MACHINE ONLY, install it as a trusted root yourself - it prompts, so a"
Write-Host "script cannot do it for you:"
Write-Host ""
Write-Host "  `$c = Get-ChildItem Cert:\CurrentUser\My\$($certificate.Thumbprint)"
Write-Host "  Export-Certificate -Cert `$c -FilePath `$env:TEMP\pastejump-dev.cer | Out-Null"
Write-Host "  Import-Certificate -FilePath `$env:TEMP\pastejump-dev.cer -CertStoreLocation Cert:\CurrentUser\Root"
Write-Host ""
Write-Host "Understand what that does: anything signed with this certificate is then trusted on this account,"
Write-Host "not just PasteJump. Undo it by deleting the certificate from Cert:\CurrentUser\Root."
