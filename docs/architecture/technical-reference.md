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

### FileSystemScanner.cs
*   **İşlev:** Diski tarayıp bellekteki veri yapılarını (Ağaç ve İndeks) inşa eder.
*   **Veri Yapıları:** N-ary Tree (Ağaç), Dictionary (Metadata), Inverted Index (Ters Dizin).
*   **Algoritma:** DFS (Depth-First Search) - Klasörleri derinlemesine (recursive) tarar.

### IndexManager.cs
*   **İşlev:** İndeksleme sürecini yönetir, veritabanı ve bellek arasındaki senkronizasyonu sağlar.
*   **Veri Yapıları:** HashSet (Senkronize dosyalar için $O(1)$), Dictionary.
*   **Algoritma:** Delta Sync - Disk ve DB arasındaki farkı bulur.

### IndexDatabase.cs
*   **İşlev:** Verilerin kalıcı olarak saklanmasını (Persistence) sağlar.
*   **Veri Yapıları:** B-Tree (SQLite'ın disk üzerindeki yapısı).

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
