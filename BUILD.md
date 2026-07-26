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

Bu komut `publish\SmartFileLauncher.UI.exe` dosyasını oluşturur (~70MB).
.NET runtime gerektirmez, tek başına çalışır.

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
│   └── SmartFileLauncher.UI.exe  # Ana uygulama (~70MB)
├── installer/
│   ├── OmniSpotSetup.iss      # Inno Setup script
│   └── output/                 # Kurulum dosyası çıktısı
├── SmartFileLauncher.Core/     # İş mantığı
├── SmartFileLauncher.UI/       # WPF arayüz
└── Tools/
    └── IconGenerator/
        └── omnispot.ico       # Uygulama ikonu
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
signtool sign /f certificate.pfx /p password /t http://timestamp.digicert.com publish\SmartFileLauncher.UI.exe
```
