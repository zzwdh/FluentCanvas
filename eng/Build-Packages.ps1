#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Local','Store')][string]$Mode = 'Local',
    [ValidateSet('x64','arm64')][string[]]$Architecture = @('x64','arm64'),
    [string]$StoreIdentity,
    [string]$TestCertificateThumbprint,
    [string]$DotnetPath = 'dotnet',
    [string]$WindowsSdkBin,
    [string]$OutputDirectory,
    [string[]]$RestoreSource,
    [switch]$WindowsAppSdkComponents,
    [switch]$LockedRestore
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'FluentCanvas/FluentCanvas.csproj'
$template = Join-Path $root 'FluentCanvas.Package/Package.appxmanifest'
$requiredSdk = (Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$desktopRuntimeVersion = $buildProperties.Project.PropertyGroup.FluentCanvasDesktopRuntimeVersion
$minimumWindowsVersion = $buildProperties.Project.PropertyGroup.FluentCanvasMinimumWindowsVersion
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts/packages/$Mode" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($Mode -eq 'Store') {
    if (!$StoreIdentity -or !(Test-Path -LiteralPath $StoreIdentity -PathType Leaf)) {
        throw 'Store builds require -StoreIdentity <JSON> with Partner Center Name, Publisher, PublisherDisplayName and DisplayName.'
    }
    $identity = Get-Content -LiteralPath $StoreIdentity -Raw | ConvertFrom-Json -AsHashtable
    foreach ($field in 'Name','Publisher','PublisherDisplayName','DisplayName') {
        if ([string]::IsNullOrWhiteSpace($identity[$field]) -or $identity[$field] -match '[<>]|FILL_IN|REPLACE_ME') {
            throw "Missing Partner Center identity field: $field"
        }
    }
    if ($identity.Name -in 'FluentCanvas.LocalTest','FluentCanvas' -and $identity.Publisher -in 'CN=FluentCanvas Local Test','CN=FluentCanvas') {
        throw 'The local or historical test identity cannot be submitted to the Store.'
    }
    if ($TestCertificateThumbprint) { throw 'Local signing is only available in Local mode.' }
}
if (!$WindowsSdkBin) {
    $sdkDir = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $WindowsSdkBin = Get-ChildItem -LiteralPath $sdkDir -Directory |
        Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' -and [version]$_.Name -ge [version]'10.0.26100.0' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName $hostArch } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'makeappx.exe') } | Select-Object -First 1
}
if (!$WindowsSdkBin) { throw 'Install Windows SDK 10.0.26100 or later, or specify -WindowsSdkBin.' }
function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE) { throw "$exe failed with exit code $LASTEXITCODE" }
}
function Clear-PackageIntermediates([string]$buildRoot, [string]$stage) {
    $buildDirectory = Get-Item -LiteralPath $buildRoot
    $stageDirectory = Get-Item -LiteralPath $stage
    if ($stageDirectory.Name -notmatch '^[0-9a-f]{32}$' -or
        $stageDirectory.Parent.FullName -ne $buildDirectory.FullName) {
        throw "Unexpected package staging directory: $stage"
    }
    foreach ($directory in $buildDirectory.Parent, $buildDirectory, $stageDirectory) {
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Skipping cleanup through a linked directory: $($directory.FullName)"
        }
    }
    foreach ($leaf in 'managed-x64','managed-arm64','x64','arm64','inner','logos','submission','results') {
        $path = Join-Path $stageDirectory.FullName $leaf
        if (!(Test-Path -LiteralPath $path -PathType Container)) { continue }
        $item = Get-Item -LiteralPath $path
        if ($item.Parent.FullName -ne $stageDirectory.FullName -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected package cleanup target: $path"
        }
        # Preserve diagnostic logs even when a build tool writes them into its intermediate tree.
        $logs = Get-ChildItem -LiteralPath $path -File -Recurse -Force |
            Where-Object Extension -in '.log','.binlog','.etl'
        foreach ($log in $logs) {
            $relative = [IO.Path]::GetRelativePath($stageDirectory.FullName, $log.FullName)
            $savedLog = Join-Path $stageDirectory.FullName 'logs' $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($savedLog)) | Out-Null
            Copy-Item -LiteralPath $log.FullName -Destination $savedLog -ErrorAction Stop
        }
        Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop
    }
}
$makeappx = Join-Path $WindowsSdkBin 'makeappx.exe'
$makepri = Join-Path $WindowsSdkBin 'makepri.exe'
$signtool = Join-Path $WindowsSdkBin 'signtool.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$buildRoot = Join-Path $root 'artifacts/build'
$stage = (New-Item -ItemType Directory (Join-Path $buildRoot ([guid]::NewGuid().ToString('N')))).FullName
$results = New-Item -ItemType Directory (Join-Path $stage 'results')
$innerPackages = New-Item -ItemType Directory (Join-Path $stage 'inner')
$architectures = @($Architecture | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
Push-Location $root
try {
    $selected = & $DotnetPath --version
    if ($LASTEXITCODE -or $selected -ne $requiredSdk) { throw "Install SDK $requiredSdk (found $selected)." }
    [xml]$manifest = Get-Content -LiteralPath $template -Raw
    [xml]$wap = Get-Content -LiteralPath (Join-Path $root 'FluentCanvas.Package/FluentCanvas.Package.wapproj') -Raw
    $wapMinimum = $wap.SelectSingleNode("//*[local-name()='TargetPlatformMinVersion']")
    if (!$wapMinimum -or $wapMinimum.InnerText -ne $minimumWindowsVersion -or
        $manifest.Package.Dependencies.TargetDeviceFamily.MinVersion -ne $minimumWindowsVersion) {
        throw "Both MSIX build inputs must declare the Windows minimum $minimumWindowsVersion."
    }
    $app = $manifest.Package.Applications.Application
    if ($Mode -eq 'Store') {
        $manifest.Package.Identity.Name = $identity.Name
        $manifest.Package.Identity.Publisher = $identity.Publisher
        $manifest.Package.Properties.PublisherDisplayName = $identity.PublisherDisplayName
        $manifest.Package.Properties.DisplayName = $identity.DisplayName
        $app.VisualElements.DisplayName = $identity.DisplayName
        ($app.Extensions.Extension | Where-Object Category -eq 'windows.startupTask').StartupTask.DisplayName = $identity.DisplayName
    }
    $version = [version]$manifest.Package.Identity.Version
    if ($version.Major -lt 1 -or $version.Revision -ne 0) { throw "Invalid Store package version $version" }
    $name = $manifest.Package.Identity.Name
    $priConfig = Join-Path $stage 'priconfig.xml'
    Run $makepri @('createconfig','/cf',$priConfig,'/dq','zh-CN','/o')
    [xml]$configXml = Get-Content -LiteralPath $priConfig -Raw
    # Keep all scale variants in this package's index rather than separate resource packs.
    $null = $configXml.resources.RemoveChild($configXml.resources.packaging)
    $configXml.Save($priConfig)
    $assetLayout = New-Item -ItemType Directory (Join-Path $stage 'logos')
    $assets = New-Item -ItemType Directory (Join-Path $assetLayout 'Assets')
    foreach ($content in $wap.SelectNodes("//*[local-name()='Content']")) {
        $include = $content.GetAttribute('Include')
        if ($include.StartsWith('Assets\')) {
            Copy-Item -LiteralPath (Join-Path "$root/FluentCanvas.Package" $include) -Destination $assets.FullName
        }
    }
    $assetPri = Join-Path $stage 'resources.pri'
    $sources = foreach ($source in $RestoreSource) { '--source'; $source }
    $symbolDir = New-Item -ItemType Directory (Join-Path $stage 'symbols')
    $rustSysroot = & rustc --print sysroot
    if ($LASTEXITCODE) { throw 'Could not locate the Rust standard library notices.' }
    foreach ($arch in $architectures) {
        $platform = if ($arch -eq 'arm64') { 'ARM64' } else { 'x64' }
        $rid = "win-$arch"
        $managedArtifacts = Join-Path $stage "managed-$arch"
        $layout = New-Item -ItemType Directory (Join-Path $stage $arch)
        $argsCommon = @($project,"-p:Platform=$platform",'--artifacts-path',$managedArtifacts,'-m:1','-nr:false')
        if ($WindowsAppSdkComponents) { $argsCommon += '-p:FluentCanvasUseWindowsAppSdkComponents=true' }
        $restoreOptions = if ($LockedRestore) { '--locked-mode' }
        Run $DotnetPath (@('restore') + $argsCommon + @($restoreOptions | Where-Object { $_ }) + @($sources | Where-Object { $_ }))
        Run $DotnetPath (@('publish') + $argsCommon + @('-r',$rid,'-c','Release','--no-restore','--self-contained','true',
            "-p:PublishProfile=$rid","-p:ManifestTool=$(Join-Path $WindowsSdkBin 'mt.exe')","-p:MakePriExeFullPath=$makepri",'-o',$layout.FullName))
        $embeddedManifest = Join-Path $stage "$arch-executable.manifest"
        Run (Join-Path $WindowsSdkBin 'mt.exe') @('-nologo',"-inputresource:$(Join-Path $layout 'FluentCanvas.exe');#1","-out:$embeddedManifest")
        [xml]$native = Get-Content -LiteralPath $embeddedManifest -Raw
        $dpi = $native.GetElementsByTagName('dpiAwareness','http://schemas.microsoft.com/SMI/2016/WindowsSettings')
        if ($dpi.Count -ne 1 -or $dpi.Item(0).InnerText -ne 'PerMonitorV2') { throw 'Executable manifest missing PerMonitorV2 DPI awareness.' }
        $supportedOS = $native.GetElementsByTagName('supportedOS','urn:schemas-microsoft-com:compatibility.v1')
        if ('{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}' -notin @($supportedOS | ForEach-Object { $_.Id })) {
            throw 'Executable manifest missing the Windows 10 compatibility declaration.'
        }
        $appVersion = [version](Get-Item -LiteralPath (Join-Path $layout 'FluentCanvas.exe')).VersionInfo.FileVersion
        if ($appVersion -ne $version) { throw "Executable $appVersion and package $version differ." }
        $manifest.Package.Identity.SetAttribute('ProcessorArchitecture', $arch)
        $manifest.Save((Join-Path $layout 'AppxManifest.xml'))
        Copy-Item -LiteralPath $assets.FullName -Destination $layout.FullName -Recurse
        if (Test-Path -LiteralPath (Join-Path $layout 'resources.pri')) {
            throw 'A published application resource index would collide with the package logo index.'
        }
        # WinUI needs its controls, themes and localized strings in the package's primary resource map.
        Copy-Item -LiteralPath (Join-Path $layout 'FluentCanvas.pri') -Destination $assetLayout.FullName -Force
        Run $makepri @('new','/pr',$assetLayout.FullName,'/cf',$priConfig,'/in',$name,'/of',$assetPri,'/o')
        $priDump = Join-Path $stage "$arch-resources.xml"
        Run $makepri @('dump','/if',$assetPri,'/of',$priDump,'/o')
        [xml]$index = Get-Content -Raw -LiteralPath $priDump
        $primary = $index.SelectSingleNode("//ResourceMap[@primary='true']")
        if ($primary.name -ne $name -or !$primary.SelectSingleNode("./ResourceMapSubtree[@name='Microsoft.UI.Xaml']") -or
            !$primary.SelectSingleNode("./ResourceMapSubtree[@name='Files']/ResourceMapSubtree[@name='Assets']")) {
            throw "Primary resources.pri is missing the WinUI resources or tile assets for $name."
        }
        Copy-Item -LiteralPath $assetPri -Destination $layout.FullName
        Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $layout 'LICENSE')
        $storeDocs = New-Item -ItemType Directory (Join-Path $layout 'docs/store')
        Copy-Item -LiteralPath "$root/docs/store/THIRD-PARTY.md","$root/docs/store/PRIVACY.zh-CN.md" -Destination $storeDocs.FullName
        $rustNotices = New-Item -ItemType Directory -Force (Join-Path $layout 'licenses/rust')
        Copy-Item -LiteralPath (Join-Path $rustSysroot 'share/doc/rust/COPYRIGHT-library.html') -Destination $rustNotices.FullName
        $resolved = Get-Content -LiteralPath (Join-Path $managedArtifacts 'obj/FluentCanvas/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        $packages = @($resolved.libraries.Values | Where-Object type -eq 'package' | ForEach-Object { $_.path })
        $packages += "microsoft.netcore.app.runtime.$rid/$desktopRuntimeVersion","microsoft.windowsdesktop.app.runtime.$rid/$desktopRuntimeVersion"
        foreach ($packagePath in ($packages | Sort-Object -Unique)) {
            $packageDir = $resolved.packageFolders.Keys | ForEach-Object { Join-Path $_ $packagePath } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if (!$packageDir) { continue }
            $notices = Get-ChildItem -LiteralPath $packageDir -File | Where-Object Name -match '^(LICEN[CS]E|THIRD.PARTY.NOTICES|ThirdPartyNotices|COPYING)(\..*)?$'
            if ($notices) {
                $noticeDir = New-Item -ItemType Directory -Force (Join-Path $layout "licenses/$packagePath")
                $notices | Copy-Item -Destination $noticeDir.FullName
            }
        }
        $symbols = New-Item -ItemType Directory (Join-Path $symbolDir $arch)
        $rustTarget = if ($arch -eq 'arm64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }
        Copy-Item -LiteralPath (Join-Path $root "artifacts/cargo/$rustTarget/release/fluent_canvas_core.pdb") -Destination $symbols.FullName
        foreach ($pdb in Get-ChildItem -LiteralPath $layout -Filter 'FluentCanvas*.pdb') {
            Move-Item -LiteralPath $pdb.FullName -Destination $symbols.FullName
        }
        $fileName = "${name}_${version}_${arch}.msix"
        $package = Join-Path $innerPackages $fileName
        Run $makeappx @('pack','/d',$layout.FullName,'/p',$package,'/o')
        & "$PSScriptRoot/Test-Package.ps1" -Package $package -Architecture $arch -IdentityName $name -Publisher $manifest.Package.Identity.Publisher
    }
    $bundleName = "${name}_${version}.msixbundle"
    $bundle = Join-Path $results $bundleName
    Run $makeappx @('bundle','/d',$innerPackages.FullName,'/p',$bundle,'/bv',$version.ToString(),'/o')
    if ($Mode -eq 'Store') {
        $uploadLayout = New-Item -ItemType Directory (Join-Path $stage 'submission')
        Copy-Item -LiteralPath $bundle -Destination $uploadLayout.FullName
        # Portable PDBs contain local source paths; keep them out of the Store upload.
        [IO.Compression.ZipFile]::CreateFromDirectory($uploadLayout.FullName,(Join-Path $results "${name}_${version}.msixupload"))
    } else {
        & "$PSScriptRoot/Sign-TestPackage.ps1" -Files @($bundle; (Get-ChildItem $innerPackages -Filter '*.msix').FullName) `
            -Subject $manifest.Package.Identity.Publisher -Thumbprint $TestCertificateThumbprint -SignTool $signtool -OutputCertificate (Join-Path $results 'FluentCanvas-LocalTest.cer')
    }
    Get-ChildItem -LiteralPath $innerPackages -Filter '*.msix' | Copy-Item -Destination $results.FullName
    Get-ChildItem -LiteralPath $results -File | ForEach-Object { (Get-FileHash $_.FullName).Hash + '  ' + $_.Name } |
        Set-Content -LiteralPath (Join-Path $results 'SHA256SUMS.txt')
    $destination = Join-Path $OutputDirectory ("$version-" + ($architectures -join '-') + '-' + (Split-Path $stage -Leaf).Substring(0,8))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($artifact in Get-ChildItem -LiteralPath $results -File) {
        Copy-Item -LiteralPath $artifact.FullName -Destination $destination -ErrorAction Stop
    }
    Write-Host "Packages: $destination"
    try {
        Clear-PackageIntermediates $buildRoot $stage
    } catch {
        Write-Warning "Package output is ready. Cleanup stopped for ${stage}: $_"
    }
} finally {
    Pop-Location
}
