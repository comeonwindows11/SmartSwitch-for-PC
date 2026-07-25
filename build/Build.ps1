[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

Push-Location $projectRoot
try {
    & dotnet restore SmartSwitch.sln
    if ($LASTEXITCODE -ne 0) {
        throw "La restauration NuGet a échoué."
    }

    & dotnet build SmartSwitch.sln --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "La compilation a échoué."
    }

    & dotnet test tests/SmartSwitch.Core.Tests/SmartSwitch.Core.Tests.csproj `
        --configuration $Configuration `
        --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "Les tests ont échoué."
    }
}
finally {
    Pop-Location
}
