#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [string]$OutputDirectory,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$token = [Guid]::NewGuid().ToString('N').Substring(0, 6)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts/winapp-regression/$stamp-$token" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
if ($OutputDirectory.StartsWith($PublishDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must not be inside PublishDirectory (recursive staging would copy itself).'
}
if (!(Test-Path -LiteralPath (Join-Path $PublishDirectory 'FluentCanvas.exe') -PathType Leaf)) {
    throw 'PublishDirectory must contain the published FluentCanvas.exe and its dependencies.'
}
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "OutputDirectory must be new so that an existing session cannot be overwritten: $OutputDirectory"
}
if (Get-Process -Name FluentCanvas -ErrorAction SilentlyContinue) {
    throw 'Close existing FluentCanvas instances before running the isolated UI regression.'
}
$winapp = (Get-Command winapp -ErrorAction Stop).Source
$identityName = 'FluentCanvas.WinAppTest.' + (Get-Date -Format 'yyyyMMddHHmmss') + $token
$inputDirectory = Join-Path $OutputDirectory 'input'
$runtimeRoot = Join-Path ([IO.Path]::GetTempPath()) "FluentCanvas-WinApp-$stamp-$token"
if (Test-Path -LiteralPath $runtimeRoot) { throw 'Runtime staging directory already exists.' }
$layoutDirectory = Join-Path $runtimeRoot 'layout'
$commandsDirectory = Join-Path $OutputDirectory 'commands'
$documentsDirectory = Join-Path $OutputDirectory 'documents'
$screenshotsDirectory = Join-Path $OutputDirectory 'screenshots'
$sessionFile = Join-Path $OutputDirectory 'session.json'
$script:commandNumber = 0
$script:packageFullName = $null
$script:localState = $null
$script:prepared = $false
$previousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$workflowId = "FluentCanvas-winapp-$stamp-$token"
$script:session = [ordered]@{
    SchemaVersion = 1
    Started = (Get-Date).ToUniversalTime().ToString('o')
    State = 'Preparing'
    OutputDirectory = $OutputDirectory
    PublishDirectory = $PublishDirectory
    IdentityName = $identityName
    PackageFullName = $null
    PackageFamilyName = $null
    LocalState = $null
    InputDirectory = $inputDirectory
    LayoutDirectory = $layoutDirectory
    RuntimeRoot = $runtimeRoot
    DocumentsDirectory = $documentsDirectory
    ProcessIds = @()
    WinAppVersion = $null
    WorkflowId = $workflowId
    Error = $null
}
New-Item -ItemType Directory -Path $inputDirectory, $commandsDirectory, $documentsDirectory, $screenshotsDirectory -Force | Out-Null

function Save-RegressionSession {
    $script:session | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $sessionFile -Encoding utf8
}

function Test-OwnedPath([string]$Path) {
    if (!$Path) { return $false }
    $full = [IO.Path]::GetFullPath($Path)
    $full.StartsWith($OutputDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
    $full.StartsWith($runtimeRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-WinApp {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 60,
        [switch]$AllowFailure
    )
    $script:commandNumber++
    $prefix = Join-Path $commandsDirectory ('{0:D3}' -f $script:commandNumber)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $winapp
    $start.WorkingDirectory = $OutputDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $metadata = [ordered]@{ Arguments = $Arguments; Started = (Get-Date).ToUniversalTime().ToString('o'); TimeoutSeconds = $TimeoutSeconds; ExitCode = $null; TimedOut = $false }
    $stdout = ''; $stderr = ''
    try {
        if (!$process.Start()) { throw 'Could not start winapp.' }
        $readOut = $process.StandardOutput.ReadToEndAsync()
        $readError = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $metadata.TimedOut = $true
            # Only the process this invocation created is terminated; application cleanup is separately scoped.
            $process.Kill()
            $process.WaitForExit()
        }
        $stdout = $readOut.GetAwaiter().GetResult()
        $stderr = $readError.GetAwaiter().GetResult()
        $metadata.ExitCode = $process.ExitCode
    }
    finally {
        $metadata['Finished'] = (Get-Date).ToUniversalTime().ToString('o')
        $stdout | Set-Content -LiteralPath "$prefix.stdout.txt" -Encoding utf8
        $stderr | Set-Content -LiteralPath "$prefix.stderr.txt" -Encoding utf8
        $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$prefix.command.json" -Encoding utf8
        $process.Dispose()
    }
    if (!$AllowFailure -and ($metadata.TimedOut -or $metadata.ExitCode -ne 0)) {
        throw "winapp failed (exit $($metadata.ExitCode), timeout $($metadata.TimedOut)); see $prefix.stderr.txt. $stderr $stdout"
    }
    [pscustomobject]@{ ExitCode = $metadata.ExitCode; TimedOut = $metadata.TimedOut; Stdout = $stdout; Stderr = $stderr; EvidencePrefix = $prefix }
}

function Get-OwnedProcesses {
    @(Get-Process -Name FluentCanvas -ErrorAction SilentlyContinue | Where-Object {
        try { Test-OwnedPath $_.Path } catch { $false }
    })
}

function Copy-RegressionState {
    if ($script:localState -and (Test-Path -LiteralPath $script:localState)) {
        $destination = Join-Path $OutputDirectory 'local-state'
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($name in 'Settings.json', 'Settings.json.bak', 'Log.txt', 'error-reporting.disabled') {
            $source = Join-Path $script:localState $name
            if (Test-Path -LiteralPath $source -PathType Leaf) {
                # App data on a secondary Windows app volume can be EFS-encrypted.
                # Export our test evidence as bytes rather than copying its encryption attribute.
                [IO.File]::WriteAllBytes((Join-Path $destination $name), [IO.File]::ReadAllBytes($source))
            }
        }
    }
}

function Remove-RegressionSession {
    # The staged identity and executable path are both checked before touching any process/package.
    $exportErrors = [Collections.Generic.List[string]]::new()
    try { Copy-RegressionState } catch { $exportErrors.Add($_.Exception.Message) }
    foreach ($process in Get-OwnedProcesses) {
        $null = $process.CloseMainWindow()
        if (!$process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    try { Copy-RegressionState } catch { $exportErrors.Add($_.Exception.Message) }
    # Registration may have succeeded before the CLI or identity lookup failed.
    # Recover only this run's exact names AND installation path; never use --force/prune.
    foreach ($package in @(Get-AppxPackage -Name "$identityName*" -ErrorAction Stop)) {
        if ($package.Name -notin @($identityName, "$identityName.debug")) { continue }
        if (!(Test-OwnedPath $package.InstallLocation)) { throw 'Refusing cleanup: package installation is outside this run.' }
        Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
    }
    if (Get-OwnedProcesses) { throw 'Owned test processes remain after cleanup.' }
    $remaining = @(Get-AppxPackage -Name "$identityName*" -ErrorAction Stop | Where-Object {
        $_.Name -in @($identityName, "$identityName.debug")
    })
    if ($remaining.Count) { throw 'The exact test package remains registered after cleanup.' }
    if (Test-Path -LiteralPath $runtimeRoot) {
        $resolvedRuntime = (Resolve-Path -LiteralPath $runtimeRoot).Path
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (!$resolvedRuntime.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFullPath($runtimeRoot) -ne $resolvedRuntime -or
            (Split-Path -Leaf $resolvedRuntime) -ne "FluentCanvas-WinApp-$stamp-$token" -or
            (Get-Item -LiteralPath $resolvedRuntime).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing cleanup: runtime directory did not match this run under the system temporary directory.'
        }
        Remove-Item -LiteralPath $resolvedRuntime -Recurse -Force
    }
    if ($exportErrors.Count) {
        $script:session['StateExportErrors'] = @($exportErrors.ToArray())
        throw ('Test instance removed, but state export failed: ' + ($exportErrors -join '; '))
    }
}

function Invoke-RegressionFlow {
    . (Join-Path $PSScriptRoot 'Invoke-WinAppRegressionFlow.ps1')
}

try {
    $env:WINAPP_UI_WORKFLOW_ID = $workflowId
    Save-RegressionSession
    $version = Invoke-WinApp -Arguments @('--version')
    $script:session.WinAppVersion = $version.Stdout.Trim()
    foreach ($item in Get-ChildItem -LiteralPath $PublishDirectory -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $inputDirectory -Recurse -Force
    }
    $assetTarget = Join-Path $inputDirectory 'Assets'
    New-Item -ItemType Directory -Path $assetTarget -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath (Join-Path $root 'FluentCanvas.Package/Assets') -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $assetTarget -Recurse -Force
    }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'FluentCanvas.Package/Package.appxmanifest') -Raw
    $manifest.Package.Identity.SetAttribute('Name', $identityName)
    $manifest.Package.Identity.SetAttribute('ProcessorArchitecture', 'x64')
    foreach ($extensions in @($manifest.SelectNodes("//*[local-name()='Extensions']"))) {
        $null = $extensions.ParentNode.RemoveChild($extensions)
    }
    $manifestPath = Join-Path $inputDirectory 'AppxManifest.xml'
    $manifest.Save($manifestPath)
    Invoke-WinApp -Arguments @('run', $inputDirectory, '--no-launch', '--output-appx-directory', $layoutDirectory,
        '--manifest', $manifestPath, '--exe', 'FluentCanvas.exe', '--json') -TimeoutSeconds 180 | Out-Null
    [xml]$registeredManifest = Get-Content -LiteralPath (Join-Path $layoutDirectory 'AppxManifest.xml') -Raw
    $registeredName = [string]$registeredManifest.Package.Identity.Name
    if ($registeredName -notin @($identityName, "$identityName.debug")) { throw 'Unexpected staged package identity.' }
    $package = Get-AppxPackage -Name $registeredName -ErrorAction Stop
    if (!$package -or @($package).Count -ne 1) { throw "Expected exactly one registered package named $registeredName." }
    if (!(Test-OwnedPath $package.InstallLocation)) { throw 'Refusing to use a package installation outside this run.' }
    $script:packageFullName = $package.PackageFullName
    $script:localState = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Get-WinAppLocalState.ps1') -PackageFamilyName $package.PackageFamilyName | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or ![IO.Path]::IsPathFullyQualified($script:localState)) { throw 'Windows could not initialize the isolated package data store.' }
    $script:session.PackageFullName = $script:packageFullName
    $script:session.PackageFamilyName = $package.PackageFamilyName
    $script:session.LocalState = $script:localState
    Save-RegressionSession
    New-Item -ItemType Directory -Path $script:localState -Force | Out-Null
    $settings = [ordered]@{
        appearance = @{ language = 'zh-CN'; isEnableDisPlayFloatBarText = $true }
        canvas = @{ usingWhiteboard = $true; inkWidth = 4; inkStyle = 0 }
        automation = @{ autoSavedStrokesLocation = $documentsDirectory; autoDelSavedFiles = $false; isAutoSaveStrokesAtClear = $false }
        behavior = @{ powerPointSupport = $false; enableWpsSlideShow = $false }
        inkToShape = @{ isInkToShapeEnabled = $false }
        startup = @{ isFoldAtStartup = $false; isAutoUpdate = $false }
        advanced = @{ isSecondConfimeWhenShutdownApp = $false; isEnableEdgeGestureUtil = $false }
        globalHotkeys = @{ EndSlideShow = ''; ClearCanvas = ''; Capture = ''; ToggleToolbar = ''; Draw = ''; Pointer = ''; Board = '' }
    }
    $settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $script:localState 'Settings.json') -Encoding utf8
    foreach ($name in 'Names.txt', 'Replace.txt', 'error-reporting.disabled') {
        [IO.File]::WriteAllText((Join-Path $script:localState $name), '')
    }
    Invoke-WinApp -Arguments @('run', $inputDirectory, '--detach', '--devtools', 'off',
        '--output-appx-directory', $layoutDirectory, '--manifest', $manifestPath, '--exe', 'FluentCanvas.exe', '--json') -TimeoutSeconds 180 | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $owned = @(Get-OwnedProcesses)
        if ($owned.Count) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    if (!$owned.Count) { throw 'No owned FluentCanvas process appeared after launch.' }
    $script:session.ProcessIds = @($owned | ForEach-Object Id)
    $logPath = Join-Path $script:localState 'Log.txt'
    $deadline = (Get-Date).AddSeconds(45)
    do {
        $loaded = (Test-Path -LiteralPath $logPath -PathType Leaf) -and
            (Select-String -LiteralPath $logPath -SimpleMatch 'FluentCanvas Loaded' -Quiet)
        if ($loaded) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    if (!$loaded) { throw 'Application did not finish initialization using its isolated data store.' }
    $script:session.State = 'Prepared'
    $script:prepared = $true
    Save-RegressionSession
    if ($PrepareOnly) {
        Write-Output "Prepared isolated session: $sessionFile"
        return
    }
    Invoke-RegressionFlow
    $script:session.State = 'Passed'
}
catch {
    $script:session.State = 'Failed'
    $script:session.Error = $_.Exception.Message
    Save-RegressionSession
    throw
}
finally {
    $cleanupFailure = $null
    if (!($PrepareOnly -and $script:prepared)) {
        try { Remove-RegressionSession; $script:session['CleanedUp'] = $true }
        catch {
            $cleanupFailure = $_.Exception.Message
            $script:session['CleanupError'] = $cleanupFailure
            $script:session.State = 'Failed'
            Write-Warning $cleanupFailure
        }
    }
    try {
        $yieldResult = Invoke-WinApp -Arguments @('ui', 'yield', '--json') -AllowFailure
        if ($yieldResult.ExitCode -ne 0 -or $yieldResult.TimedOut) { throw 'winapp could not release the desktop workflow.' }
    }
    catch {
        $script:session['WorkflowReleaseError'] = $_.Exception.Message
        $script:session.State = 'Failed'
        if (!$cleanupFailure) { $cleanupFailure = $_.Exception.Message }
    }
    finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflowId }
    $script:session['Finished'] = (Get-Date).ToUniversalTime().ToString('o')
    Save-RegressionSession
    if ($cleanupFailure) { throw "Regression cleanup failed: $cleanupFailure" }
}
