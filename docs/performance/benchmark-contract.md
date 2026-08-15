# OmniSpot tekrarlanabilir benchmark sözleşmesi

- Sürüm: `0.1`
- Durum: Onaylı ölçüm pilotu öncesi sözleşme
- Tarih: `2026-08-14`
- Source snapshot: `be08a719d9ca588709f013d5451041b6dcc1ef58`

> Bu belge benchmark koduyla birlikte sürümlenir. Ürün davranışı konusunda bu
> belge ile güncel source, production composition veya ilgili test çelişirse
> source, production composition ve test esastır; sözleşme aynı değişiklik
> kapsamında düzeltilir.

Bu sürüm benchmark sonucu veya ürün bütçesi tanımlamaz. Önce neyin, hangi
gizlilik sınırında ve hangi geçerlilik kapılarıyla ölçüleceğini dondurur.
Tekrar sayıları, GC modu ve regresyon eşikleri B-2 pilotundan sonra `1.0`
sürümünde kesinleşecektir.

## 1. Amaç ve kapsam

Amaç; indeksleme, reconciliation, watcher olayları, değişmez arama durumu,
arama çağrıları, SQLite boyutu ve bellek davranışı için aynı makinede
tekrarlanabilir önce/sonra karşılaştırmaları üretmektir.

Bu turun içinde:

- gerçek kullanıcı ağacından yalnız sayısal ve gizlilik korumalı metadata
  profili çıkarmak;
- profile dayalı, seed'li sentetik bellek ve disk corpus'u üretmek;
- headless Core senaryolarını ve arama mikro ölçümlerini çalıştırmak;
- bağımsız full-scan oracle ile doğruluğu performanstan önce doğrulamak;
- ham ölçümleri ve ortam manifestini saklamak.

Bu turun dışında:

- UI frame-time ve tuş→piksel gecikmesi;
- AI/Groq ağ gecikmesi;
- çoklu cihaz ve mutlak ürün bütçeleri;
- gerçek kullanıcı dosya içeriği;
- gerçek kullanıcı adlarının veya yollarının benchmark corpus'una kopyalanması.

## 2. Source of truth ve güncel entegrasyon yüzeyi

Knowledge graph branch metadata'sı bu snapshot'ta repo HEAD'inden geridedir.
Bu nedenle graph yalnız hedef bulmak için kullanılmış, aşağıdaki sınırlar
güncel source ve yakın testlerden doğrulanmıştır.

- Production composition `BasicTokenizer`, `IndexManager`,
  `IndexedLocationProvider` ve `IndexLifecycleService` kullanır.
- `IIndexLifecycleService`; `InitializeAsync`, `EnsureSyncedAsync`,
  `GetIndexedRoots`, `CreateSearchState`, `CreateSearchSnapshot` ve
  `GetStats` yüzeylerini sunar.
- `SearchState.Create(IEnumerable<FileSystemNode>, ITokenizer)`,
  `Get`, `GetPartial` ve `GetFuzzy` public ve headless ölçüme uygundur.
- `SearchState.Create`, bir öğenin tokenlarını
  `OrdinalIgnoreCase` benzersiz kümeye dönüştürür. Profildeki token
  istatistikleri de aynı semantiği kullanır.
- İlk indeks hazırlığında arama durumu
  `PublishSearchStateFromCurrentIndex` içinde `Volatile.Write` ile
  yayımlanır; watcher kurulumu ve background reconciliation bundan sonra
  başlar.
- `PublishSearchStateFromCurrentIndex`, tokenizasyon alt süresi ve
  `_lock` tutma süresi private sınırlar olduğundan yalnız kesin in-situ
  ayrıştırmaları B-5 enstrümantasyon kararı gerektirir. R5'in karar-kritik
  toplam süre ve allocation ölçümü public `SearchState.Create` üzerinden
  B-5 beklemeden yapılır.
- En yakın mevcut doğruluk kanıtları
  `SearchBehaviorTests`, `SearchConcurrencyTests` ve
  `IndexManagerReconciliationTests` içindedir. Türkçe `I/İ/ı/i`
  regresyonu için özel test bulunmadığı sözleşmede açık test boşluğudur.

## 3. Repo ve çıktı yerleşimi

- Araç projesi: `Tools/OmniSpot.Benchmarking`
- Varsayılan solution: araç projeye eklenmez; normal build/test yavaşlamaz.
- Sözleşme: `docs/performance/benchmark-contract.md`
- Ham ve özet sonuçlar: `notes-local/benchmarks/`
- Gerçek ağaç profilleri: `notes-local/benchmarks/profiles/`
- Sentetik corpus çalışma alanı: OmniSpot'un indekslediği köklerin dışında,
  kullanıcı tarafından seçilen geçici dizin.

İlk uygulama tek executable ve alt komutlardan oluşur: `profile`, `pilot`,
`measure`, `compare`, `phases`, `corpus`, `run` ve `verify`. Mikro ölçüm
`measure` komutunun içindedir; gerçek bir izolasyon sorunu ölçülmeden ikinci
proje açılmaz.

## 4. Gerçek ağaç profili gizlilik sözleşmesi

### 4.1 Kesin yasaklar

- Dosya içeriği hiçbir koşulda açılmaz.
- Dosya adı, klasör adı, tam/kısmi path, kullanıcı adı, ad/path hash'i,
  örnek token veya bunları geri üretmeye yarayacak sıralı örnek diske
  yazılmaz.
- Hata mesajları path içerebileceğinden kalıcı çıktıya yazılmaz; yalnız
  sabit hata sınıfı ve sayısı tutulur.
- Gerçek `index.db` açılmaz, değiştirilmez veya ölçüm girdisi yapılmaz.
- Çalışan OmniSpot süreci durdurulmaz ve indeks köklerine sentetik veri
  eklenmez.

Kullanıcı ağacından türeyen kalıcı değerler sayısaldır. JSON alan adları,
şema sürümü, önceden tanımlı sabit enumlar ve aşağıdaki eşiği geçen yaygın
uzantılar bu kuralın yapısal istisnasıdır.

### 4.2 Tarama onayı ve kök manifesti

- Komut önce çözümlenen kökleri ekranda gösterir. Kullanıcı profili altındaki
  yollar varsayılan olarak `%USERPROFILE%` ile maskelenir; profil dışındaki
  yollar `<gizli-path>` olarak gösterilir.
- Gerçek kök ve çıktı yolları yalnız açık `--show-paths` ile terminale yazılır.
  Bu seçenek agent, CI veya paylaşılan terminal kaydında kullanılmamalıdır ve
  kalıcı profil JSON'unun içeriğini değiştirmez.
- Açık etkileşimli onay veya otomasyonda açık `--yes` olmadan tarama
  başlamaz.
- Kök path'leri profile yazılmaz.
- Manifestte yalnız sabit mantıksal tür
  (`desktop`, `documents`, `downloads`, `pictures`, `music`,
  `videos`, `custom`), ordinal sıra ve kök başına sayımlar bulunur.
- Birden fazla custom kök `custom-1`, `custom-2` biçiminde ayrılır;
  gerçek path saklanmaz.

### 4.3 Uzantı gizlilik eşiği

Bir uzantı ancak hem en az 50 dosyada hem de taranan dosyaların en az
`%0,1`'inde görülüyorsa açık yazılabilir. İki koşuldan birini geçemeyen
uzantılar `other` altında toplanır. Uzantısız dosyalar ayrı sayısal kovadır.
Eşik altındaki uzantılar top-N listesine veya hata çıktısına sızdırılmaz.

### 4.4 Bellekte işleme

Ad ve tokenlar yalnız ilgili giriş işlenirken veya exact sayım için gereken
process belleğinde tutulabilir; uygulama tarafından dosyaya, loga, telemetry'ye
ya da crash artifact'ine bilerek yazılmaz. Process dışı sistem crash dump'ları
bu aracın ürettiği paylaşılabilir çıktı sayılmaz ve benchmark çalışırken
toplanmamalıdır.

## 5. B-1 zorunlu profil şeması

Dağılımlar sabit kovalı histogramlarla birlikte `p50`, `p90`, `p95`,
`p99` ve `max` değerlerini taşır. Yüzdelik hesap yöntemi şema sürümüyle
birlikte sabitlenir. Dosya ve klasör sonuçları anlamlı olduğu her yerde ayrı
raporlanır.

Şema `2.0`/`2.1` ve profiler `0.3.1`/`0.4.0` yüzdelikleri nearest-rank
yöntemiyle hesaplar:
`rank = ceil(p * count)` ve sıralı örnekte 1 tabanlı `rank` değerindeki öğe
seçilir. Dosya boyutunda `0` ayrı kovadır; pozitif bir byte değeri
`floor(log2(bytes))` üslü `2^n` kovasına girer. Oran alanları JSON'da `0..1`
aralığında tutulur; insan-okunur özette yüzdeye çevrilir.

Makine-okunur JSON source of truth'tur. Aynı profilin `--print` çıktısı;
manifest, temel ağaç dağılımları, token fan-out, Türkçe göstergeleri, uzantı
özeti ve özel durumları tek oturuşta gözle denetlenebilir biçimde gösterir.
Özet en fazla 120 satır ve 16 KiB UTF-8 olur; kovalar taşarsa ayrıntı JSON'da
kalır ve özet sabit üst-kuyruk göstergelerini korur. `--print` kendiliğinden
dosya yazmaz. Bu sınırlar profiler kabul testinin parçasıdır; şemaya yeni alan
eklemek insan-okunur özeti sessizce büyütemez. `--print` tek başına dosya
yazmaz; açık bir `--output` ile birlikte verildiğinde aynı tek tarama geçişinde
özet basılır ve makine-okunur profil JSON'u ayrıca yazılır.

Uzantı özeti en fazla ilk 20 yayımlanabilir uzantıyı tek tek gösterir. Kalan
yayımlanabilir uzantılar için uzantı adlarını açmadan adet, toplam dosya sayısı
ve toplam dosya oranı yazılır. Görünen uzantı dosyaları + yalnız JSON'da kalan
yayımlanabilir uzantı dosyaları + `other` + uzantısız dosyalar toplamı her zaman
`file_count` değerine eşit olmalıdır.

### 5.1 Ağaç ve metadata

- toplam öğe, dosya ve klasör sayısı;
- kök başına öğe sayıları;
- dizin derinliği;
- dizin başına doğrudan dosya, klasör ve toplam çocuk sayısı;
- dosya ve klasör adı uzunluğu;
- dosya boyutu için sıfır boyut kovası ve log2 boyut kovaları;
- eşik üstü uzantı dağılımı, `other` ve uzantısız sayısı;
- hidden ve system öznitelik oranları;
- erişilemeyen dizin ve metadata okuma hatası sayıları.

Ad uzunluğu .NET `string.Length` ile, yani UTF-16 code unit olarak ölçülür.
Derinlik her tarama kökünde `0`'dan başlar. Dosya boyutu yalnız metadata'dan
okunur.

### 5.2 Token fan-out ve çakışma

Bu grup B-1'in en yüksek öncelikli çıktısıdır. Production'daki
`BasicTokenizer` kullanılır; her öğe için yinelenen tokenlar
`SearchState.Create` ile aynı biçimde tekilleştirilir.

Bir token için belge frekansı:

`df(t) = token t'yi içeren farklı öğe sayısı`

Kalıcı çıktıda hiçbir token değeri bulunmaz. Zorunlu sayılar:

- token üreten öğe sayısı ve oranı;
- öğe başına benzersiz token sayısı dağılımı;
- benzersiz token sayısı;
- toplam token→öğe bağlantısı: `sum(df(t))`;
- token başına öğe sayısı dağılımı: `p50/p90/p95/p99/max`;
- sabit `df` histogramı:
  `1`, `2`, `3-4`, `5-8`, `9-16`, `17-32`, `33-64`,
  `65-128`, `129-256`, `257-512`, `513-1024`, `1025+`;
- her `df` kovasında token sayısı ve token→öğe bağlantısı sayısı;
- tekil token oranı:
  `count(df(t) = 1) / unique_token_count`;
- yinelenen bağlantı oranı:
  `(sum(df(t)) - unique_token_count) / sum(df(t))`;
- paylaşılan-token bağlantı oranı:
  `sum(df(t), df(t) >= 2) / sum(df(t))`.

Tek bir “çakışma oranı” bu dağılımın yerine geçemez. Corpus generator,
özellikle `df` histogramını ve üst yüzdelikleri hedeflemelidir; ortalama
tek başına yeterli değildir.

### 5.3 Türkçe ve harf durumu göstergeleri

Dosyalarda son uzantı çıkarıldıktan sonraki gövde, klasörlerde tam ad
“ad çekirdeği” kabul edilir. En az bir harf içeren çekirdekler için:

- Türkçe'ye özgü `çÇğĞıİöÖşŞüÜ` karakterlerinden birini içeren ad oranı;
- `I`, `İ`, `ı` veya `i` karakterlerinden birini içeren ad oranı;
- harflerinin tamamı Unicode büyük harf olan ad oranı; harf olmayan
  karakterler yok sayılır;
- `tr-TR` küçük harfe çevirme sonucu invariant küçük harften farklı olan
  ad oranı;
- yalnız ASCII karakter içeren ve ASCII dışı karakter içeren ad oranları.

Bu göstergeler R8 için corpus katmanını ve
`turkish.dotted-i` / `turkish.diacritic` sorgu bucket'larını doğrudan
belirler. `INDEX_RAPORU.txt` benzeri sentetik canary adları bu dağılımdan
bağımsız olarak her corpus sürümünde bulunur.

### 5.4 Özel durumlar

- reparse point toplamı; güvenle ayırt edilebildiğinde junction, symlink ve
  other sayıları;
- aynı parent altında `Ordinal` olarak farklı,
  `OrdinalIgnoreCase` olarak eşit case-only çift sayısı;
- mutlak path uzunluğu 260 karakteri aşan öğe sayısı;
- whitespace içeren ad, `%` içeren ad;
- hidden/system öğe sayısı ve oranı;
- erişilemeyen dizin sayısı.

Reparse tag okunmadığı bir profiler sürümünde toplam sayı yine ölçülür; subtype
alanları `null` olur ve insan-okunur özet ayrımın ölçülmediğini açıkça söyler.
`0`, bilinmeyen subtype sayısı yerine kullanılamaz. Toplam reparse sayısı `0`
ise subtype sayılarının da `0` olduğu kesin kabul edilir.

Yerel yetenek probunda standard user ile junction ve sparse file üretimi
başarılı, mevcut çağrı yoluyla symlink üretimi başarısız ve geçici dizin
case-insensitive bulunmuştur. Bu nedenle junction ve sparse varsayılan;
symlink, aynı anda case-only çift ve erişim-reddi strata'sı capability-gated
ve opt-in olacaktır. Case-only rename operation trace'i ayrıca korunur.

## 6. Profil manifesti ve determinism

Manifest kullanıcı verisi taşımadan şunları kaydeder:

- profil şema major/minor sürümü;
- profiler/generator sürümü;
- tarama başlangıç ve bitiş Unix zamanı ile süre;
- mantıksal kök türleri ve sayıları;
- tamamlanan, atlanan ve erişilemeyen öğe sayıları;
- OS build, .NET SDK/runtime, process architecture;
- CPU, mantıksal çekirdek, RAM ve taranan kök volume'lerinin disk türü;
- GC modu, power plan, Defender realtime ve Windows Search durumu;
- CPU nominal taban MHz'i, koşum başı/sonu yük altı gerçek MHz'i ve AC/DC
  `PROCTHROTTLEMAX` başlangıç/bitiş yüzdeleri;
- anlamlı koşum içi frekans veya frekans politikası kaymasında
  `frekans-kaymasi` etiketi;
- çalışan OmniSpot örneği var/yok;
- repo HEAD ve tracked/untracked Git status girişlerini kapsayan yalnız dirty
  boolean/count; dirty dosya adları yazılmaz.

Disk türü, her farklı yerel kök volume'ü için Windows Storage cmdlet'lerinden
okunan media type değerlerinin privacy-safe birleşimidir: `ssd`, `hdd`, `scm`
veya birden fazla tür varsa `mixed`. UNC/çözülemeyen volume, bilinmeyen media
type, komut yokluğu, izin hatası veya timeout durumunda `disk_kind=null` olur.
Defender cmdlet'i realtime durumunu güvenle döndüremezse
`defender_realtime_enabled=null` olur. Bu alanlarda `null`, “kapalı” değil
“ölçülemedi” demektir; boolean `false` yalnız başarılı probe sonucudur.
`duration_milliseconds` yalnız ağaç taramasını ölçer; taramayı çevreleyen ortam
ve yük altı frekans problarının yaklaşık maliyetini içermez.

Ağaç değişmediğinde iki koşumun deterministik kabulü, volatile manifest
alanları (zaman, süre ve frekans ölçümleri) kapsam dışındayken canonical
`metrics` byte'larının birebir eşitliğidir. Parmak izleri aynı canonical metrics
sözleşmesini kullanan şema/profiler çiftlerinde karşılaştırılır. Yalnız manifest
alanı ekleyen ve bu belgede açıkça uyumlu ilan edilen minor geçiş bunun
istisnasıdır. Karşılaştırma için ad/path hash'i üretilmez.

Şema `2.0` ve `2.1`, bu eşitliği tek bakışta denetlemek için `metrics_fingerprint`
alanını `ProfileDocument` kökünde taşır; alan `metrics` içine girmez ve manifest
veya kök sayımlarını kapsamaz. Canonical girdi, yalnız `metrics` nesnesinin
`ProfileJson.SerializeCanonicalMetrics(metrics)` sonucu olan sıkıştırılmış UTF-8
byte'larıdır. Canonical biçim girintisizdir; yapısal whitespace, CRLF/LF, BOM
ve son satır sonu içermez. Property ve enum yazımı `snake_case_lower` olarak
sabittir.
Parmak izi SHA-256 ve 64 karakter lowercase hex biçimindedir. Bu byte'ları
değiştiren serializer ayarı veya metrics sözleşmesi değişikliği şema sürümünü
değiştirmeden yapılamaz. Şema `2.0` / profiler `0.3.1` ile şema `2.1` /
profiler `0.4.0` arasındaki tek fark manifest frekans alanlarıdır; canonical
`metrics` sözleşmesi değişmediği için bu iki çiftin parmak izleri
karşılaştırılabilir. Başka sürüm çiftleri açık uyumluluk kaydı olmadan
karşılaştırılmaz.

Şema `0.2` çıktıları ile schema `1.0` / profiler `0.3.0` tek-A çıktısı teslim
edilmemiş, superseded exploratory profillerdir ve `2.0` ile karşılaştırılmaz.
İlk kararlı şemada culture-fold alanları
`culture_fold_difference_name_count` ve
`culture_fold_difference_name_ratio` adlarını taşır.

## 7. Metrik sözlüğü v0.1

| Metrik | Başlangıç | Bitiş | Geçerlilik notu |
|---|---|---|---|
| `ttfi.cold` | public `IndexManager.InitializeAsync` girişi | yeni `SearchState` değerinin `Volatile.Write` ile yayını | Boş benchmark DB/app cache; OS file cache'in soğuk olduğu iddia edilmez |
| `ttfi.warm` | aynı | aynı | Uygun ve dolu benchmark cache'i |
| `startup.ready` | aynı | `InitializeAsync` dönüşü | Watcher kurulmuş, background reconciliation başlatılmış |
| `publish.create.public` | kontrollü public `SearchState.Create` çağrısı | dönüşü | R5'in birincil toplam süre metriği; B-5 gerekmez |
| `publish.full` | `PublishSearchStateFromCurrentIndex` girişi | dönüşü | In-situ ek maliyet; private sınır nedeniyle B-5 veya eşdeğer kontrollü probe gerekir |
| `publish.tokenize` | `SearchState.Create` öğe tokenizasyonu | son öğenin tokenizasyonu | `publish.full` alt span'i |
| `alloc.publish` | kontrollü `SearchState.Create` çağrısı öncesi | çağrı sonrası | Transient allocation; steady RAM değildir |
| `event.searchable` | olayın watcher kuyruğuna kabulü | ilgili sentetik path'in yayımlanmış state'te bulunması | p50/p95; oracle geçmeli |
| `event.burst.saturation` | artan sentetik olay hızı | dondurulmuş latency eşiğinin ilk kalıcı aşımı | Eşik v1.0'da |
| `reconcile.noop` | değişikliksiz reconciliation girişi | dönüşü ve oracle pass | |
| `reconcile.converge` | seed'li delta enjeksiyonu | oracle eşitliği | |
| `lock.apply.hold` | ilgili `_lock` alınışı | bırakılışı | B-5; Core değişikliği öncesi ayrı kullanıcı onayı |
| `mem.steady.managed` | init + zorlanmış GC + dondurulmuş idle | ölçüm anı | MB; idle v1.0'da |
| `mem.workingset` | aynı | aynı | Process working set; managed heap değildir |
| `db.size` | koşum sonu | `index.db + -wal + -shm` toplamı | Bayt/MB |
| `search.first` | headless search çağrısı | sonuç listesinin dönüşü | Bucket başına p50/p95 |
| `oracle.equivalence` | senaryo sonu | bağımsız full-scan karşılaştırması | Pass/fail performans kapısı |

`alloc.*` ve `mem.*` aynı başlık altında birleştirilemez.

R5, B-5'e bağlı değildir. `publish.create.public` ve `alloc.publish` aynı
public `SearchState.Create` yüzeyinde B-2'den itibaren ölçülür ve R5'in ana
kararını besler. B-5 yalnız production akışındaki lock, state yayını ve
tokenizasyon gibi alt span'leri birbirinden ayırmak; public ölçüm ile in-situ
ölçüm arasındaki ek maliyeti açıklamak için gerekir.

`publish.tokenize` ve `SearchState.Create`'in diğer fazları, B-5 beklemeden
`phases` komutuyla **instrumented profiler lane'inde** ölçülür (protokol
"Deney düzeni": instrumented profiler run ile düşük-overhead timing run
ayrılır). Bu lane in-situ span üretmez: aynı fixture üzerinde kümülatif
replika koşumlarının farkını raporlar ve replikanın üretimi birebir
yansıttığı, `SearchState.Create` çıktısına karşı doğruluk kapısıyla
doğrulanır. Bu lane süre regresyon kararı vermez; çıktısı faz payıdır.

## 8. Ölçüm rejimi

### 8.1 Dondurulan kurallar

- Karşılaştırılabilir ölçümler `Release`, x64 ve aynı makinede alınır.
- Her koşum bütün ham örnekleri ve ortam manifestini saklar.
- Doğruluk kapısını geçmeyen koşum özet performans tablosuna giremez.
- Dirty çalışma ağacı sonucu exploratory'dir; kalıcı baseline olamaz.
- Disk I/O içeren koşumda `disk_kind` veya `defender_realtime_enabled` null ise
  sonuç exploratory'dir; kalıcı disk baseline'ı olamaz.
- Ortalama tek başına raporlanmaz. Birincil özet medyan ve yeterli örnek
  sayısı dondurulduktan sonra p95'tir.
- Durumlu dosya sistemi koşumlarında outlier silinmez; gürültü de ölçümün
  parçasıdır. Ayrık sistem olayı varsa örnek silinmek yerine işaretlenir.
- Koşum içinde yük altı CPU frekansı `%2`'den fazla değişirse veya AC/DC
  `PROCTHROTTLEMAX` politikası değişirse sonuç `frekans-kaymasi` etiketi alır;
  örnekler sessizce silinmez ve sonuç kalıcı baseline olamaz.
- “Cold”, yalnız boş app DB/cache anlamına gelir. OS cache'i temizlenmediyse
  “disk cold” veya “physical cold” denmez.
- Tek makine sonuçları mutlak kullanıcı cihazı bütçesi üretmez.

### 8.2 B-2 pilotunda dondurulacaklar

Ortam manifesti benchmark oturumunda warmup ve ölçüm iterasyonlarının dışında
alınır: sabit alanlar başlangıçta bir kez, frekans ve `PROCTHROTTLEMAX` ise
başlangıçta ve bitişte ölçülür. Bu bir çalışma kuralıdır; aşağıdaki sekiz
karardan biri değildir. B-1 profiler aynı alanları tek taramayı çevreleyecek
biçimde alır.

B-2 karşılaştırması sayı üretmeden önce fixture parmak izi, ortam manifesti,
contract+tool sürümü ve CPU frekans politikası/frekans uyumu olmak üzere dört
guard'ı geçer. Pilot turu 2 boyunca AC ve DC `PROCTHROTTLEMAX` `%99` kalır;
InProcess toolchain kullanılmaz.

Pilotun donduracağı sekiz karar:

- Workstation/Server ve concurrent açık/kapalı GC kombinasyonlarından biri;
- custom probe ve diagnoser overhead'i;
- stateful warmup ve ölçüm tekrar sayıları;
- p95 için asgari örnek sayısı ve yüzdelik yöntemi;
- BDN outlier ayarı;
- iki koşum varyans bandı;
- regresyon eşiği ve kaç ardışık aşımın regresyon sayılacağı;
- steady-memory idle süresi.

### 8.3 B-2 sonucu — pilot kapatıldı, rejim dondurulmadı (`2026-08-15`)

Yedi pilot turu, `79` dakika ölçüm ve `93` BenchmarkDotNet koşumu sonunda
**kabul edilmiş bir minimum ölçülebilir fark üretilemedi.** Sonuç cümlesi:
*geliştirici laptopunda küçük süre farkları güvenilir biçimde kalibre
edilemedi; allocation güvenilir ana metriktir.*

- `8.2`'deki sekiz kararın hiçbiri dondurulmadı. Bu belge `1.0`'a
  **çıkarılmaz**, `0.1` kalır.
- Süre için kalıcı regresyon kapısı bu makinede kurulamadı. `R5` gibi ürün
  kararları allocation ve `phases` faz payı üzerinden verilir; süre yalnız
  ikincil ve büyüklük mertebesi göstergesidir.
- Korunan zemin: parmak izli sentetik fixture, ortam manifesti (CPU frekans
  alanları dahil), allocation ölçümü, `compare` guard'ları ve `phases`
  profiler lane'i.
- `pilot` komutu ve canary otomasyonu **deneysel** kalır; ürün geliştirmesinin
  kapısı değildir.

Pilot turlarının kalıcı iki bulgusu:

- **Doğrulandı:** eşleştirilmiş (paired) tasarım canary sinyalinin yönünü
  düzeltti. Önceki sıralı tasarımda bilerek yavaşlatılmış kod baseline'dan
  hızlı görünüyordu.
- **Çürütüldü:** driftin CPU turbo değişiminden geldiği hipotezi — frekans
  `3195→3195 MHz` sabitken de canary yakalanamadı. Driftin Defender kaynaklı
  olduğu iddiası da B-1 manifestiyle düştü (`defender_realtime_enabled: false`).
  Frekans sabitleme yine de doğru bir önlemdir, ama kök neden değildi.

## 9. Araç seçimi

- CLI: exact-pin `System.CommandLine 2.0.11`
- Mikro ölçüm: exact-pin `BenchmarkDotNet 0.15.8`
- Metadata enumeration: .NET `System.IO.Enumeration` ve
  `EnumerationOptions`
- Derin teşhis: makinede bulunan WPR/WPA; normal koşumun bağımlılığı değildir
- `dotnet-counters`, `dotnet-trace`, `dotnet-gcdump`: yalnız ihtiyaç
  doğarsa ayrı ve sürümlü tanılama aracı

`BenchmarkDotNet`; `SearchState.Create`, `Get`, `GetPartial`,
`GetFuzzy` ve allocation mikro ölçümlerinde kullanılır. Soğuk tarama,
watcher, reconciliation ve oracle gibi durumlu senaryolar özel headless runner
ile yürütülür.
`SearchState.Create` pilotu varsayılan out-of-process toolchain ile çalışır ve
`DontEnforcePowerPlan` kullanarak makinenin power plan'ını değiştirmez.
`InProcessEmitToolchain`, süreç izolasyonu ve allocation ölçümünü zayıflatacağı
için B-2 pilot rejiminin parçası değildir.

`Bogus` ana corpus generator değildir: locale fallback'i ve sürümle
deterministik dizinin değişebilmesi corpus sözleşmesini gereksiz yere dış
pakete bağlar. `DiskSpd` ham storage yükü ölçtüğü, OmniSpot'un uçtan uca
indeks davranışını ölçmediği için ana harness değildir. Yeni plugin gerekmez.

## 10. Corpus ve oracle sözleşmesi

- `seed + generator major version` aynı corpus ve operation trace'i üretir.
- Bellek corpus'u diske yazmaz; arama/publish ölçümleri içindir.
- Disk corpus'u gerçek benchmark DB'si ve indeks kökleri dışında kurulur.
- Generator değişikliği golden küçük-corpus testiyle yakalanır. Bilinçli
  determinism kırılması generator major sürümünü artırır.
- B-3 disk katmanının ilk işi 50.000 öğelik erken A5 probudur. Gerçek üretim
  süresi, silme süresi, tepe logical/allocated boyut ve hata oranı ölçülür;
  aynı profile göre 500.000 öğenin planlama maliyeti hesaplanır. Bu izdüşüm
  benchmark sonucu sayılmaz; gerçek 500k turunun kurulup kurulmayacağına
  B-3 içinde, full harness beklenmeden karar verdirir.
- Gerçek B-1 profili corpus parametrelerinde birincil kaynaktır. FAST metadata
  çalışmaları yalnız eksik parametreler için prior ve sensitivity aralığıdır.
- Full-scan oracle, indeksleme kodunu veya indeks DB'sini tekrar kullanmaz.
- Oracle eşitsizliğinde sentetik corpus için fark sayıları ve sentetik örnekler
  ham çıktıda kalabilir; gerçek kullanıcı profili hiçbir path örneği yazmaz.
- Oracle geçmeyen sonuçtan hız/regresyon kararı çıkarılamaz.

## 11. Beş aşama ve karar kapıları

1. **Sözleşme v0.1:** Bu belge, README bağlantısı ve source sınırı doğrulaması.
2. **B-1 profiler:** Privacy testleri, iki-koşum determinism ve kullanıcı
   onaylı gerçek tarama.
3. **B-2 pilot:** GC/overhead/gürültü ölçümü. **Kapatıldı (`8.3`):** rejim
   dondurulamadı, belge `0.1` kaldı; allocation ve faz payı birincil karar
   metriği oldu.
4. **B-3 corpus:** Bellek/disk generator, capability strata, golden
   determinism testi ve 50k erken A5 maliyet probu.
5. **B-4/B-5 harness:** Oracle, arama bucket'ları, A3/A4/A6, canary regresyon;
   Core enstrümantasyonu ayrıca onaylanır.

Bir aşamanın kabul ölçütü geçmeden sonraki aşamanın kalıcı koduna başlanmaz.

R1'in tamamlanması için:

1. aynı komutun iki koşumu dondurulmuş gürültü bandında kalmalı;
2. kontrollü canary yavaşlatması regresyon olarak yakalanmalı;
3. oracle fail sonucu otomatik olarak performans özetinden dışlanmalıdır.

## 12. Değişiklik yönetimi

- Metrik anlamı, profil kovası veya privacy alanı değişirse sözleşme ve şema
  aynı değişiklik kapsamında güncellenir.
- Geriye uyumsuz profil değişikliği schema major sürümünü artırır.
- Yalnız geriye uyumlu yeni alan ekleyen değişiklik schema minor sürümünü artırır.
- Benchmark sonucu contract ve tool sürümünü taşır.
- Eski sonuçlar yeni sözleşmeyle sessizce karşılaştırılmaz.
- Belge source/test ile çelişirse sonuç üretimi durdurulur; önce sözleşme
  düzeltilir.

## 13. Kaynaklar

- [BenchmarkDotNet jobs](https://benchmarkdotnet.org/articles/configs/jobs.html)
- [BenchmarkDotNet diagnosers](https://benchmarkdotnet.org/articles/configs/diagnosers.html)
- [.NET EnumerationOptions](https://learn.microsoft.com/dotnet/api/system.io.enumerationoptions)
- [A Five-Year Study of File-System Metadata, FAST '07](https://www.usenix.org/conference/fast-07/five-year-study-file-system-metadata)
- [Generating Realistic Impressions for File-System Benchmarking, FAST '09](https://www.usenix.org/event/fast09/tech/full_papers/agrawal/agrawal.pdf)
