# Natural Language Search - Test Guide

## ✅ Entegrasyon Tamamlandı!

### Yeni Özellikler

#### 1. **Doğal Dil Arama Modu** 🤖
- Arama kutusunun üstünde "🤖 Doğal Dil" toggle butonu eklendi
- Aktif olduğunda natural language query'ler işlenir
- Intent parser structured query çıkarır

#### 2. **Folder Name Indexing** 📁
- Klasör adları artık tokenize edilip indeksleniyor
- Klasör adına göre arama yapılabiliyor
- Klasör match olursa içindeki dosyalar listeleniyor

#### 3. **File Type Filtering** 🎬
- "video", "image", "document" gibi generic type'lar destekleniyor
- Otomatik extension mapping (video → .mp4, .avi, .mkv vs.)

#### 4. **Date Filtering** 📅
- "last week", "today", "this month" gibi relative tarihler
- Absolute tarihler (YYYY-MM-DD format)

---

## 🧪 Test Senaryoları

### Senaryo 1: Folder İçerik Listeme
**Test Data Gerekli**:
```
Desktop/
└── workplacesafety/
    ├── 1.mp4
    ├── 2.mp4
    ├── 3.mp4
    ├── 4.mp4
    └── 5.mp4
```

**Test Query**: 
```
"List to me videos of workplace safety lesson"
```

**Beklenen Sonuç**:
1. Intent parser çıkarımı:
   ```json
   {
     "keywords": ["workplace", "safety", "lesson"],
     "file_types": ["mp4", "avi", "mkv", "video"],
     "include_folder_contents": true
   }
   ```

2. Arama sonuçları:
   - `workplacesafety` klasörü eşleşir (workplace + safety tokenleri)
   - Klasör içindeki tüm .mp4 dosyalar listelenir
   - 5 dosya döner: 1.mp4, 2.mp4, 3.mp4, 4.mp4, 5.mp4

### Senaryo 2: Generic Type Search
**Test Data**:
```
Desktop/
├── photo1.jpg
├── photo2.png
├── document.pdf
└── song.mp3
```

**Test Query**: 
```
"Show me all images"
```

**Beklenen Sonuç**:
- File type filter: [.jpg, .png, .gif, .bmp, etc.]
- Sonuç: photo1.jpg, photo2.png

### Senaryo 3: Kombinasyon
**Test Query**: 
```
"Find excel files with budget"
```

**Beklenen Sonuç**:
- Keywords: ["budget"]
- File types: ["xlsx", "xls", "spreadsheet"]
- "budget" kelimesini içeren .xlsx/.xls dosyalar

---

## 🎮 Nasıl Test Edilir?

### Adım 1: Test Verisi Hazırla
Desktop'una şu klasörü oluştur:
```powershell
# Desktop path
$desktop = [Environment]::GetFolderPath("Desktop")

# workplacesafety klasörü oluştur
New-Item -Path "$desktop\workplacesafety" -ItemType Directory -Force

# Boş video dosyaları oluştur (test için)
1..5 | ForEach-Object {
    New-Item -Path "$desktop\workplacesafety\$_.mp4" -ItemType File -Force
}
```

### Adım 2: Uygulamayı Çalıştır
```powershell
dotnet run --project .\SmartFileLauncher.UI\SmartFileLauncher.UI.csproj
```

### Adım 3: Test Et

1. **Uygulama açıldığında**:
   - 🐛 Debug konsolunu aç
   - Taramanın tamamlandığını doğrula
   - workplacesafety klasörünün listelendiğini kontrol et

2. **Doğal Dil Modunu Aktifleştir**:
   - "🤖 Doğal Dil" toggle'ını tıkla
   - Watermark text "🤖 Doğal dil ile ara..." olmalı

3. **Query Gir**:
   ```
   List to me videos of workplace safety lesson
   ```

4. **Konsol Çıktısını İncele**:
   ```
   [HH:MM:SS] 🤖 Doğal dil işleniyor...
   [HH:MM:SS] 📋 Çıkarılan niyet:
   [HH:MM:SS]    Intent: list_files
   [HH:MM:SS]    Keywords: [workplace, safety, lesson]
   [HH:MM:SS]    File Types: [video, mp4, avi, mkv]
   [HH:MM:SS]    Include Folders: True
   [HH:MM:SS] ✅ Sonuç sayısı: 5
   [HH:MM:SS] 🏆 İlk 5 sonuç:
   [HH:MM:SS]    • 1.mp4 (skor: XXX)
   [HH:MM:SS]    • 2.mp4 (skor: XXX)
   ...
   ```

5. **Sonuçları Doğrula**:
   - 5 adet .mp4 dosya listelenmeli
   - Her birinin path'i workplacesafety klasörü içinde olmalı

---

## 🔍 Debug Checklist

Eğer sonuç gelmiyorsa:

### ☑️ Klasör indekslendi mi?
```
Konsol çıktısında ara:
"📄 Örnek dosyalar (10):"
   - workplacesafety   <-- Bu görünmeli
```

### ☑️ Intent doğru parse edildi mi?
```
Konsol'da "📋 Çıkarılan niyet:" altına bak:
Keywords: [workplace, safety, ...]  <-- En az 2 keyword olmalı
File Types: [mp4, video, ...]       <-- Video extensions olmalı
```

### ☑️ Index'te token var mı?
```
Sonuç 0 ise konsol şunu gösterir:
"Token 'workplace' → indekste X eşleşme"
X > 0 olmalı (workplacesafety klasörü eşleşmeli)
```

---

## 🚀 Gelecek İyileştirmeler

### Phase 2: Gerçek ONNX Inference
Şu an fallback rule-based parser kullanılıyor. Phi-3 ONNX modelini tam entegre etmek için:

1. **Tokenizer Integration**
   - `tokenizer.json` parse et
   - BPE encoding implement et

2. **ONNX Runtime Inference**
   - Model input tensörü oluştur
   - Inference çalıştır
   - Output decode et

3. **Performance Optimization**
   - Model caching
   - Batch processing
   - GPU acceleration (opsiyonel)

### Phase 3: Advanced Features
- Synonym expansion ("movie" → "video")
- Multi-language support
- Context-aware search
- Query history & suggestions

---

## 📊 Performans Notları

**Mevcut Sistem**:
- Fallback parser: ~1-5 ms
- Folder expansion: O(k × n) k=matched folders, n=avg files/folder
- Total search: ~10-50 ms (bin dosya için)

**ONNX Model ile (gelecek)**:
- Model inference: ~50-200 ms (CPU)
- GPU ile: ~10-50 ms
- Trade-off: Daha akıllı intent understanding

---

## ✅ Şu Anki Durum

✅ Intent parser infrastructure hazır  
✅ Structured query model tanımlı  
✅ File type mapping çalışıyor  
✅ Folder indexing aktif  
✅ Advanced search engine implement edildi  
✅ UI toggle ve watermark eklendi  
✅ Debug console logging entegre  
🔄 **Fallback rule-based parser aktif** (ONNX yerine)  
⏳ Full ONNX inference TODO (gelecek iterasyon)

---

**Test et ve geri bildirimini ver!** 🚀
