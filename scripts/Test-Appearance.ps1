$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Test-Desktop.ps1') -FunctionsOnly
function HexAt($x,$y) {
 $b=[System.Drawing.Bitmap]::new(1,1);$g=[System.Drawing.Graphics]::FromImage($b)
 try { $g.CopyFromScreen([int]$x,[int]$y,0,0,$b.Size);$c=$b.GetPixel(0,0); return ('#{0:X2}{1:X2}{2:X2}' -f $c.R,$c.G,$c.B) }
 finally {$g.Dispose();$b.Dispose()}
}
function AssertPixel($x,$y,$expected,$label) {
 $actual=HexAt $x $y
 if($actual -ne $expected){throw "$label pixel: expected $expected, got $actual"}
}
function SelectAppearance($light,$color,$save=$true) {
 Invoke 'Settings'
 $appearance=Find 'Appearance mode'
 $appearance.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 Start-Sleep -Milliseconds 150
 $item=if($light){'Light'}else{'Dark'}
 (Find $item).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 (Find "$color color scheme").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 if($save){Invoke 'Save'}else{Invoke 'Cancel'}
 Start-Sleep -Milliseconds 200
}
function CheckSavedSelection($light,$color) {
 Invoke 'Settings'
 $selected=(Find "$color color scheme").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
 if(!$selected){throw "Saved color is not $color"}
 $a=(Find 'Appearance mode').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
 $expected=if($light){'Light'}else{'Dark'}
 if($a[0].Current.Name -ne $expected){throw 'Saved appearance mode mismatch'}
 Invoke 'Cancel'
}
foreach($pair in @(@('Conversion modes','Images mode'),@('Compression modes','Image compression mode'))) {
 $header=Find $pair[0]
 if($header.Current.ItemStatus -eq 'Collapsed'){Invoke $pair[0]}
 Invoke $pair[0]
 if((Find $pair[0]).Current.ItemStatus -ne 'Collapsed'){throw 'Group did not collapse'}
 $e=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$pair[1]))
 if($e -and !$e.Current.IsOffscreen){throw 'Collapsed group still exposes visible mode buttons'}
 Invoke $pair[0]
 Find $pair[1] | Out-Null
 "PASS: $($pair[0]) collapses and expands independently"
}
foreach($mode in @('Image','Video','Audio','PDF')) {
 Invoke "$mode compression mode"
 $label=if($mode -eq 'Image'){'Images'}else{$mode}
 Find "$label compression is on the way" | Out-Null
 if((Find "$mode compression mode").Current.ItemStatus -ne 'Current mode'){throw 'Compression mode selection missing'}
 "PASS: $mode compression navigation is an honest future workspace"
}
Invoke 'Images mode'
Invoke 'Clear queue'
AddImage ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Fluxa.App/Assets/AppIcon.png')))
$palettes=@{Red='#B34040';Orange='#B37A40';Yellow='#B3A940';Green='#71B340';Blue='#4066B3';Purple='#8D40B3'}
foreach($light in @($false,$true)) {
 foreach($color in @('Red','Orange','Yellow','Green','Blue','Purple')) {
  SelectAppearance $light $color
  $r=$window.Current.BoundingRectangle
  AssertPixel ($r.Right-20) ($r.Top+150) $(if($light){'#F0F2EF'}else{'#1C1D1C'}) 'Workspace'
  AssertPixel ($r.Left+12) ($r.Top+150) $(if($light){'#E3E7E4'}else{'#292B2A'}) 'Sidebar'
  $buttonCondition=[System.Windows.Automation.AndCondition]::new(
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Convert images'),
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))
  $button=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$buttonCondition).Current.BoundingRectangle
  AssertPixel ($button.X+12) ($button.Y+$button.Height/2) $palettes[$color] 'Accent button'
  CheckSavedSelection $light $color
  "PASS: $color / light=$light applies exact backgrounds and accent; settings reopen correctly"
 }
 Screenshot $(if($light){'fluxa-light'}else{'fluxa-dark'})
}
# Cancel must not apply changes.
SelectAppearance $false 'Blue' $false
CheckSavedSelection $true 'Purple'
'PASS: Cancel leaves the saved appearance unchanged'
SelectAppearance $false 'Blue'
# Capture the exposed empty part of each button throughout hover, not just after settling.
foreach($name in @('Video mode','Image compression mode','Settings')) {
 # Reacquire real foreground focus after dismissing a WinUI popup before injecting pointer input.
 $appRect=$window.Current.BoundingRectangle
 [DesktopNative]::SetCursorPos([int]($appRect.Left+400),[int]($appRect.Top+15)) | Out-Null
 [DesktopNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
 [DesktopNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 300
 $r=(Find $name).Current.BoundingRectangle
 [DesktopNative]::SetCursorPos([int]($r.Right+70),[int]($r.Y+10)) | Out-Null
 Start-Sleep -Milliseconds 100
 [DesktopNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
 [DesktopNative]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2)) | Out-Null
 # Inject actual mouse movement as well as moving the cursor; WinUI input can coalesce SetCursorPos alone.
 [DesktopNative]::mouse_event(1,1,0,0,[UIntPtr]::Zero)
 $seen=@()
 for($i=0;$i -lt 14;$i++) { $seen+=HexAt ($r.Right-20) ($r.Y+$r.Height/2);Start-Sleep -Milliseconds 8 }
 if(@($seen | Where-Object {$_ -notin @('#292B2A','#3B3C3C')}).Count -gt 0){throw "Unexpected intermediate hover color on ${name}: $($seen -join ',')"}
 if($seen[-1] -ne '#3B3C3C'){throw "Incorrect hover color on ${name}: $($seen -join ',')"}
 "PASS: $name hover settles directly to #3B3C3C; sampled colors: $(($seen | Select-Object -Unique) -join ',')"
}
[DesktopNative]::SetCursorPos(1300,950) | Out-Null
Invoke 'Clear queue'
Invoke 'Clear finished'
$bad=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/corrupt-ui-test.png'))
[IO.File]::WriteAllText($bad,'not a real PNG')
AddImage $bad
Invoke 'Convert images'
Start-Sleep -Milliseconds 600
Find 'Remove corrupt-ui-test.png' (Find 'Pending files') | Out-Null
$errors=(Elements (Find 'Pending files')) | Where-Object {$_.Current.Name -like 'Failed*'}
if(!$errors){throw 'Failed conversion was not kept retryable in queue'}
$retryButton=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$buttonCondition)
if(!$retryButton.Current.IsEnabled){throw 'Failed conversion cannot be retried'}
'PASS: failed conversion remains in pending files and is retryable'
Invoke 'Clear queue'
Invoke 'Compression modes'
$settingsPath=Join-Path $env:LOCALAPPDATA 'Fluxa/settings.json'
$saved=Get-Content $settingsPath -Raw | ConvertFrom-Json
if($saved.AccentColor -ne 'Blue' -or $saved.LightMode -or $saved.CompressionExpanded){throw 'Appearance/group persistence did not save to Fluxa settings'}
Invoke 'Compression modes'
Screenshot 'fluxa-dark'
'PASS: final preferences are Blue/Dark with both mode groups expanded'
