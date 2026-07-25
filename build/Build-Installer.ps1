[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts"))
$publishDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot "publish\$Runtime"))
$installerDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot "installer"))
$setupInputDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot "setup-input"))
$payloadArchive = Join-Path $setupInputDirectory "Payload.zip"

foreach ($path in @($publishDirectory, $installerDirectory, $setupInputDirectory)) {
    if (-not $path.StartsWith(
        $projectRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Chemin de sortie inattendu: $path"
    }
}

Push-Location $projectRoot
try {
    if (-not $SkipTests) {
        & dotnet test tests/SmartSwitch.Core.Tests/SmartSwitch.Core.Tests.csproj `
            --configuration $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "Les tests ont échoué; l'installateur n'a pas été créé."
        }
    }

    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    if (Test-Path -LiteralPath $installerDirectory) {
        Remove-Item -LiteralPath $installerDirectory -Recurse -Force
    }

    if (Test-Path -LiteralPath $setupInputDirectory) {
        Remove-Item -LiteralPath $setupInputDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $setupInputDirectory -Force | Out-Null

    & dotnet publish src/SmartSwitch.App/SmartSwitch.App.csproj `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $publishDirectory `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "La publication autonome de l'application a échoué."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDirectory,
        $payloadArchive,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    & dotnet publish src/SmartSwitch.Setup/SmartSwitch.Setup.csproj `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $installerDirectory `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        "-p:PayloadPath=$payloadArchive"
    if ($LASTEXITCODE -ne 0) {
        throw "La création de l'assistant d'installation a échoué."
    }

    $installer = Get-ChildItem -LiteralPath $installerDirectory `
        -Filter "SmartSwitch-Setup.exe" `
        -Recurse |
        Select-Object -First 1
    if ($null -eq $installer) {
        throw "Le build s'est terminé sans produire SmartSwitch-Setup.exe."
    }

    Write-Output "Installateur créé: $($installer.FullName)"
}
finally {
    Pop-Location
}
