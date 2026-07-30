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
  -p:EnableCompressionInSingleFile=true `
  -o .\publish
```

Bu komut `publish\OmniSpot.exe` dosyasını oluşturur (~70MB).
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
1. Önce publish yap (yukarıdaki komut)
2. `installer\OmniSpotSetup.iss` dosyasını Inno Setup ile aç
3. **Build > Compile** (veya Ctrl+F9)
4. `installer\output\OmniSpot-1.0.0-Setup.exe` oluşur

### Komut Satırından
```powershell
# Inno Setup kuruluysa
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\OmniSpotSetup.iss
```

## 📁 Proje Yapısı

```
OmniSpot/
├── publish/                    # Yayınlanmış dosyalar
│   └── OmniSpot.exe              # Ana uygulama (~70MB)
├── installer/
│   ├── OmniSpotSetup.iss      # Inno Setup script
│   └── output/                 # Kurulum dosyası çıktısı
├── SmartFileLauncher.Core/     # İş mantığı
├── SmartFileLauncher.UI/       # WPF arayüz
├── tests/                       # Otomatik testler
├── docs/                        # Teknik belgeler ve rehberler
├── scripts/                     # Geliştirme yardımcıları
├── assets/branding/             # Logo kaynakları
└── Tools/IconGenerator/         # İkon üretim aracı
```

## 🔧 Publish Seçenekleri

| Seçenek | Açıklama |
|---------|----------|
| `-c Release` | Optimizasyon açık |
| `-r win-x64` | 64-bit Windows |
| `--self-contained true` | .NET runtime dahil |
| `-p:PublishSingleFile=true` | Tek exe dosyası |
| `-p:EnableCompressionInSingleFile=true` | Sıkıştırma |

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

2. `installer\OmniSpotSetup.iss`:
```iss
#define MyAppVersion "1.0.0"
OutputBaseFilename=OmniSpot-1.0.0-Setup
```

## ✅ Release Checklist

- [ ] Version numarasını güncelle
- [ ] Release modda test et
- [ ] `dotnet publish` çalıştır
- [ ] Inno Setup ile kurulum dosyası oluştur
- [ ] Kurulum dosyasını test et (temiz VM'de)
- [ ] Antivirus taraması yap
- [ ] Release notes hazırla

## 📋 Kurulum İçeriği

Kurulum programı şunları yapar:
- ✅ Program Files'a uygulama kopyalar
- ✅ Başlat menüsü kısayolu oluşturur
- ✅ Masaüstü kısayolu (opsiyonel)
- ✅ Windows başlangıcında çalıştır (opsiyonel)
- ✅ Kaldırma desteği

## 🔒 Gelecek: Code Signing

Daha sonra imzalama eklemek için:
1. Code signing sertifikası al
2. `signtool.exe` ile exe'yi imzala:
```powershell
signtool sign /f certificate.pfx /p password /t http://timestamp.digicert.com publish\OmniSpot.exe
```
