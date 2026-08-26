# OmniSpot V1.0 - Akıllı Dosya Tarayıcı

<p align="center">
  <img src="assets/branding/omnispot.svg" alt="OmniSpot logosu" width="180">
</p>

Modern, hafif ve hızlı dosya tarayıcısı. Ctrl+Space ile tüm dosyalara tek noktadan erişin!

![OmniSpot](https://img.shields.io/badge/version-1.0.0-blue) ![.NET](https://img.shields.io/badge/.NET-8.0-purple) ![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)

## 🚀 Özellikler

### Temel Özellikler
- ✅ **Global Hotkey**: Ctrl+Space ile her yerden açın
- ✅ **Akıllı Arama**: Dosya adlarında hızlı arama
- ✅ **Doğal Dil Arama**: İsteğe bağlı Groq desteği ve kural tabanlı fallback ile sorguları yapılandırın
- ✅ **Klasör Navigasyonu**: Uygulama içinde klasörlerde gezinin
- ✅ **Dosya İşlemleri**: Kopyala, Kes, Yapıştır, Sil, Yeniden Adlandır
- ✅ **Thumbnail Önizleme**: Resim ve video önizlemeleri
- ✅ **Renkli Klasörler**: Klasör türüne göre renk kodlaması
- ✅ **Kalıcı Çoklu Dizin İndeksi**: Desktop, Documents, Downloads, Pictures, Music ve Videos klasörlerini indeksleyin; sonraki açılışlarda kayıtlı indeksi kullanın
- ✅ **Tanılama Penceresi**: Canlı bellek, indeks ve küçük resim sayaçları; isteğe bağlı dosyaya günlükleme

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

### Doğal Dil Arama ve Gizlilik

- Standart dosya araması yerel indeks üzerinde çalışır.
- Doğal Dil modu, sorguyu yapılandırmak için Groq API'ına iki paralel istek göndermeyi dener. Bu mod kullanıldığında sorgu metni harici servise aktarılabilir.
- Groq anahtarı eksik/geçersizse veya servis yanıt vermezse uygulama kural tabanlı yerel parser'a döner. Fallback daha sınırlı sorgu semantiği sunar.
- Yapılandırma ve güncel mimari için [doğal dil arama rehberine](docs/guides/llm-setup.md) bakın.

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

### Tanılama Penceresi

Arama çubuğunun sağındaki 🐞 düğmesi **ayrı bir tanılama penceresi** açar. Ana
pencereden bağımsız olduğu için uygulamayı kullanırken sayaçları aynı anda
izleyebilirsiniz.

Pencerenin solunda uygulama günlüğü, sağında saniyede bir tazelenen sayaçlar yer
alır:

| Bölüm | İçerik |
|---|---|
| `SÜREÇ` | private / working set belleği, iş parçacığı, handle, yönetilen yığın, GC sayaçları |
| `İNDEKS` | indekslenen dosya ve dizin sayısı, token sayısı, uzlaştırma durumu |
| `KÜÇÜK RESİM` | önbellek doluluğu ve boyutu, istek sayısı ve kaynağı (bellek / disk / kabuk), çözülen bitmap boyutu |
| `SON KLASÖR` | açılan klasör, listelenen öğe sayısı, kesme sınırına takılıp takılmadığı |

Eşik aşan değerler renk değiştirir; örneğin çözülen küçük resim istenen boyuttan
büyükse sarıya döner.

**Dosyaya yazma.** İki bağımsız anahtar var; ikisi de aynı dizini kullanır ve
aynı oturum damgasını taşıdıkları için dosyalar eşleşir.

| Anahtar | Dosya | İçerik |
|---|---|---|
| `Günlüğü yaz` | `omnispot-YYYYAAGG-SSDDss.log` | soldaki olay akışı, düz metin |
| `Sayaçları yaz` | `omnispot-YYYYAAGG-SSDDss-metrik.csv` | sağdaki sayaçların zaman serisi |

Günlük dosyasının başına sürüm, işletim sistemi, çekirdek sayısı, süreç
kimliği, veritabanı yolu ve yapılandırma damgaları yazılır; her satır zaman
damgalıdır.

Sayaç dosyası **uzun biçim** CSV'dir — `zaman;bölüm;etiket;değer;sayısal`.
Her okuma ayrı bir satırdır; böylece çalışma sırasında yeni metrik eklendiğinde
sabit başlıklı bir tabloda kaybolmaz. `değer` ekranda görünen metin
(`878,4 MB`), `sayısal` ise birimsiz ham değerdir (`921010176`, ondalık nokta).
Varsayılan `5` saniyede bir örnek alınır; ayrıca klasör açma gibi olaylar
`OLAY` bölümünde ayrı satır olarak işaretlenir, böylece bir sıçramanın
kullanıcı eylemine mi arka plan işine mi denk geldiği sonradan ayırt edilebilir.

Dosyalar yazılırken de okunabilir; uygulama açıkken inceleyebilirsiniz.
`Dizin seç…` ile hedef klasör seçilir, `Dizini hatırla` işaretliyse seçim
ayarlarda saklanır ve sonraki açılışta yazma kaldığı yerden sürer. Her iki
dosya da dosya adları ve gezilen klasör yolları içerebilir; paylaşmadan önce
göz atın.

`Kopyala` düğmesi damgaları, bütün sayaçları ve ekrandaki günlüğü tek seferde
panoya alır.

### Sistem ve Güvenlik Davranışı

- Uygulama Explorer.exe'yi veya Windows sistem dosyalarını değiştirmez.
- Ayarlar ve indeks önbelleği kullanıcı profilinde kalıcı olarak saklanır.
- "Windows ile başlat" seçeneği etkinleştirildiğinde kullanıcıya ait `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` kaydı güncellenir.
- Installer'lar kısayol, kaldırma bilgisi ve seçilen başlangıç ayarları için kullanıcı kapsamındaki dosya/registry kayıtlarını değiştirebilir.
- Doğal Dil modu etkin kullanıldığında sorgu metni Groq API'ına gönderilebilir; standart arama yerel çalışır.

### Gelecek Özellikler (TODO)

- [ ] Türkçe morfolojik analiz (Zemberek entegrasyonu)
- [ ] TF-IDF skorlama
- [ ] Trie tabanlı autocomplete
- [ ] Sesli komut desteği (Vosk)
- [ ] Kullanıcı tanımlı indeks kökleri
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

- İndeksleme süresi; dosya sayısı, disk hızı, seçilen kökler ve cache durumuna bağlıdır.
- Arama bellek içi snapshot üzerinde çalışır; gerçek süre ve bellek tüketimi indeks boyutuna ve sorguya göre değişir.
- Release kararı vermeden önce hedef sistemde temsilî veriyle ölçüm yapılmalıdır.

### Katkıda Bulunma

Bu bir eğitim projesidir. Öneriler için issue açabilirsiniz.

### Lisans

Depoda henüz bir `LICENSE` dosyası yoktur. Kullanım, kopyalama ve dağıtım izinleri açık bir lisans seçilene kadar tanımlı değildir.

## 📚 Dokümantasyon

Tüm belgeler için [dokümantasyon indeksine](docs/README.md) bakın.

- [Derleme ve yayınlama](docs/guides/build.md)
- [Doğal dil arama yapılandırması](docs/guides/llm-setup.md)
- [Doğal dil arama testi](docs/guides/nlu-integration.md)
- [Teknik referans](docs/architecture/technical-reference.md)
- [Veri yapıları](docs/architecture/data-structures.md)
- [Güvenlik](SECURITY.md)
