# OmniSpot V1.0 - Akıllı Dosya Tarayıcı

<p align="center">
  <img src="assets/branding/omnispot.svg" alt="OmniSpot logosu" width="180">
</p>

Modern, hafif ve hızlı dosya tarayıcısı. Ctrl+Space ile her yerden erişin!

![OmniSpot](https://img.shields.io/badge/version-1.0.0-blue) ![.NET](https://img.shields.io/badge/.NET-8.0-purple) ![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)

## 🚀 Özellikler

### Temel Özellikler
- ✅ **Global Hotkey**: Ctrl+Space ile her yerden açın
- ✅ **Akıllı Arama**: Dosya adlarında hızlı arama
- ✅ **AI Destekli Arama**: Doğal dil ile arama ("indirilenler klasöründeki resimler")
- ✅ **Klasör Navigasyonu**: Uygulama içinde klasörlerde gezinin
- ✅ **Dosya İşlemleri**: Kopyala, Kes, Yapıştır, Sil, Yeniden Adlandır
- ✅ **Thumbnail Önizleme**: Resim ve video önizlemeleri
- ✅ **Renkli Klasörler**: Klasör türüne göre renk kodlaması
- ✅ **Çoklu Dizin Desteği**: Birden fazla klasörü indeksleyin

### Klavye Kısayolları
| Kısayol | İşlem |
|---------|-------|
| **Ctrl+Space** | OmniSpot'u aç/kapat |
| **Ctrl+C** | Kopyala (hover) |
| **Ctrl+X** | Kes (hover) |
| **Ctrl+V** | Yapıştır |
| **F2** | Yeniden adlandır (hover) |
| **Delete** | Sil (hover) |
| **F5** | Yenile |
| **ESC** | Kapat |

## 📦 Kurulum

### Yöntem 1: Hazır Kurulum (Önerilen)
1. [Releases](https://github.com/baverarslanargun/OmniSpot/releases) sayfasından `OmniSpot-1.0.0-Setup.exe` indirin
2. Kurulum sihirbazını takip edin
3. Ctrl+Space ile başlatın!

### Yöntem 2: PowerShell Kurulumu
```powershell
# Proje klasöründe çalıştırın
powershell -ExecutionPolicy Bypass -File installer\Install.ps1
```

### Yöntem 3: Portable (Kurulum Gerektirmez)
1. `publish\OmniSpot.exe` dosyasını istediğiniz yere kopyalayın
2. Çalıştırın

## 🔧 Derleme (Geliştiriciler İçin)

### Gereksinimler
- .NET 8.0 SDK
- Windows 10/11

### Derleme
```powershell
# Debug build
dotnet build

# Release publish (tek dosya)
dotnet publish SmartFileLauncher.UI\SmartFileLauncher.UI.csproj -c Release -o .\publish
```

Detaylı talimatlar için [derleme rehberine](docs/guides/build.md) bakın.

### Güvenlik Notları

⚠️ **GÜVENLİK**: Bu uygulama sisteminize kalıcı değişiklik yapmaz:
- Explorer.exe'yi kapatmaz veya değiştirmez
- Kayıt defterini değiştirmez
- Sistem dosyalarını değiştirmez
- Kapatıldığında her şey normale döner

### Gelecek Özellikler (TODO)

- [ ] Türkçe morfolojik analiz (Zemberek entegrasyonu)
- [ ] TF-IDF skorlama
- [ ] Trie tabanlı autocomplete
- [ ] Sesli komut desteği (Vosk)
- [ ] Çoklu klasör tarama
- [ ] Tema desteği
- [ ] Özelleştirilebilir kısayollar
- [ ] Dosya içerik araması

### Proje Yapısı

```
OmniSpot/
├── SmartFileLauncher.Core/   # Çekirdek mantık ve indeksleme
├── SmartFileLauncher.UI/     # WPF masaüstü uygulaması
├── tests/                    # Otomatik testler
├── docs/                     # Rehberler ve teknik belgeler
├── scripts/                  # Geliştirme yardımcıları
├── assets/                   # Marka ve görsel dosyaları
├── installer/                # Kurulum paketi
└── SmartFileLauncher.sln
```

### Teknik Detaylar

- **Framework**: .NET 8, WPF
- **Dil**: C# 12
- **Platform**: Windows 10/11
- **Bağımlılıklar**: NuGet paketleri çözüm restore edilirken yüklenir

### Performans

- Desktop tarama: ~100-500 dosya için <100ms
- Arama: Tipik sorgular için <10ms
- Bellek: ~20-50 MB (dosya sayısına bağlı)

### Katkıda Bulunma

Bu bir eğitim projesidir. Öneriler için issue açabilirsiniz.

### Lisans

Eğitim amaçlı proje.

## 📚 Dokümantasyon

Tüm belgeler için [dokümantasyon indeksine](docs/README.md) bakın.

- [Derleme ve yayınlama](docs/guides/build.md)
- [Teknik referans](docs/architecture/technical-reference.md)
- [Veri yapıları](docs/architecture/data-structures.md)
- [Güvenlik](SECURITY.md)
