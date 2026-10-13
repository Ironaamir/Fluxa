$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Test-Desktop.ps1') -FunctionsOnly
$folder=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/Revision Inputs'))
Add-Type @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class ExplorerMenuNative {
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint m,UIntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr m);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetMenuString(IntPtr m,uint i,StringBuilder text,int size,uint flags);
 [DllImport("user32.dll")] public static extern bool GetMenuItemRect(IntPtr window,IntPtr menu,uint index,out Rect rect);
 public struct Rect {public int Left,Top,Right,Bottom;}
}
'@
$shell=New-Object -ComObject Shell.Application
$shell.Open($folder)
Start-Sleep -Milliseconds 800
$explorer=@($shell.Windows()) | Where-Object {$_.LocationURL -like '*Revision%20Inputs*'} | Select-Object -First 1
if(!$explorer){throw 'Test Explorer folder did not open'}
function OpenMenu($names) {
 $flags=29
 foreach($name in $names){$explorer.Document.SelectItem($explorer.Document.Folder.ParseName($name),$flags);$flags=9}
 $h=[IntPtr]$explorer.HWND
 [DesktopNative]::SetForegroundWindow($h) | Out-Null
 Start-Sleep -Milliseconds 250
 $root=[Windows.Automation.AutomationElement]::FromHandle($h)
 $item=Find $names[0] $root
 $r=$item.Current.BoundingRectangle
 [DesktopNative]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+35)) | Out-Null
 [DesktopNative]::mouse_event(8,0,0,0,[UIntPtr]::Zero)
 [DesktopNative]::mouse_event(16,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 500
 $desktop=[Windows.Automation.AutomationElement]::RootElement
 $more=$desktop.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Show more options'))
 if($more){Click $more;Start-Sleep -Milliseconds 400}
 # Classic owner-drawn Shell menus expose no UIA descendants on this desktop.
 # Read their actual Win32 menu strings and rectangles, then click with the mouse.
 $nativeMenu=$desktop.FindFirst([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty,'#32768'))
 if(!$nativeMenu){throw 'Explorer did not display a classic context menu'}
 $menuHandle=[ExplorerMenuNative]::SendMessage([IntPtr]$nativeMenu.Current.NativeWindowHandle,0x1E1,[UIntPtr]::Zero,[IntPtr]::Zero)
 $open=$null;$convert=$null
 for($i=0;$i -lt [ExplorerMenuNative]::GetMenuItemCount($menuHandle);$i++) {
  $text=[Text.StringBuilder]::new(512)
  [ExplorerMenuNative]::GetMenuString($menuHandle,$i,$text,512,0x400) | Out-Null
  if($text.ToString() -eq 'Open in Fluxa'){$open=@{Handle=$menuHandle;Index=$i}}
  if($text.ToString() -eq 'Convert to .png with Fluxa'){$convert=@{Handle=$menuHandle;Index=$i}}
 }
 if(!$open -or !$convert){throw 'Fluxa verbs missing from Explorer menu'}
 Write-Host "PASS: Explorer displays Convert to .png with Fluxa and Open in Fluxa for $($names.Count) selected image(s)"
 return $open,$convert
}
function ClickNative($item) {
 $r=[ExplorerMenuNative+Rect]::new()
 if(![ExplorerMenuNative]::GetMenuItemRect([IntPtr]::Zero,$item.Handle,$item.Index,[ref]$r)){throw 'Could not locate Explorer menu item'}
 [DesktopNative]::SetCursorPos(($r.Left+$r.Right)/2,($r.Top+$r.Bottom)/2) | Out-Null
 [DesktopNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
 [DesktopNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 500
}
[Windows.Forms.SendKeys]::SendWait('{ESC}')
Invoke 'Clear queue';Invoke 'Clear finished'
$menu=OpenMenu @('shell.png')
ClickNative $menu[0]
Find 'Remove shell.png' (Find 'Pending files') | Out-Null
if((Find 'Finished files').FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition).Count -ne 0){throw 'Open menu unexpectedly converted file'}
'PASS: physical Explorer Open menu click queues without conversion'
Invoke 'Clear queue'
# Exit current app to test Explorer starting a fresh instance.
& (Join-Path $PSScriptRoot 'Test-Tray.ps1') -ExitAtEnd
$menu=OpenMenu @('one & first.png','two.png','three.png')
ClickNative $menu[1]
$deadline=(Get-Date).AddSeconds(20)
do {
 $newProcess=Get-Process Fluxa -ErrorAction SilentlyContinue | Where-Object {$_.MainWindowHandle -ne 0} | Select-Object -First 1
 if($newProcess){break};Start-Sleep -Milliseconds 200
} while((Get-Date) -lt $deadline)
if(!$newProcess){throw 'Explorer conversion did not launch Fluxa'}
$process=$newProcess
$window=[Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$deadline=(Get-Date).AddSeconds(20)
do {
 $finished=Find 'Finished files'
 $rows=$finished.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)).Count
 if($rows -eq 3){break};Start-Sleep -Milliseconds 200
} while((Get-Date) -lt $deadline)
if($rows -ne 3){throw "Multi-selection converted $rows instead of 3 images"}
foreach($name in @('one & first.png','two.png','three.png')){Find "Remove $name" $finished | Out-Null}
if((Get-Process Fluxa).Count -ne 1){throw 'Explorer multi-selection launched more than one app instance'}
'PASS: physical Explorer Convert menu click launches one Fluxa instance and converts all 3 selected files, including spaces/ampersand'
Screenshot 'revision-explorer-converted'
Invoke 'Clear finished'
# Close only the Explorer window used for these tests.
$explorer.Quit()
