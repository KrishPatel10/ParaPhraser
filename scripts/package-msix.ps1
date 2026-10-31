param(
    [string]$Version = "1.0.0.0",

    # Use the three identity values Partner Center shows after reserving the app name.
    [string]$IdentityName = "KrishPatel.ParaPhraser",
    [string]$Publisher = "CN=Krish Patel",
    [string]$PublisherDisplayName = "Krish Patel"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $repositoryRoot "artifacts\msix"
$layoutDirectory = Join-Path $outputDirectory "layout"
$packagePath = Join-Path $outputDirectory "ParaPhraser-$Version-x64.msix"

Push-Location $repositoryRoot

try {
    # A running copy locks its files, so deleting the folder would stop halfway.
    if (Get-Process ParaPhraser -ErrorAction SilentlyContinue | Where-Object Path -like "$layoutDirectory\*") {
        throw "Exit ParaPhraser first. It is running from $layoutDirectory."
    }

    if (Test-Path $layoutDirectory) {
        Remove-Item $layoutDirectory -Recurse -Force
    }

    # Not single-file: MSIX compresses and shares files itself, and a package
    # must not unpack itself at startup.
    dotnet publish `
        .\src\ParaPhraser.Desktop\ParaPhraser.Desktop.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $layoutDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    Remove-Item (Join-Path $layoutDirectory "*.pdb")
    Copy-Item .\packaging\Assets $layoutDirectory -Recurse

    $manifest = (Get-Content .\packaging\AppxManifest.xml -Raw).
        Replace('$IdentityName$', $IdentityName).
        Replace('$Publisher$', $Publisher).
        Replace('$PublisherDisplayName$', $PublisherDisplayName).
        Replace('$Version$', $Version)
    Set-Content (Join-Path $layoutDirectory "AppxManifest.xml") $manifest -Encoding utf8

    dotnet restore .\packaging\PackagingTools.proj --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Could not download the packaging tools." }

    $packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
    $makeAppx = Get-ChildItem (Join-Path $packagesRoot "microsoft.windows.sdk.buildtools") -Recurse -Filter makeappx.exe |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Select-Object -First 1
    if ($null -eq $makeAppx) { throw "makeappx.exe was not found." }

    & $makeAppx.FullName pack /d $layoutDirectory /p $packagePath /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx rejected the package." }

    Write-Host "Created $packagePath"
    Write-Host "Unsigned: upload it to Partner Center, or see docs\packaging.md to test it here."
}
finally {
    Pop-Location
}
