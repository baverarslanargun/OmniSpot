# Smart File Launcher (Prototype)

## Mimari Özeti
Tamamen lokal çalışan WPF tam ekran overlay uygulaması. Core katmanı dosya sistemini N-ary ağaç, sözlükler ve ters indeks ile temsil eder. Arama motoru tokenizasyon + inverted index + PriorityQueue kullanarak sonuçları skorlar. Gelecekte TF-IDF, trie tabanlı autocomplete, Türkçe morfolojik analiz ve sesli komut eklentileri için `ITokenizer` ve `IScoringStrategy` uzatma noktaları bırakıldı.

## Veri Yapıları
- N-ary Tree (`FileSystemNode`): Dosya/klasör hiyerarşisi. Gezinti O(N).
- Dictionary (path->metadata): O(1) erişim.
- Inverted Index (token->liste): Ekleme O(1) ortalama, k token sorgu + m eşleşme toplama O(k + m).
- PriorityQueue: Skor sıralama O(m log m').

## Akış
1. Uygulama açılır, Desktop taranır, indeksler oluşturulur.
2. Kullanıcı arama kutusuna yazar, her değişimde arama tetiklenir.
3. Tokenizasyon + indeks sorgusu + skor + sonuç listesi.
4. Çift tıklama ile dosya varsayılan uygulamada açılır, kullanım frekansı artar.

## Tanılama Yüzeyi

Ölçüm ve hata ayıklama için ana pencereden bağımsız bir yüzey. Dört parça:

- `Core/Diagnostics/DiagnosticsMetrics` — bölüm/etiket sırası korunan, iş
  parçacığı güvenli metrik deposu. `Revision` yalnız **yeni** bir metrik
  eklendiğinde artar; gösterim katmanı bunu görsel ağacı ne zaman yeniden
  kuracağına karar vermek için kullanır, böylece saniyelik tazelemede kaydırma
  konumu bozulmaz.
- `Core/Diagnostics/DiagnosticsFileLog` — oturum başına tek dosya, başlıkta
  damgalar. Satırlar tek bayt dizisi hâlinde tek `Write` çağrısıyla yazılır;
  `StreamWriter` üstüne `FileStream` yığmak eşzamanlı yazarlarda satırları
  yırtıyordu (`ConcurrentWritersLoseNoLines` bunu yakalıyor). Satır başına
  `Flush` çökme dayanıklılığı içindir ve **test tarafından kanıtlanmamıştır**.
- `Core/Diagnostics/DiagnosticsMetricLog` — sayaçların zaman serisi,
  `zaman;bölüm;etiket;değer;sayısal` uzun biçim CSV. Uzun biçim seçildi çünkü
  metrikler çalışma sırasında ekleniyor (`SON KLASÖR` bölümü klasör açılana
  kadar yok); sabit başlıklı geniş tabloda sonradan gelen sütunlar kaybolurdu.
  `değer` ekrandaki metin, `sayısal` birimsiz ham değer (invariant ondalık) —
  ikisi ayrı çünkü gösterim birimi eşik geçtikçe değişiyor.
- `Core/Diagnostics/DiagnosticsRateTracker` — kümülatif sayaçlardan türev
  üretir. Farkı **gerçek geçen süreye** böler; `Refresh()` üç ayrı yerden
  (pencere `1` s, metrik zamanlayıcı, olay işaretçileri) düzensiz aralıkla
  çağrıldığı için sabit aralık varsayılamaz. İlk gözlemde, sayaç geriye
  gittiğinde (süreç/sayaç sıfırlanması) ve aralık asgari eşiğin altındaysa
  yeni değer üretmez — sonuncusunda son bilinen hızı döndürür ki `1` s
  tazelemede gösterim titremesin.
- `Core/Services/IndexDiagnosticsReport` — uzlaştırma ve yeniden yayım
  sayaçları. `changes` daha önce yalnız yerel değişkendi; pahalı yeniden
  yayımı tetikleyen koşul o olduğu için (`if (changes > 0)`) rapora alındı.
  Tarama süresi ile tur süresi ayrı tutulur, yeniden yayım süresi üçüncü bir
  ölçüdür — üçü tek sayıda toplanırsa maliyetin nereden geldiği kaybolur.
- `UI/Services/ProcessIoCounters` — `GetProcessIoCounters` P/Invoke sarmalayıcı.
  Süreç G/Ç sayaçları .NET `Process` sınıfında yoktur; harici örnekleyiciye
  gerek kalmaması için buradan okunur.
- `UI/Services/DiagnosticsCollector` — süreç, bellek, G/Ç, indeks, küçük resim
  ve arama sayaçlarını toplayıp `DiagnosticsMetrics`'e yazar. `BELLEK`
  bölümündeki `yığın`/`ayrılmış`/`parçalanma` **son GC'ye ait** değerlerdir,
  anlık değil; `toplam ayrılan` ise monoton sayaçtır ve pencere farkı doğrudan
  o aralıkta ayrılan bayttır.
- `UI/Services/DiagnosticsSession` — iki günlüğü, toplayıcıyı ve zamanlayıcıları
  birlikte tutar. Zamanlayıcı burada olduğu için günlükleme tanılama penceresi
  kapatılınca durmaz; yazma bir **ayar**, pencere yalnız görüntüleyici.
  `RecordFolder`, `RecordSearch` ve uzlaştırma durum aboneliği `OLAY` işaretçisi
  düşürür. Disk önbelleği ölçümü ayrı ve yavaş bir zamanlayıcıdadır (`60` s) ve
  yalnız pencere açıkken veya sayaç günlüğü yazarken çalışır: `thumbcache`
  altında on binlerce dosya olabildiği için her tazelemede sayılamaz.
- `UI/Views/DiagnosticsWindow` — ayrı pencere; solda `ApplicationLog` akışı,
  sağda metrikler. Ana pencereyi kapatmadığı için uygulama kullanılırken
  sayaçlar izlenebilir.

Her iki günlük de `FileShare.ReadWrite` ile açılır; dosyalar yazılırken
dışarıdan okunabilir.

### Kontrollü `bos-uretim` profili

`App.OnStartup`, `--profil bos-uretim` seçeneğini production composition
kurulmadan önce ayrıştırır. Profil geçerli bir `--tanila <koşum-dizini>` olmadan
başlatılmaz. Koşum düzeni; settings, SQLite/WAL/SHM, thumbnail cache ve tek boş
corpus kökünü `bos-uretim-data` altında oluşturur. Production APPDATA yollarıyla
çakışma, relative/sürücü-kökü yol, reparse koşum dizini veya dolu managed data
dizini fail-closed hata üretir; production profile sessiz fallback yapılmaz.
Windows yolu gerçek disk hedefine çözülür; kısa yol/sürücü takma adıyla yapılan
production çakışmaları ile herhangi bir üst klasördeki junction/symlink de
reddedilir. Corpus içindeki ilk tarama, uzlaştırma, klasör gezgini ve dosya
işlemleri reparse point üzerinden başka bir köke geçmez. UNC/device namespace
koşumları kabul edilmez; yerel koşum `FileShare.None` sahiplik dosyasını süreç
boyunca açık tutarak ikinci sürecin aynı data root'u paylaşmasını engeller.
Doğrulama ile sonraki dosya işlemi atomik tek kernel işlemi değildir; profil
hostile same-user sürece karşı sandbox güvenlik sınırı iddia etmez.

`ApplicationCompositionRoot` aynı gerçek WPF pencere, arama, SQLite, watcher,
uzlaştırma, thumbnail, tray ve hotkey servislerini kurar. Yalnız path/provider
bağları ölçüm düzenine yönelir; Windows startup registration ve indeks rebuild
ölçüm sırasında devre dışıdır. `bos-uretim` dosya işlemlerini
`RootScopedFileOperationService` ile boş corpus köküne kapatır.
`uretim-kopya`, normal `IndexedLocationProvider` ile gerçek kullanıcı
köklerini okur; `ReadOnlyFileOperationService` tüm dosya eylemlerini inner
production servise ulaştırmadan reddeder. Tanılama oturum başlığı profil ve bütün managed canonical
yolları, `OLAY` zinciri ise `profil hazır` ve indeks başlangıç/bitiş fazlarını
kaydeder.

### Kontrollü `uretim-kopya` profili

`App.OnStartup`, `--profil uretim-kopya` seçeneğini composition kurulmadan önce
ayrıştırır ve `--tanila <koşum-dizini>` olmadan başlatmaz. Orkestratör,
production kapalıyken ayrı snapshot aracı veya transactional SQLite backup ile
hazırlanmış settings kopyasını (yoksa güvenli varsayılan) ve zorunlu,
temiz/checkpoint edilmiş `index.db` dosyasını orkestratör
`uretim-kopya-data/settings` ile `uretim-kopya-data/index` altına pre-seed
eder; uygulama production verisini kopyalamaz. `index.db-wal` ve
`index.db-shm` sidecar'ları reddedilir. `uretim-kopya-data/thumbcache`
her turda boş ve izoledir; gerçek thumbnail cache okunmaz veya kopyalanmaz.
`corpus` dizini bu profilde yoktur.

İndeks yöneticisi bu profilde normal gerçek kullanıcı kökleriyle çalışır;
reparse point'ler ilk tarama, watcher, klasör gezgini ve uzlaştırmada atlanır.
Kopya SQLite dosyası schema/repair ve uzlaştırma yazılarının hedefidir.
Kullanıcı dosyası değiştiren işlemler fail-closed adapter ile reddedilir;
shell/process handoff dahil tüm dosya eylemleri engellenir. Koşum kökü, production APPDATA ve
LOCALAPPDATA yollarının fiziksel çakışması, reparse/ancestor, beklenmeyen
managed içerik ve eşzamanlı lease ihlali başlamadan reddedilir; hata halinde
production'a sessiz fallback yapılmaz.

`ApplicationLog` damgayı tek yerde üretir (`[SS:dd:ss.fff]`), hem pencere hem
dosya aynı satırı görür. Bellekte tuttuğu geçmiş `MaxRetainedMessages` ile
sınırlıdır.

## Gelecek (TODO)
- TF-IDF & gelişmiş skor: `IScoringStrategy`.
- Türkçe morfoloji: yeni tokenizer implementasyonu.
- Trie autocomplete: ek veri yapısı.
- Ses komutları: ayrı service.
