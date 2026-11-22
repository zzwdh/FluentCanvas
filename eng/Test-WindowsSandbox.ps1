[CmdletBinding(DefaultParameterSetName='Package')]
param(
    [Parameter(Mandatory,ParameterSetName='Package')][string]$Msix,
    [Parameter(Mandatory,ParameterSetName='Bundle')][string]$Bundle,
    [Parameter(Mandatory)][string]$Certificate,
    [Parameter(ParameterSetName='Package')][string]$UpgradeFixture,
    [Parameter(ParameterSetName='Package')][string]$FixtureCertificate,
    [Parameter(ParameterSetName='Package')][version]$PreviousVersion = '5.0.1.0',
    [Parameter(ParameterSetName='Package')][switch]$Wack,
    [Parameter(ParameterSetName='Package')][switch]$RequireReleasePass,
    [int]$TimeoutSeconds = 1500
)
$ErrorActionPreference = 'Stop'
if ($RequireReleasePass -and (!$Wack -or !$UpgradeFixture -or !$FixtureCertificate)) {
    throw 'Release acceptance requires certification and an upgrade package with its certificate.'
}
$running = Get-Process -Name WindowsSandbox,WindowsSandboxClient -ErrorAction SilentlyContinue
$wsbCli = Get-Command wsb.exe -ErrorAction SilentlyContinue
$sessionListed = $false
if ($wsbCli) {
    try {
        $sessions = & $wsbCli.Source list 2>$null
        $sessionListed = !$LASTEXITCODE -and ($sessions -join ' ') -match '[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}'
    }
    catch { Write-Verbose 'The Windows Sandbox command alias is unavailable. Checked running processes instead.' }
}
if ($running -or $sessionListed) {
    throw 'An existing Windows Sandbox session is in use. Close it before starting a clean acceptance run.'
}
$repo = Split-Path -Parent $PSScriptRoot
$run = Join-Path $repo ('artifacts\sandbox\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6))
$inputDir = Join-Path $run 'input'
$evidence = Join-Path $run 'evidence'
New-Item -ItemType Directory -Path $inputDir,$evidence -Force | Out-Null
$candidate = if ($Bundle) { $Bundle } else { $Msix }
$candidateHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
$stagedCandidate = Join-Path $inputDir $(if ($Bundle) { 'candidate.msixbundle' } else { 'candidate.msix' })
Copy-Item -LiteralPath $candidate -Destination $stagedCandidate
Copy-Item -LiteralPath $Certificate -Destination (Join-Path $inputDir 'candidate.cer')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-SandboxGuest.ps1') -Destination $inputDir
$resourceDir = Join-Path $repo 'FluentCanvas\Localization'
$captionKeys = [ordered]@{
    Canvas = @('WindowTitle','MoreTools','Pen','Settings','SaveDocument','PreviousPage','Redo','Undo','EraseStrokes')
    Settings = @('WindowTitle','Restart','DiagnosticsTitle')
    Actions = @('OpenDocument')
}
$captionProfiles = foreach ($canvas in Get-ChildItem -LiteralPath $resourceDir -Filter 'Canvas*.resx' -File) {
    $suffix = $canvas.BaseName.Substring('Canvas'.Length)
    $profile = [ordered]@{}
    foreach ($area in $captionKeys.Keys) {
        $resourcePath = Join-Path $resourceDir "$area$suffix.resx"
        $resources = [xml](Get-Content -LiteralPath $resourcePath -Raw -Encoding UTF8)
        foreach ($key in $captionKeys[$area]) {
            $caption = $resources.SelectSingleNode("/root/data[@name='$key']/value").InnerText
            if ([string]::IsNullOrWhiteSpace($caption)) { throw "Missing caption $area.$key in $resourcePath" }
            $profile["$area.$key"] = $caption
        }
    }
    [pscustomobject]$profile
}
ConvertTo-Json -InputObject @($captionProfiles) | Set-Content -LiteralPath (Join-Path $inputDir 'captions.json') -Encoding UTF8
if ($UpgradeFixture -or $FixtureCertificate) {
    if (!$UpgradeFixture -or !$FixtureCertificate) {throw 'Supply both upgrade fixture paths.'}
    Copy-Item -LiteralPath $UpgradeFixture -Destination (Join-Path $inputDir 'previous.msix')
    Copy-Item -LiteralPath $FixtureCertificate -Destination (Join-Path $inputDir 'previous.cer')
    $PreviousVersion.ToString() | Set-Content -LiteralPath (Join-Path $inputDir 'previous-version.txt') -Encoding ascii
}
if ($Wack) {
    # Stage only the installed SDK's certification MSIs and matching cabinets.
    $packages = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'Windows App Certification Kit*' -and $_.InstallSource }
    if (!$packages) { throw 'Install the Windows SDK App Certification Kit component and retry.' }
    $kit = New-Item -ItemType Directory -Path (Join-Path $inputDir 'wack')
    foreach ($package in $packages) {
        Get-ChildItem -LiteralPath $package.InstallSource -File | Where-Object Extension -In '.msi','.cab' |
            Copy-Item -Destination $kit.FullName
    }
    # WACK's Windows Runtime metadata validator reads the platform's contracts from
    # Windows Kits/10/References/<SDK version>. The WACK MSIs do not supply them.
    $references=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/References'
    $version=Get-ChildItem -LiteralPath $references -Directory | Where-Object Name -Match '^10\.0\.\d+\.\d+$' |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $contracts=@(Get-ChildItem -LiteralPath $version.FullName -Recurse -File -Filter '*.winmd')
    if (!$contracts.Count) {throw 'Install Windows SDK for Windows Store Apps Contracts and Metadata.'}
    foreach ($contract in $contracts) {
        $relative=[IO.Path]::GetRelativePath($references,$contract.FullName)
        $destination=Join-Path "$inputDir/References" $relative
        $null=New-Item -ItemType Directory -Force -Path (Split-Path $destination)
        Copy-Item -LiteralPath $contract.FullName -Destination $destination
    }
}
$manifest = [xml]@"
<Configuration><Networking>Disable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><AudioInput>Disable</AudioInput><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB>
<MappedFolders><MappedFolder><HostFolder></HostFolder><SandboxFolder>C:\Inputs</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder><MappedFolder><HostFolder></HostFolder><SandboxFolder>C:\Evidence</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder></MappedFolders>
<LogonCommand><Command>powershell.exe -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\Inputs\Test-SandboxGuest.ps1</Command></LogonCommand></Configuration>
"@
$manifest.Configuration.MappedFolders.MappedFolder[0].HostFolder = [string]$inputDir
$manifest.Configuration.MappedFolders.MappedFolder[1].HostFolder = [string]$evidence
$wsb = Join-Path $run 'test.wsb'
$manifest.Save($wsb)
Write-Output "Guest evidence: $evidence"
Start-Process -FilePath "$env:SystemRoot\System32\WindowsSandbox.exe" -ArgumentList ('"' + $wsb + '"') -WindowStyle Hidden
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$done = Join-Path $evidence 'complete.json'
while ((Get-Date) -lt $deadline -and !(Test-Path -LiteralPath $done)) { Start-Sleep -Seconds 4 }
if (!(Test-Path -LiteralPath $done)) { throw "No guest completion before timeout. Inspect $evidence" }
$result = Get-Content -Raw $done -Encoding UTF8 | ConvertFrom-Json
$result | ConvertTo-Json -Depth 5
if (@($result.Results | Where-Object Status -eq 'FAIL').Count -gt 0) { throw "Guest checks failed. Inspect $evidence" }
if ($RequireReleasePass) {
    $installedCandidate = $result.Results | Where-Object { $_.Check -eq 'Install signed artifact' -and $_.Status -eq 'PASS' }
    $upgrade = $result.Results | Where-Object { $_.Check -like 'Upgrade retains LocalState*' -and $_.Status -eq 'PASS' }
    $certification = $result.Results | Where-Object { $_.Check -eq 'Windows App Certification Kit' -and $_.Status -eq 'PASS' }
    if (!$installedCandidate -or $installedCandidate.Detail -notlike "*SHA256 $candidateHash" -or !$upgrade -or !$certification) {
        throw "Release acceptance requires passing installation, upgrade and certification for SHA256 $candidateHash. Inspect $evidence"
    }
}
if (@($result.Results | Where-Object Status -eq 'WARNING').Count -gt 0) { Write-Warning "The certification kit reported warnings. Inspect $evidence\wack.xml" }
