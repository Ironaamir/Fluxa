$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form=[System.Windows.Forms.Form]::new()
$form.Text='Fluxa Drag Test'
$form.StartPosition='Manual'
$form.Location=[System.Drawing.Point]::new(1450,350)
$form.Size=[System.Drawing.Size]::new(300,140)
$form.TopMost=$true
$button=[System.Windows.Forms.Button]::new()
$button.Text='Drag test image'
$button.Dock='Fill'
$button.Add_MouseDown({
 $path=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Fluxa.App/Assets/AppIcon.png'))
 $data=[System.Windows.Forms.DataObject]::new()
 $data.SetData([System.Windows.Forms.DataFormats]::FileDrop,[string[]]@($path))
 $button.DoDragDrop($data,[System.Windows.Forms.DragDropEffects]::Copy) | Out-Null
})
$form.Controls.Add($button)
[System.Windows.Forms.Application]::Run($form)
