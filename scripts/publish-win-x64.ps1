$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $repositoryRoot "artifacts\win-x64"

Push-Location $repositoryRoot

try {
    dotnet publish `
        .\src\ParaPhraser.Desktop\ParaPhraser.Desktop.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        --output $outputDirectory

    Write-Host "Published ParaPhraser to $outputDirectory"
}
finally {
    Pop-Location
}

