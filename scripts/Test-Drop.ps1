$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class DropTestNative{
 [DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
}
'@
$root=[System.Windows.Automation.AutomationElement]::RootElement
$source=$root.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Fluxa Drag Test'))
if(!$source){throw 'Start the isolated drag-source helper first'}
$button=$source.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Drag test image'))
$p=Get-Process Fluxa|Select-Object -First 1
$window=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$drop=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Image drop zone'))
$a=$button.Current.BoundingRectangle;$b=$drop.Current.BoundingRectangle
$x=[int]($a.X+$a.Width/2);$y=[int]($a.Y+$a.Height/2)
$tx=[int]($b.X+$b.Width/2);$ty=[int]($b.Y+$b.Height/2)
[DropTestNative]::SetCursorPos($x,$y)|Out-Null
[DropTestNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 150
for($i=1;$i -le 25;$i++){
 [DropTestNative]::SetCursorPos([int]($x+($tx-$x)*$i/25),[int]($y+($ty-$y)*$i/25))|Out-Null
 Start-Sleep -Milliseconds 30
}
Start-Sleep -Milliseconds 350
[DropTestNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Seconds 1
$added=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Remove AppIcon.png'))
if(!$added){throw 'The native drag/drop did not add the image'}
'PASS: actual native OLE file drag/drop adds a queue item'
$source.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
