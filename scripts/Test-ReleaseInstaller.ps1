param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$PreviousSetup
)

$ErrorActionPreference = 'Stop'
if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Kurulum testi yalnız geçici GitHub Windows makinesinde çalışır.'
}

$install = Join-Path $env:ProgramFiles 'OmniSpot'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}_is1'
$settingsDir = Join-Path $env:APPDATA 'OmniSpot'
$settings = Join-Path $settingsDir 'settings.json'
$corpus = Join-Path $env:RUNNER_TEMP 'omnispot-corpus'
$sample = Join-Path $corpus 'omnispot-yayin-ornegi.txt'
$service = 'OmniSpotChangeFeed'
if ((Test-Path $install) -or (Test-Path $settingsDir) -or (Get-Service $service -ErrorAction SilentlyContinue)) {
    throw 'Test makinesinde mevcut OmniSpot kurulumu veya kullanıcı ayarları bulundu.'
}

function Invoke-Setup([string]$Executable) {
    Write-Host "Çalıştırılan dosya: $Executable"
    $Executable = (Get-Item -LiteralPath $Executable -ErrorAction Stop).FullName
    $process = Start-Process -FilePath $Executable -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-' -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Kurulum işlemi başarısız: $Executable"
    }
}

function Uninstall-OmniSpot {
    $record = Get-ItemProperty -LiteralPath $uninstallKey
    $uninstaller = $record.UninstallString.Trim('"')
    if ([IO.Path]::GetDirectoryName($uninstaller) -ne $install) { throw "Beklenmeyen kaldırıcı: $uninstaller" }
    Invoke-Setup $uninstaller
    if (Test-Path $uninstallKey) { throw 'Kaldırma kaydı silinmedi.' }
}

function Assert-Installed([string]$Version) {
    if ((Get-Service $service).Status -ne 'Running') { throw 'Servis çalışmıyor.' }
    foreach ($file in 'OmniSpot.exe','service\OmniSpot.ChangeFeedService.exe') {
        $info = (Get-Item -LiteralPath (Join-Path $install $file)).VersionInfo
        if ($info.FileVersion -ne "$Version.0" -or ($info.ProductVersion -split '\+')[0] -ne $Version) {
            throw "$file sürümü uyuşmuyor: $($info.FileVersion) / $($info.ProductVersion)"
        }
    }
}

$version = [version](Get-Item -LiteralPath $Setup).VersionInfo.FileVersion
$version = $version.ToString(3)
Invoke-Setup $Setup
Assert-Installed $version
Uninstall-OmniSpot
if (Get-Service $service -ErrorAction SilentlyContinue) { throw 'Temiz kurulum kaldırıldıktan sonra servis kaldı.' }

Invoke-Setup $PreviousSetup
Assert-Installed '1.0.0'
New-Item -Type Directory -Path $corpus,$settingsDir -Force | Out-Null
Set-Content -LiteralPath $sample -Value 'release smoke fixture'
@{ IndexedFolders = @($corpus); SearchDebounceMs = 300; StartMinimized = $false } | ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding utf8
$expectedSettings = (Get-FileHash $settings).Hash
$expectedSample = (Get-FileHash $sample).Hash
Uninstall-OmniSpot
Invoke-Setup $Setup
Assert-Installed $version
if ((Get-FileHash $settings).Hash -ne $expectedSettings -or (Get-FileHash $sample).Hash -ne $expectedSample) {
    throw 'Geçiş sırasında örnek dosya veya ayar değişti.'
}

Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$exe = Join-Path $install 'OmniSpot.exe'
$ui = Start-Process -FilePath $exe -PassThru
$scope = [Windows.Automation.TreeScope]::Descendants
$id = [Windows.Automation.AutomationElement]::AutomationIdProperty
$bySearchBox = [Windows.Automation.PropertyCondition]::new($id,'SearchBox')
$byResult = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'omnispot-yayin-ornegi.txt')
$deadline = [DateTime]::UtcNow.AddSeconds(100)
$found = $false
while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds 2
    $ui.Refresh()
    if ($ui.HasExited) { throw "Kurulu uygulama kapandı: $($ui.ExitCode)" }
    if ($ui.MainWindowHandle -eq [IntPtr]::Zero) { continue }
    $window = [Windows.Automation.AutomationElement]::FromHandle($ui.MainWindowHandle)
    if ($window.FindFirst($scope,$byResult)) { $found = $true; break }
    $box = $window.FindFirst($scope,$bySearchBox)
    if ($box) {
        $value = $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
        $query = if ($value.Current.Value -eq 'omnispot-yayin') { 'omnispot-yayin-ornegi' } else { 'omnispot-yayin' }
        $value.SetValue($query)
    }
}
if (-not $found) {
    Write-Host "UI: $($ui.MainWindowTitle), HWND: $($ui.MainWindowHandle), arama alanı: $([bool]$box)"
    if ($window) {
        $window.FindAll($scope,[Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -First 100 | Out-Host
    }
    throw 'Kurulu arayüzde örnek dosya arama sonucunda bulunamadı.'
}
Uninstall-OmniSpot
if (Get-Service $service -ErrorAction SilentlyContinue) { throw 'Kaldırılan servisin kaydı bulundu.' }
