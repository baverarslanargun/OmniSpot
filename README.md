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
| `SÜREÇ` | private / working set / tepe working set belleği, iş parçacığı, handle, çalışma süresi, toplam CPU ve anlık CPU yüzdesi |
| `BELLEK` | yönetilen yığın, son GC'deki yığın ve ayrılmış bayt, parçalanma, GC duraklama yüzdesi, GC sayaçları, süreç başından beri toplam ayrılan bayt ve ayırma hızı |
| `G/Ç` | okuma / yazma / diğer işlem sayıları, okunan ve yazılan bayt, okuma işlemi ve bayt hızı |
| `İNDEKS` | indekslenen dosya, dizin ve token sayısı, uzlaştırma durumu; son turun zamanı, bulduğu değişiklik sayısı, tarama ve tur süresi; yeniden yayım sayısı ve süresi; yayımlanan arama durumundaki girdi sayısı |
| `KÜÇÜK RESİM` | bellek önbelleği doluluğu, boyutu ve tahliye sayısı, istek sayısı ve kaynağı (bellek / disk / kabuk), işlemdeki ve kuyruktaki üretim sayısı, çözülen bitmap boyutu, disk önbelleğinin dosya sayısı ve boyutu |
| `ARAMA` | sorgu sayısı, son sorgunun uzunluğu, süresi ve sonuç sayısı |
| `SON KLASÖR` | açılan klasör, listelenen öğe sayısı, kesme sınırına takılıp takılmadığı |

Eşik aşan değerler renk değiştirir; örneğin çözülen küçük resim istenen boyuttan
büyükse sarıya döner.

**Türev sayaçlar.** `ayırma hızı`, `CPU %` ve `okuma hızı` iki örnek arasındaki
farktan hesaplanır ve **gerçek geçen süreye** bölünür; tazeleme aralığı düzensiz
olduğu için sabit aralık varsayılmaz. İlk örnekte, sayaç sıfırlandığında veya
aralık çok kısa olduğunda değer üretilmez.

**Son GC değerleri.** `BELLEK` bölümündeki `yığın`, `ayrılmış` ve `parçalanma`
satırları anlık değil, **en son çöp toplamada** ölçülen değerlerdir; GC arasında
sabit kalırlar.

`toplam ayrılan` ise monoton artan bir sayaçtır: iki örnek arasındaki fark, o
pencerede ayrılan bayttır. `working set` bunu göstermez — GC toplayıp serbest
bırakırsa iz kalmaz.

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
Varsayılan `5` saniyede bir örnek alınır; ayrıca olaylar `OLAY` bölümünde ayrı
satır olarak işaretlenir, böylece bir sıçramanın kullanıcı eylemine mi arka plan
işine mi denk geldiği sonradan ayırt edilebilir:

| Olay | Ne zaman | Ayrıntı alanı |
|---|---|---|
| `klasör açıldı` | klasöre girildiğinde | klasör adı, öğe sayısı |
| `arama` | her tamamlanan aramada | sorgu uzunluğu, sonuç sayısı, süre |
| `uzlaştırma başladı` | arka plan uzlaştırması başlarken | — |
| `uzlaştırma bitti` | uzlaştırma biterken | bulunan değişiklik sayısı, yeniden yayım süresi |

Uzlaştırma işaretçileri, bir bellek sıçramasının kullanıcı eyleminden mi arka
plan taramasından mı geldiğini ayırmak için vardır; ikisi karıştığında ölçüm
yanlış atfedilir.

Dosyalar yazılırken de okunabilir; uygulama açıkken inceleyebilirsiniz.
`Dizin seç…` ile hedef klasör seçilir, `Dizini hatırla` işaretliyse seçim
ayarlarda saklanır ve sonraki açılışta yazma kaldığı yerden sürer. Her iki
dosya da dosya adları ve gezilen klasör yolları içerebilir; paylaşmadan önce
göz atın. Arama sorgusunun **metni** kaydedilmez, yalnız karakter sayısı yazılır.

**Komut satırından açma.** Düğmelere dokunmadan, ayarları değiştirmeden bir
ölçüm turu başlatmak için:

```powershell
OmniSpot.exe --tanila "C:\olcum\tur-3"
```

Her iki dosya da o dizine yazılır. `--tanila=C:\olcum\tur-3` biçimi de kabul
edilir; dizin verilmezse uygulama günlüğüne uyarı düşer ve yazma başlamaz.

**Boş üretim profili.** Gerçek OmniSpot UI, SQLite, watcher, arama ve thumbnail
servislerini sıfır kullanıcı dosyasıyla ölçmek için:

```powershell
OmniSpot.exe --tanila "C:\olcum\bos-uretim-tur-1" --profil bos-uretim
```

Profil değeri yalnız ASCII `bos-uretim` biçimindedir. Ayar, indeks, WAL/SHM,
thumbnail cache ve izlenen boş corpus; koşum dizinindeki `bos-uretim-data`
altında oluşturulur. Gerçek `%APPDATA%\OmniSpot`, `%LOCALAPPDATA%\OmniSpot` ve
Windows kullanıcı klasörleri kullanılmaz. `bos-uretim-data` daha önceki bir
koşumdan doluysa uygulama hiçbir şeyi silmez ve başlamayı reddeder; her ölçüm
için yeni bir koşum dizini kullanın.

Koşum yolu Windows'ta diskteki gerçek hedefe çözülür. Yolun herhangi bir üst
klasörü junction/symlink ise, gerçek hedef production veri yoluyla çakışıyorsa
veya corpus içindeki bir dosya işlemi yeniden yönlendirilmiş bir yoldan dışarı
çıkabiliyorsa profil fail-closed biçimde işlemi reddeder. İlk tarama ve klasör
gezgini de ölçüm profilinde reparse point'leri izlemez. Koşum dizini yalnız yerel
bir Windows sürücüsünde olabilir ve uygulama açıkken özel bir sahiplik kilidiyle
ikinci sürece kapatılır.

Bu sınır, yanlış yol seçimi ve kazara yönlendirmeye karşı ölçüm izolasyonudur;
aynı kullanıcı yetkisiyle çalışan düşmanca bir sürece karşı işletim sistemi
sandbox'ı değildir.

**Üretim kopyası profili.** Gerçek kullanıcı corpus'u üzerinde production
index/settings kopyası, watcher ve uzlaştırma davranışını ölçmek için:

~~~powershell
OmniSpot.exe --tanila "C:\olcum\uretim-kopya-tur-1" --profil uretim-kopya
~~~

Orkestratör uygulama başlamadan önce yalnızca aşağıdaki exact yerleşimi pre-seed
eder; uygulama production APPDATA/LOCALAPPDATA'dan hiçbir dosya kopyalamaz:
`uretim-kopya-data/settings/settings.json` (isteğe bağlı),
`uretim-kopya-data/index/index.db` (zorunlu, temiz/checkpoint edilmiş).
`index.db-wal` ve `index.db-shm` sidecar'ları reddedilir.
`uretim-kopya-data/thumbcache` her turda boş olmalıdır; production thumbnail
cache kopyalanmaz ve `corpus` dizini yoktur.
Bu seed, production kapalıyken ayrı snapshot aracı veya transactional SQLite
backup ile hazırlanmalıdır; canlı production dosyalarında `File.Copy` yapılmaz.
Normal
`IndexedLocationProvider` gerçek Desktop, Documents, Downloads, Pictures,
Music ve Videos köklerini okumaya ve watcher/uzlaştırmaya devam eder. Kullanıcı
dosyalarını değiştiren kopyala/taşı/sil/yeniden adlandır/yapıştır işlemleri ve
indeks yeniden oluşturma bu profilde fail-closed devre dışıdır; startup
registration da uygulanmaz; watcher, ilk tarama, uzlaştırma ve klasör gezgini
reparse point'leri izlemez. Production `index.db` yoksa, pre-seed yerleşimi
beklenmeyen dosya içeriyorsa veya koşum yolu
güvenlik doğrulamasından geçmezse uygulama production'a dönmeden başlamayı
reddeder.

**Sayaç dosyasını okuma.** CSV'yi elle ayrıştırmak yerine:

```powershell
dotnet run --project Tools\OmniSpot.Benchmarking -- diag --file "C:\olcum\tur-3\omnispot-20260827-010000-metrik.csv"
```

Çıktı; oturum kapsamını, `OLAY` zincirini ve her olay penceresi arasında en çok
değişen sayaçları verir. `atlanan satır` sıfır değilse dosyada bozuk satır var
demektir. `--output` ile JSON, `--top` ile pencere başına satır sayısı.

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
