# 🤖 LLM Entegrasyonu - Kurulum ve Kullanım

## ✅ Durum: AKTİF

LLM entegrasyonu başarıyla aktifleştirildi ve test edildi!

---

## 📋 Sistem Bilgileri

- **Model**: Phi-3-mini-4k-instruct-q4.gguf
- **Model Boyutu**: 2.28 GB (Q4_K quantization)
- **Parametre Sayısı**: 3.82 Milyar
- **Framework**: LLamaSharp 0.19.0
- **Backend**: CPU (AVX2 optimized)
- **Bellek Gereksinimi**: ~3 GB RAM

---

## 🚀 Çalıştırma

### Yöntem 1: Environment Variable (Önerilen)

```powershell
$env:OMNISPOT_MODEL_PATH="C:\LLMModels\Phi-3-mini-4k-instruct-q4.gguf"
dotnet run --project .\SmartFileLauncher.UI\SmartFileLauncher.UI.csproj
```

### Yöntem 2: Modeli Proje Dizinine Kopyalama

```powershell
# Models klasörü oluştur
New-Item -ItemType Directory -Force -Path ".\SmartFileLauncher.UI\bin\Debug\net8.0-windows\Models"

# Modeli kopyala
Copy-Item "C:\LLMModels\Phi-3-mini-4k-instruct-q4.gguf" ".\SmartFileLauncher.UI\bin\Debug\net8.0-windows\Models\"

# Çalıştır (environment variable'a gerek yok)
dotnet run --project .\SmartFileLauncher.UI\SmartFileLauncher.UI.csproj
```

---

## 🎯 Kullanım

### UI'da Doğal Dil Modu

1. Uygulamayı başlat
2. Arama kutusunun üstündeki **"🤖 Doğal Dil"** toggle butonunu aktifleştir
3. Doğal dilde sorgular yaz:
   - "Show me all workplace safety videos"
   - "Find excel files about budget"
   - "List PDF documents from last week"

### Model Yükleme

Model ilk kez kullanıldığında yüklenir (5-10 saniye). Console'da şu mesajları göreceksiniz:

```
[IntentParser] 🚀 Starting LLM model loading...
[IntentParser] Model path: C:\LLMModels\Phi-3-mini-4k-instruct-q4.gguf
[IntentParser] ✅ Model file found, loading weights...
[IntentParser] File size: 2282 MB
[IntentParser] Parameters: Context=2048, Threads=8
...
[IntentParser] 🎉 LLM fully loaded and ready to use!
```

---

## 🧪 Test

Basit konsol test programı ile doğrulama:

```powershell
cd TestLLM
$env:OMNISPOT_MODEL_PATH="C:\LLMModels\Phi-3-mini-4k-instruct-q4.gguf"
dotnet run
```

**Test Sorgusu**: "Show me all workplace safety videos"

**Beklenen Çıktı**:
```
Intent: search_files
Keywords: workplace, safety, videos
FileTypes: video
PredictedExtensions: .mp4, .mkv, .avi
IncludeFolderContents: True
```

---

## ⚙️ Yapılandırma

### Model Yolu

Kod, model dosyasını şu sırayla arar:

1. `OMNISPOT_MODEL_PATH` environment variable
2. `{AppDirectory}\Models\Phi-3-mini-4k-instruct-q4.gguf`

Kaynak: `SmartFileLauncher.Core\Services\IntentParser.cs` (satır 23-25)

### Model Parametreleri

```csharp
ContextSize = 2048        // Token window
GpuLayerCount = 0         // CPU only (GPU desteği için >0)
BatchSize = 512           // Inference batch size
Threads = 8               // CPU thread sayısı (auto-detect)
```

---

## 📊 Performans

### İlk Yükleme
- **Süre**: 5-10 saniye
- **Bellek**: 2.3 GB (model) + 768 MB (KV cache) = ~3 GB

### Inference
- **CPU (8 thread)**: 2-5 saniye per query
- **Bellek**: Ek 100-200 MB per query

### Optimizasyon İpuçları

1. **GPU Kullanımı** (eğer CUDA kartınız varsa):
   ```powershell
   dotnet add package LLamaSharp.Backend.Cuda12
   ```
   `GpuLayerCount = 32` yapın (10-20x hız artışı)

2. **Context Boyutu Azaltma**:
   2048 → 1024 yaparsanız bellek kullanımı %50 azalır

3. **Thread Sayısı**:
   `Threads = 4` gibi düşük değer kullanırsanız diğer işlemler etkilenmez

---

## 🔄 Fallback Sistemi

Eğer LLM yüklenemezse (model bulunamazsa veya bellek yetersizse), otomatik olarak **rule-based parser**'a geçilir. Uygulama çökmez, temel arama çalışmaya devam eder.

Console'da şunu göreceksiniz:
```
[IntentParser] ❌ Failed to load LLM model: ...
[IntentParser] Will fall back to rule-based parser.
```

---

## 🐛 Sorun Giderme

### Problem: "Model file not found"

**Çözüm**:
```powershell
# Model yolunu kontrol et
Test-Path "C:\LLMModels\Phi-3-mini-4k-instruct-q4.gguf"

# Environment variable'ı doğru set et
$env:OMNISPOT_MODEL_PATH="C:\LLMModels\Phi-3-mini-4k-instruct-q4.gguf"
```

### Problem: "Access Violation Exception" (eski versiyon)

**Çözüm**: LlamaSharp 0.19.0'a güncelleyin (zaten yapıldı)
```powershell
dotnet add package LLamaSharp --version 0.19.0
dotnet add package LLamaSharp.Backend.Cpu --version 0.19.0
```

### Problem: Çok yavaş inference

**Çözüm 1**: Context boyutunu azalt (2048 → 1024)
**Çözüm 2**: GPU backend kullan (eğer varsa)
**Çözüm 3**: Daha küçük model kullan (örn: Phi-2 veya TinyLlama)

---

## 📦 Dependencies

```xml
<PackageReference Include="LLamaSharp" Version="0.19.0" />
<PackageReference Include="LLamaSharp.Backend.Cpu" Version="0.19.0" />
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.19.2" />
<PackageReference Include="System.Text.Json" Version="8.0.5" />
```

---

## 🔮 Gelecek İyileştirmeler

- [ ] GPU acceleration (CUDA/Vulkan)
- [ ] Model caching (daha hızlı startup)
- [ ] Streaming inference (real-time sonuçlar)
- [ ] Synonym expansion (movie → video)
- [ ] Multi-turn conversation (context-aware)
- [ ] Query history & suggestions
- [ ] Türkçe dil desteği

---

## 📚 Kaynaklar

- **LlamaSharp Docs**: https://github.com/SciSharp/LLamaSharp
- **Phi-3 Model**: https://huggingface.co/microsoft/Phi-3-mini-4k-instruct
- **GGUF Format**: https://github.com/ggerganov/llama.cpp

---

**Son Güncelleme**: 20 Kasım 2025  
**Versiyon**: 1.0  
**Durum**: ✅ Production Ready
