param([switch]$Prepare)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Test-Desktop.ps1') -FunctionsOnly
if($Prepare) {
 Invoke 'Settings'
 (Find 'Appearance mode').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 (Find 'Light').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 (Find 'Orange color scheme').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Invoke 'Save'
 foreach($name in @('Conversion modes','Compression modes')) {
  if((Find $name).Current.ItemStatus -ne 'Collapsed'){Invoke $name}
 }
 # AppWindow close is asynchronous after the settings popup; use the tray's real Exit action.
 & (Join-Path $PSScriptRoot 'Test-Tray.ps1') -ExitAtEnd
 if(!$process.HasExited){$process.Refresh()}
 if(!$process.WaitForExit(5000)){throw 'Prepared process did not exit'}
 'PASS: saved Light/Orange and collapsed groups, then exited the actual app'
 return
}
foreach($name in @('Conversion modes','Compression modes')) {
 if((Find $name).Current.ItemStatus -ne 'Collapsed'){throw "$name did not remain collapsed after restart"}
 Invoke $name
}
Invoke 'Settings'
if(!(Find 'Orange color scheme').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected){throw 'Orange palette did not survive restart'}
$a=(Find 'Appearance mode').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
if($a[0].Current.Name -ne 'Light'){throw 'Light mode did not survive restart'}
Invoke 'Cancel'
$r=(Find 'Video mode').Current.BoundingRectangle
[DesktopNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
[DesktopNative]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2)) | Out-Null
[DesktopNative]::mouse_event(1,1,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 300
$b=[System.Drawing.Bitmap]::new(1,1);$g=[System.Drawing.Graphics]::FromImage($b)
try {
 $g.CopyFromScreen([int]($r.Right-20),[int]($r.Y+$r.Height/2),0,0,$b.Size)
 $c=$b.GetPixel(0,0)
 if(('{0:X2}{1:X2}{2:X2}' -f $c.R,$c.G,$c.B) -ne 'D1D7D3'){throw 'Light sidebar hover is not the expected neutral gray'}
} finally {$g.Dispose();$b.Dispose()}
Screenshot 'fluxa-light'
Click (Find 'Video mode')
Find 'Video conversion is on the way' | Out-Null
Invoke 'Images mode'
'PASS: palette, light mode, and both collapsed groups survive restart; physical navigation and light hover work'
Invoke 'Settings'
(Find 'Appearance mode').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
(Find 'Dark').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
(Find 'Blue color scheme').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Invoke 'Save'
Screenshot 'fluxa-dark'
'PASS: restored Blue/Dark, both sidebar groups expanded'
