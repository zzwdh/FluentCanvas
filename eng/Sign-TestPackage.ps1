#requires -Version 7.2
using namespace System.Security.Cryptography
using namespace System.Security.Cryptography.X509Certificates
[CmdletBinding()]
param([string[]]$Files, [string]$Subject, [string]$Thumbprint, [string]$SignTool, [string]$OutputCertificate)
$ErrorActionPreference = 'Stop'
$pfx = $null
$rsa = $null
$cert = $null
try {
    if ($Thumbprint) {
        $cert = Get-Item -LiteralPath "Cert:/CurrentUser/My/$Thumbprint"
        if (!$cert.HasPrivateKey -or $cert.Subject -ne $Subject) { throw "A current-user signing certificate with subject $Subject is required." }
        $options = @('/sha1',$cert.Thumbprint)
    } else {
        $rsa = [RSA]::Create(3072)
        $request = [CertificateRequest]::new($Subject,$rsa,[HashAlgorithmName]::SHA256,[RSASignaturePadding]::Pkcs1)
        $oids = [OidCollection]::new()
        $null = $oids.Add([Oid]::new('1.3.6.1.5.5.7.3.3'))
        $request.CertificateExtensions.Add([X509EnhancedKeyUsageExtension]::new($oids,$true))
        $request.CertificateExtensions.Add([X509BasicConstraintsExtension]::new($false,$false,0,$true))
        $request.CertificateExtensions.Add([X509KeyUsageExtension]::new([X509KeyUsageFlags]::DigitalSignature,$true))
        $now = [DateTimeOffset]::UtcNow
        $cert = $request.CreateSelfSigned($now.AddMinutes(-10),$now.AddMonths(6))
        $password = [Convert]::ToBase64String([RandomNumberGenerator]::GetBytes(32))
        $pfx = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.pfx')
        [IO.File]::WriteAllBytes($pfx,$cert.Export([X509ContentType]::Pfx,$password))
        $options = @('/f',$pfx,'/p',$password)
    }
    foreach ($package in $Files) {
        & $SignTool sign /fd SHA256 @options $package
        if ($LASTEXITCODE) { throw "Failed to sign $package" }
        $signature = Get-AuthenticodeSignature -LiteralPath $package
        if ($signature.SignerCertificate.Thumbprint -ne $cert.Thumbprint -or $signature.Status -in 'HashMismatch','NotSigned') {
            throw "Package signature did not match the test certificate for $package"
        }
    }
    [IO.File]::WriteAllBytes($OutputCertificate,$cert.Export([X509ContentType]::Cert))
} finally {
    if ($pfx -and (Test-Path -LiteralPath $pfx)) { Remove-Item -LiteralPath $pfx -Force }
    if ($cert) { $cert.Dispose() }
    if ($rsa) { $rsa.Dispose() }
}
