# OmniSpot Teknik Referans Dokümanı

Bu doküman, OmniSpot uygulamasının teknik mimarisini, kullanılan veri yapılarını, algoritmaları ve temel bileşenlerin işlevlerini detaylandırır.

## 1. Temel Bileşenler ve Algoritmalar

### FuzzyMatcher.cs (Bulanık Arama)
Bu sınıf, uygulamanın "Bulanık Arama" (Fuzzy Search) yeteneğini sağlayan kritik bir bileşendir. Kullanıcının yazım hatalarını (typo) tolere ederek doğru sonuçları bulmasını sağlar.

*   **İşlev:** Levenshtein Distance (Edit Distance) algoritmasını kullanarak kelimeler arası benzerliği hesaplar.
*   **Metotlar:**
    *   `IsFuzzyMatch`: İki kelimenin benzer olup olmadığını kontrol eder.
    *   `LevenshteinDistance`: İki kelime arasındaki işlem sayısını hesaplar.
    *   `FindFuzzyMatches`: Benzerlik sırasına göre en iyi eşleşmeleri bulur.
*   **Veri Yapıları:**
    *   **2D Array (Matris):** Dinamik Programlama (Dynamic Programming) için `int[,] dp` matrisi kullanılır. Zaman ve Bellek Karmaşıklığı: $O(M \times N)$.
    *   **List ve Tuple:** Aday kelimeleri ve skorları saklamak için `List<(string candidate, int distance)>` kullanılır.

## 2. Servisler (Services)

### Normal uygulamada Live katalog

Normal `ApplicationCompositionRoot`, ölçüm profili seçilmediğinde `EnableLiveCatalog` ile Live backend'i kullanır. Aşağıdaki SQLite/Compact bootstrap ve watcher kira devri açıklamaları eski backend ve onu açıkça seçen ölçüm yolları içindir.

* **İlk edinim:** Seçili klasörler `FileSystemEnumerable` ile listelenir; kayıt geldiğinde ad, üst klasör kimliği, metadata ve arama listeleri doğrudan katalog sayfalarına yazılır. Tüm dosyaları nesne/dictionary olarak biriktirme ve ardından SQLite'a aktarma aşaması yoktur. Sürücü kökü için mevcut MFT envanter kaynağı korunur. Hidden/system ve erişim kuralları uygulanır; erişilemeyen ilk tarama kapsamları raporlanır. Kendi `index.db.live` çalışma ağacı indekslenmez.
* **Kalıcılık:** Her kökün `LiveCatalogStore` nesli `index.db.live/root-<GUID>` altındadır. RAM sayfaları 256 KiB kalır; disk güncellemesi 64 baytlık değişen blokları tek ham temel sayfaya bağlı fark dosyasında tutar. Fark 128 KiB'ye ulaşırsa yalnız o sayfa tam yazılır. Checksum'lı `transaction.wal` atomik olarak `commit-<sıra>.bin` olur; ardından immutable arama görünümü yayınlanır. `head.bin` her 64 commit sonrasında yeni işlem başlamadan yenilenir, kapsadığı commit dosyaları kaldırılır. Eski sorgular son okuyucu bitene kadar eski sayfalara erişir. Açılış başlık zincirini, temel/fark sayfalarını ve checksum'ları doğrular; yarım commit varsa günlüğü uygular. Format v3, v2 ham sayfa önbelleğini yeniden taramadan açar; sabit codec sözleşmesi build MVID'sinden bağımsızdır. Süreç kesintisi kurtarması testlidir; fiziksel güç kesintisi için ek garanti verilmez.
* **Canlı takip:** Destekli NTFS kökleri açık/kapalı uygulamada aynı servis USN kuyruğunu kullanır. Capability `continuous-usn-v1` gereklidir; Live istemci watcher kirası tutmaz. Devam sayfaları tamamlanıp ilgili kayıtlar dayanıklı commit edilmeden receipt ACK edilmez. Kalıcı teslim kimliği tekrar uygulamayı ayırır. Yalnız kendi depolama dosyalarını içeren olaylar katalog yazmadan onaylanır. İlgisiz köke commit yapılmaz; aynı dosyanın Modified olayları create/delete/rename/dizin olaylarının sırasını koruyan bölümler içinde birleştirilir. USN haritası aynıysa küçük cursor kaydı kullanılır; journal/konum/güvenlik damgası da aynıysa tekrar yazılmaz.
* **Watcher ve onarım:** USN'nin desteklemediği kökler ve reparse bağlantı kapsamları `FileSystemWatcher` kullanır. Uygulanamayan metadata olayı yalnız bilinen dosya/alt klasör onarımına yazılır; generic servis arızası tam tarama başlatmaz. Gerçek journal/queue/cursor kaybı yalnız etkilenen kökü yeni nesilde yeniler. Eski katalog bu sırada aranabilir. `control.bin` kapsam/ilk kurulum/onarımları, kök frame'i teslim kimliği ve ilgili bekleyen onarımları taşır.
* **Servis maliyeti:** Owner başına ortak USN coordinator projection'ı saklar. Dizin ilişkileri değişmediyse büyük harita tekrar serileştirilmez; yalnız checksum'lı küçük cursor dosyası ilerler. Harita nesli cursor ile eşleşmelidir; geçici journal erişim hatası önceki cursor'u korur.
* **Özellikler:** `LiveSearchState` mevcut arama, AI yapılandırılmış sorgu, filtre, sıralama ve klasör genişletme sözleşmelerini besler. Tam yol ihtiyaç anında üst klasör zincirinden üretilir. Thumbnail üretim/önbellek ve AI sağlayıcı işlemleri değişmedi. Eski `index.db` yeni normal yol tarafından açılmaz; ayarlar ayrı kalır. UI bakım yüzeyi gerçek Live dizin boyutunu gösterir, kullanıcı yeniden oluşturma istediğinde sonraki açılışta yeni nesil kurulur.

### IndexManager.cs
*   **İşlev:** Tüm standart köklerde ilk taramayı, kalıcı indeks yüklemeyi, veritabanı/bellek senkronizasyonunu ve canlı dosya izlemeyi yönetir.
*   **Veri Yapıları:** HashSet (Senkronize dosyalar için $O(1)$), Dictionary.
*   **Algoritma:** Normal uygulamada seçili klasörlerden oluşan kapsam doğrudan taranır. Kompakt bootstrap'ın `FileSystemEnumerable` yürüyüşü sıradan dosyaların boyut/tarih/attribute bilgisini listeleme kaydından alır; dosya başına yeniden metadata sorgulamaz. Hidden/system dışlamaları ve bağlantı kapsamı korunur. Bütün kökler yerel sürücü kökü olduğunda kompakt katalog servisten sayfalı ham NTFS MFT envanteri alır; aynı volume önce dizin ilişkileri, sonra ad/boyut/tarih kayıtları için iki geçişte okunur. Klasör/sürücü karışık listeler ve UNC kökleri hedefli filesystem yolunu kullanır. MFT servis/protokol/edinim başarısızlığında da filesystem bootstrap kullanılır. Legacy katalog ve ölçüm profillerinin kaynak seçimi değişmez; Delta Sync disk ve DB farklarını uzlaştırır.
*   **Devir:** MFT yolunda watcher ilk envanterden önce değişiklikleri duraklatılmış yakalar; tek SQLite transaction'ından sonra USN devrinde oturum ve watcher sağlığı doğrulanır. MFT'nin `initial_inventory_pending` işareti watcher kuyruğu uygulanana kadar kalır. Hedefli filesystem yolu mevcut USN kuyruk devri ve gerektiğinde uzlaştırma akışını kullanır; MFT oturum doğrulamasını taklit etmez.
*   **Tanılama:** `Metadata.last_bootstrap_source` ilk edinimin `mft` veya `filesystem` yolunu kaydeder; `filesystem` doğrudan seçilmiş olabilir, tek başına MFT hatası anlamına gelmez. `last_bootstrap_link_scopes` MFT'ye ek olarak taranan bağlantı kapsamı sayısıdır; filesystem yolunda sıfırdır. MFT bağlantı snapshot'ı devirde değişmemişse tekrar DB materializasyonu yapılmaz. Hedefli edinim `directory_inventory` aşamasıyla kayıt/süre ilerlemesi verir. `directory-probe --omnispot-roots --database <yeni-db> --output <yeni-json>` ayrı boş indeks üzerinde exploratory ölçüm yapar; toplamı dolu production indeksi yenilemesiyle eşdeğer değildir.

### Ham MFT envanteri ve servis kanalı
*   **Geçici kullanıcı kapsamı:** Normal UI sürecinde `OMNISPOT_INDEX_USER_PROFILE=1` verilirse indeks kökü mevcut kullanıcının profil dizini olur; başlangıçta gösterilen masaüstü konumu korunur. Değişken verilmediğinde standart altı konum kullanılır. Bu seçenek ölçüm profillerinin kapsamını veya gizli/sistem dosyası dışlamalarını değiştirmez.
*   **Kapsam değişiminde servis devri:** İstenen bütün köklerin aboneliği doğrulandıktan sonra serviste bu kullanıcıya ait eski kökler kaldırılır; temizleme başarısızsa devir başarılı sayılmaz. Böylece altı klasörden kullanıcı köküne geçiş, eski abonelikler için tekrar tarama başlatmaz. Aynı olay sayfasında sonradan silindiği doğrulanan geçici dosyaların kaybolmuş metadata'sı yeniden tarama gerektirmez; silme olayı katalog ve veritabanına uygulanır.
*   **Değişikliklerin görünürlük kuralı:** Kompakt olay işleme, ilk taramayla aynı gizli/sistem dosyası ve üst klasör dışlamalarını kullanır. Açıkça seçilmiş en yakın indeks kökü bu üst klasör kontrolünün sınırıdır. Sonradan gizlenen veya dışlanan bir yere taşınan kayıtlar katalog ve DB'den çıkarılır. Hazırlık tamamlanana kadar dosya olayları masaüstü görünümünü yenilemez; başlangıç tamamlandığında görünüm güncel katalogdan yüklenir.
*   **Yerel onarım:** Protokol 5 yanıtındaki isteğe bağlı `AuthorizationScopesUtf16`, güvenle adı gösterilebilen onarım sınırlarını Base64 UTF-16 olarak taşır; erişilemeyen alt adları içermez. Servis en fazla 32 kapsamı sayfaya koyar, kalanları devam sayfasında korur. Sayfa kapasitesini aşan görünür olay da bilinen yollarını onarım kapsamı olarak taşır. İstemci yolları kök sınırı içinde doğrular; uygulanamayan olayın yalnız ilgili dosya/klasörünü onarır. Kaybolmuş veya dışlanan kapsam yalnız kendi kayıtlarından kaldırılır. Başarısız işler `Metadata.pending_repair_scopes_utf16` içinde transaction ile kaydedilmeden receipt onaylanmaz. Kayıtlı işler açılışta devirden önce yüklenir ve arka planda yalnız kendi konumlarında yeniden denenir; yeni işler eski kuyruğu silmez. Marker başarılı DB commit ve katalog yayını sonrasında, aynı iş yeniden eklenmediyse kaldırılır. Gerçek erişim reddi tamamlanmış onarım sayılmaz. Geçerli cache ve sağlıklı devir altında yerel hata tam taramayı tetiklemez; bilinmeyen kapsam, gerçek üretici/yakalama kaybı ve eksik servis devri ayrı kurtarma nedenleridir.
*   **Kalıcı değişiklik kuyruğu:** Onaylanmamış paketler diskte saklanır; toplam 512 paket/64 MiB birikmesi artık olay silme veya `DeliveryQueueOverflow` üretme nedeni değildir. Tek paket en fazla 512 KiB, bir okuma en fazla 64 paket/512 KiB'dır. İstemci bir bölümün receipt'ini onayladıktan sonra `HasMore` varsa sonraki bölümü çeker; 512 sayfalık ilerlemesiz zincir sınacı yalnız başarılı ACK ile sıfırlanır. Devir boyunca kira yenilenir; yenileme başarısızsa tüketim durdurulur, yenileme işi bitmeden kira bırakılmaz. Uygulama kapalıyken disk kuyruğu büyüyebilir, onaylanan bölümler temizlenir. Yazma başarısızlığında önceki paketler korunur ve USN checkpoint ilerlemez. Eski sürümün bıraktığı gerçek kayıp işaretleri gizlenmez; bu değişiklik önceden silinmiş olayları geri getirmez.
*   **Sözleşme:** ChangeFeed protokolü sürüm 5, `Inventory`, `ValidateInventory` ve `CancelInventory` isteklerini içerir. Envanter sayfaları event delivery/receipt verisinden ayrıdır; dosya yolları UTF-16 kod birimlerini korumak için Base64 taşınır.
*   **Yetki:** Ham volume okuması servis kimliğiyle; root admission ve sayfa görünürlüğü doğrulanmış çağıran kimliğiyle yürür. Oturum owner, protokol ve abonelik kökleri/kimlikleri/kuşaklarına bağlıdır. Native envanter MFT öncesi USN konumundan itibaren, kapsam içindeki dosya/dizin kimlikleri ile köklerin tüm üst dizin kimliklerini etkileyen güvenlik/reparse değişikliklerini completion ve devir doğrulamalarında denetler; journal değişimi veya kayıp kayıtlar envanteri reddettirir. Hardlink'ler ortak dosya kimliğiyle korunur. Event kuyruğunun epoch ve genel security stamp'i delivery/receipt için geçerlidir; bağımsız native envanteri ilgisiz volume değişiklikleri nedeniyle iptal etmez.
*   **Bellek sınırı:** Aktarım kuyruğu 512 kayıt, sayfa hedefi 256 KiB, protokol üst sınırı 1 MiB'dır. Dizin ilişkileri ve oturumun güvenlik doğrulaması için dosya kimlikleri bellekte tutulur; toplam servis RAM'i volume dizin ve seçili envanter kayıt sayısına bağlıdır. Bu sabit bellek garantisi değildir. Journal handle ve kimlik kümesi oturum iptali/sonlandırılmasında bırakılır.
*   **Doğrulama:** `Tools/OmniSpot.Benchmarking` içindeki `mft-probe --volume C:\ --root <test-kökü> --max-entries 0 --verify-metadata` salt okunur exploratory envanter/oracle denemesidir; yönetici yetkisi gerektirir. Metadata karşılaştırması ek dosya okumaları yapar, bu süre uygulama performansı olarak yorumlanmaz.

### IndexDatabase.cs
*   **İşlev:** Verilerin kalıcı olarak saklanmasını (Persistence) sağlar.
*   **Veri Yapıları:** B-Tree (SQLite'ın disk üzerindeki yapısı).
*   **Toplu yazım:** Kompakt ilk kurulum ve uzlaştırma, dosyaları en fazla 64 kayıtlık gruplarla aynı parametreli UPSERT komutuna gönderir; son kısa grup tekil komutu kullanır. Klasör kimlikleri alt öğeler için gerektiğinden klasörler sırayla yazılır. Transaction çağırana aittir; hata veya iptal bütün işlemi geri alır. `Dispose` bekleyen veri yazmaz ve ilerleme yalnız başarılı yazımdan sonra artar. Şema, FK denetimi ve kalıcılık ayarları değişmez.

### FileWatcherService.cs
*   **İşlev:** Dosya sistemi değişikliklerini anlık izler.
*   **Veri Yapıları:** ConcurrentQueue (Thread-Safe Kuyruk), HashSet (Hariç tutulan yollar).
*   **Algoritma:** Producer-Consumer (Olayları kuyruğa atar ve işler).

### IntentParser.cs
*   **İşlev:** Doğal dil sorgularını yapılandırılmış verilere dönüştürür.
*   **Veri Yapıları:** Dictionary (Tür eşleşmeleri), HashSet (Stopwords).
*   **Algoritma:** Regex (Kural tabanlı) ve LLM Inference (Yapay zeka).

### FileTypeMapper.cs
*   **İşlev:** "Video" gibi genel terimleri uzantılara (.mp4, .avi) çevirir.
*   **Veri Yapıları:** Dictionary (Tür -> Uzantı Listesi).

### ThumbnailService.cs
*   **İşlev:** Dosya önizlemelerini oluşturur ve önbellekte saklar.
*   **Veri Yapıları:** Dictionary (Memory Cache), SemaphoreSlim (Eşzamanlılık).
*   **Algoritma:** LRU Cache benzeri mantık.

### GlobalHotkeyService.cs
*   **İşlev:** Sistem genelinde klavye kısayollarını dinler.
*   **Yapı:** Doğrudan Windows API çağrıları.

### Shell32Helper.cs
*   **İşlev:** Windows Shell işlemlerini yürütür.
*   **Yapı:** Struct (SHELLEXECUTEINFO).

## 3. Arama Motoru (Search Engine)

### CompactCatalog.cs (Kompakt katalog)
*   **Bellek düzeni:** Yeni kataloglar sürüm 3 biçiminde, öğe başına 64 baytlık sabit kayıtlarla yazılır. Ad/yol metni ve arama listeleri ayrıca saklanır; 64 bayt toplam öğe maliyeti değildir. Çocuk aralıkları yalnız alt öğesi bulunan ebeveynler için tutulur; token sayısı ardışık ofsetlerden çıkarılır. Boyut, tarih hassasiyeti, `DateTimeKind`, açılma sayısı ve UTF-16 kod birimleri korunur.
*   **Arama ve uyumluluk:** Yeni kataloglarda mevcut varint posting codec'i varsayılandır. Okuyucu eski sürüm 2 / 80 bayt kayıtları da açabilir. SQLite şeması değişmez.
*   **Hazır katalogdan açılış:** Normal kompakt modda, temiz kapanışta watcher ve uzlaştırma durduktan sonra temel katalog ve sınırlı değişiklik katmanı saklanır. Sonraki açılış, DB SHA256'sı, kökler, tokenizer/Core sürümü, kültür ve saat dilimi eşleşirse kataloğu salt okunur bellek eşlemesiyle açar. Eşleştirme sırasında tam yol dizeleri yerine tekrar kullanılan bir UTF-16 tamponu hash üretir. Dolu WAL, eksik/bozuk veya eski cache, mevcut SQLite yüklemesine döndürür; açılışın watcher/USN devri aynen yürür.
*   **Cache dosyaları:** DB yanında `index.db.catalog.<kimlik>.bin` ve `index.db.catalog.meta` bulunur. Her temel dosya değişmez bir kimlik alır; manifest en son atomik değiştirilir. Önceki okuyucular kullandıkları eşlemeyi korur, kullanılmayan eski dosyalar sonraki yükleme/kayıtta temizlenir. Dosyalar yeniden üretilebilir ek disk alanıdır; SQLite'ın yerini almaz. Anormal kapanış veya sürüm değişikliği sonrası hazır cache kullanılacağı garanti edilmez. Ölçüm yol güvenliği etkin profiller ve özel tokenizer'lar bu cache'i kullanmaz.
*   **Ölçüm:** `directory-probe` için isteğe bağlı `--catalog-output <yeni-dosya>` katalog bölümlerini, öğe başına toplam baytı ve örnek sorguları raporlar. Katalog dışa aktarımı, zorlanmış GC ve sorgu incelemesi bootstrap süre/tahsis ölçümünden sonra yapılır. `LiveCoreManagedBytesAfterForcedGc` yalnız ölçüm sürecindeki yaşayan yönetilen Core belleğidir; WPF uygulamasının toplam RAM'i değildir.

### SearchEngine.cs (Temel Arama)
*   **İşlev:** Basit ve hızlı kelime bazlı arama.
*   **Algoritma:** Tokenize -> Inverted Index Sorgusu -> Scoring -> Sıralama.
*   **Veri Yapıları:** PriorityQueue (Sıralama), Dictionary (Eşleşme takibi).

### AdvancedSearchEngine.cs (Gelişmiş Arama)
*   **İşlev:** Yapılandırılmış sorguları (StructuredQuery) işler.
*   **Özellikler:** Filtreleme (Tür, Tarih, Boyut), Filter-Only Mode, AI Entegrasyonu.
*   **Algoritma:** Aday bulma -> LINQ ile filtreleme.

### BasicTokenizer.cs
*   **İşlev:** Metni anlamlı parçalara (token) böler.
*   **Özellikler:** Türkçe karakter desteği, büyük/küçük harf duyarsız.

### BasicScoringStrategy.cs
*   **İşlev:** Dosya alaka düzeyini puanlar.
*   **Formül:** Tam Eşleşme (100) + Kısmi Eşleşme (25) + Sıklık Bonusu (Açılma * 2).

### Arayüzler
*   **ITokenizer:** Parçalayıcı bağımlılığını yönetir.
*   **IScoringStrategy:** Puanlama mantığını soyutlar.

## 4. Veri Modelleri (Models)

*   **FileSystemNode.cs:** N-ary Tree düğümü. Dosya hiyerarşisini modeller.
*   **StructuredQuery.cs:** `List<string>` ile anahtar kelime ve filtreleri tutar.
*   **SearchResult.cs:** Arama sonuçlarını taşıyan DTO. PriorityQueue içinde kullanılır.
*   **FileMetadata.cs:** Temel dosya bilgilerini tutar. Dictionary içinde saklanır.
*   **FileChangeEvent.cs:** Dosya olaylarını taşır. ConcurrentQueue içinde saklanır.
*   **IndexedDirectory.cs / IndexedFile.cs:** Veritabanı tablo satırları (B-Tree üzerinde saklanır).
*   **IndexMetadata.cs:** Anahtar-Değer ayarları.

## 5. Veri Yapıları İmplementasyonu

### InvertedIndex.cs
Arama motorunun kalbidir. Hızlı arama ve silme için iki yapıyı bir arada kullanır.

#### A. Ana İndeks (Forward Index)
*   **Yapı:** `Dictionary<string, List<FileSystemNode>>`
*   **İşlev:** Token -> Dosya Listesi.
*   **Karmaşıklık:** Ekleme ve Arama $O(1)$.

#### B. Ters İndeks (Reverse Index / Node Map)
*   **Yapı:** `Dictionary<string, HashSet<string>>`
*   **İşlev:** Dosya Yolu -> Token Kümesi.
*   **Amaç:** Dosya silindiğinde veya değiştiğinde, ilgili tokenları hızlıca bulup temizlemek.
*   **Karmaşıklık:** Silme $O(T)$ (T: Kelime sayısı). Bu yapı olmasaydı $O(N)$ olurdu.

#### Algoritmalar
*   **Add:** İki yapıyı senkronize ekler.
*   **RemoveByPath:** Önce ters indeksten kelimeleri bulur, sonra ana indeksten temizler (Çift yönlü haritalama).
