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
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw
$desktopRuntimeVersion = $buildProperties.Project.PropertyGroup.FluentCanvasDesktopRuntimeVersion
$minimumWindowsVersion = $buildProperties.Project.PropertyGroup.FluentCanvasMinimumWindowsVersion
$archive = [ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package))
function Read-PackageText([string]$name) {
    $reader = [IO.StreamReader]::new($archive.GetEntry($name).Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Get-WebViewContractTypes([string]$file, [switch]$Definitions) {
    $inputStream = $archive.GetEntry($file).Open()
    $memory = [IO.MemoryStream]::new()
    try {
        $inputStream.CopyTo($memory)
        $memory.Position = 0
        $reader = [PEReader]::new($memory)
        try {
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reader)
            $handles = if ($Definitions) { $metadata.TypeDefinitions } else { $metadata.TypeReferences }
            foreach ($handle in $handles) {
                $type = if ($Definitions) { $metadata.GetTypeDefinition($handle) } else { $metadata.GetTypeReference($handle) }
                if ($metadata.GetString($type.Namespace) -ceq 'Microsoft.Web.WebView2.Core') { $metadata.GetString($type.Name) }
            }
        } finally { $reader.Dispose() }
    } finally { $inputStream.Dispose(); $memory.Dispose() }
}
try {
    [xml]$xml = Read-PackageText 'AppxManifest.xml'
    $manifest = $xml.Package
    $identity = $manifest.Identity
    $app = $manifest.Applications.Application
    if ($identity.Name -ne $IdentityName -or $identity.Publisher -ne $Publisher -or $identity.ProcessorArchitecture -ne $Architecture) { throw 'Package identity or architecture mismatch.' }
    $families = $manifest.Dependencies.SelectNodes("*[local-name()='TargetDeviceFamily']")
    if ($families.Count -ne 1 -or $families[0].Name -ne 'Windows.Desktop' -or
        $families[0].MinVersion -ne $minimumWindowsVersion) {
        throw "Expected one Windows.Desktop device family with minimum version $minimumWindowsVersion."
    }
    if ($manifest.Dependencies.SelectSingleNode("*[local-name()='PackageDependency']")) {
        throw 'The self-contained MSIX declares an external framework dependency.'
    }
    $startup = $app.Extensions.Extension | Where-Object Category -eq 'windows.startupTask'
    if ($app.Executable -ne $startup.Executable -or $startup.StartupTask.TaskId -ne 'FluentCanvasStartup' -or $startup.StartupTask.Enabled -ne 'false') { throw 'Startup activation contract mismatch.' }
    $association = $app.Extensions.Extension | Where-Object Category -eq 'windows.fileTypeAssociation'
    if (Compare-Object @('.icart','.icstk') @($association.FileTypeAssociation.SupportedFileTypes.FileType)) { throw 'Document associations do not match.' }
    $required = @($app.Executable,'resources.pri','FluentCanvas.dll','FluentCanvas.CanvasCore.dll','Sentry.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','PresentationFramework.dll',
        'Microsoft.UI.Xaml.dll','Microsoft.UI.Xaml.winmd','Microsoft.Web.WebView2.Core.winmd','Microsoft.WindowsAppRuntime.dll','Microsoft.UI.Xaml.Controls.pri','LICENSE','docs/store/THIRD-PARTY.md','docs/store/PRIVACY.zh-CN.md')
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) { $null = $files.Add($entry.FullName.Replace('\','/')) }
    # Simplified Chinese is embedded in the main assembly. All other interface languages have satellites.
    foreach ($resource in $manifest.Resources.Resource) {
        $language = $resource.Language
        if ($language -eq 'zh-CN') { continue }
        if ($language -eq 'ha') { throw 'Use ha-Latn-NG in the package manifest for Windows resource registration.' }
        $culture = [Globalization.CultureInfo]::GetCultureInfo($language)
        $folder = if ($language -eq 'zh-TW') { 'zh-TW' } else { $culture.TwoLetterISOLanguageName }
        $required += "$folder/FluentCanvas.resources.dll"
    }
    foreach ($file in $required) {
        if (!$files.Contains($file.Replace('\','/'))) { throw "Missing $file in $Package" }
    }
    $references = @(Get-WebViewContractTypes 'Microsoft.UI.Xaml.winmd' | Sort-Object -Unique)
    $definitions = @(Get-WebViewContractTypes 'Microsoft.Web.WebView2.Core.winmd' -Definitions)
    $missing = @($references | Where-Object { $_ -cnotin $definitions })
    if (!$references.Count -or $missing.Count) { throw "Missing or unresolved WinUI WebView2 metadata references: $($missing -join ', ')." }
    Write-Host "Resolved all $($references.Count) WinUI WebView2 type references in the shipped contract."
    $config = Read-PackageText 'FluentCanvas.runtimeconfig.json' | ConvertFrom-Json
    foreach ($framework in 'Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') {
        $included = @($config.runtimeOptions.includedFrameworks | Where-Object name -eq $framework)
        if ($included.Count -ne 1 -or $included[0].version -ne $desktopRuntimeVersion) { throw "Incorrect $framework version or deployment mode. Expected $desktopRuntimeVersion." }
    }
    $deps = Read-PackageText 'FluentCanvas.deps.json' | ConvertFrom-Json -AsHashtable
    if (!$deps.runtimeTarget.name.EndsWith("/win-$Architecture")) { throw 'Dependency graph targets a different RID.' }
    foreach ($runtime in 'Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') {
        $runtimePack = "runtimepack.$runtime.Runtime.win-$Architecture/$desktopRuntimeVersion"
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
                    # Microsoft's x64 Windows App Runtime 2.7.0 includes this ARM64 workload resource helper.
                    if ($Architecture -eq 'x64' -and $entry.FullName -eq 'Microsoft.Windows.Workloads.Resources_ec.dll' -and $pe.CoffHeader.Machine -eq 'Arm64') {
                        $memory.Position = 0
                        if ((Get-FileHash -InputStream $memory).Hash -eq '29DFF0ACA7DD1C7B770C3BC31B350A21D8D8DC27E8E44788CE4120D283FB897B') { continue }
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
