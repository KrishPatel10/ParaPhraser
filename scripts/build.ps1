$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

try {
    dotnet restore .\ParaPhraser.sln
    dotnet build .\ParaPhraser.sln --configuration Release --no-restore
    dotnet run `
        --project .\tests\ParaPhraser.Core.SmokeTests\ParaPhraser.Core.SmokeTests.csproj `
        --configuration Release `
        --no-build
}
finally {
    Pop-Location
}

