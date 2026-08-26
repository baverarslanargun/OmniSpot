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
- `UI/Services/DiagnosticsCollector` — süreç, indeks ve küçük resim
  sayaçlarını toplayıp `DiagnosticsMetrics`'e yazar.
- `UI/Services/DiagnosticsSession` — iki günlüğü, toplayıcıyı ve örnekleme
  zamanlayıcısını birlikte tutar. Zamanlayıcı burada olduğu için günlükleme
  tanılama penceresi kapatılınca durmaz; yazma bir **ayar**, pencere yalnız
  görüntüleyici. `RecordFolder` hem sayaçları tazeler hem `OLAY` işaretçisi
  düşer.
- `UI/Views/DiagnosticsWindow` — ayrı pencere; solda `ApplicationLog` akışı,
  sağda metrikler. Ana pencereyi kapatmadığı için uygulama kullanılırken
  sayaçlar izlenebilir.

Her iki günlük de `FileShare.ReadWrite` ile açılır; dosyalar yazılırken
dışarıdan okunabilir.

`ApplicationLog` damgayı tek yerde üretir (`[SS:dd:ss.fff]`), hem pencere hem
dosya aynı satırı görür. Bellekte tuttuğu geçmiş `MaxRetainedMessages` ile
sınırlıdır.

## Gelecek (TODO)
- TF-IDF & gelişmiş skor: `IScoringStrategy`.
- Türkçe morfoloji: yeni tokenizer implementasyonu.
- Trie autocomplete: ek veri yapısı.
- Ses komutları: ayrı service.
