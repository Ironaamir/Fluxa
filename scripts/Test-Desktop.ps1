param([switch]$Inspect, [switch]$Resume, [switch]$FunctionsOnly)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DesktopNative {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint msg,UIntPtr w,IntPtr l);
}
'@
$process = Get-Process Fluxa | Select-Object -First 1
if (!$process) { throw 'Launch the published app first.' }
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
function Elements($root=$window) { $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) }
function Find($name,$root=$window) {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name)
 $deadline=(Get-Date).AddSeconds(4)
 do {
  $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
  if($element){break}
  Start-Sleep -Milliseconds 100
 } while ((Get-Date) -lt $deadline)
 if (!$element) { throw "Element not found: $name" }
 return $element
}
function Invoke($name,$root=$window) {
 $condition = [System.Windows.Automation.AndCondition]::new(
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name),
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))
 $e=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
 if(!$e){throw "Button not found: $name"}
 if ($e.Current.IsOffscreen) {
  $pattern=$null
  if($e.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern,[ref]$pattern)){$pattern.ScrollIntoView()}
 }
 $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Start-Sleep -Milliseconds 400
}
function Click($element) {
 $r=$element.Current.BoundingRectangle
 if($r.IsEmpty){throw 'Cannot click an invisible element'}
 [DesktopNative]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2)) | Out-Null
 [DesktopNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
 [DesktopNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 400
}
function FocusDialog($name) {
 $deadline=(Get-Date).AddSeconds(5)
 do {
  $d=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
  if($d){break}; Start-Sleep -Milliseconds 100
 } while ((Get-Date) -lt $deadline)
 if(!$d){throw "Dialog not found: $name"}
 $r=$d.Current.BoundingRectangle
 # A physical title-bar click reliably focuses the native picker even when another app has focus.
 [DesktopNative]::SetCursorPos([int]($r.X+200),[int]($r.Y+15)) | Out-Null
 [DesktopNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
 [DesktopNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 300
}
function AddImage($path) {
 Invoke 'Browse images'
 FocusDialog 'Open'
 [System.Windows.Forms.SendKeys]::SendWait('%n')
 [System.Windows.Forms.SendKeys]::SendWait($path)
 [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
 Start-Sleep -Seconds 1
}
function Screenshot($name) {
 $r=$window.Current.BoundingRectangle
 $bitmap=[System.Drawing.Bitmap]::new([int]$r.Width,[int]$r.Height)
 $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
 try {$graphics.CopyFromScreen([int]$r.X,[int]$r.Y,0,0,$bitmap.Size); $bitmap.Save((Join-Path $PSScriptRoot "../artifacts/$name.png"))}
 finally {$graphics.Dispose();$bitmap.Dispose()}
}
function Dump($root=$window) { foreach($e in (Elements $root)){if($e.Current.Name){'{0} | {1} | enabled={2} | {3}' -f $e.Current.ControlType.ProgrammaticName,$e.Current.Name,$e.Current.IsEnabled,$e.Current.BoundingRectangle}} }
if($FunctionsOnly){return}
if($Inspect){Dump;return}
[DesktopNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
if(!$Resume) {
Invoke 'Clear queue'
Screenshot 'app-images'
foreach($mode in @('Video','Audio','PDF')) {
 Invoke "$mode mode"
 $expected="$mode conversion is on the way"
 Find $expected | Out-Null
 "PASS: $mode navigation"
}
Invoke 'Images mode'
$testInput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Fluxa.App/Assets/AppIcon.png'))
$testOutput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/UIOutput'))
New-Item -ItemType Directory -Force $testOutput | Out-Null
AddImage $testInput
Find 'Remove AppIcon.png' | Out-Null
'PASS: native file picker added a real image'
Invoke 'Choose folder'
FocusDialog 'Select Folder'
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait('%d')
[System.Windows.Forms.SendKeys]::SendWait($testOutput)
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Milliseconds 600
$dialog=Find 'Select Folder'
Click (Find 'Select Folder' $dialog)
Start-Sleep -Milliseconds 800
}
$testOutput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/UIOutput'))
if($Resume){
 $testInput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Fluxa.App/Assets/AppIcon.png'))
 $dialog=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Select Folder'))
 if($dialog){Click (Find 'Select Folder' $dialog);Start-Sleep -Milliseconds 800}
}
$actualFolder = (Find 'Output folder').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
if($actualFolder -ne $testOutput){throw "Output folder picker did not select test directory: $actualFolder"}
'PASS: native output folder picker'
$format=Find 'Output format'
$format.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep -Milliseconds 400
(Find 'JPEG  (.jpg)').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 300
if(!(Find 'Output quality').Current.IsEnabled){throw 'JPEG quality slider should be enabled'}
$before=@(Get-ChildItem $testOutput -Filter '*.jpg').Count
Invoke 'Convert images'
$deadline=(Get-Date).AddSeconds(15)
while(@(Get-ChildItem $testOutput -Filter '*.jpg').Count -eq $before -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 200}
if(@(Get-ChildItem $testOutput -Filter '*.jpg').Count -ne ($before+1)){throw 'UI conversion did not produce a JPEG'}
Find 'Batch complete' | Out-Null
if ((Find 'Pending files').FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)).Count -ne 0) { throw 'Successful item remained in pending queue' }
Find 'Remove AppIcon.png' (Find 'Finished files') | Out-Null
Invoke 'Clear queue'
Find 'Remove AppIcon.png' (Find 'Finished files') | Out-Null
'PASS: conversion moves the file to Finished; Clear queue preserves finished files'
# Add a second input while the completed item remains separate.
$secondInput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/queue-second.png'))
Copy-Item $testInput $secondInput -Force
AddImage $secondInput
Find 'Remove queue-second.png' (Find 'Pending files') | Out-Null
Find 'Remove AppIcon.png' (Find 'Finished files') | Out-Null
Invoke 'Clear finished'
Find 'Remove queue-second.png' (Find 'Pending files') | Out-Null
$pendingY = (Find 'Remove queue-second.png').Current.BoundingRectangle.Y
if ($pendingY -ge (Find 'Image drop zone').Current.BoundingRectangle.Y) { throw 'Queue must precede the import area' }
'PASS: pending queue is above imports; Clear finished preserves pending files'
'PASS: output dropdown, quality state, and real UI conversion'
Screenshot 'app-converted'
Invoke 'Settings'
Start-Sleep -Milliseconds 300
Find 'Settings & supported formats' | Out-Null
Find 'Keep running in the system tray when I close the window' | Out-Null
Invoke 'Cancel'
'PASS: native settings dialog'
Invoke 'Clear queue'
Invoke 'Clear finished'
'PASS: clear queue and finished independently'
Screenshot 'app-images'

