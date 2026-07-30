# Veri Yapıları Analizi - OmniSpot Launcher

Bu döküman, OmniSpot projesinde kullanılan veri yapılarını, algoritmaları ve bunların kullanım amaçlarını akademik bir perspektifle açıklar.

---

## 1. Inverted Index (Ters Çevrilmiş İndeks)
**Konum:** `SmartFileLauncher.Core/DataStructures/InvertedIndex.cs`

### Tanım
Klasik arama motorlarının (Google, Elasticsearch) temelini oluşturan veri yapısıdır. Dokümanlardan kelimelere gitmek yerine, kelimelerden (token) dokümanlara giden bir haritalama sağlar.

### Yapı
```csharp
Dictionary<string, List<FileSystemNode>> _index;
```
- **Key (Anahtar):** Token (örn: "proje", "rapor", "2024")
- **Value (Değer):** Bu token'ı içeren dosyaların listesi (`List<FileSystemNode>`)

### Karmaşıklık Analizi
- **Ekleme (Insert):** $O(1)$ (Amortized) - Dictionary lookup + List append.
- **Arama (Lookup):** $O(1)$ - Token'a karşılık gelen listeyi döndürür.
- **Kesişim (Intersection):** Çok kelimeli aramalarda (örn: "yıllık rapor"), listelerin kesişimi alınır. En kötü durumda $O(N)$ (N: toplam dosya sayısı).

---

## 2. N-ary Tree (Genel Ağaç)
**Konum:** `SmartFileLauncher.Core/Models/FileSystemNode.cs`

### Tanım
Dosya sistemi hiyerarşisini bellekte modellemek için kullanılan ağaç yapısıdır. Her düğüm (klasör) sınırsız sayıda çocuğa (dosya/alt klasör) sahip olabilir.

### Yapı
```csharp
public class FileSystemNode {
    public FileSystemNode? Parent;
    public List<FileSystemNode> Children; // N-ary yapısı
    // ...
}
```

### Kullanım Amacı
- **Gezinme (Traversal):** Klasörler arası geçiş (Parent/Child ilişkisi).
- **Görselleştirme:** UI tarafında Breadcrumb ve klasör yapısının gösterimi.
- **Algoritma:** DFS (Depth-First Search) kullanılarak disk taranır ve bu ağaç oluşturulur.

---

## 3. Priority Queue (Öncelik Kuyruğu)
**Konum:** `SmartFileLauncher.Core/Search/SearchEngine.cs`

### Tanım
Arama sonuçlarını "en alakalıdan" "en az alakalıya" doğru sıralamak için kullanılan veri yapısıdır. .NET 6+ ile gelen `PriorityQueue<TElement, TPriority>` kullanılır.

### Algoritma
Arama sırasında her dosya için bir **Skor (Score)** hesaplanır:
- Tam eşleşme: +200 puan
- Token eşleşmesi: +50 puan
- Kullanım sıklığı (Frequency): +2 * OpenCount

### Karmaşıklık Analizi
- **Ekleme (Enqueue):** $O(\log K)$ (K: Kuyruktaki eleman sayısı).
- **Çıkarma (Dequeue):** $O(\log K)$.
- **Top-N Sorgusu:** Binlerce sonuç arasından en iyi 50 sonucu getirmek için Heap yapısı sayesinde sıralama maliyeti minimize edilir.

---

## 4. Hash Set (Küme)
**Konum:** `SmartFileLauncher.Core/Services/IndexManager.cs` (Delta Sync) ve `SearchEngine.cs`

### Tanım
Benzersiz elemanları saklayan ve varlık kontrolünü (Contains) $O(1)$ sürede yapan veri yapısıdır.

### Kullanım Senaryoları
1. **Delta Senkronizasyonu:**
   - `HashSet<string> _syncedPaths`: Hangi klasörlerin tarandığını takip eder. Tekrar taramayı önler.
   - Fark (Diff) Hesaplama: Disk'teki dosyalar kümesi ile DB'deki dosyalar kümesi arasındaki fark ($A - B$) hızlıca bulunur.
2. **Arama Filtreleme:**
   - `HashSet<string> matchedTokens`: Bir dosyanın sorgudaki kelimelerden hangilerini içerdiğini (duplicate olmadan) takip eder.

---

## 5. 2D Array (Matris) - Dinamik Programlama
**Konum:** `SmartFileLauncher.Core/Utilities/FuzzyMatcher.cs`

### Tanım
Levenshtein Distance (Edit Distance) algoritması için kullanılan 2 boyutlu matris yapısıdır.

### Yapı
```csharp
int[,] dp = new int[m + 1, n + 1];
```

### İşlev
Kullanıcı "rapor" yerine "rapır" yazdığında, iki kelime arasındaki harf değiştirme/silme/ekleme maliyetini hesaplar.
- **Karmaşıklık:** $O(M \times N)$ (M ve N kelime uzunlukları).
- **Kullanım:** Hatalı yazımları tolere eden "Fuzzy Search" özelliği.

---

## 6. ObservableCollection
**Konum:** `SmartFileLauncher.UI/Views/MainWindow.xaml.cs`

### Tanım
Observer tasarım desenini uygulayan özel bir liste yapısıdır. Listeye eleman eklendiğinde veya çıkarıldığında UI'ya (WPF) otomatik bildirim gönderir.

### Kullanım
- `ObservableCollection<DesktopIconViewModel> _desktopIcons`: Ekranda görünen ikonlar.
- **Thread Safety:** UI thread dışında erişildiğinde `BindingOperations.EnableCollectionSynchronization` veya `Dispatcher` ile senkronize edilmelidir.

---

## 7. ConcurrentQueue (Thread-Safe Kuyruk)
**Konum:** `SmartFileLauncher.Core/Services/FileWatcherService.cs`

### Tanım
FileSystemWatcher event'lerini thread-safe şekilde buffer'lamak için kullanılır. Producer-Consumer pattern uygulanır.

### Karmaşıklık
- **Enqueue/Dequeue:** $O(1)$ (Lock-free implementasyon).

---

## 8. Dictionary (Hash Map)
**Konum:** Proje genelinde (`IndexManager`)

### Tanım
Anahtar-Değer (Key-Value) çiftlerini saklar.

### Kullanım
- **Metadata Map:** `Dictionary<string, FileMetadata>`
  - Dosya yolundan ($O(1)$) dosya boyutuna ve tarihine erişim sağlar.
- **Path to Node:** `Dictionary<string, FileSystemNode>`
  - Dosya yolundan ağaçtaki düğüme doğrudan erişim (Ağacı gezmeden).

---

## 9. SQLite Database (B-Tree)
**Konum:** `SmartFileLauncher.Core/Services/IndexDatabase.cs`

### Tanım
Verilerin kalıcı olarak saklanması için kullanılan gömülü ilişkisel veritabanı. İndeksleme için B-Tree yapısını kullanır.

### Kullanım
- Uygulama kapandığında indeksin kaybolmaması için veriler diske yazılır.
- Açılışta tüm diski taramak yerine ($O(N)$ disk I/O), veritabanından okunur ($O(N)$ sequential read).

---

## Özet Tablo

| Veri Yapısı | C# Karşılığı | Kullanım Alanı | Temel İşlem Karmaşıklığı |
|-------------|--------------|----------------|--------------------------|
| **Inverted Index** | `Dictionary<string, List<T>>` | Arama Motoru Çekirdeği | $O(1)$ Erişim |
| **N-ary Tree** | `Class Node { List<Node> }` | Dosya Sistemi Hiyerarşisi | $O(N)$ Gezinme |
| **Priority Queue** | `PriorityQueue<T, double>` | Arama Sonuçlarını Sıralama | $O(\log N)$ Ekleme/Çıkarma |
| **Hash Set** | `HashSet<T>` | Delta Sync, Tekillik Kontrolü | $O(1)$ Varlık Kontrolü |
| **2D Matrix** | `int[,]` | Fuzzy Search (Levenshtein) | $O(M \times N)$ Hesaplama |
| **ObservableCollection** | `ObservableCollection<T>` | UI Data Binding | $O(1)$ Bildirim |
| **ConcurrentQueue** | `ConcurrentQueue<T>` | Event Buffering | $O(1)$ Thread-Safe |
| **B-Tree** | `SQLite (Disk)` | Kalıcı Önbellek (Cache) | $O(\log N)$ Disk Erişimi |

---

**Proje**: OmniSpot Smart File Launcher  
**Ders**: Veri Yapıları  
**Tarih**: Aralık 2025
