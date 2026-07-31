# Doğal Dil Arama Yapılandırması

## Güncel durum

OmniSpot'un aktif doğal dil akışı yerel Phi-3, ONNX veya LLamaSharp modeli kullanmaz. Doğal Dil modu iki Groq API isteğini paralel çalıştırır ve sonuçları `StructuredQuery` nesnesine dönüştürür. Intent isteği başarısız olursa uygulama kural tabanlı yerel parser'a geçer; keyword isteği tek başına başarısız olursa intent sonucu ve sorgu metniyle devam eder.

Standart arama Groq kullanmaz ve yerel indeks üzerinde çalışır.

## Akış

1. Kullanıcı arayüzünde Doğal Dil modu açılır.
2. `IntentParser.ParseWithGroqAsync` aynı sorgu için intent ve keyword isteklerini başlatır.
3. Intent sonucu filtre, hedef türü, tarih/boyut ve benzeri yapılandırılmış alanlara dönüştürülür.
4. Keyword sonucu başarılıysa ağırlığı `0.3` üzerindeki token'lar sorguya eklenir.
5. Intent isteği, bağlantı veya zaman aşımı nedeniyle başarısızsa `ParseIntent` kural tabanlı fallback'i çalışır.

Aktif endpoint ve modeller:

| Amaç | Model |
|---|---|
| Intent analizi | `qwen/qwen3.6-27b` |
| Keyword üretimi | `qwen/qwen3.6-27b` |

Her iki istek de `reasoning_effort=none` ile `https://api.groq.com/openai/v1/chat/completions` endpoint'ini kullanır ve 30 saniyelik timeout sınırına sahiptir.

## API anahtarı

Önerilen yerel geliştirme akışı, anahtarları Windows DPAPI ile şifreleyip kullanıcı profili altında tutar. Bir kez çalıştırın:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\configure-ai-shortcut.ps1
```

Script intent ve keyword anahtarlarını görünmeden ister, `%LOCALAPPDATA%\OmniSpot\groq-keys.json` dosyasına DPAPI ile şifreli yazar ve Masaüstünde `OmniSpot AI` kısayolu oluşturur. Sonraki başlatmalarda yalnız bu kısayolu kullanın.

Kısayol:

- şifreli anahtarları yalnız mevcut Windows kullanıcısı için çözer,
- anahtarları OmniSpot alt sürecine ortam değişkeni olarak verir,
- başlatıcı süreçteki geçici değerleri hemen temizler,
- Release çıktısı yoksa uygulamayı bir kez derler.

Anahtarları güncellemek için yapılandırma komutunu yeniden çalıştırın. Yerel anahtarları ve kısayolu kaldırmak için:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\configure-ai-shortcut.ps1 -Remove
```

Şifreli dosya repo dışında kalır ve yalnız aynı Windows kullanıcısı tarafından aynı bilgisayarda çözülebilir. Anahtarları kaynak koda, `appsettings` dosyalarına, loglara veya Git geçmişine yazmayın. Yanlışlıkla paylaşılan anahtarları yalnız repodan silmek yeterli değildir; Groq tarafında iptal edip yenileyin.

Geçici manuel başlatma gerektiğinde ortam değişkenleri hâlâ kullanılabilir:

```powershell
$env:OMNISPOT_GROQ_INTENT_API_KEY = "gsk_..."
$env:OMNISPOT_GROQ_KEYWORD_API_KEY = "gsk_..."
dotnet run --project .\SmartFileLauncher.UI\SmartFileLauncher.UI.csproj
```

## Fallback davranışı

API anahtarı yoksa, geçersizse veya Groq'a erişilemiyorsa Doğal Dil modu önce API yolunu dener, ardından intent isteğinin hatası üzerine kural tabanlı parser'a döner. Fallback:

- sorgudan temel anahtar kelimeleri çıkarır,
- bilinen dosya türlerini uzantılara eşler,
- klasör içeriğini aramaya dahil eder,
- Groq intent sonucundaki gelişmiş tarih, boyut ve eylem semantiğini garanti etmez.

Fallback kullanıldığında UI uyarı gösterir ve neden `StructuredQuery.FallbackReason` alanında taşınır. Kullanıcının başlattığı cancellation fallback'e çevrilmez; işlem iptal edilir.

## Gizlilik ve ağ davranışı

Doğal Dil modu çalıştırıldığında sorgu metni intent ve keyword analizi için Groq endpoint'ine gönderilmeyi dener. Anahtar eksik olsa bile yetkisiz istek denemesi sorgu gövdesini harici endpoint'e taşıyabilir. Ağ üzerinden sorgu göndermek istemiyorsanız standart arama modunu kullanın.

OmniSpot dosya içeriklerini Groq'a göndermez; doğal dil akışında gönderilen veri arama sorgusu ve kodda tanımlı sistem istemleridir.

## Sorun giderme

### Sürekli fallback kullanılıyor

1. Anahtarın uygulamayı başlatan süreçte göründüğünü doğrulayın:

   ```powershell
   Test-Path Env:OMNISPOT_GROQ_API_KEY
   ```

2. Ortak anahtar yerine aşamaya özel değişkenler kullanıyorsanız ikisinin de boş olmadığını kontrol edin.
3. `https://api.groq.com` erişimini ve sistem saatini doğrulayın.
4. Uygulama logunda intent ve keyword çağrılarının ayrı hata mesajlarını inceleyin.

### Yerel model ayarları çalışmıyor

`OMNISPOT_MODEL_PATH`, Phi-3 GGUF dosyaları, LLamaSharp paketleri ve geçmişte belgelenen `TestLLM` projesi aktif uygulama yolunun parçası değildir. `SmartFileLauncher.Core/Legacy` altındaki yardımcı sınıflar güncel doğal dil çağrı zincirine bağlı değildir.

## Manuel doğrulama

Groq başarı ve fallback senaryolarını adım adım doğrulamak için [doğal dil arama test rehberini](nlu-integration.md) kullanın.
