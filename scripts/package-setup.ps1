param(
    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $repositoryRoot "artifacts\setup"
$publishDirectory = Join-Path $outputDirectory "publish"
$releaseDirectory = Join-Path $outputDirectory "releases"

Push-Location $repositoryRoot

try {
    if (Test-Path $publishDirectory) {
        Remove-Item $publishDirectory -Recurse -Force
    }

    # Not single-file: Velopack replaces the app's whole folder on each update.
    dotnet publish `
        .\src\ParaPhraser.Desktop\ParaPhraser.Desktop.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:Version=$Version `
        --output $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    Remove-Item (Join-Path $publishDirectory "*.pdb")

    # The ID sets the install folder and must never change, or updates stop reaching
    # existing installs. It can't be ParaPhraser: logs already live in that folder.
    dotnet dnx --yes vpk@1.2.161 pack `
        --packId KrishPatel.ParaPhraser `
        --packVersion $Version `
        --packTitle ParaPhraser `
        --packAuthors "Krish Patel" `
        --packDir $publishDirectory `
        --mainExe ParaPhraser.exe `
        --icon .\src\ParaPhraser.Desktop\Assets\ParaPhraser.ico `
        --outputDir $releaseDirectory
    if ($LASTEXITCODE -ne 0) { throw "vpk could not pack the release." }

    # Only the files people download get friendly names. The app finds updates by the
    # .nupkg names in releases.win.json, so those must stay as vpk wrote them.
    Remove-Item (Join-Path $releaseDirectory "ParaPhraser-*")
    Rename-Item (Join-Path $releaseDirectory "KrishPatel.ParaPhraser-win-Setup.exe") "ParaPhraser-Setup-$Version.exe"
    Rename-Item (Join-Path $releaseDirectory "KrishPatel.ParaPhraser-win-Portable.zip") "ParaPhraser-Portable-$Version.zip"

    Write-Host "Created $releaseDirectory. See docs\packaging.md to publish it."
}
finally {
    Pop-Location
}
