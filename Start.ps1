$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'dist/Fluxa.exe'
if (!(Test-Path $exe)) {
    $project = Join-Path $PSScriptRoot 'src/Fluxa.App/Fluxa.App.csproj'
    $publisher = Join-Path $PSScriptRoot 'scripts/Publish.ps1'
    if (!(Test-Path $project) -or !(Test-Path $publisher)) {
        throw 'Fluxa runtime files are missing. Extract the entire portable Windows ZIP, keeping Start.ps1 and the dist folder together. The executable cannot run by itself.'
    }
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'This is a source download, not a ready-to-run app. Download and extract the portable Windows x64 ZIP from the release assets when available (not Source code.zip). To build this source download, install the .NET 8 SDK and Windows WinUI build tools. The original 0.1.0 GitHub release had no binary asset.'
    }
    Write-Host 'Source download detected. Building Fluxa once; this needs .NET 8 and Windows WinUI build tools.'
    & $publisher -NoArchive
    if (!(Test-Path $exe)) { throw 'The build did not produce a runnable Fluxa app. See the build error above.' }
}
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
