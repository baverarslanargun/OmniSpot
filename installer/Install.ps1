<# 
    OmniSpot V1.0 - Basit Kurulum Script'i
    Bu script Inno Setup olmadan kurulum yapar
#>

param(
    [switch]$Uninstall,
    [switch]$Silent
)

$AppName = "OmniSpot"
$AppVersion = "1.0.0"
$InstallDir = "$env:LOCALAPPDATA\$AppName"
$SourceExe = Join-Path $PSScriptRoot "..\publish\OmniSpot.exe"
$SourceIcon = Join-Path $PSScriptRoot "..\SmartFileLauncher.UI\Resources\app.ico"

function Show-Message($msg) {
    if (-not $Silent) {
        Write-Host $msg -ForegroundColor Cyan
    }
}

function Install-OmniSpot {
    Show-Message "OmniSpot $AppVersion kurulumu başlıyor..."
    
    # Kurulum klasörü oluştur
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Show-Message "Kurulum klasörü oluşturuldu: $InstallDir"
    }
    
    # Exe kopyala
    if (Test-Path $SourceExe) {
        Copy-Item $SourceExe -Destination "$InstallDir\OmniSpot.exe" -Force
        Show-Message "Uygulama kopyalandı"
    } else {
        Write-Host "HATA: $SourceExe bulunamadı!" -ForegroundColor Red
        Write-Host "Önce 'dotnet publish' çalıştırın." -ForegroundColor Yellow
        return
    }
    
    # Icon kopyala
    if (Test-Path $SourceIcon) {
        Copy-Item $SourceIcon -Destination "$InstallDir\omnispot.ico" -Force
    }
    
    # Başlat menüsü kısayolu
    $StartMenuPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
    $ShortcutPath = "$StartMenuPath\$AppName.lnk"
    
    $WshShell = New-Object -ComObject WScript.Shell
    $Shortcut = $WshShell.CreateShortcut($ShortcutPath)
    $Shortcut.TargetPath = "$InstallDir\OmniSpot.exe"
    $Shortcut.WorkingDirectory = $InstallDir
    $Shortcut.IconLocation = "$InstallDir\omnispot.ico"
    $Shortcut.Description = "OmniSpot - Akıllı Dosya Tarayıcı"
    $Shortcut.Save()
    Show-Message "Başlat menüsü kısayolu oluşturuldu"
    
    # Masaüstü kısayolu (opsiyonel)
    if (-not $Silent) {
        $createDesktop = Read-Host "Masaüstü kısayolu oluşturulsun mu? (E/H)"
        if ($createDesktop -eq "E" -or $createDesktop -eq "e") {
            $DesktopPath = [Environment]::GetFolderPath("Desktop")
            $DesktopShortcut = $WshShell.CreateShortcut("$DesktopPath\$AppName.lnk")
            $DesktopShortcut.TargetPath = "$InstallDir\OmniSpot.exe"
            $DesktopShortcut.WorkingDirectory = $InstallDir
            $DesktopShortcut.IconLocation = "$InstallDir\omnispot.ico"
            $DesktopShortcut.Save()
            Show-Message "Masaüstü kısayolu oluşturuldu"
        }
    }
    
    # Uninstall bilgisi (Registry)
    $UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
    New-Item -Path $UninstallKey -Force | Out-Null
    Set-ItemProperty -Path $UninstallKey -Name "DisplayName" -Value $AppName
    Set-ItemProperty -Path $UninstallKey -Name "DisplayVersion" -Value $AppVersion
    Set-ItemProperty -Path $UninstallKey -Name "Publisher" -Value "OmniSpot"
    Set-ItemProperty -Path $UninstallKey -Name "InstallLocation" -Value $InstallDir
    Set-ItemProperty -Path $UninstallKey -Name "UninstallString" -Value "powershell -ExecutionPolicy Bypass -File `"$InstallDir\Uninstall.ps1`""
    Set-ItemProperty -Path $UninstallKey -Name "DisplayIcon" -Value "$InstallDir\omnispot.ico"
    Set-ItemProperty -Path $UninstallKey -Name "NoModify" -Value 1
    Set-ItemProperty -Path $UninstallKey -Name "NoRepair" -Value 1
    
    # Uninstall script kopyala
    $UninstallScript = @"
# OmniSpot Kaldırma Script'i
`$InstallDir = "$InstallDir"
`$AppName = "$AppName"

Write-Host "OmniSpot kaldırılıyor..." -ForegroundColor Cyan

# Uygulamayı kapat
Get-Process -Name "OmniSpot" -ErrorAction SilentlyContinue | Stop-Process -Force

# Dosyaları sil
Remove-Item -Path "`$InstallDir" -Recurse -Force -ErrorAction SilentlyContinue

# Kısayolları sil
Remove-Item "`$env:APPDATA\Microsoft\Windows\Start Menu\Programs\`$AppName.lnk" -Force -ErrorAction SilentlyContinue
Remove-Item "`$([Environment]::GetFolderPath('Desktop'))\`$AppName.lnk" -Force -ErrorAction SilentlyContinue

# Başlangıç kaydını sil
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name `$AppName -ErrorAction SilentlyContinue

# Registry kaydını sil
Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\`$AppName" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "OmniSpot kaldırıldı!" -ForegroundColor Green
Read-Host "Çıkmak için Enter'a basın"
"@
    
    $UninstallScript | Out-File -FilePath "$InstallDir\Uninstall.ps1" -Encoding UTF8
    
    Show-Message ""
    Show-Message "========================================="
    Show-Message "  OmniSpot $AppVersion kurulumu tamamlandı!"
    Show-Message "========================================="
    Show-Message ""
    Show-Message "Kurulum yeri: $InstallDir"
    Show-Message "Başlat menüsünden veya Ctrl+Space ile açabilirsiniz."
    Show-Message ""
    
    # Uygulamayı başlat
    if (-not $Silent) {
        $launch = Read-Host "Uygulama şimdi başlatılsın mı? (E/H)"
        if ($launch -eq "E" -or $launch -eq "e") {
            Start-Process "$InstallDir\OmniSpot.exe"
        }
    }
}

function Uninstall-OmniSpot {
    Show-Message "OmniSpot kaldırılıyor..."
    
    # Uygulamayı kapat
    Get-Process -Name "OmniSpot" -ErrorAction SilentlyContinue | Stop-Process -Force
    
    # Dosyaları sil
    if (Test-Path $InstallDir) {
        Remove-Item -Path $InstallDir -Recurse -Force
        Show-Message "Dosyalar silindi"
    }
    
    # Kısayolları sil
    Remove-Item "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\$AppName.lnk" -Force -ErrorAction SilentlyContinue
    Remove-Item "$([Environment]::GetFolderPath('Desktop'))\$AppName.lnk" -Force -ErrorAction SilentlyContinue
    
    # Başlangıç kaydını sil
    Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name $AppName -ErrorAction SilentlyContinue
    
    # Registry kaydını sil
    Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName" -Recurse -Force -ErrorAction SilentlyContinue
    
    Show-Message "OmniSpot kaldırıldı!"
}

# Ana işlem
if ($Uninstall) {
    Uninstall-OmniSpot
} else {
    Install-OmniSpot
}
