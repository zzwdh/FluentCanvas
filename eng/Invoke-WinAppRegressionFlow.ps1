# Dot-sourced by Test-WinAppRegression.ps1 after its isolated instance is ready.
# All UI input is performed through winapp; saved ink is independently decoded by WPF.
$script:flowChecks = [Collections.Generic.List[object]]::new()
$script:flowWindow = $null
$script:flowRecording = $null
$script:flowResult = [ordered]@{
    schemaVersion = 1
    started = (Get-Date).ToUniversalTime().ToString('o')
    status = 'Running'
    identityName = $script:session.IdentityName
    window = $null
    checks = @()
    documents = [ordered]@{}
    recordings = @()
    limitations = @('Synthetic input on this x64 desktop; not physical pen or ARM64 acceptance.',
        'Ink geometry checks cover points, pressure and selected drawing attributes; not embedded media.')
}
$inkEvidenceDirectory = Join-Path $OutputDirectory 'ink-evidence'
New-Item -ItemType Directory -Path $inkEvidenceDirectory -Force | Out-Null

function Write-FlowResult {
    $script:flowResult.checks = @($script:flowChecks.ToArray())
    $script:flowResult | ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding utf8
}

function Add-FlowCheck([string]$Name, [bool]$Passed, [string]$Detail, [switch]$Required) {
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    $script:flowChecks.Add([ordered]@{ name = $Name; status = $status; detail = $Detail; time = (Get-Date).ToUniversalTime().ToString('o') })
    Add-Content -LiteralPath (Join-Path $OutputDirectory 'progress.txt') -Value "$(Get-Date -Format o) [$status] $Name $Detail" -Encoding utf8
    Write-Host "[$status] $Name $Detail"
    Write-FlowResult
    if (!$Passed -and $Required) { throw "$Name : $Detail" }
}

function Wait-FlowCondition([scriptblock]$Condition, [string]$Description, [int]$Seconds = 20) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "Timed out waiting for $Description."
}

function Read-FlowUi([string]$Window = $script:flowWindow, [switch]$RootOnly) {
    $arguments = @('ui', 'inspect', '-w', $Window, '--hide-offscreen', '--json')
    if ($RootOnly) { $arguments += @('--depth', '1') }
    else { $arguments += '--interactive' }
    $response = Invoke-WinApp -Arguments $arguments
    $data = $response.Stdout | ConvertFrom-Json -AsHashtable
    $windows = @($data['windows'])
    $target = @($windows | Where-Object { [string]$_['hwnd'] -eq $Window })
    if (!$target.Count -and $windows.Count -eq 1) { $target = $windows }
    if ($target.Count -ne 1) { throw "Expected one UI tree for window $Window." }
    $target[0]
}

function Find-FlowElement {
    param([string]$Id, [string]$Name, [switch]$Leftmost, [string]$Window = $script:flowWindow)
    $tree = Read-FlowUi -Window $Window
    $found = @($tree['elements'] | Where-Object {
        $visible = !($_.ContainsKey('isOffscreen') -and $_['isOffscreen']) -and
            (![bool]$_.ContainsKey('isEnabled') -or [bool]$_['isEnabled']) -and
            [double]$_['width'] -gt 0 -and [double]$_['height'] -gt 0
        $match = if ($Id) { $_.ContainsKey('automationId') -and $_['automationId'] -eq $Id } else { $_['name'] -eq $Name }
        $visible -and $match
    })
    if ($Leftmost -and $found.Count) { return ($found | Sort-Object { [double]$_['x'] } | Select-Object -First 1) }
    if ($found.Count -ne 1) { throw "Expected exactly one visible enabled element (id='$Id', name='$Name'); found $($found.Count)." }
    $found[0]
}

function Invoke-FlowControl {
    param([string]$Id, [string]$Name, [switch]$Leftmost, [string]$Window = $script:flowWindow)
    $element = Find-FlowElement -Id $Id -Name $Name -Leftmost:$Leftmost -Window $Window
    Invoke-WinApp -Arguments @('ui', 'invoke', [string]$element['selector'], '-w', $Window, '--json') | Out-Null
    Start-Sleep -Milliseconds 300
    Read-FlowUi -Window $Window | Out-Null
}

function Take-FlowScreenshot([string]$Name) {
    $path = Join-Path $screenshotsDirectory "$Name.png"
    $capture = Invoke-WinApp -Arguments @('ui', 'screenshot', '-w', $script:flowWindow, '-o', $path, '--json') -AllowFailure
    $valid = $capture.ExitCode -eq 0 -and (Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -gt 0
    Add-FlowCheck "Screenshot $Name" $valid $path
}

function Start-FlowRecording([string]$Name) {
    if ($script:flowRecording) { throw 'A regression recording is already active.' }
    $script:commandNumber++
    $prefix = Join-Path $commandsDirectory ('{0:D3}' -f $script:commandNumber)
    $path = Join-Path $OutputDirectory "$Name.mp4"
    $arguments = @('ui', 'record', '-w', $script:flowWindow, '--duration-sec', '0', '--fps', '10', '--max-edge', '1920', '-o', $path, '--json')
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $winapp
    $start.WorkingDirectory = $OutputDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    if ($env:WINAPP_UI_WORKFLOW_ID) { $start.Environment['WINAPP_UI_WORKFLOW_ID'] = $env:WINAPP_UI_WORKFLOW_ID }
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (!$process.Start()) { $process.Dispose(); throw 'Could not start the winapp recording.' }
    $script:flowRecording = @{
        Process = $process; Output = $process.StandardOutput.ReadToEndAsync(); Error = $process.StandardError.ReadToEndAsync()
        Prefix = $prefix; Path = $path; Arguments = $arguments; WorkflowId = $env:WINAPP_UI_WORKFLOW_ID; Started = (Get-Date).ToUniversalTime().ToString('o')
    }
    Start-Sleep -Milliseconds 500
    if ($process.HasExited) { Stop-FlowRecording; throw 'The recording exited before UI tests started.' }
}

function Stop-FlowRecording {
    if (!$script:flowRecording) { return }
    $recording = $script:flowRecording
    $process = $recording.Process
    $timedOut = $false
    try {
        if (!$process.HasExited) { $process.StandardInput.WriteLine(); $process.StandardInput.Flush(); $process.StandardInput.Close() }
        if (!$process.WaitForExit(30000)) { $timedOut = $true; $process.Kill(); $process.WaitForExit() }
        $recording.Output.GetAwaiter().GetResult() | Set-Content -LiteralPath ($recording.Prefix + '.stdout.txt') -Encoding utf8
        $recording.Error.GetAwaiter().GetResult() | Set-Content -LiteralPath ($recording.Prefix + '.stderr.txt') -Encoding utf8
        [ordered]@{ Arguments = $recording.Arguments; WorkflowId = $recording.WorkflowId; Started = $recording.Started; Finished = (Get-Date).ToUniversalTime().ToString('o'); ExitCode = $process.ExitCode; TimedOut = $timedOut } |
            ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($recording.Prefix + '.command.json') -Encoding utf8
        $valid = !$timedOut -and $process.ExitCode -eq 0 -and (Test-Path -LiteralPath $recording.Path) -and (Get-Item -LiteralPath $recording.Path).Length -gt 1024
        $script:flowResult.recordings += $recording.Path
        Add-FlowCheck 'UI recording finalized' $valid $recording.Path
    }
    finally { $process.Dispose(); $script:flowRecording = $null }
}

function Read-FlowInk([string]$Path, [string]$Name) {
    $evidencePath = Join-Path $inkEvidenceDirectory "$Name.json"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'eng/Get-WinAppInkEvidence.ps1'), '-Path', $Path, '-OutputPath', $evidencePath)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (!$process.Start()) { throw 'Could not start the independent WPF ink reader.' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit(); throw 'Ink decoding timed out.' }
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $inkEvidenceDirectory "$Name.stdout.txt") -Encoding utf8
        $errorText = $stderr.GetAwaiter().GetResult()
        $errorText | Set-Content -LiteralPath (Join-Path $inkEvidenceDirectory "$Name.stderr.txt") -Encoding utf8
        if ($process.ExitCode -ne 0) { throw "Ink decoding failed: $errorText" }
        Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json -AsHashtable
    }
    finally { $process.Dispose() }
}

function Save-FlowDocument([string]$Name) {
    $before = @(Get-ChildItem -LiteralPath $documentsDirectory -Filter '*.icart' -File -Recurse | ForEach-Object FullName)
    Invoke-FlowControl -Id 'BoardToolbarMoreButton'
    Invoke-FlowControl -Name '保存作品'
    $saved = Wait-FlowCondition {
        Get-ChildItem -LiteralPath $documentsDirectory -Filter '*.icart' -File -Recurse |
            Where-Object { $_.FullName -notin $before -and $_.Directory.Name -like 'User Saved -*' } |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    } "new saved document ($Name)"
    $script:flowResult.documents[$Name] = $saved.FullName
    Write-FlowResult
    Read-FlowInk -Path $saved.FullName -Name $Name
}

function Assert-FlowInkCount($Evidence, [int]$Expected, [string]$Name) {
    Add-FlowCheck $Name ([int]$Evidence['count'] -eq $Expected) "Expected $Expected strokes; observed $($Evidence['count'])." -Required
}

function Assert-FlowInkEqual($Expected, $Actual, [string]$Name) {
    $same = $Expected['geometrySha256'] -eq $Actual['geometrySha256'] -and $Expected['count'] -eq $Actual['count']
    Add-FlowCheck $Name $same "Expected $($Expected['geometrySha256']); observed $($Actual['geometrySha256'])." -Required
}

function Assert-FlowUndo($Expected, $Actual, [string]$Name) {
    $expectedHashes = @($Expected['strokes'] | ForEach-Object { $_['geometrySha256'] } | Sort-Object)
    $actualHashes = @($Actual['strokes'] | ForEach-Object { $_['geometrySha256'] } | Sort-Object)
    $sameStrokes = $Expected['count'] -eq $Actual['count'] -and
        ($expectedHashes -join ',') -eq ($actualHashes -join ',')
    Add-FlowCheck "$Name restored every stroke" $sameStrokes 'Compared the complete multiset of per-stroke geometry, pressure and attributes.' -Required
    # Order may affect overlapping ink. Keep this failure visible, but continue the
    # independent page/save checks using the actual restored state as their baseline.
    Add-FlowCheck "$Name preserved stroke order" ($Expected['geometrySha256'] -eq $Actual['geometrySha256']) `
        "Expected $($Expected['geometrySha256']); observed $($Actual['geometrySha256'])."
}

function Send-FlowPen([string]$Path, [double]$Pressure) {
    Invoke-WinApp -Arguments @('ui', 'focus', '-w', $script:flowWindow, '--json') | Out-Null
    Invoke-WinApp -Arguments @('ui', 'pen', '-w', $script:flowWindow, '--path', $Path, '--pressure',
        $Pressure.ToString([Globalization.CultureInfo]::InvariantCulture), '--duration-ms', '900', '--json') | Out-Null
    Start-Sleep -Milliseconds 300
    Read-FlowUi | Out-Null
}

function Open-FlowDocument([string]$Path) {
    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $documentRoot = [IO.Path]::GetFullPath($documentsDirectory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedPath.StartsWith($documentRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetExtension($resolvedPath) -ne '.icart') {
        throw 'Only an .icart document saved inside this regression session may be reopened.'
    }
    $logPath = Join-Path $script:localState 'Log.txt'
    $openedMessage = 'Opened .icart:'
    $before = @(Select-String -LiteralPath $logPath -SimpleMatch $openedMessage).Count

    function Get-FlowOpenDialogs {
        $dialogs = @{}
        foreach ($ownedId in $script:session.ProcessIds) {
            # Process-scoped enumeration plus an exact owner HWND prevents selecting
            # another application's identically titled common file dialog.
            $response = Invoke-WinApp -Arguments @('ui', 'list-windows', '-a', [string]$ownedId, '--json')
            foreach ($window in @($response.Stdout | ConvertFrom-Json -AsHashtable)) {
                if ($window['title'] -ne '打开' -or $window['className'] -ne '#32770' -or
                    [string]$window['ownerHwnd'] -ne $script:flowWindow) { continue }
                if ($window.ContainsKey('processId') -and [int]$window['processId'] -notin $script:session.ProcessIds) { continue }
                $dialogs[[string]$window['hwnd']] = $window
            }
        }
        @($dialogs.Values)
    }

    Invoke-FlowControl -Id 'BoardToolbarMoreButton'
    $openCommand = Find-FlowElement -Name '打开作品'
    Invoke-WinApp -Arguments @('ui', 'invoke', [string]$openCommand['selector'], '-w', $script:flowWindow, '--json') | Out-Null
    # Inspect the newly owned dialog instead of the modal-blocked main window.
    Start-Sleep -Milliseconds 300
    $dialog = Wait-FlowCondition {
        $matches = @(Get-FlowOpenDialogs)
        if ($matches.Count -gt 1) { throw 'More than one owned Open dialog appeared.' }
        if ($matches.Count -eq 1) { $matches[0] }
    } 'the owned Open file dialog'
    $dialogWindow = [string]$dialog['hwnd']
    $tree = Read-FlowUi -Window $dialogWindow
    # The native file picker commits the child Edit's value.
    $fileNames = @($tree['elements'] | Where-Object {
        $_['type'] -eq 'ComboBox' -and $_['automationId'] -eq '1148' -and
        $_['isEditable'] -and $_['isEnabled'] -and !$_['isOffscreen']
    })
    if ($fileNames.Count -ne 1) { throw 'Expected one visible editable filename ComboBox in the owned Open dialog.' }
    $edits = @($fileNames[0]['children'] | Where-Object { $_['type'] -eq 'Edit' -and $_['isEditable'] })
    if ($edits.Count -ne 1) { throw 'Could not identify the file name edit control.' }
    $editSelector = [string]$edits[0]['selector']
    Invoke-WinApp -Arguments @('ui', 'set-value', $editSelector, $resolvedPath,
        '-w', $dialogWindow, '--json') | Out-Null
    $valueResponse = Invoke-WinApp -Arguments @('ui', 'get-property', $editSelector, '-w', $dialogWindow, '--property', 'Value', '--json')
    $actualValue = ($valueResponse.Stdout | ConvertFrom-Json -AsHashtable)['properties']['Value']
    if ($actualValue -ne $resolvedPath) { throw 'The file name edit did not receive the expected path.' }

    if (@(Get-FlowOpenDialogs | Where-Object { [string]$_['hwnd'] -eq $dialogWindow }).Count -ne 1) {
        throw 'The owned Open dialog changed before confirming the filename.'
    }
    Invoke-WinApp -Arguments @('ui', 'send-keys', 'enter', '--target', $editSelector, '-w', $dialogWindow, '--via', 'post-message', '--json') | Out-Null
    Wait-FlowCondition { @(Get-FlowOpenDialogs | Where-Object { [string]$_['hwnd'] -eq $dialogWindow }).Count -eq 0 } 'Open dialog dismissal' | Out-Null
    Wait-FlowCondition { @(Select-String -LiteralPath $logPath -SimpleMatch $openedMessage).Count -gt $before } 'a new successful .icart load log entry' 30 | Out-Null
    Start-Sleep -Milliseconds 300
    Read-FlowUi | Out-Null
    Add-FlowCheck 'Saved document opened through file picker' $true $resolvedPath
}

try {
    Write-FlowResult
    $logPath = Join-Path $script:localState 'Log.txt'
    Wait-FlowCondition { Test-Path -LiteralPath $logPath -PathType Leaf } 'application log' 30 | Out-Null
    Wait-FlowCondition { Select-String -LiteralPath $logPath -SimpleMatch 'FluentCanvas Loaded' -Quiet } 'FluentCanvas Loaded' 45 | Out-Null
    $processId = [string]$script:session.ProcessIds[0]
    $windowsResponse = Invoke-WinApp -Arguments @('ui', 'list-windows', '-a', $processId, '--json')
    $windows = @($windowsResponse.Stdout | ConvertFrom-Json -AsHashtable)
    $mainWindows = @($windows | Where-Object { $_['title'] -eq 'FluentCanvas 画板' -and $_['width'] -gt 0 -and $_['height'] -gt 0 })
    if ($mainWindows.Count -ne 1) { throw "Expected one visible test whiteboard window; found $($mainWindows.Count)." }
    $script:flowWindow = [string]$mainWindows[0]['hwnd']
    $script:flowResult.window = $script:flowWindow
    $rootTree = Read-FlowUi -RootOnly
    $rootElement = @($rootTree['elements'] | Where-Object { $_['type'] -eq 'Window' })[0]
    $left = [double]$rootElement['x']; $top = [double]$rootElement['y']
    $width = [double]$rootElement['width']; $height = [double]$rootElement['height']
    if ($width -lt 640 -or $height -lt 480) { throw 'The test window is too small for the bounded drawing area.' }
    function Point-Flow([double]$X, [double]$Y) { '{0},{1}' -f [int]($left + $width * $X), [int]($top + $height * $Y) }
    $firstPath = @((Point-Flow .30 .32), (Point-Flow .36 .32), (Point-Flow .42 .32), (Point-Flow .48 .32)) -join ' '
    $secondPath = @((Point-Flow .30 .46), (Point-Flow .36 .53), (Point-Flow .42 .46), (Point-Flow .48 .53)) -join ' '
    $erasePath = @((Point-Flow .39 .29), (Point-Flow .39 .35)) -join ' '
    $pageTwoPath = @((Point-Flow .36 .38), (Point-Flow .44 .45), (Point-Flow .52 .38)) -join ' '

    Start-FlowRecording 'regression'
    Invoke-FlowControl -Name '切换白板与桌面批注' -Leftmost
    Invoke-FlowControl -Id 'BoardToolbarPenButton'
    Send-FlowPen -Path $firstPath -Pressure .2
    Send-FlowPen -Path $secondPath -Pressure .8
    $pageOne = Save-FlowDocument '01-page1-written'
    Assert-FlowInkCount $pageOne 2 'Two synthetic pen strokes persisted'
    $pressureOne = [double]$pageOne['strokes'][0]['pressure']['mean']
    $pressureTwo = [double]$pageOne['strokes'][1]['pressure']['mean']
    Add-FlowCheck 'Low-pressure data persisted' ([Math]::Abs($pressureOne - .2) -le .08) "Injected 0.2; saved mean $pressureOne (tolerance 0.08)."
    Add-FlowCheck 'High-pressure data persisted' ([Math]::Abs($pressureTwo - .8) -le .08) "Injected 0.8; saved mean $pressureTwo (tolerance 0.08)."
    Take-FlowScreenshot '01-written'

    Invoke-FlowControl -Id 'BoardToolbarEraseStrokesButton'
    Send-FlowPen -Path $erasePath -Pressure .5
    $erased = Save-FlowDocument '02-erased'
    Assert-FlowInkCount $erased 1 'Whole-stroke eraser removed one stroke'
    Add-FlowCheck 'Eraser retained the untouched second stroke' `
        ($erased['strokes'][0]['geometrySha256'] -eq $pageOne['strokes'][1]['geometrySha256']) `
        'Remaining ink must equal original stroke 2.' -Required
    Take-FlowScreenshot '02-erased'
    Invoke-FlowControl -Name '撤销'
    $undone = Save-FlowDocument '03-undo'
    Assert-FlowUndo $pageOne $undone 'Undo'
    Take-FlowScreenshot '03-undo'
    Invoke-FlowControl -Name '重做'
    $redone = Save-FlowDocument '04-redo'
    Assert-FlowInkEqual $erased $redone 'Redo restored erased state'
    Invoke-FlowControl -Name '撤销'
    $restored = Save-FlowDocument '05-undo-again'
    Assert-FlowUndo $pageOne $restored 'Second undo'

    Invoke-FlowControl -Name '下一页' -Leftmost
    $blank = Save-FlowDocument '06-page2-blank'
    Assert-FlowInkCount $blank 0 'New page is blank'
    Invoke-FlowControl -Id 'BoardToolbarPenButton'
    Send-FlowPen -Path $pageTwoPath -Pressure .6
    $pageTwo = Save-FlowDocument '07-page2-written'
    Assert-FlowInkCount $pageTwo 1 'Second page has its own stroke'
    Take-FlowScreenshot '04-page2'
    Invoke-FlowControl -Name '上一页' -Leftmost
    $returnedOne = Save-FlowDocument '08-page1-returned'
    Assert-FlowInkEqual $restored $returnedOne 'Previous page preserved page-one ink'
    Take-FlowScreenshot '05-page1-returned'
    Invoke-FlowControl -Name '下一页' -Leftmost
    $returnedTwo = Save-FlowDocument '09-page2-returned'
    Assert-FlowInkEqual $pageTwo $returnedTwo 'Next page preserved page-two ink'

    Open-FlowDocument -Path $pageOne['path']
    $reopened = Save-FlowDocument '10-reopened'
    Assert-FlowInkEqual $pageOne $reopened 'Saved drawing reopened with exact ink'
    Take-FlowScreenshot '06-reopened'
}
catch {
    Add-FlowCheck 'Regression execution' $false $_.Exception.Message
    if ($script:flowWindow) {
        try { Take-FlowScreenshot 'failure' } catch { Add-FlowCheck 'Failure screenshot' $false $_.Exception.Message }
    }
}
finally {
    try { Stop-FlowRecording } catch { Add-FlowCheck 'Recording finalization' $false $_.Exception.Message }
    $failed = @($script:flowChecks | Where-Object { $_.status -eq 'FAIL' })
    $script:flowResult.status = if ($failed.Count) { 'Failed' } else { 'Passed' }
    $script:flowResult['finished'] = (Get-Date).ToUniversalTime().ToString('o')
    Write-FlowResult
}
if ($script:flowResult.status -ne 'Passed') {
    throw "WinApp regression has $($failed.Count) failed check(s). See $(Join-Path $OutputDirectory 'results.json')."
}
