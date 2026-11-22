#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Msix)
$ErrorActionPreference='Stop'
$sdks=Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory | Where-Object Name -match '^10\.0\.\d+\.\d+$' | Sort-Object {[version]$_.Name} -Descending
$bin=$sdks | ForEach-Object {Join-Path $_.FullName 'x64'} | Where-Object {Test-Path (Join-Path $_ 'makeappx.exe')} | Select-Object -First 1
if (!$bin) {throw 'Windows SDK x64 packaging tools are required.'}
$repo=Split-Path -Parent $PSScriptRoot
$run=Join-Path $repo ('artifacts\upgrade-fixtures\'+[guid]::NewGuid().ToString('N'))
$layout=New-Item -ItemType Directory -Path (Join-Path $run 'layout') -Force
& (Join-Path $bin 'makeappx.exe') unpack /p $Msix /d $layout.FullName /o | Out-Null
if ($LASTEXITCODE) {throw 'Unpack failed.'}
$path=Join-Path $layout.FullName 'AppxManifest.xml'
$manifest=[xml](Get-Content -LiteralPath $path -Raw)
$identity=$manifest.Package.Identity
if ($identity.Name -ne 'FluentCanvas.LocalTest' -or $identity.Publisher -ne 'CN=FluentCanvas Local Test' -or $identity.ProcessorArchitecture -ne 'x64') {throw 'Expected the signed local x64 test package.'}
if ([version]$identity.Version -le [version]'5.0.1.0') {throw 'Candidate must be newer than the test fixture.'}
$identity.Version='5.0.1.0'
$manifest.Save($path)
foreach ($name in 'AppxBlockMap.xml','AppxSignature.p7x','[Content_Types].xml','AppxMetadata\CodeIntegrity.cat') {
    $footprint=Join-Path $layout.FullName $name
    if (Test-Path -LiteralPath $footprint) {Remove-Item -LiteralPath $footprint -Force}
}
$previous=Join-Path $run 'upgrade-fixture.msix'
$cert=Join-Path $run 'upgrade-fixture.cer'
& (Join-Path $bin 'makeappx.exe') pack /d $layout.FullName /p $previous /o | Out-Null
if ($LASTEXITCODE) {throw 'Repack failed.'}
& (Join-Path $PSScriptRoot 'Sign-TestPackage.ps1') -Files $previous -Subject $identity.Publisher -SignTool (Join-Path $bin 'signtool.exe') -OutputCertificate $cert
Write-Output "Test fixture only: $previous"
Write-Output "Public test certificate: $cert"
