param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Uninstall')]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$ServiceExePath
)

$ErrorActionPreference = 'Stop'

$serviceName = 'OmniSpotChangeFeed'
$serviceDisplayName = 'OmniSpot Değişiklik Akışı'
$pipeName = 'OmniSpot.ChangeFeed'
$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$scPath = Join-Path $env:SystemRoot 'System32\sc.exe'
$expectedExePath = [System.IO.Path]::GetFullPath($ServiceExePath)

function Get-ServiceImagePath {
    $imagePath = (Get-ItemProperty -LiteralPath $serviceRegistryPath -Name ImagePath).ImagePath
    return [Environment]::ExpandEnvironmentVariables([string]$imagePath).Trim()
}

function Assert-OwnedService {
    $actualImagePath = Get-ServiceImagePath
    $expectedImagePath = '"{0}"' -f $expectedExePath
    if (-not $actualImagePath.Equals($expectedImagePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Mevcut $serviceName servisi bu installer'a ait değil: $actualImagePath"
    }
}

function Wait-ServiceState([string]$State, [int]$TimeoutSeconds) {
    $controller = Get-Service -Name $serviceName -ErrorAction Stop
    try {
        $targetState = [System.Enum]::Parse(
            [System.ServiceProcess.ServiceControllerStatus],
            $State)
        $controller.WaitForStatus(
            $targetState,
            [TimeSpan]::FromSeconds($TimeoutSeconds))
    }
    finally {
        $controller.Dispose()
    }
}

function Remove-OwnedService {
    if (-not (Test-Path -LiteralPath $serviceRegistryPath)) {
        return
    }

    Assert-OwnedService

    $controller = Get-Service -Name $serviceName -ErrorAction Stop
    try {
        if ($controller.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $serviceName -Force -ErrorAction Stop
            Wait-ServiceState -State 'Stopped' -TimeoutSeconds 20
        }
    }
    finally {
        $controller.Dispose()
    }

    & $scPath delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Servis silinemedi. sc.exe çıkış kodu: $LASTEXITCODE"
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ((Test-Path -LiteralPath $serviceRegistryPath) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    if (Test-Path -LiteralPath $serviceRegistryPath) {
        throw 'Servis silme zaman aşımına uğradı.'
    }
}

function Wait-PipeReady([int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $pipe = $null
        try {
            $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
                '.',
                $pipeName,
                [System.IO.Pipes.PipeDirection]::InOut,
                [System.IO.Pipes.PipeOptions]::None)
            $pipe.Connect(500)
            return
        }
        catch [System.TimeoutException] {
        }
        catch [System.IO.IOException] {
        }
        finally {
            if ($null -ne $pipe) {
                $pipe.Dispose()
            }
        }

        Start-Sleep -Milliseconds 250
    }
    while ([DateTime]::UtcNow -lt $deadline)

    throw "Servis çalışıyor ancak $pipeName pipe'ı hazır olmadı."
}

function Install-Service {
    if (-not (Test-Path -LiteralPath $expectedExePath -PathType Leaf)) {
        throw "Servis EXE'si bulunamadı: $expectedExePath"
    }
    if (Test-Path -LiteralPath $serviceRegistryPath) {
        throw "$serviceName adlı servis zaten mevcut. Kurulum güvenli biçimde durduruldu."
    }

    $created = $false
    try {
        New-Service `
            -Name $serviceName `
            -BinaryPathName ('"{0}"' -f $expectedExePath) `
            -DisplayName $serviceDisplayName `
            -StartupType Automatic | Out-Null
        $created = $true

        & $scPath description $serviceName 'OmniSpot kapalıyken NTFS değişiklik akışını güvenli biçimde izler.' | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Servis açıklaması ayarlanamadı. sc.exe çıkış kodu: $LASTEXITCODE"
        }

        & $scPath sidtype $serviceName unrestricted | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Servis SID türü ayarlanamadı. sc.exe çıkış kodu: $LASTEXITCODE"
        }

        Assert-OwnedService
        $serviceRecord = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'"
        if ($null -eq $serviceRecord -or
            $serviceRecord.StartName -ne 'LocalSystem' -or
            $serviceRecord.StartMode -ne 'Auto') {
            throw 'Servis LocalSystem/Automatic sözleşmesi doğrulanamadı.'
        }

        $sidType = (Get-ItemProperty -LiteralPath $serviceRegistryPath -Name ServiceSidType).ServiceSidType
        if ([int]$sidType -ne 1) {
            throw "Servis SID türü UNRESTRICTED değil: $sidType"
        }

        Start-Service -Name $serviceName -ErrorAction Stop
        Wait-ServiceState -State 'Running' -TimeoutSeconds 20
        Wait-PipeReady -TimeoutSeconds 20
    }
    catch {
        if ($created) {
            try {
                Remove-OwnedService
            }
            catch {
                Write-Warning "Kurulum geri alınırken servis temizlenemedi: $($_.Exception.Message)"
            }
        }
        throw
    }
}

try {
    if ($Action -eq 'Install') {
        Install-Service
    }
    else {
        Remove-OwnedService
    }
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}

exit 0
