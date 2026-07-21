[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $OutputDirectory = 'Publish/ToUpload'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectDirectory = $PSScriptRoot
$projectPath = Join-Path $projectDirectory 'p3rpc.camfix.csproj'
$publishRoot = Join-Path $projectDirectory 'Publish'
$buildDirectory = Join-Path $publishRoot 'Build'
$toolsDirectory = Join-Path $publishRoot 'Tools/Reloaded-Tools'
$publisherPath = Join-Path $toolsDirectory 'Reloaded.Publisher.exe'
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $OutputDirectory))
$metadataFileName = 'p3rpc.camfix.ReleaseMetadata.json'
$metadataAssetFileName = "$metadataFileName.br"
$packageFileName = "P3R-CameraFix-$Version.7z"

function Remove-DirectoryIfPresent([string] $Path) {
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

Push-Location $projectDirectory
try {
    Remove-DirectoryIfPresent $buildDirectory
    Remove-DirectoryIfPresent $outputPath
    New-Item -Path $buildDirectory -ItemType Directory -Force | Out-Null
    New-Item -Path $outputPath -ItemType Directory -Force | Out-Null

    dotnet publish $projectPath `
        -c Release `
        --self-contained false `
        -o $buildDirectory `
        -p:OutputPath=$buildDirectory `
        -p:P3RCamFixSkipSync=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $builtConfigPath = Join-Path $buildDirectory 'ModConfig.json'
    $modConfig = Get-Content -LiteralPath $builtConfigPath -Raw | ConvertFrom-Json
    $modConfig.ModVersion = $Version
    $modConfig | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $builtConfigPath -Encoding utf8
    Get-ChildItem -LiteralPath $buildDirectory -Recurse -File -Include '*.pdb', '*.xml', '*.exe' |
        Remove-Item -Force

    if ($modConfig.ReleaseMetadataFileName -ne $metadataFileName) {
        throw "ModConfig.json must use ReleaseMetadataFileName '$metadataFileName'."
    }

    if (-not (Test-Path -LiteralPath $publisherPath)) {
        $toolsArchive = Join-Path $publishRoot 'Reloaded-Tools.zip'
        New-Item -Path $publishRoot -ItemType Directory -Force | Out-Null
        Invoke-WebRequest `
            -Uri 'https://github.com/Reloaded-Project/Reloaded-II/releases/latest/download/Tools.zip' `
            -OutFile $toolsArchive
        New-Item -Path $toolsDirectory -ItemType Directory -Force | Out-Null
        Expand-Archive -LiteralPath $toolsArchive -DestinationPath $toolsDirectory -Force
        Remove-Item -LiteralPath $toolsArchive -Force
    }

    & $publisherPath `
        --modfolder $buildDirectory `
        --packagename 'P3R-CameraFix-' `
        --outputfolder $outputPath `
        --publishtarget Default
    if ($LASTEXITCODE -ne 0) {
        throw "Reloaded.Publisher failed with exit code $LASTEXITCODE."
    }

    $packagePath = Join-Path $outputPath $packageFileName
    $metadataPath = Join-Path $outputPath $metadataAssetFileName
    if (-not (Test-Path -LiteralPath $packagePath)) {
        throw "Reloaded.Publisher did not create '$packageFileName'."
    }
    if (-not (Test-Path -LiteralPath $metadataPath)) {
        throw "Reloaded.Publisher did not create '$metadataAssetFileName'."
    }

    Write-Host "Reloaded-II release assets created in $outputPath"
    Write-Host " - $packageFileName"
    Write-Host " - $metadataAssetFileName"
}
finally {
    Pop-Location
}
