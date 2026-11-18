#requires -Version 7.2
using namespace System.IO.Compression
using namespace System.Reflection.PortableExecutable
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$IdentityName,
    [Parameter(Mandatory)][string]$Publisher
)
$ErrorActionPreference = 'Stop'
$archive = [ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package))
function Read-PackageText([string]$name) {
    $reader = [IO.StreamReader]::new($archive.GetEntry($name).Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}
try {
    [xml]$xml = Read-PackageText 'AppxManifest.xml'
    $manifest = $xml.Package
    $identity = $manifest.Identity
    $app = $manifest.Applications.Application
    if ($identity.Name -ne $IdentityName -or $identity.Publisher -ne $Publisher -or $identity.ProcessorArchitecture -ne $Architecture) { throw 'Package identity or architecture mismatch.' }
    $startup = $app.Extensions.Extension | Where-Object Category -eq 'windows.startupTask'
    if ($app.Executable -ne $startup.Executable -or $startup.StartupTask.TaskId -ne 'FluentCanvasStartup' -or $startup.StartupTask.Enabled -ne 'false') { throw 'Startup activation contract mismatch.' }
    $association = $app.Extensions.Extension | Where-Object Category -eq 'windows.fileTypeAssociation'
    if (Compare-Object @('.icart','.icstk') @($association.FileTypeAssociation.SupportedFileTypes.FileType)) { throw 'Document associations do not match.' }
    $required = @($app.Executable,'resources.pri','FluentCanvas.dll','Sentry.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','PresentationFramework.dll',
        'Microsoft.UI.Xaml.dll','Microsoft.WindowsAppRuntime.dll','Microsoft.UI.Xaml.Controls.pri','LICENSE','docs/store/THIRD-PARTY.md','docs/store/PRIVACY.zh-CN.md')
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) { $null = $files.Add($entry.FullName.Replace('\','/')) }
    foreach ($file in $required) {
        if (!$files.Contains($file.Replace('\','/'))) { throw "Missing $file in $Package" }
    }
    $config = Read-PackageText 'FluentCanvas.runtimeconfig.json' | ConvertFrom-Json
    foreach ($framework in 'Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') {
        $included = @($config.runtimeOptions.includedFrameworks | Where-Object name -eq $framework)
        if ($included.Count -ne 1 -or $included[0].version -ne '11.0.0') { throw "Incorrect $framework version or deployment mode." }
    }
    $deps = Read-PackageText 'FluentCanvas.deps.json' | ConvertFrom-Json -AsHashtable
    if (!$deps.runtimeTarget.name.EndsWith("/win-$Architecture")) { throw 'Dependency graph targets a different RID.' }
    foreach ($runtime in 'Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') {
        $runtimePack = "runtimepack.$runtime.Runtime.win-$Architecture/11.0.0"
        if (!$deps.libraries.ContainsKey($runtimePack)) { throw "Missing pinned pack $runtimePack in dependency graph." }
    }
    $nativeCount = 0
    foreach ($entry in $archive.Entries) {
        if ($entry.Name -notmatch '\.(dll|exe)$') { continue }
        $stream = $entry.Open()
        $memory = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            $memory.Position = 0
            $reader = [PEReader]::new($memory)
            try {
                $pe = $reader.PEHeaders
                # Resource-only image DLLs may be shared across architectures.
                if ($pe.SectionHeaders | Where-Object { [uint32]$_.SectionCharacteristics -band 0x20 }) {
                    if ($pe.CorHeader -and ($pe.CorHeader.Flags -band [CorFlags]::ILOnly) -and $pe.CoffHeader.Machine -eq 'I386' -and !($pe.CorHeader.Flags -band [CorFlags]::Requires32Bit)) { continue }
                    $expected = if ($Architecture -eq 'x64') { 'Amd64' } else { 'Arm64' }
                    # Microsoft's x64 WindowsAppRuntime 1.8 framework also carries its ARM64EC workload resource helper.
                    if ($Architecture -eq 'x64' -and $entry.FullName -eq 'Microsoft.Windows.Workloads.Resources_ec.dll' -and $pe.CoffHeader.Machine -eq 'Arm64') {
                        $memory.Position = 0
                        if ((Get-FileHash -InputStream $memory).Hash -eq '3DBEFA883EA9DCBB0FEE463AFDA0121E385BB678652CA2FAA19CF2ABF517091E') { continue }
                    }
                    if ($pe.CoffHeader.Machine.ToString() -ne $expected) { throw "Unexpected PE machine $($pe.CoffHeader.Machine) in $($entry.FullName) (expected $expected)." }
                    $nativeCount++
                }
            } finally { $reader.Dispose() }
        } finally { $memory.Dispose(); $stream.Dispose() }
    }
    if ($nativeCount -eq 0) { throw 'No architecture-specific PE binaries were checked.' }
    Write-Host "$($identity.Name) $($identity.Version) $Architecture passed package checks ($nativeCount architecture-specific binaries)."
} finally { $archive.Dispose() }
