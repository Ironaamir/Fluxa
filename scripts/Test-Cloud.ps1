$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Test-Desktop.ps1') -FunctionsOnly
[DesktopNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
foreach($name in @('Import from Google Drive','Import from Dropbox')) {
 if(!(Find $name).Current.IsEnabled){throw "$name is disabled"}
}
Invoke 'Import from Google Drive'
Find 'Settings & supported formats' | Out-Null
$google=Find 'Google Drive OAuth client ID';$dropbox=Find 'Dropbox app key'
if($google.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '' -or $dropbox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne ''){throw 'Cloud smoke check requires unconfigured accounts; do not overwrite user identifiers'}
Find 'Cloud connections' | Out-Null
$redirect=Find 'Dropbox OAuth redirect URI'
if($redirect.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'http://127.0.0.1:53682/fluxa/oauth/'){throw 'Dropbox redirect mismatch'}
Invoke 'Cancel'
Find 'Cloud app setup required' | Out-Null
'PASS: cloud import buttons enabled; missing OAuth app routes to settings with an honest setup warning'
# Temporary public identifiers only; no token/password is supplied and Connect is not clicked.
Invoke 'Settings'
(Find 'Google Drive OAuth client ID').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('native-ui-test.apps.googleusercontent.com')
(Find 'Dropbox app key').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('native-ui-test')
Invoke 'Save'
try {
 foreach($name in @('Google Drive','Dropbox')) {
  Invoke "Import from $name"
  $dialog=Find "$name import"
  Find 'Cloud files' $dialog | Out-Null
  if((Find 'Disconnect' $dialog).Current.IsEnabled){throw 'Unconnected account appears connected'}
  if((Find 'Import selected' $dialog).Current.IsEnabled){throw 'Unconnected import is enabled'}
  Find 'Connect' $dialog | Out-Null
  Find 'Load more' $dialog | Out-Null
  Find 'Open folder' $dialog | Out-Null
  Invoke 'Cancel cloud operation' $dialog
  Find 'Cancelled. Close and reopen the import browser to try again.' $dialog | Out-Null
  Screenshot $(if($name -eq 'Google Drive'){'cloud-google-browser'}else{'cloud-dropbox-browser'})
  Invoke 'Close' $dialog
  "PASS: $name native browser exposes Connect, paging/navigation, disabled import until sign-in, and cancellation"
 }
} finally {
 Invoke 'Settings'
 (Find 'Google Drive OAuth client ID').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('')
 (Find 'Dropbox app key').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('')
 Invoke 'Save'
}
Screenshot 'cloud-final-workspace'
'PASS: temporary app identifiers removed; no accounts/tokens were fabricated'
'BLOCKED: real provider login, account folder listing, refresh/revocation and image download require the user OAuth app identifiers and consent'
