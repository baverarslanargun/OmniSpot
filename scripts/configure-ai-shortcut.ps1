[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ConfigPath = (Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "OmniSpot\groq-keys.json"),
    [string]$ShortcutPath = (Join-Path ([Environment]::GetFolderPath("Desktop")) "OmniSpot AI.lnk"),
    [switch]$SkipShortcut,
    [switch]$Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcherPath = Join-Path $PSScriptRoot "start-with-ai.ps1"

if ($Remove) {
    if (Test-Path -LiteralPath $ConfigPath) {
        if ($PSCmdlet.ShouldProcess($ConfigPath, "Sifreli AI anahtarlarini sil")) {
            Remove-Item -LiteralPath $ConfigPath -Force
        }
    }

    if (-not $SkipShortcut -and (Test-Path -LiteralPath $ShortcutPath)) {
        if ($PSCmdlet.ShouldProcess($ShortcutPath, "OmniSpot AI kisayolunu sil")) {
            Remove-Item -LiteralPath $ShortcutPath -Force
        }
    }

    Write-Host "Yerel AI yapilandirmasi kaldirildi." -ForegroundColor Green
    return
}

if (-not (Test-Path -LiteralPath $launcherPath)) {
    throw "Baslatici bulunamadi: $launcherPath"
}

$intentSecret = $null
$keywordSecret = $null

try {
    $intentSecret = Read-Host "Intent API anahtarini girin" -AsSecureString
    $keywordSecret = Read-Host "Keyword API anahtarini girin" -AsSecureString

    if ($intentSecret.Length -eq 0 -or $keywordSecret.Length -eq 0) {
        throw "API anahtarlari bos olamaz."
    }

    $configDirectory = Split-Path -Parent $ConfigPath
    New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null

    $payload = [ordered]@{
        version = 1
        intentApiKey = ConvertFrom-SecureString -SecureString $intentSecret
        keywordApiKey = ConvertFrom-SecureString -SecureString $keywordSecret
        updatedAt = (Get-Date).ToString("o")
    }

    $temporaryPath = Join-Path $configDirectory ([IO.Path]::GetRandomFileName())
    try {
        $json = $payload | ConvertTo-Json
        [IO.File]::WriteAllText(
            $temporaryPath,
            $json + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $ConfigPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $identity) {
        throw "Gecerli Windows kullanicisi belirlenemedi."
    }

    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetOwner($identity)
    $acl.SetAccessRuleProtection($true, $false)
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $identity,
        [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $ConfigPath -AclObject $acl

    if (-not $SkipShortcut) {
        $shortcutDirectory = Split-Path -Parent $ShortcutPath
        New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null

        $powershellPath = (Get-Command powershell.exe -ErrorAction Stop).Source
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $powershellPath
        $shortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $launcherPath + '"'
        $shortcut.WorkingDirectory = $repoRoot
        $shortcut.Description = "OmniSpot'u yerel sifreli AI anahtarlariyla baslatir"

        $iconPath = Join-Path $repoRoot "SmartFileLauncher.UI\bin\Release\net8.0-windows\win-x64\OmniSpot.exe"
        if (Test-Path -LiteralPath $iconPath) {
            $shortcut.IconLocation = $iconPath
        }

        $shortcut.Save()
    }

    Write-Host "AI anahtarlari Windows DPAPI ile sifrelendi." -ForegroundColor Green
    Write-Host "Yapilandirma: $ConfigPath"
    if (-not $SkipShortcut) {
        Write-Host "Kisayol: $ShortcutPath"
    }
}
finally {
    if ($null -ne $intentSecret) {
        $intentSecret.Dispose()
    }
    if ($null -ne $keywordSecret) {
        $keywordSecret.Dispose()
    }
}
