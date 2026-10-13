$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'dist/Fluxa.exe'
if (!(Test-Path $exe)) {
    throw 'Published app not found. See README.md for the dotnet publish command.'
}
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
