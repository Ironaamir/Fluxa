param([switch]$ExitAtEnd)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class TrayTestNative {
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,UIntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint m,UIntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool GetMenuItemRect(IntPtr h,IntPtr menu,uint index,out Rect r);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 public struct Rect { public int Left,Top,Right,Bottom; }
}
'@
$p=Get-Process Fluxa | Select-Object -First 1
$window=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
function Find($name,$root=$window){
 $e=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
 if(!$e){throw "Not found: $name"}; return $e
}
function InvokeButton($name){
 $c=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))
 $e=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
 $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 300
}
function OpenTrayMenu {
 [TrayTestNative]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
 [TrayTestNative]::SetCursorPos(1100,850) | Out-Null
 # Simulate the version-4 notification from Shell, then interact with the real native popup.
 [TrayTestNative]::PostMessage($p.MainWindowHandle,0x802A,[UIntPtr]::Zero,[IntPtr]0x1000400) | Out-Null
 Start-Sleep -Milliseconds 350
 $menu=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty,'#32768'))
 if(!$menu){throw 'Tray callback did not open a native popup menu'}
 return $menu
}
function ClickMenuItem($menu,$index){
 $handle=[IntPtr]$menu.Current.NativeWindowHandle
 $hm=[TrayTestNative]::SendMessage($handle,0x1E1,[UIntPtr]::Zero,[IntPtr]::Zero)
 $r=[TrayTestNative+Rect]::new()
 if(![TrayTestNative]::GetMenuItemRect([IntPtr]::Zero,$hm,$index,[ref]$r)){throw 'Cannot find native menu item rectangle'}
 [TrayTestNative]::SetCursorPos(($r.Left+$r.Right)/2,($r.Top+$r.Bottom)/2) | Out-Null
 [TrayTestNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
 [TrayTestNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
}
foreach($mode in @('Video','Audio','PDF','Images')){
 $menu=OpenTrayMenu
 $index=@('Images','Video','Audio','PDF').IndexOf($mode)
 ClickMenuItem $menu $index
 Start-Sleep -Milliseconds 350
 $heading=if($mode -eq 'Images'){'Convert images'}elseif($mode -eq 'Video'){'Convert video'}else{"$mode conversion is on the way"}
 Find $heading | Out-Null
 "PASS: tray native menu routes to $mode"
}
InvokeButton 'Settings'
$toggle=Find 'Keep running in the system tray when I close the window'
$pattern=$toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
if($pattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On){$pattern.Toggle()}
InvokeButton 'Save'
[TrayTestNative]::PostMessage($p.MainWindowHandle,0x10,[UIntPtr]::Zero,[IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 400
if([TrayTestNative]::IsWindowVisible($p.MainWindowHandle)){throw 'Close-to-tray did not hide the window'}
$menu=OpenTrayMenu
ClickMenuItem $menu 0
Start-Sleep -Milliseconds 400
if(![TrayTestNative]::IsWindowVisible($p.MainWindowHandle)){throw 'Tray did not restore the hidden window'}
'PASS: close-to-tray and restore'
InvokeButton 'Settings'
$toggle=Find 'Keep running in the system tray when I close the window'
$pattern=$toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
if($pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On){$pattern.Toggle()}
InvokeButton 'Save'
'NOTE: notification callback was simulated; Explorer icon clicking was not automated.'
if($ExitAtEnd){
 $menu=OpenTrayMenu
 ClickMenuItem $menu 6
 if(!$p.WaitForExit(5000)){throw 'Tray Exit did not terminate the app'}
 'PASS: tray Exit terminates the app'
}
