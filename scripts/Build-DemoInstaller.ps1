param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Version = '1.0.0',
    [string]$InnoCompilerPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\demo-installer'))
$expectedRoot = $repoRoot.TrimEnd('\') + '\artifacts\demo-installer'
if (-not $artifactRoot.Equals($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Beklenmeyen artifact yolu: $artifactRoot"
}

$uiOutput = Join-Path $artifactRoot 'ui'
$serviceOutput = Join-Path $artifactRoot 'service'
$bundleVerifier = Join-Path $PSScriptRoot 'Test-SingleFileBundle.ps1'
$installerScript = Join-Path $repoRoot 'installer\OmniSpotSetup.iss'

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $uiOutput, $serviceOutput -Force | Out-Null

function Invoke-Publish([string]$Project, [string]$Output) {
    & dotnet publish $Project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:UseSharedCompilation=false `
        -m:1 `
        -nodeReuse:false `
        --output $Output

    if ($LASTEXITCODE -ne 0) {
        throw "Publish başarısız: $Project"
    }
}

Invoke-Publish `
    (Join-Path $repoRoot 'SmartFileLauncher.UI\SmartFileLauncher.UI.csproj') `
    $uiOutput
Invoke-Publish `
    (Join-Path $repoRoot 'SmartFileLauncher.ChangeFeedService\SmartFileLauncher.ChangeFeedService.csproj') `
    $serviceOutput

$uiExe = Join-Path $uiOutput 'OmniSpot.exe'
$serviceExe = Join-Path $serviceOutput 'OmniSpot.ChangeFeedService.exe'
$uiBundle = & $bundleVerifier -Path $uiExe
$serviceBundle = & $bundleVerifier -Path $serviceExe

if ((Get-ChildItem -LiteralPath $uiOutput -File).Count -ne 1) {
    throw "UI publish klasörü yalnız OmniSpot.exe içermelidir: $uiOutput"
}
if ((Get-ChildItem -LiteralPath $serviceOutput -File).Count -ne 1) {
    throw "Servis publish klasörü yalnız OmniSpot.ChangeFeedService.exe içermelidir: $serviceOutput"
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $InnoCompilerPath = $candidates |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath) -or
    -not (Test-Path -LiteralPath $InnoCompilerPath)) {
    throw 'Inno Setup 6 derleyicisi bulunamadı. Inno Setup 6 kurulmalı veya -InnoCompilerPath verilmelidir.'
}

& $InnoCompilerPath `
    "/DMyAppVersion=$Version" `
    "/DUiPublishDir=$uiOutput" `
    "/DServicePublishDir=$serviceOutput" `
    $installerScript

if ($LASTEXITCODE -ne 0) {
    throw 'Inno Setup derlemesi başarısız.'
}

$installerPath = Join-Path $repoRoot "installer\output\OmniSpot-$Version-Demo-Setup.exe"
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Installer çıktısı bulunamadı: $installerPath"
}

$hash = Get-FileHash -LiteralPath $installerPath -Algorithm SHA256
$hashPath = "$installerPath.sha256"
Set-Content -LiteralPath $hashPath -Value "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($installerPath))" -Encoding Ascii

[pscustomobject]@{
    Installer = $installerPath
    Sha256 = $hash.Hash.ToLowerInvariant()
    UiBundleEntries = $uiBundle.EntryCount
    UiCompressedEntries = $uiBundle.CompressedEntryCount
    ServiceBundleEntries = $serviceBundle.EntryCount
    ServiceCompressedEntries = $serviceBundle.CompressedEntryCount
}
