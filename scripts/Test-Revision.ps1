$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Test-Desktop.ps1') -FunctionsOnly
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ShellVerbTest {
 [DllImport("shell32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
 public static extern IntPtr ShellExecute(IntPtr window,string verb,string file,string args,string directory,int show);
}
'@
function Pixel($x,$y) {
 $b=[Drawing.Bitmap]::new(1,1);$g=[Drawing.Graphics]::FromImage($b)
 try{$g.CopyFromScreen([int]$x,[int]$y,0,0,$b.Size);$c=$b.GetPixel(0,0);return ('#{0:X2}{1:X2}{2:X2}' -f $c.R,$c.G,$c.B)}finally{$g.Dispose();$b.Dispose()}
}
function AssertPixel($x,$y,$hex,$label) {
 $actual=Pixel $x $y;if($actual -ne $hex){throw "${label}: expected $hex, got $actual"}
}
function SelectFormat($name,$value) {
 (Find $name).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 Start-Sleep -Milliseconds 150
 (Find $value).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Start-Sleep -Milliseconds 150
}
function WaitFinished($count) {
 $deadline=(Get-Date).AddSeconds(20)
 do {
  $actual=(Find 'Finished files').FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)).Count
  if($actual -eq $count){return}
  Start-Sleep -Milliseconds 100
 } while((Get-Date) -lt $deadline)
 throw "Expected $count finished rows, got $actual"
}
# Close a previously inspected dialog if one is open.
$dialog=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Settings & supported formats'))
if($dialog){Invoke 'Cancel'}
[DesktopNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
Invoke 'Images mode';Invoke 'Clear queue';Invoke 'Clear finished'
$folder=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/Revision Inputs'))
New-Item -ItemType Directory -Force $folder | Out-Null
$icon=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Fluxa.App/Assets/AppIcon.png'))
foreach($name in @('one & first.png','two.png','three.png','manual.png','shell.png')){Copy-Item $icon (Join-Path $folder $name) -Force}
$output=(Find 'Output folder').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
SelectFormat 'Output format' 'JPEG  (.jpg)'
for($batch=1;$batch -le 3;$batch++) {
 Invoke 'Browse images';FocusDialog 'Open'
 [Windows.Forms.SendKeys]::SendWait('%d');[Windows.Forms.SendKeys]::SendWait($folder);[Windows.Forms.SendKeys]::SendWait('{ENTER}')
 Start-Sleep -Milliseconds 300
 [Windows.Forms.SendKeys]::SendWait('%n');[Windows.Forms.SendKeys]::SendWait('"one & first.png" "two.png" "three.png"');[Windows.Forms.SendKeys]::SendWait('{ENTER}')
 Start-Sleep -Milliseconds 700
 $pending=(Find 'Pending files').FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)).Count
 if($pending -ne 3){throw "Batch $batch added $pending files instead of 3"}
 Invoke 'Convert images';WaitFinished ($batch*3)
 "PASS: native picker batch $batch converted all 3 files without clearing Finished"
}
Invoke 'Clear queue';Invoke 'Clear finished'
$palettes=@{Red='#B34040';Orange='#B37A40';Yellow='#B3A940';Green='#71B340';Blue='#4066B3';Purple='#8D40B3'}
$hovers=@{Red='#9D3131';Orange='#9D6731';Yellow='#9D9431';Green='#669D31';Blue='#31559D';Purple='#79319D'}
foreach($light in @($false,$true)) {
 foreach($color in @('Red','Orange','Yellow','Green','Blue','Purple')) {
  Invoke 'Settings';SelectFormat 'Appearance mode' $(if($light){'Light'}else{'Dark'})
  (Find "$color color scheme").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select();Invoke 'Save'
  Invoke 'Settings'
  # GridView's selected border has a stock entrance transition; sample its settled state.
  Start-Sleep -Milliseconds 350
  $save=(Find 'Save').Current.BoundingRectangle
  [DesktopNative]::SetCursorPos(1300,200) | Out-Null;Start-Sleep -Milliseconds 150
  AssertPixel ($save.X+10) ($save.Y+$save.Height/2) $palettes[$color] 'Settings Save normal'
  $selected=(Find "$color color scheme").Current.BoundingRectangle
  AssertPixel ($selected.X+1) ($selected.Y+$selected.Height/2) $palettes[$color] 'Selected scheme border'
  [DesktopNative]::SetCursorPos([int]($save.X+$save.Width/2),[int]($save.Y+$save.Height/2)) | Out-Null
  [DesktopNative]::mouse_event(1,1,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 300
  AssertPixel ($save.X+10) ($save.Y+$save.Height/2) $hovers[$color] 'Settings Save hover'
  $bad=(Elements) | Where-Object {$_.Current.Name -like '*desktop palettes*'}
  if($bad){throw 'Removed palette explanation is still visible'}
  Screenshot "revision-$color-$light-settings"
  Invoke 'Cancel'
  $slider=Find 'Output quality'
  $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(60)
  $r=$slider.Current.BoundingRectangle
  if($r.IsEmpty -or $slider.Current.IsOffscreen){throw 'Quality slider must be visible for pixel checks'}
  [DesktopNative]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2)) | Out-Null
  [DesktopNative]::mouse_event(1,1,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 300
  AssertPixel ($r.X+50) ($r.Y+$r.Height/2) $hovers[$color] 'Quality slider hover track'
  "PASS: $color / light=$light Save normal/hover, selected palette border, and slider hover use Fluxa colors"
 }
}
Invoke 'Settings';SelectFormat 'Appearance mode' 'Dark'
(Find 'Blue color scheme').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
SelectFormat 'Context menu output format' 'JPEG  (.jpg)';Invoke 'Save'
$key=Get-Item 'HKCU:\Software\Classes\SystemFileAssociations\.png\shell\Fluxa.Convert'
if($key.GetValue('') -ne 'Convert to .jpg with Fluxa'){throw 'Saved context-menu label did not update'}
$manual=Join-Path $folder 'manual.png';$shell=Join-Path $folder 'shell.png'
function ShellAction($verb,$file) {
 $result=[ShellVerbTest]::ShellExecute([IntPtr]::Zero,$verb,$file,$null,$null,1)
 if($result.ToInt64() -le 32){throw "Shell verb failed: $verb / $result"}
 Start-Sleep -Milliseconds 600
}
$before=@(Get-ChildItem $output -Filter '*.jpg').Count
ShellAction 'Fluxa.Open' $manual
Find 'Remove manual.png' (Find 'Pending files') | Out-Null
if(@(Get-ChildItem $output -Filter '*.jpg').Count -ne $before){throw 'Open in Fluxa converted immediately'}
SelectFormat 'Output format' 'PNG  (.png)'
ShellAction 'Fluxa.Convert' $shell;WaitFinished 1
Find 'Remove manual.png' (Find 'Pending files') | Out-Null
if(@(Get-ChildItem $output -Filter '*.jpg').Count -ne ($before+1)){throw 'Explorer conversion did not use separate JPEG setting'}
if((Get-Process Fluxa).Count -ne 1){throw 'Shell routing created another Fluxa window'}
'ShellAction PASS: real ShellExecute verbs route to the existing instance; Open queues only; Convert uses JPEG and leaves manual files pending'
Invoke 'Settings';SelectFormat 'Context menu output format' 'PNG  (.png)';Invoke 'Save'
$toggleName='Show Fluxa in the image context menu'
Invoke 'Settings'
(Find $toggleName).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle();Invoke 'Save'
if(Test-Path 'HKCU:\Software\Classes\SystemFileAssociations\.png\shell\Fluxa.Convert'){throw 'Disabled Explorer commands are still registered'}
Invoke 'Settings'
(Find $toggleName).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle();Invoke 'Save'
if(!(Test-Path 'HKCU:\Software\Classes\SystemFileAssociations\.png\shell\Fluxa.Open\command')){throw 'Explorer commands were not restored'}
'PASS: settings update context format, disable removes owned verbs, enable restores them'
Invoke 'Clear queue';Invoke 'Clear finished'
Screenshot 'revision-final'
'PASS: final defaults restored to Blue/Dark, PNG workspace/context format, integration enabled'
