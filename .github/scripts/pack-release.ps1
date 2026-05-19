param(
    [Parameter(Mandatory = $true)]
    [string] $ProjectPath,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $ProductVersion,

    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [string] $OutputRoot = "artifacts",
    [string] $WixVersion = "6.0.2"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$projectFullPath = Resolve-Path (Join-Path $repositoryRoot $ProjectPath)
$outputFullPath = Join-Path $repositoryRoot $OutputRoot
$publishRoot = Join-Path $outputFullPath "publish"
$distPath = Join-Path $outputFullPath "dist"
$wixWorkPath = Join-Path $outputFullPath "wix"
$assemblyVersion = "$ProductVersion.0"
$assetPrefix = "PortChecker-$Version-$Runtime"

if (Test-Path $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $distPath, $publishRoot, $wixWorkPath | Out-Null

function Invoke-DotNetPublish {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PublishPath,

        [Parameter(Mandatory = $true)]
        [bool] $SingleFile
    )

    $singleFileValue = $SingleFile.ToString().ToLowerInvariant()
    dotnet publish $projectFullPath `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $PublishPath `
        -p:Version=$Version `
        -p:AssemblyVersion=$assemblyVersion `
        -p:FileVersion=$assemblyVersion `
        -p:InformationalVersion=$Version `
        -p:PublishSingleFile=$singleFileValue `
        -p:PublishReadyToRun=true `
        -p:IncludeNativeLibrariesForSelfExtract=$singleFileValue `
        -p:DebugType=none `
        -p:DebugSymbols=false
}

function Get-WixCommand {
    $toolPath = Join-Path $outputFullPath ".tools"
    $wixPath = Join-Path $toolPath "wix.exe"
    if (-not (Test-Path $wixPath)) {
        dotnet tool install wix --tool-path $toolPath --version $WixVersion | Out-Host
    }

    return $wixPath
}

function Get-StableGuid {
    param([Parameter(Mandatory = $true)][string] $Value)

    $namespace = [Guid] "7f147a07-3fa3-4d04-bf1f-78788df1f3c7"
    $namespaceBytes = $namespace.ToByteArray()
    $valueBytes = [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
    $bytesToHash = [byte[]]::new($namespaceBytes.Length + $valueBytes.Length)
    [Array]::Copy($namespaceBytes, 0, $bytesToHash, 0, $namespaceBytes.Length)
    [Array]::Copy($valueBytes, 0, $bytesToHash, $namespaceBytes.Length, $valueBytes.Length)

    $sha1 = [Security.Cryptography.SHA1]::Create()
    try {
        $hash = $sha1.ComputeHash($bytesToHash)
    }
    finally {
        $sha1.Dispose()
    }

    $guidBytes = [byte[]]::new(16)
    [Array]::Copy($hash, $guidBytes, 16)
    $guidBytes[6] = ($guidBytes[6] -band 0x0f) -bor 0x50
    $guidBytes[8] = ($guidBytes[8] -band 0x3f) -bor 0x80

    return ([Guid]::new($guidBytes)).ToString("B").ToUpperInvariant()
}

function Get-WixId {
    param(
        [Parameter(Mandatory = $true)][string] $Prefix,
        [Parameter(Mandatory = $true)][string] $Value
    )

    $sha1 = [Security.Cryptography.SHA1]::Create()
    try {
        $hash = $sha1.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant()))
    }
    finally {
        $sha1.Dispose()
    }

    $hex = -join ($hash[0..11] | ForEach-Object { $_.ToString("x2") })
    return "$Prefix$hex"
}

function Write-DirectoryNode {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlWriter] $Writer,

        [Parameter(Mandatory = $true)]
        [IO.DirectoryInfo] $Directory,

        [Parameter(Mandatory = $true)]
        [string] $SourceRoot,

        [AllowEmptyCollection()]
        [Collections.Generic.List[string]] $ComponentIds
    )

    $files = Get-ChildItem -LiteralPath $Directory.FullName -File | Sort-Object FullName
    foreach ($file in $files) {
        $relativePath = [IO.Path]::GetRelativePath($SourceRoot, $file.FullName)
        $componentId = Get-WixId "cmp" $relativePath
        $fileId = Get-WixId "fil" $relativePath

        $Writer.WriteStartElement("Component")
        $Writer.WriteAttributeString("Id", $componentId)
        $Writer.WriteAttributeString("Guid", (Get-StableGuid $relativePath))

        $Writer.WriteStartElement("File")
        $Writer.WriteAttributeString("Id", $fileId)
        $Writer.WriteAttributeString("Source", $file.FullName)
        $Writer.WriteAttributeString("KeyPath", "yes")
        $Writer.WriteEndElement()

        $Writer.WriteEndElement()
        $ComponentIds.Add($componentId)
    }

    $directories = Get-ChildItem -LiteralPath $Directory.FullName -Directory | Sort-Object FullName
    foreach ($childDirectory in $directories) {
        $relativePath = [IO.Path]::GetRelativePath($SourceRoot, $childDirectory.FullName)
        $directoryId = Get-WixId "dir" $relativePath

        $Writer.WriteStartElement("Directory")
        $Writer.WriteAttributeString("Id", $directoryId)
        $Writer.WriteAttributeString("Name", $childDirectory.Name)
        Write-DirectoryNode -Writer $Writer -Directory $childDirectory -SourceRoot $SourceRoot -ComponentIds $ComponentIds
        $Writer.WriteEndElement()
    }
}

function New-WixSource {
    param(
        [Parameter(Mandatory = $true)][string] $SourcePath,
        [Parameter(Mandatory = $true)][string] $DestinationPath
    )

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [Text.UTF8Encoding]::new($false)

    $componentIds = [Collections.Generic.List[string]]::new()
    $writer = [Xml.XmlWriter]::Create($DestinationPath, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement("Wix", "http://wixtoolset.org/schemas/v4/wxs")
        $writer.WriteStartElement("Package")
        $writer.WriteAttributeString("Name", "Port Checker")
        $writer.WriteAttributeString("Manufacturer", "PortChecker")
        $writer.WriteAttributeString("Version", $ProductVersion)
        $writer.WriteAttributeString("UpgradeCode", "{B84256A8-A3B4-4FB4-B445-4A42E7A84EEA}")
        $writer.WriteAttributeString("Scope", "perMachine")

        $writer.WriteStartElement("MajorUpgrade")
        $writer.WriteAttributeString("DowngradeErrorMessage", "A newer version of Port Checker is already installed.")
        $writer.WriteEndElement()

        $writer.WriteStartElement("MediaTemplate")
        $writer.WriteAttributeString("EmbedCab", "yes")
        $writer.WriteEndElement()

        $writer.WriteStartElement("StandardDirectory")
        $writer.WriteAttributeString("Id", "ProgramFiles64Folder")
        $writer.WriteStartElement("Directory")
        $writer.WriteAttributeString("Id", "INSTALLFOLDER")
        $writer.WriteAttributeString("Name", "Port Checker")
        Write-DirectoryNode -Writer $writer -Directory (Get-Item $SourcePath) -SourceRoot $SourcePath -ComponentIds $componentIds
        $writer.WriteEndElement()
        $writer.WriteEndElement()

        $writer.WriteStartElement("Feature")
        $writer.WriteAttributeString("Id", "MainFeature")
        $writer.WriteAttributeString("Title", "Port Checker")
        $writer.WriteAttributeString("Level", "1")
        foreach ($componentId in $componentIds) {
            $writer.WriteStartElement("ComponentRef")
            $writer.WriteAttributeString("Id", $componentId)
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()

        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }
}

$exePublishPath = Join-Path $publishRoot "exe"
$portablePublishPath = Join-Path $publishRoot "portable"
$msiPublishPath = Join-Path $publishRoot "msi"

Invoke-DotNetPublish -PublishPath $exePublishPath -SingleFile $true
Copy-Item -LiteralPath (Join-Path $exePublishPath "PortChecker.exe") -Destination (Join-Path $distPath "$assetPrefix.exe")

Invoke-DotNetPublish -PublishPath $portablePublishPath -SingleFile $false
New-Item -ItemType File -Force -Path (Join-Path $portablePublishPath ".portable") | Out-Null
$portableFiles = Get-ChildItem -LiteralPath $portablePublishPath -Force | ForEach-Object { $_.FullName }
Compress-Archive -LiteralPath $portableFiles -DestinationPath (Join-Path $distPath "$assetPrefix-portable.zip") -Force

Invoke-DotNetPublish -PublishPath $msiPublishPath -SingleFile $false
$wixSourcePath = Join-Path $wixWorkPath "PortChecker.wxs"
$msiPath = Join-Path $distPath "$assetPrefix.msi"
New-WixSource -SourcePath $msiPublishPath -DestinationPath $wixSourcePath
$wixCommand = Get-WixCommand
& $wixCommand build $wixSourcePath -arch x64 -o $msiPath
if ($LASTEXITCODE -ne 0) {
    throw "WiX build failed with exit code $LASTEXITCODE."
}

Get-ChildItem -LiteralPath $distPath -Filter "*.wixpdb" -File | Remove-Item -Force

Get-ChildItem -LiteralPath $distPath -File | Sort-Object Name | ForEach-Object {
    Write-Host "Created $($_.FullName)"
}
