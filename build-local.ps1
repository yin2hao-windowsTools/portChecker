param(
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [switch] $SelfContained
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repositoryRoot "PortChecker\PortChecker.csproj"
$stagingPath = Join-Path $repositoryRoot "artifacts-local\build-local"
$outputPath = Join-Path $repositoryRoot "output"

function Reset-Directory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

$selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()

Reset-Directory -Path $stagingPath

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $selfContainedValue `
    --output $stagingPath `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Reset-Directory -Path $outputPath
Get-ChildItem -LiteralPath $stagingPath -Force | Copy-Item -Destination $outputPath -Recurse -Force

Write-Host "Build output copied to: $outputPath"
