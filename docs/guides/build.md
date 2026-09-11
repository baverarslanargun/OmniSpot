# OmniSpot V1.0 - Build & Release Guide

## 🚀 Hızlı Build

### Geliştirme (Debug)
```powershell
cd c:\OmniSpot
dotnet build
dotnet run --project SmartFileLauncher.UI\SmartFileLauncher.UI.csproj
```

### Prodüksiyon (Release)
```powershell
cd c:\OmniSpot

# Self-contained tek dosya olarak publish
dotnet publish SmartFileLauncher.UI\SmartFileLauncher.UI.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=false `
  -o .\publish
```

Bu komut `publish\OmniSpot.exe` dosyasını oluşturur. Sıkıştırmasız self-contained
çıktının boyutu bağımlılıklara göre değişir; mevcut build yaklaşık 166 MB'tır.
.NET runtime gerektirmez, tek başına çalışır.

## ⚠️ Uyarı Politikası

Repo kökündeki `Directory.Build.props` yeni derleyici ve NuGet uyarılarını hata
olarak ele alır. Eski Windows API Code Pack bağımlılığından gelen `NU1701`, paket
değişimi tamamlanana kadar kayıtlı geçici istisnadır. CI build'i MSBuild
uyarılarını da reddeder.

NuGet audit servisine erişilemeyen çevrimdışı bir yerel doğrulamada restore
geçici olarak `-p:NuGetAudit=false` ile çalıştırılabilir. Bu seçenek CI'da
kullanılmamalıdır; normal akışta paket güvenlik denetimi açık kalır.

## 📦 Kurulum Dosyası Oluşturma

### Gereksinimler
1. [Inno Setup 6.x](https://jrsoftware.org/isdl.php) indir ve kur

### Kurulum Dosyası Oluştur

Build betiği UI ve `OmniSpotChangeFeed` servisini ayrı ayrı sıkıştırmasız,
self-contained single-file olarak publish eder; bundle manifestlerini doğrular ve
installer ile SHA-256 dosyasını üretir.

### Komut Satırından
```powershell
& .\scripts\Build-DemoInstaller.ps1 -Version 1.0.0
```

Çıktılar:

- `installer\output\OmniSpot-1.0.0-Demo-Setup.exe`
- `installer\output\OmniSpot-1.0.0-Demo-Setup.exe.sha256`

Installer yükseltilmiş per-machine kurulum yapar. Aynı adlı
`OmniSpotChangeFeed` servisi zaten varsa mevcut kaydı devralmadan durur.

## 📁 Proje Yapısı

```
OmniSpot/
├── artifacts/demo-installer/   # Geçici UI ve servis publish çıktıları
├── installer/
│   ├── OmniSpotSetup.iss       # Inno Setup script
│   ├── Manage-ChangeFeedService.ps1
│   └── output/                 # Kurulum dosyası ve SHA-256 çıktısı
├── SmartFileLauncher.Core/     # İş mantığı
├── SmartFileLauncher.UI/       # WPF arayüz
├── tests/                       # Otomatik testler
├── docs/                        # Teknik belgeler ve rehberler
├── scripts/                     # Geliştirme yardımcıları
└── assets/branding/             # Logo kaynakları (SVG ve üretilen ICO)
```

## 🔧 Publish Seçenekleri

| Seçenek | Açıklama |
|---------|----------|
| `-c Release` | Optimizasyon açık |
| `-r win-x64` | 64-bit Windows |
| `--self-contained true` | .NET runtime dahil |
| `-p:PublishSingleFile=true` | Tek exe dosyası |
| `-p:EnableCompressionInSingleFile=false` | EXE bundle girdileri sıkıştırılmaz |

### Alternatif: Framework-dependent (Küçük dosya)
```powershell
dotnet publish SmartFileLauncher.UI\SmartFileLauncher.UI.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o .\publish-small
```
Bu ~15MB ama .NET 8 runtime gerektirir.

## 🏷️ Versiyon Güncelleme

1. `SmartFileLauncher.UI.csproj`:
```xml
<Version>1.0.0</Version>
<FileVersion>1.0.0.0</FileVersion>
```

2. Build betiğine sürümü ver:
```powershell
& .\scripts\Build-DemoInstaller.ps1 -Version 1.0.0
```

## ✅ Release Checklist

- [ ] Version numarasını güncelle
- [ ] Release modda test et
- [ ] `dotnet publish` çalıştır
- [ ] UI ve servis bundle'larında sıkıştırılmış girdi sayısının `0` olduğunu doğrula
- [ ] Inno Setup ile kurulum dosyası oluştur
- [ ] Kurulum dosyasını test et (temiz VM'de)
- [ ] Servisin `LocalSystem`, `Automatic` ve `UNRESTRICTED` SID türünde olduğunu doğrula
- [ ] İkinci normal başlatmanın yeni süreç bırakmadan mevcut pencereyi öne getirdiğini doğrula
- [ ] Çalışan UI açıkken uninstall'ın UI'yi kapatıp dosyaları kaldırdığını doğrula
- [ ] Stop/start ve uninstall sonrasında servis kaydının silindiğini doğrula
- [ ] Antivirus taraması yap
- [ ] Release notes hazırla

## 📋 Kurulum İçeriği

Kurulum programı şunları yapar:
- ✅ Program Files'a uygulama kopyalar
- ✅ `OmniSpotChangeFeed` servisini LocalSystem/Automatic olarak kurar
- ✅ Servis SID türünü ilk başlangıçtan önce `UNRESTRICTED` yapar
- ✅ Servisi başlatır ve IPC pipe hazır olana kadar bekler
- ✅ Başlat menüsü kısayolu oluşturur
- ✅ Masaüstü kısayolu (opsiyonel)
- ✅ Normal kullanımda tek OmniSpot örneği çalıştırır; sonraki başlatmalar mevcut pencereyi öne getirir
- ✅ Kaldırmadan önce çalışan OmniSpot'u kontrollü kapatır, ardından servisi durdurup siler
- ✅ Kullanıcı ayarlarını ve trusted change-feed deposunu kaldırmada korur

## 🔒 Gelecek: Code Signing

Daha sonra imzalama eklemek için:
1. Code signing sertifikası al
2. `signtool.exe` ile exe'yi imzala:
```powershell
signtool sign /f certificate.pfx /p password /t http://timestamp.digicert.com publish\OmniSpot.exe
```
