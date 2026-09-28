# Run Daxis from source:            .\run.ps1
# Build a single-file executable:   .\run.ps1 -Publish   (output in dist\)
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'Daxis needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0'
}
if ($Publish) {
    dotnet publish src/Daxis.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
    Write-Host 'Built: dist\Daxis.exe'
} else {
    dotnet run --project src/Daxis.App -c Release
}
