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

## Gelecek (TODO)
- TF-IDF & gelişmiş skor: `IScoringStrategy`.
- Türkçe morfoloji: yeni tokenizer implementasyonu.
- Trie autocomplete: ek veri yapısı.
- Ses komutları: ayrı service.
