# Executed exclusively by Test-WindowsSandbox.ps1 inside a disposable guest.
$ErrorActionPreference = 'Stop'
$bundleMode=Test-Path C:\Inputs\candidate.msixbundle
$artifact=if ($bundleMode) {'C:\Inputs\candidate.msixbundle'} else {'C:\Inputs\candidate.msix'}
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or !(Test-Path C:\Evidence) -or !(Test-Path $artifact)) {
    throw 'Run the host script; these tests require a disposable Windows Sandbox guest.'
}
$results = New-Object System.Collections.ArrayList
$evidence = 'C:\Evidence'
$started = Get-Date
function Record($name,$status,$detail) {
    [void]$results.Add([ordered]@{ Check=$name; Status=$status; Detail=[string]$detail })
    Add-Content "$evidence\progress.txt" "$(Get-Date -Format o) [$status] $name $detail" -Encoding UTF8
}
function Step($name,[scriptblock]$body) {
    try {
        $info = & $body
        if ($info -is [pscustomobject] -and $info.Status -eq 'WARNING') { Record $name WARNING $info.Detail }
        else { Record $name PASS ($info -join ' ') }
    }
    catch { Record $name FAIL $_.Exception.GetBaseException().Message }
}
function Until([scriptblock]$test,[int]$seconds=30) {
    $until = (Get-Date).AddSeconds($seconds)
    do { $value = & $test; if ($value) { return $value }; Start-Sleep -Milliseconds 350 } while ((Get-Date) -lt $until)
    throw "Timeout: $test"
}
function Find-LocalizedProcess([string]$titleKey) {
    foreach ($process in Get-Process -Name FluentCanvas -ErrorAction SilentlyContinue) {
        foreach ($match in $script:windowTitles[$titleKey]) {
            $hwnd = [GuestInput]::Window($process.Id, $match.Name)
            if ($hwnd -ne [IntPtr]::Zero) {
                # Multiple languages can share the same title. Keep each matching caption profile.
                return [pscustomobject]@{ Id=$process.Id; MainWindowHandle=$hwnd; Process=$process; Captions=$match.Group }
            }
        }
    }
}
function MainProcess {
    # The WPF overlay has a hidden owner and ShowInTaskbar=False. Find it by PID and title.
    Find-LocalizedProcess 'Canvas.WindowTitle'
}
function SettingsProcess { Find-LocalizedProcess 'Settings.WindowTitle' }
function CaptionNames($profiles, [string]$key) {
    $profiles | ForEach-Object { $_.$key } | Select-Object -Unique
}
function Find-LocalizedWindow($process, [string]$key) {
    foreach ($caption in CaptionNames $process.Captions $key) {
        $hwnd = [GuestInput]::Window($process.Id, $caption)
        if ($hwnd -ne [IntPtr]::Zero) { return $hwnd }
    }
    return [IntPtr]::Zero
}
function CloseMain {
    $main = MainProcess
    if (!$main) { throw 'No active whiteboard.' }
    if (![GuestInput]::PostMessage($main.MainWindowHandle,0x10,[IntPtr]::Zero,[IntPtr]::Zero) -or !$main.Process.WaitForExit(20000)) { throw 'Main window did not close normally.' }
}
function Find-LocalizedVisible([string]$key, $process) {
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $property = [Windows.Automation.AutomationElement]::NameProperty
    foreach ($caption in CaptionNames $process.Captions $key) {
        $query = New-Object Windows.Automation.PropertyCondition($property,$caption)
        $element = $window.FindAll([Windows.Automation.TreeScope]::Descendants,$query) |
            Where-Object { !$_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 1 } | Select-Object -First 1
        if ($element) { return $element }
    }
}
function ClickControl($element) {
    if (!$element) {throw 'Control is not visible.'}
    $invoke = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$invoke)) { $invoke.Invoke();return }
    $rect = $element.Current.BoundingRectangle
    [GuestInput]::Click([int]($rect.X+$rect.Width/2),[int]($rect.Y+$rect.Height/2))
}
function OpenSettings($main) {
    $settings = SettingsProcess
    if ($settings) { return $settings }
    [GuestInput]::SetForegroundWindow($main.MainWindowHandle) | Out-Null
    $more = Find-LocalizedVisible 'Canvas.MoreTools' $main
    if (!$more) {
        ClickControl (Until { Find-LocalizedVisible 'Canvas.Pen' $main } 5)
        $more = Until { Find-LocalizedVisible 'Canvas.MoreTools' $main } 8
    }
    ClickControl $more
    ClickControl (Until {Find-LocalizedVisible 'Canvas.Settings' $main} 5)
    Until { SettingsProcess } 15
}
function Find-SettingsControl($settings, $id) {
    $window = [Windows.Automation.AutomationElement]::FromHandle($settings.MainWindowHandle)
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function OpenErrorPreference($main) {
    $settings = OpenSettings $main
    Until { $box = Find-SettingsControl $settings 'ErrorReporting'; if ($box -and $box.Current.IsEnabled) { $box } } 10
}
function OpenDocument($path,$needle) {
    $before = @(Select-String -LiteralPath $logFile -SimpleMatch $needle -ErrorAction SilentlyContinue).Count
    Start-Process -FilePath $path
    Until { @(Select-String -LiteralPath $logFile -SimpleMatch $needle -ErrorAction SilentlyContinue).Count -gt $before } 30 | Out-Null
}
function SaveThroughToolbar($main) {
    $folder=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'FluentCanvas'
    $getUserSaves = {
        Get-ChildItem -Path $folder -File -Filter *.icart -Recurse -ErrorAction SilentlyContinue |
            Where-Object DirectoryName -Like '*\User Saved - * Strokes'
    }
    $previous=@(& $getUserSaves | ForEach-Object FullName)
    ClickControl (Find-LocalizedVisible 'Canvas.MoreTools' $main)
    ClickControl (Until { Find-LocalizedVisible 'Canvas.SaveDocument' $main } 5)
    Until { & $getUserSaves | Where-Object FullName -NotIn $previous | Select-Object -First 1 } 8
}
function InkCount($file) {
    $inputFile=[IO.File]::OpenRead($file)
    $zip=[IO.Compression.ZipArchive]::new($inputFile,[IO.Compression.ZipArchiveMode]::Read,$false)
    try {
        $source=$zip.GetEntry('strokes.icstk').Open(); $mem=New-Object IO.MemoryStream
        try {$source.CopyTo($mem);$mem.Position=0;return ([Windows.Ink.StrokeCollection]::new($mem)).Count} finally {$source.Dispose();$mem.Dispose()}
    } finally {$zip.Dispose()}
}
function Capture($name) {
    $bounds = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object Drawing.Bitmap($bounds.Width,$bounds.Height)
    $g = [Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($bounds.Location, [Drawing.Point]::Empty, $bounds.Size)
        $bmp.Save("$evidence\$name.png",[Drawing.Imaging.ImageFormat]::Png)
    } finally { $g.Dispose(); $bmp.Dispose() }
}
function Create-Classroom($docFolder) {
    # A real classroom example for import: six ink strokes on a diagram with a text layer.
    $w = [Windows.SystemParameters]::PrimaryScreenWidth
    $h = [Windows.SystemParameters]::PrimaryScreenHeight
    $ratio = [Math]::Min($w/1000, $h/680)
    $strokes = New-Object Windows.Ink.StrokeCollection
    $draw = {
        param([double[]]$points,[string]$color,[double]$width)
        $list = New-Object Windows.Input.StylusPointCollection
        for($i=0; $i -lt $points.Length; $i+=2) { $list.Add([Windows.Input.StylusPoint]::new($points[$i]*$ratio,$points[$i+1]*$ratio)) }
        $s = [Windows.Ink.Stroke]::new($list)
        $s.DrawingAttributes.Color = [Windows.Media.ColorConverter]::ConvertFromString($color)
        $s.DrawingAttributes.Width=$width*$ratio; $s.DrawingAttributes.Height=$width*$ratio
        $strokes.Add($s)
    }
    & $draw @(140,365,140,185) '#225782' 4
    & $draw @(140,185,380,365) '#225782' 4
    & $draw @(380,365,140,365) '#225782' 4
    & $draw @(140,343,163,343,163,365) '#2B7D76' 3
    & $draw @(530,404,606,404) '#2B7D76' 3
    & $draw @(636,377,644,385,663,361) '#2B7D76' 3
    $raw = Join-Path $docFolder '课后 练习.icstk'
    $f = [IO.File]::Create($raw);try {$strokes.Save($f)}finally{$f.Dispose()}
    $art = Join-Path $docFolder '几何 课堂.icart'
    $f = [IO.File]::Create($art)
    $zip = [IO.Compression.ZipArchive]::new($f,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        $ink = $zip.CreateEntry('strokes.icstk').Open()
        try {$strokes.Save($ink)}finally{$ink.Dispose()}
        $pic = New-Object Drawing.Bitmap(900,470)
        $g=[Drawing.Graphics]::FromImage($pic);$g.Clear([Drawing.Color]::White)
        $g.TextRenderingHint=[Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $title=[Drawing.Font]::new('Segoe UI',26,[Drawing.FontStyle]::Bold)
        $sub=[Drawing.Font]::new('Segoe UI',15)
        $math=[Drawing.Font]::new('Cambria',26,[Drawing.FontStyle]::Italic)
        $dark=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(28,56,77))
        $teal=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(35,116,109))
        try {
            $g.DrawString('The right triangle',$title,$dark,33,16)
            $g.DrawString('Find the missing side using the Pythagorean theorem.',$sub,$dark,35,73)
            $g.DrawString('3',$math,$teal,15,204);$g.DrawString('4',$math,$teal,148,305);$g.DrawString('c',$math,$teal,199,174)
            $g.DrawString('a² + b² = c²',$math,$dark,445,154)
            $g.DrawString('9 + 16 = 25',$math,$dark,445,226)
            $g.DrawString('c = 5',$math,$teal,445,292)
            $data=New-Object IO.MemoryStream
            try {
                $pic.Save($data,[Drawing.Imaging.ImageFormat]::Png);$data.Position=0
                $asset=$zip.CreateEntry('File Dependency/lesson.png').Open()
                try {$data.CopyTo($asset)}finally{$asset.Dispose()}
            }finally{$data.Dispose()}
        }finally{$g.Dispose();$pic.Dispose();$title.Dispose();$sub.Dispose();$math.Dispose();$dark.Dispose();$teal.Dispose()}
        $scale = [Globalization.CultureInfo]::InvariantCulture
        $xml = [string]::Format($scale,"<InkCanvas xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Image Source='File Dependency/lesson.png' InkCanvas.Left='{0}' InkCanvas.Top='{1}' Width='{2}' Height='{3}'/></InkCanvas>",80*$ratio,55*$ratio,900*$ratio,470*$ratio)
        $writer = New-Object IO.StreamWriter($zip.CreateEntry('elements.xaml').Open());try{$writer.Write($xml)}finally{$writer.Dispose()}
    }finally{$zip.Dispose();$f.Dispose()}
    return @($raw,$art)
}
try {
    $captions = Get-Content -LiteralPath 'C:\Inputs\captions.json' -Raw -Encoding UTF8 | ConvertFrom-Json
    $script:windowTitles = @{
        'Canvas.WindowTitle' = @($captions | Group-Object -Property 'Canvas.WindowTitle' -CaseSensitive)
        'Settings.WindowTitle' = @($captions | Group-Object -Property 'Settings.WindowTitle' -CaseSensitive)
    }
    $os = [Environment]::OSVersion.VersionString
    Record 'Isolated Windows guest' PASS "$os, $env:PROCESSOR_ARCHITECTURE, $env:USERNAME"
    Add-Type -AssemblyName PresentationFramework,System.IO.Compression,System.Windows.Forms,System.Drawing,UIAutomationClient,UIAutomationTypes
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GuestInput {
  delegate bool Visit(IntPtr hwnd,IntPtr data);
  [DllImport("user32.dll")] static extern bool EnumWindows(Visit visit,IntPtr data);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint id);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd,System.Text.StringBuilder text,int size);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wp,IntPtr lp);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool AreDpiAwarenessContextsEqual(IntPtr first,IntPtr second);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
  public static IntPtr Window(int process,string title){var result=IntPtr.Zero;EnumWindows(delegate(IntPtr h,IntPtr ignored){uint id;GetWindowThreadProcessId(h,out id);if(id==(uint)process){var t=new System.Text.StringBuilder(256);GetWindowText(h,t,t.Capacity);if(t.ToString()==title){result=h;return false;}}return true;},IntPtr.Zero);return result;}
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
  public static void Click(int x,int y){SetCursorPos(x,y);mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero);}
  public static void Drag(int x,int y,int dx,int dy){SetCursorPos(x,y);System.Threading.Thread.Sleep(50);mouse_event(2,0,0,0,UIntPtr.Zero);for(int i=1;i<=20;i++){SetCursorPos(x+dx*i/20,y+dy*i/20);System.Threading.Thread.Sleep(15);}mouse_event(4,0,0,0,UIntPtr.Zero);}
}
"@
    $cert = Get-PfxCertificate C:\Inputs\candidate.cer
    if ($cert.Subject -ne 'CN=FluentCanvas Local Test') { throw 'Expected the local-test certificate.' }
    Import-Certificate C:\Inputs\candidate.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    $sig = Get-AuthenticodeSignature $artifact
    if ($sig.SignerCertificate.Thumbprint -ne $cert.Thumbprint -or $sig.Status -ne 'Valid') { throw "Package signature: $($sig.Status)" }
    Add-AppxPackage -Path $artifact
    $pkg = Get-AppxPackage -Name FluentCanvas.LocalTest -PackageTypeFilter Main
    if (!$pkg) {throw 'Package registration missing.'}
    Record 'Install signed artifact' PASS "$($pkg.PackageFullName), SHA256 $((Get-FileHash $artifact).Hash)"
    $logFile = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalState\Log.txt"
    $settingsFile = Join-Path (Split-Path $logFile) 'Settings.json'
    $manifest = Get-AppxPackageManifest $pkg
    $entry = $manifest.Package.Applications.Application
    $aumid = $pkg.PackageFamilyName + '!' + $entry.Id
    if ($bundleMode) {
        $f=[IO.File]::OpenRead($artifact)
        $zip=[IO.Compression.ZipArchive]::new($f,[IO.Compression.ZipArchiveMode]::Read)
        try {
            $reader=[IO.StreamReader]::new($zip.GetEntry('AppxMetadata/AppxBundleManifest.xml').Open())
            try { $bundleManifest=[xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally {$zip.Dispose();$f.Dispose()}
        if ($pkg.Architecture -ne 'X64' -or $pkg.Version -ne $bundleManifest.Bundle.Identity.Version) {throw 'Bundle selected an unexpected architecture or version.'}
        Record 'Bundle selected installed payload' PASS "$($pkg.Name), $($pkg.Architecture), $($pkg.Version)"
        Start-Process explorer.exe "shell:AppsFolder\$aumid"
        Until { (MainProcess) -and (Select-String -LiteralPath $logFile -SimpleMatch 'FluentCanvas Loaded' -Quiet -ErrorAction SilentlyContinue) } 45 | Out-Null
        Record 'Launch from bundle installation' PASS "PID $((MainProcess).Id), $aumid"
        Step 'Runtime whiteboard PerMonitorV2 DPI awareness' {
            $hwnd=(MainProcess).MainWindowHandle
            $pmv2=[GuestInput]::AreDpiAwarenessContextsEqual([GuestInput]::GetWindowDpiAwarenessContext($hwnd),[IntPtr]::new(-4))
            if (!$pmv2) {throw 'Live whiteboard window does not use per-monitor V2 DPI awareness.'}
            "PerMonitorV2=$pmv2; DPI=$([GuestInput]::GetDpiForWindow($hwnd))"
        }
        Step 'Sentry user preference survives restart and can be re-enabled' {
            $marker=Join-Path (Split-Path $logFile) 'error-reporting.disabled'
            $main=MainProcess
            $box=OpenErrorPreference $main
            $pattern=$box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
            if ((Test-Path $marker) -or $pattern.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) {throw 'Error reporting unexpectedly inactive.'}
            $pattern.Toggle()
            Until { (Test-Path $marker) -and $pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::Off } 5 | Out-Null
            $loaded=@(Select-String -LiteralPath $logFile -SimpleMatch 'FluentCanvas Loaded').Count
            $settings = SettingsProcess
            CloseMain
            if ($settings) { Until { $settings.Process.HasExited } 8 | Out-Null }
            Start-Process explorer.exe "shell:AppsFolder\$aumid"
            $next=Until { $p=MainProcess;if($p -and $p.Id -ne $main.Id -and @(Select-String -LiteralPath $logFile -SimpleMatch 'FluentCanvas Loaded').Count -gt $loaded){$p} } 30
            $box=OpenErrorPreference $next
            $pattern=$box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
            if (!(Test-Path $marker) -or $pattern.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {throw 'Opt-out did not survive application restart.'}
            $pattern.Toggle()
            Until { !(Test-Path $marker) -and $pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On } 5 | Out-Null
            "On → Off → new PID $($next.Id) Off → On; LocalState marker verified"
        }
        if (MainProcess) {CloseMain}
        Copy-Item -LiteralPath $logFile -Destination "$evidence\FluentCanvas.log"
        return
    }
    $docFolder = New-Item -ItemType Directory -Path (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'FluentCanvas Acceptance')
    $files = @(Create-Classroom $docFolder.FullName)
    $shell = New-Object -ComObject WScript.Shell
    Step 'First Start-menu activation' {
        Start-Process explorer.exe "shell:AppsFolder\$aumid"
        Until { (MainProcess) -and (Select-String -LiteralPath $logFile -SimpleMatch 'FluentCanvas Loaded' -Quiet -ErrorAction SilentlyContinue) } 45
    }
    Step 'Graceful close' { CloseMain }
    Step 'Cold .icstk shell activation' { OpenDocument $files[0] 'Opened .icstk: 6 strokes, 0 elements' }
    Step 'Ready whiteboard for lesson' {
        $main = MainProcess
        [GuestInput]::SetForegroundWindow($main.MainWindowHandle) | Out-Null
        $shell.SendKeys('%b')
        Start-Sleep -Milliseconds 1400
        Until { Find-LocalizedVisible 'Canvas.PreviousPage' $main } 5 | Out-Null
        $script:boardSeen=$true
    }
    Step 'Warm .icart shell activation' {
        $idBefore = (MainProcess).Id
        OpenDocument $files[1] 'Opened .icart: 6 strokes, 1 elements'
        if ((MainProcess).Id -ne $idBefore) {throw 'Existing instance changed.'}
        if ($script:boardSeen) {Start-Sleep -Seconds 3;Capture 'Whiteboard'}
    }
    Step 'Canceling a file activation preserves an edited blank canvas and redo' {
        $main = MainProcess
        [GuestInput]::SetForegroundWindow($main.MainWindowHandle) | Out-Null
        $shell.SendKeys('^z')
        Until { $redo=Find-LocalizedVisible 'Canvas.Redo' $main; if ($redo -and $redo.Current.IsEnabled) { $redo } } 5 | Out-Null
        $shell.SendKeys('^z')
        Until { $undo=Find-LocalizedVisible 'Canvas.Undo' $main; $undo -and !$undo.Current.IsEnabled } 5 | Out-Null
        $before = @(Select-String -LiteralPath $logFile -SimpleMatch 'Opened .icstk:').Count
        Start-Process -FilePath $files[0]
        $dialog = Until {
            $handle=Find-LocalizedWindow $main 'Actions.OpenDocument'
            if ($handle -ne [IntPtr]::Zero) { $handle }
        } 10
        [GuestInput]::SetForegroundWindow($dialog) | Out-Null
        $shell.SendKeys('{ESC}')
        Until { (Find-LocalizedWindow $main 'Actions.OpenDocument') -eq [IntPtr]::Zero } 5 | Out-Null
        $redo = Find-LocalizedVisible 'Canvas.Redo' $main
        if (!$redo -or !$redo.Current.IsEnabled -or @(Select-String -LiteralPath $logFile -SimpleMatch 'Opened .icstk:').Count -ne $before) {
            throw 'Canceled file activation changed the open canvas or its redo state.'
        }
        [GuestInput]::SetForegroundWindow($main.MainWindowHandle) | Out-Null
        $shell.SendKeys('^y')
        Start-Sleep -Milliseconds 300
        $shell.SendKeys('^y')
    }
    Step 'Keyboard undo and redo' {
        $main = MainProcess
        [GuestInput]::SetForegroundWindow($main.MainWindowHandle) | Out-Null
        $shell.SendKeys('^z');Start-Sleep -Milliseconds 500
        $redo = Find-LocalizedVisible 'Canvas.Redo' $main
        if (!$redo -or !$redo.Current.IsEnabled) {throw 'Redo unavailable after Ctrl+Z.'}
        $shell.SendKeys('^y');Start-Sleep -Milliseconds 500
        $undo = Find-LocalizedVisible 'Canvas.Undo' $main
        if (!$undo -or !$undo.Current.IsEnabled) {throw 'Undo unavailable after Ctrl+Y.'}
    }
    Step 'Save classroom document through toolbar' {
        $main = MainProcess
        $work = SaveThroughToolbar $main
        $script:savedPath=$work.FullName
        if ((InkCount $script:savedPath) -ne 6) {throw 'Unexpected saved stroke count.'}
    }
    Step 'Mouse-driven pen tool and whole-stroke erasing' {
        $main=MainProcess
        $screen=[Windows.Forms.Screen]::PrimaryScreen.Bounds
        $x=[int]($screen.Width*0.60);$y=[int]($screen.Height*0.72)
        ClickControl (Find-LocalizedVisible 'Canvas.Pen' $main)
        [GuestInput]::Drag($x,$y,75,0)
        if ((InkCount (SaveThroughToolbar $main).FullName) -ne 7) {throw 'Pen did not add one stroke.'}
        ClickControl (Find-LocalizedVisible 'Canvas.EraseStrokes' $main)
        [GuestInput]::Drag($x+30,$y-10,0,20)
        if ((InkCount (SaveThroughToolbar $main).FullName) -ne 6) {throw 'Eraser did not remove one stroke.'}
    }
    Step 'Settings persisted after reopening saved work' {
        $main=MainProcess
        $settings=OpenSettings $main
        $expectedStates=@{}
        foreach ($setting in 'RotateWithTwoFingers','InkRecognition') {
            $toggle=Until { $control=Find-SettingsControl $settings $setting; if ($control -and $control.Current.IsEnabled) {$control} } 10
            $togglePattern=$toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
            $before=$togglePattern.Current.ToggleState
            $togglePattern.Toggle()
            $after=Until {
                $state=$togglePattern.Current.ToggleState
                if ($state -ne $before -and $toggle.Current.IsEnabled) {[pscustomobject]@{Value=$state}}
            } 8
            $expectedStates[$setting]=$after.Value
        }
        Until {
            $stored=Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
            $stored.gesture.isEnableTwoFingerRotation -eq ($expectedStates.RotateWithTwoFingers -eq [Windows.Automation.ToggleState]::On) -and
                $stored.inkToShape.isInkToShapeEnabled -eq ($expectedStates.InkRecognition -eq [Windows.Automation.ToggleState]::On)
        } 4 | Out-Null
        CloseMain
        if (!$settings.Process.WaitForExit(8000)) {throw 'Settings did not close with its owner.'}
        OpenDocument $script:savedPath 'Opened .icart: 6 strokes, 1 elements'
        $main=MainProcess
        $settings=OpenSettings $main
        foreach ($setting in 'RotateWithTwoFingers','InkRecognition') {
            $toggle=Until { $control=Find-SettingsControl $settings $setting; if ($control -and $control.Current.IsEnabled) {$control} } 10
            if ($toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne $expectedStates[$setting]) {
                throw "$setting changed after app restart."
            }
        }
        'Rotation and ink recognition persisted in LocalState and reflected after reload'
    }
    Step 'Startup preference enable and disable' {
        $main=MainProcess
        $settings = OpenSettings $main
        $switch=Until { $control = Find-SettingsControl $settings 'RunAtStartup'; if ($control -and $control.Current.IsEnabled) { $control } } 10
        $pattern=$switch.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        if ($pattern.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {throw 'Startup was enabled by default.'}
        $pattern.Toggle()
        Until {$switch.Current.IsEnabled -and $pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On} 10 | Out-Null
        $pattern.Toggle()
        Until {$switch.Current.IsEnabled -and $pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::Off} 10 | Out-Null
        'Live StartupTask off → on → off'
    }
    Step 'Restart from settings' {
        $main=MainProcess
        $before=@(Select-String -LiteralPath $logFile -SimpleMatch 'FluentCanvas Loaded').Count
        $settings = OpenSettings $main
        $restart = Until { $control = Find-SettingsControl $settings 'Restart'; if ($control -and $control.Current.IsEnabled) { $control } } 10
        ClickControl $restart
        ClickControl (Until { Find-LocalizedVisible 'Settings.Restart' $settings } 10)
        Until { $next=MainProcess; $next -and $next.Id -ne $main.Id -and @(Select-String -LiteralPath $logFile -SimpleMatch 'FluentCanvas Loaded').Count -gt $before } 30 | Out-Null
        "PID $($main.Id) → $((MainProcess).Id)"
    }
    Step 'WinUI diagnostics' {
        $expectedTitles = @(CaptionNames (MainProcess).Captions 'Settings.DiagnosticsTitle')
        $diag = Start-Process -FilePath (Join-Path $pkg.InstallLocation $entry.Executable) -ArgumentList '--diagnostics' -PassThru
        Until { $diag.Refresh();$diag.MainWindowHandle -ne 0 -and $diag.MainWindowTitle -cin $expectedTitles } 20 | Out-Null
        $win = [Windows.Automation.AutomationElement]::FromHandle($diag.MainWindowHandle)
        $text=($win.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object {$_.Current.Name}) -join ' '
        if ($text -notmatch 'MSIX' -or $text -notmatch '11\.0\.0') {throw "Unexpected diagnostics: $text"}
        Capture 'Diagnostics'
        $diag.CloseMainWindow() | Out-Null
        if (!$diag.WaitForExit(10000)) {throw 'Diagnostics did not close.'}
        $text
    }
    Step 'Startup task default' {
        $node=$manifest.SelectSingleNode("//*[local-name()='StartupTask']")
        if ($node.TaskId -ne 'FluentCanvasStartup' -or $node.Enabled -ne 'false') {throw 'Unexpected startup configuration.'}
        $node.TaskId
    }
    if (MainProcess) {Step 'Close before certification' { CloseMain }}
    if (Test-Path C:\Inputs\wack) {
        Step 'Install guest WACK' {
            $sdkRoot=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
            $null=New-Item -ItemType Directory -Force -Path $sdkRoot
            Copy-Item -LiteralPath 'C:\Inputs\References' -Destination $sdkRoot -Recurse -Force
            foreach($msi in Get-ChildItem C:\Inputs\wack -Filter *.msi) {
                $localCache = 'C:\GuestWack'
                if (!(Test-Path $localCache)) {Copy-Item -LiteralPath C:\Inputs\wack -Destination $localCache -Recurse}
                $p=Start-Process msiexec.exe -ArgumentList ('/i "' + (Join-Path $localCache $msi.Name) + '" /qn /norestart /l*v "C:\Evidence\' + $msi.BaseName + '.log"') -WindowStyle Hidden -Wait -PassThru
                if ($p.ExitCode -notin 0,3010) {throw "$($msi.Name): $($p.ExitCode)"}
            }
        }
        Step 'WACK binary reader diagnostics' {
            $kitPath=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit'
            $exe=Join-Path $pkg.InstallLocation $entry.Executable
            $tempExe=Join-Path $env:TEMP 'FluentCanvas-WackInspection.exe'
            Copy-Item -LiteralPath $exe -Destination $tempExe
            $type=[Reflection.Assembly]::LoadFrom((Join-Path $kitPath 'microsoft.windows.softwarelogo.binaryinfo.interop.dll')).GetType('Microsoft.Windows.SoftwareLogo.BinaryInfo.Interop.BinaryInfoClass')
            $diagnostic=foreach($path in $exe,$tempExe) {
                $operation='CreateInstance'
                try {
                    $binary=[Activator]::CreateInstance($type)
                    $operation='LoadBinary'
                    $null=$type.GetMethod($operation).Invoke($binary,[object[]]@([string]$path))
                    $operation='IsPerMonitorV2DPIAwarenessSet'
                    $aware=$type.GetMethod($operation).Invoke($binary,$null)
                    $operation='UnloadBinary'
                    $null=$type.GetMethod($operation).Invoke($binary,$null)
                    [pscustomobject]@{Path=$path;PerMonitorV2=$aware;Operation=$operation;Result='OK'}
                } catch {
                    $exception=$_.Exception.GetBaseException()
                    [pscustomobject]@{Path=$path;Operation=$operation;HRESULT=('{0:X8}' -f $exception.HResult);Result=$exception.Message}
                }
            }
            $diagnostic | ConvertTo-Json | Set-Content "$evidence\wack-binary-reader.json" -Encoding UTF8
            'Saved both original and copied executable probe results'
        }
        Step 'Windows App Certification Kit' {
            $kit=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'
            $report="$evidence\wack.xml"
            $cmd=Start-Process $kit -ArgumentList 'reset' -WindowStyle Hidden -PassThru -Wait
            if ($cmd.ExitCode -ne 0) {throw "appcert reset: $($cmd.ExitCode)"}
            $cmd=Start-Process $kit -ArgumentList "test -packagefullname $($pkg.PackageFullName) -reportoutputpath $report" -WindowStyle Hidden -PassThru
            if (!$cmd.WaitForExit(900000)) {throw 'WACK exceeded 15 minutes.'}
            if (!(Test-Path $report)) {throw "No WACK XML report, exit $($cmd.ExitCode)"}
            $xml=[xml](Get-Content -LiteralPath $report -Raw)
            $outcome=$xml.DocumentElement.GetAttribute('OVERALL_RESULT')
            $failed=@($xml.SelectNodes('/REPORT/REQUIREMENTS/REQUIREMENT/TEST[RESULT="FAIL"]') | ForEach-Object { $_.GetAttribute('NAME') })
            $detail="WACK $outcome, exit $($cmd.ExitCode): $($failed -join ', ')"
            if ($outcome -eq 'WARNING') { return [pscustomobject]@{Status='WARNING';Detail=$detail} }
            if ($outcome -ne 'PASS' -or $cmd.ExitCode -ne 0) {throw $detail}
            $outcome
        }
    }
    if(Test-Path $logFile){Copy-Item -LiteralPath $logFile -Destination "$evidence\FluentCanvas.log"}
    Step 'Uninstall preserves user-saved works' {
        if (!$script:savedPath) {throw 'App-generated user save unavailable for retention check.'}
        $savedHash=(Get-FileHash -LiteralPath $script:savedPath).Hash
        $importHash=(Get-FileHash -LiteralPath $files[1]).Hash
        Remove-AppxPackage $pkg.PackageFullName
        if (Get-AppxPackage -Name FluentCanvas.LocalTest) {throw 'Package still registered.'}
        if ((Get-FileHash -LiteralPath $script:savedPath).Hash -ne $savedHash -or (Get-FileHash -LiteralPath $files[1]).Hash -ne $importHash) {throw 'Contents of a user document changed.'}
        'Original import and exported whiteboard both retained identical SHA256'
    }
    if (Test-Path 'C:\Inputs\previous.msix') {
        Step 'Upgrade retains LocalState and saved work' {
            $oldCertificate=Get-PfxCertificate C:\Inputs\previous.cer
            if ($oldCertificate.Subject -ne $cert.Subject) {throw 'Fixture publisher mismatch.'}
            Import-Certificate C:\Inputs\previous.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
            Add-AppxPackage C:\Inputs\previous.msix
            $old=Get-AppxPackage -Name FluentCanvas.LocalTest -PackageTypeFilter Main
            $expectedPrevious = [version](Get-Content -LiteralPath C:\Inputs\previous-version.txt -Raw).Trim()
            $oldVersion = [version]$old.Version
            $candidateVersion = [version]$pkg.Version
            if ($oldVersion -ne $expectedPrevious -or $oldVersion -ge $candidateVersion -or
                $old.PackageFamilyName -ne $pkg.PackageFamilyName) {
                throw "Unexpected upgrade fixture $($old.PackageFullName); expected $expectedPrevious in $($pkg.PackageFamilyName) before $candidateVersion."
            }
            $null=New-Item -ItemType Directory -Force -Path (Split-Path $settingsFile)
            '{"gesture":{"isEnableMultiTouchMode":false},"randSettings":{"peopleCount":37}}' | Set-Content -LiteralPath $settingsFile -Encoding UTF8
            $hash=(Get-FileHash -LiteralPath $settingsFile).Hash
            $workHash=(Get-FileHash -LiteralPath $files[1]).Hash
            Add-AppxPackage C:\Inputs\candidate.msix
            $updated=Get-AppxPackage FluentCanvas.LocalTest
            if ($updated.PackageFullName -ne $pkg.PackageFullName -or (Get-FileHash $settingsFile).Hash -ne $hash -or (Get-FileHash $files[1]).Hash -ne $workHash) {throw 'Version or retained files differ after upgrade.'}
            OpenDocument $files[1] 'Opened .icart: 6 strokes, 1 elements'
            CloseMain
            Remove-AppxPackage $updated.PackageFullName
            "$($old.Version) to $($updated.Version); both SHA256 verified"
        }
    }
}
catch {
    $failure = $_
    Record 'Guest setup' FAIL $failure.Exception.Message
    $failure | Format-List * -Force | Out-File -LiteralPath "$evidence\guest-error.txt" -Encoding UTF8
    Get-WinEvent -LogName 'Microsoft-Windows-AppXDeploymentServer/Operational' -MaxEvents 30 -ErrorAction SilentlyContinue |
        Format-List TimeCreated,Id,LevelDisplayName,Message | Out-File -LiteralPath "$evidence\appx-deployment.txt" -Encoding UTF8
}
finally {
    $result=[ordered]@{Started=$started.ToString('o');Finished=(Get-Date).ToString('o');Results=@($results)}
    $result | ConvertTo-Json -Depth 5 | Set-Content "$evidence\complete.tmp" -Encoding UTF8
    Move-Item -LiteralPath "$evidence\complete.tmp" -Destination "$evidence\complete.json"
    shutdown.exe /s /t 4
}
