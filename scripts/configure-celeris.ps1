[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "OmniSpot\celeris-key.json"),
    [Security.SecureString]$ApiKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -eq $ApiKey) {
    $ApiKey = Read-Host "Celeris API anahtarini girin" -AsSecureString
}

try {
    if ($ApiKey.Length -eq 0) { throw "API anahtari bos olamaz." }
    $directory = Split-Path -Parent $ConfigPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $payload = [ordered]@{
        version = 1
        apiKey = ConvertFrom-SecureString -SecureString $ApiKey
        updatedAt = (Get-Date).ToString("o")
    }
    $temporaryPath = Join-Path $directory ([IO.Path]::GetRandomFileName())
    try {
        [IO.File]::WriteAllText($temporaryPath, ($payload | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
        if ($null -eq $identity) { throw "Windows kullanicisi belirlenemedi." }
        $acl = [Security.AccessControl.FileSecurity]::new()
        $acl.SetOwner($identity)
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $identity, [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $temporaryPath -AclObject $acl
        Move-Item -LiteralPath $temporaryPath -Destination $ConfigPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
    Write-Host "Celeris anahtari Windows DPAPI ile kaydedildi: $ConfigPath"
}
finally {
    $ApiKey.Dispose()
}
