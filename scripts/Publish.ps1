param(
    [switch]$NoArchive
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Building from source requires the .NET 8 SDK and Windows WinUI build tools. Otherwise download the portable Windows ZIP, not the GitHub source ZIP.' }
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
$artifacts = Join-Path $root 'artifacts'
$zip = Join-Path $artifacts "Fluxa-$version-win-x64-preview.zip"
if (!$NoArchive -and (Test-Path $zip)) { throw "Archive already exists: $zip. Rename it before creating a new preview." }
$stage = Join-Path $artifacts ('portable-' + [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $stage ("Fluxa-$version-win-x64")
$app = Join-Path $bundle 'dist'
New-Item -ItemType Directory -Path $app -Force | Out-Null
& dotnet publish (Join-Path $root 'src/Fluxa.App/Fluxa.App.csproj') -c Release -p:Platform=x64 --self-contained true -o $app '-p:DebugType=none' '-p:DebugSymbols=false' '-p:IncludeSourceRevisionInInformationalVersion=false'
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE). Existing distribution was not changed." }
foreach ($required in @('Fluxa.exe','Fluxa.dll','Fluxa.Core.dll','Fluxa.pri','App.xbf','MainWindow.xbf','Magick.Native-Q8-x64.dll','coreclr.dll')) {
    if (!(Test-Path (Join-Path $app $required))) { throw "Publish is missing required runtime file: $required" }
}
# Only explicit runtime publish output enters the package. Never copy the checkout, user settings or test artifacts.
$files = Get-ChildItem $app -Recurse -File
foreach ($file in $files) {
    # Both images are required runtime assets: Fluxa's icon and WinUI's bundled noise texture.
    $relative = $file.FullName.Substring($app.Length + 1)
    $runtimeImages = @('Assets\AppIcon.png', 'Microsoft.UI.Xaml\Assets\NoiseAsset_256x256_PNG.png')
    if ($file.Extension -in @('.pdb','.cs','.ps1','.log','.bin','.env','.png','.jpg','.jpeg') -and $relative -notin $runtimeImages) { throw "Unexpected non-runtime file: $relative" }
    if ($file.Name -match '(?i)(xunit|testhost|testplatform|Fluxa.Tests|settings\.json|crash|secret|credentials)') { throw "Private or test file in publish: $($file.Name)" }
}
# Check the app's own binary and text resources for this machine's profile path/account details.
$needles = @($env:USERPROFILE, ($env:USERPROFILE -replace '\\','/'), 'Ironaamir') | Where-Object { $_ }
foreach ($file in $files | Where-Object { $_.Name -match '^Fluxa\.' -or $_.Extension -in @('.json','.xml','.config') }) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $texts = @([Text.Encoding]::UTF8.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
    foreach ($needle in $needles) {
        foreach ($text in $texts) { if ($text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "Personal build information in $($file.Name); packaging stopped." } }
    }
}
Copy-Item (Join-Path $root 'Start.ps1') (Join-Path $bundle 'Start.ps1')
@"
Fluxa $version - portable Windows x64 preview

Extract the entire ZIP before launching. Run dist\Fluxa.exe, or right-click Start.ps1 and choose Run with PowerShell. Keep the entire dist folder together. Windows 10 build 19041 or newer is required (tested on Windows 11 x64).

No SDK or administrator rights are needed. Files are converted locally. For video conversion, install FFmpeg (e.g. 'winget install Gyan.FFmpeg') or place ffmpeg.exe in the dist folder. User preferences are saved locally on your device.
"@ | Set-Content (Join-Path $bundle 'READ-ME.txt') -Encoding UTF8
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Move-Item $dist (Join-Path $artifacts ('previous-dist-' + [guid]::NewGuid().ToString('N'))) }
Copy-Item $app $dist -Recurse
if (!$NoArchive) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content ($zip + '.sha256') -Encoding ASCII
    Write-Output "Portable preview: $zip"
    Write-Output "SHA256: $hash"
}
Write-Output "Clean runtime: $dist"
