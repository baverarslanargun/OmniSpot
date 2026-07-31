[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "OmniSpot\groq-keys.json"),
    [string]$ExecutablePath,
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Show-LauncherError {
    param([string]$Message)

    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show(
            $Message,
            "OmniSpot AI",
            [System.Windows.MessageBoxButton]::OK,
            [System.Windows.MessageBoxImage]::Error) | Out-Null
    }
    catch {
        Write-Error $Message
    }
}

function Restore-EnvironmentValue {
    param(
        [string]$Name,
        [bool]$HadValue,
        [string]$Value
    )

    if ($HadValue) {
        Set-Item -Path "Env:$Name" -Value $Value
    }
    else {
        Remove-Item -Path "Env:$Name" -ErrorAction SilentlyContinue
    }
}

$intentSecure = $null
$keywordSecure = $null
$intentPlain = $null
$keywordPlain = $null

try {
    if (-not (Test-Path -LiteralPath $ConfigPath)) {
        throw "AI anahtarlari yapilandirilmadi. scripts\configure-ai-shortcut.ps1 dosyasini bir kez calistirin."
    }

    $config = Get-Content -Raw -Encoding UTF8 -LiteralPath $ConfigPath | ConvertFrom-Json
    $propertyNames = @($config.PSObject.Properties.Name)
    if ($config.version -ne 1 -or
        $propertyNames -notcontains "intentApiKey" -or
        $propertyNames -notcontains "keywordApiKey") {
        throw "AI anahtar dosyasi gecersiz veya desteklenmeyen bir surumde."
    }

    $intentSecure = ConvertTo-SecureString ([string]$config.intentApiKey)
    $keywordSecure = ConvertTo-SecureString ([string]$config.keywordApiKey)
    $intentPlain = [Net.NetworkCredential]::new("", $intentSecure).Password
    $keywordPlain = [Net.NetworkCredential]::new("", $keywordSecure).Password

    if ([string]::IsNullOrWhiteSpace($intentPlain) -or
        [string]::IsNullOrWhiteSpace($keywordPlain)) {
        throw "Sifreli AI anahtarlari cozulemedi."
    }

    if ($ValidateOnly) {
        Write-Host "AI anahtar yapilandirmasi gecerli." -ForegroundColor Green
        return
    }

    if (Get-Process -Name "OmniSpot" -ErrorAction SilentlyContinue) {
        throw "OmniSpot zaten acik. Mevcut uygulamayi kapatip AI kisayolunu yeniden calistirin."
    }

    $repoRoot = Split-Path -Parent $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        $releaseExecutable = Join-Path $repoRoot "SmartFileLauncher.UI\bin\Release\net8.0-windows\win-x64\OmniSpot.exe"
        $publishedExecutable = Join-Path $repoRoot "publish\OmniSpot.exe"

        if (Test-Path -LiteralPath $releaseExecutable) {
            $ExecutablePath = $releaseExecutable
        }
        elseif (Test-Path -LiteralPath $publishedExecutable) {
            $ExecutablePath = $publishedExecutable
        }
        else {
            $solutionPath = Join-Path $repoRoot "SmartFileLauncher.sln"
            & dotnet build $solutionPath --configuration Release --nologo
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $releaseExecutable)) {
                throw "Release uygulamasi olusturulamadi."
            }
            $ExecutablePath = $releaseExecutable
        }
    }

    $ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "OmniSpot calistirilabilir dosyasi bulunamadi: $ExecutablePath"
    }

    $intentName = "OMNISPOT_GROQ_INTENT_API_KEY"
    $keywordName = "OMNISPOT_GROQ_KEYWORD_API_KEY"
    $hadIntent = Test-Path "Env:$intentName"
    $hadKeyword = Test-Path "Env:$keywordName"
    $previousIntent = if ($hadIntent) { (Get-Item "Env:$intentName").Value } else { "" }
    $previousKeyword = if ($hadKeyword) { (Get-Item "Env:$keywordName").Value } else { "" }

    try {
        Set-Item -Path "Env:$intentName" -Value $intentPlain
        Set-Item -Path "Env:$keywordName" -Value $keywordPlain
        Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path -Parent $ExecutablePath) | Out-Null
    }
    finally {
        Restore-EnvironmentValue $intentName $hadIntent $previousIntent
        Restore-EnvironmentValue $keywordName $hadKeyword $previousKeyword
    }
}
catch {
    if ($ValidateOnly) {
        Write-Error $_
    }
    else {
        Show-LauncherError $_.Exception.Message
    }
    exit 1
}
finally {
    $intentPlain = $null
    $keywordPlain = $null
    if ($null -ne $intentSecure) {
        $intentSecure.Dispose()
    }
    if ($null -ne $keywordSecure) {
        $keywordSecure.Dispose()
    }
}
