$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

try {
    dotnet restore .\ParaPhraser.sln
    if ($LASTEXITCODE -ne 0) { throw "Restore failed." }

    dotnet build .\ParaPhraser.sln --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    dotnet run `
        --project .\tests\ParaPhraser.Core.SmokeTests\ParaPhraser.Core.SmokeTests.csproj `
        --configuration Release `
        --no-build
    if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed." }
}
finally {
    Pop-Location
}
