# Doğal Dil Arama Yapılandırması

## Güncel durum

OmniSpot'un aktif doğal dil akışı yerel Phi-3, ONNX veya LLamaSharp modeli kullanmaz. İnternet bağlantısı görülen Doğal Dil modu iki Groq API isteğini paralel çalıştırır ve sonuçları `StructuredQuery` nesnesine dönüştürür. Retriable 429/5xx için her çağrı en fazla bir kez yinelenebildiğinden normalde iki, en kötü durumda dört POST oluşabilir. İnternet yoksa arama standard/local yola düşer. Intent isteği başarısız olursa uygulama kural tabanlı yerel parser'a geçer; keyword isteği tek başına başarısız olursa intent sonucu ve sorgu metniyle devam eder.

Standart arama Groq kullanmaz ve yerel indeks üzerinde çalışır.

## Akış

1. Kullanıcı arayüzünde Doğal Dil modu açılır.
2. `IntentParser.ParseWithGroqAsync` aynı sorgu için intent ve keyword isteklerini başlatır.
3. Intent sonucu filtre, hedef türü, tarih/boyut ve benzeri yapılandırılmış alanlara dönüştürülür.
4. Keyword sonucu başarılıysa terimler zorunlu `anchor`, sıralama amaçlı `phrase` ve düşük etkili `context` rollerine ayrılır. Aynı anchor grubundaki biçim ve çeviriler alternatif, farklı anchor grupları birlikte zorunludur. Ağırlıklar modelden alınmaz; rol, kategori ve sıra temelinde uygulama tarafından deterministik atanır.
5. Intent yanlışlıkla `filter` dönse bile keyword yanıtında metadata olmayan bir anchor varsa sorgu keyword modunda korunur.
6. Intent isteği, bağlantı veya zaman aşımı nedeniyle başarısızsa `ParseIntent` kural tabanlı fallback'i çalışır.

Aktif endpoint ve modeller:

| Amaç | Model |
|---|---|
| Intent analizi | `openai/gpt-oss-120b` (`reasoning_effort=medium`) |
| Keyword üretimi | `qwen/qwen3.6-27b` |

İki istek de `https://api.groq.com/openai/v1/chat/completions` endpoint'ini kullanır ve her biri 30 saniyelik linked-timeout sınırına sahiptir. Intent çağrısı `medium`, keyword çağrısı `none` reasoning profiliyle çalışır. Varsayılan profilde araç yoktur; yalnız açıkça `groq/compound` seçilen alternatif profilde `code_interpreter` payload'ı ve `Groq-Model-Version: latest` header'ı eklenebilir.

## API anahtarı

Önerilen yerel geliştirme akışı, anahtarları Windows DPAPI ile şifreleyip kullanıcı profili altında tutar. Bir kez çalıştırın:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\configure-ai-shortcut.ps1
```

Script intent ve keyword anahtarlarını görünmeden ister, `%LOCALAPPDATA%\OmniSpot\groq-keys.json` dosyasına DPAPI ile şifreli yazar ve Masaüstünde `OmniSpot AI` kısayolu oluşturur. Sonraki AI başlatmalarında yalnız bu kısayolu veya aynı işi yapan `scripts\start-with-ai.ps1` dosyasını kullanın.

Doğrudan `OmniSpot.exe`, `dotnet run` veya normal uygulama kısayolu ile başlatılan süreç, anahtarlar kullanıcı ya da makine ortamında ayrıca tanımlı değilse şifreli dosyadaki anahtarları kendiliğinden okuyamaz. Release çıktısını yenilemek bu durumu değiştirmez; anahtarlar yalnız AI başlatıcısı tarafından alt sürece aktarılır.

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

Intent API anahtarı yoksa uygulama ağ isteği göndermeden kural tabanlı parser'a döner. Anahtar geçersizse veya Groq'a erişilemiyorsa intent isteğinin hatası üzerine aynı fallback çalışır. Fallback:

- sorgudan temel anahtar kelimeleri çıkarır,
- intent tarih filtresi korunuyorsa zaman ve ilişki sözcüklerini zorunlu arama hedefi yapmaz,
- bilinen dosya türlerini uzantılara eşler,
- klasör içeriğini aramaya dahil eder,
- Groq intent sonucundaki gelişmiş tarih, boyut ve eylem semantiğini garanti etmez.

Fallback kullanıldığında UI uyarı gösterir ve neden `StructuredQuery.FallbackReason` alanında taşınır. Kullanıcının başlattığı cancellation fallback'e çevrilmez; işlem iptal edilir.

## Gizlilik ve ağ davranışı

Doğal Dil modu geçerli intent yapılandırması ve internet bağlantısıyla çalıştırıldığında sorgu metni intent ve keyword analizi için Groq endpoint'ine gönderilir. Intent payload'ı sorguya ek olarak gün ve saat dilimini; keyword payload'ı sorguyu taşır. Intent anahtarı eksikse güncel uygulama ağ isteği yapmadan fallback'e geçer. Ağ üzerinden sorgu göndermek istemiyorsanız standart arama modunu kullanın.

OmniSpot bu çağrı zincirinde indekslenmiş dosya adlarını, path'leri, içerikleri, snippet'leri veya sonucu Groq'a göndermez; dinamik veri sorgu (intent isteğinde ayrıca gün/saat dilimi) ve kodda tanımlı sistem istemleridir.

Bu, provider'ın kendi retention/processing politikasını değerlendirme gereğini ortadan kaldırmaz; yalnız güncel uygulama payload sınırını tanımlar.

## Sorun giderme

### Sürekli fallback kullanılıyor

1. Uygulamayı doğrudan exe ile değil `OmniSpot AI` kısayoluyla başlattığınızı doğrulayın. `Groq intent API anahtarı yapılandırılmamış` uyarısı, kurulum hatasından çok çalışan sürecin anahtarı devralmadığını gösterir; uygulamayı kapatıp AI kısayoluyla yeniden başlatın.
2. Geçici manuel başlatma kullanıyorsanız anahtarların uygulamayı başlatan PowerShell sürecinde göründüğünü doğrulayın:

   ```powershell
   Test-Path Env:OMNISPOT_GROQ_API_KEY
   Test-Path Env:OMNISPOT_GROQ_INTENT_API_KEY
   Test-Path Env:OMNISPOT_GROQ_KEYWORD_API_KEY
   ```

3. Ortak anahtar yerine aşamaya özel değişkenler kullanıyorsanız ikisinin de boş olmadığını kontrol edin.
4. `https://api.groq.com` erişimini, sistem saatini ve VPN durumunu doğrulayın. TCP 443 bağlantısının açık olması Groq'un kullanılan VPN çıkışını kabul edeceğini tek başına garanti etmez.
5. Uygulama logunda intent ve keyword çağrılarının ayrı hata mesajlarını ve HTTP durum kodlarını inceleyin.

### Yerel model ayarları çalışmıyor

`OMNISPOT_MODEL_PATH`, Phi-3 GGUF dosyaları, LLamaSharp paketleri ve geçmişte belgelenen `TestLLM` projesi aktif uygulama yolunun parçası değildir. `SmartFileLauncher.Core/Legacy` altındaki yardımcı sınıflar güncel doğal dil çağrı zincirine bağlı değildir.

## Hızlı prompt probe

Prompt değişikliklerini UI açmadan, üretimdeki `IntentParser` ve .NET 8 HTTP zinciriyle denemek için:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1 -ProbeQuery "bu yaza ait biletler"
```

Thinking karşılaştırması için yalnız probe çağrısında:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1 -ProbeQuery "bu yaza ait biletler" -ProbeReasoningEffort default
```

Qwen 3.6 27B için `none` hızlı non-thinking, `default` ise thinking modudur. Probe thinking modunu yalnız intent çağrısında açar; keyword çağrısı hızlı `none` modunda kalır. Intent tarafında Groq'nun önerdiği `temperature=0.6`, `top_p=0.95` ve `max_completion_tokens=2048` profiliyle JSON çıktısına uygun `reasoning_format=hidden` kullanılır.

GPT-OSS 20B veya 120B intent karşılaştırması için `low`, `medium` veya `high` kullanın:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1 -ProbeQuery "bu yaza ait biletler" -ProbeModel openai/gpt-oss-120b -ProbeReasoningEffort medium
```

`-ProbeModel openai/gpt-oss-20b` değeriyle aynı profil 20B modelinde çalıştırılabilir. OSS profilinde intent çağrısı `temperature=1`, `top_p=1` ve `max_completion_tokens=2048` kullanır. Keyword modeli Qwen non-thinking olarak kalır.

Groq Compound veya Llama 3.3 70B intent karşılaştırması için reasoning değeri `none` bırakılır:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1 -ProbeQuery "bu yaza ait biletler" -ProbeModel groq/compound
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1 -ProbeQuery "bu yaza ait biletler" -ProbeModel llama-3.3-70b-versatile
```

İki profil de `temperature=1`, `top_p=1` ve `max_completion_tokens=2048` kullanır. Llama çağrısında JSON Object Mode açıktır. Compound çağrısında `Groq-Model-Version: latest` başlığı ile yalnız `code_interpreter` aracı açılır; `web_search` ve `visit_website` kapalıdır, `response_format` gönderilmez. Compound intent çağrısı araç bağlamını küçük tutmak için aynı JSON sözleşmesinin kısa prompt sürümünü kullanır.

Bu mod:

- DPAPI ile korunan iki anahtarı yalnız probe alt sürecine aktarır,
- intent ve keyword çağrılarını uygulamayla aynı biçimde paralel çalıştırır,
- süre, fallback durumu, yapılandırılmış intent ve ağırlıklı arama terimlerini JSON olarak yazdırır,
- çalışan OmniSpot sürecini kapatmayı gerektirmez.

Probe gerçek Groq isteği gönderir ve hesap kotasını kullanır. Anahtarları veya dosya içeriklerini çıktıya yazmaz.

## Manuel doğrulama

Groq başarı ve fallback senaryolarını adım adım doğrulamak için [doğal dil arama test rehberini](nlu-integration.md) kullanın.

## Güncelleme notu — 2026-07-31

- Kompakt paralel prompt akışı ve uygulama tarafından atanan kategori ağırlıkları belgelendi.
- Anahtarsız doğrudan başlatma ile Groq destekli AI başlatma birbirinden ayrıldı.
- Eksik anahtar, VPN ve kısmi API başarısızlığı davranışları güncel uygulamayla eşleştirildi.
- PowerShell HTTP katmanını kullanmadan gerçek .NET 8 çağrısı yapan hızlı prompt probe komutu eklendi.
- Probe için `none` ve `default` reasoning karşılaştırması eklendi.
- GPT-OSS 120B intent modeli için `low`, `medium` ve `high` probe profilleri eklendi.
- GPT-OSS 20B intent modeli aynı reasoning profillerine eklendi.
- Groq Compound ve Llama 3.3 70B intent probe profilleri eklendi.
- İlk `bu yaza ait biletler` denemesinde üç araç açık Compound isteği `413 Request Entity Too Large` ile reddedildi. Yalnız `code_interpreter` ve kısa intent promptu kullanılan son denemede 4.946 ms içinde `2026-06-01`–`2026-09-01` aralığı doğru üretildi.
- Üretim intent varsayılanı `openai/gpt-oss-120b` ve `medium` yapıldı; keyword varsayılanı Qwen `none` olarak korundu.
- Keyword çıktısı zorunlu anchor, alternatif, phrase ve context rollerine ayrıldı; yalnız anchor'ların aday ürettiği davranış belgelendi.
- `yaz dönemine ait biletler` dahil beş sabit canlı senaryo tek revizyon sınırıyla doğrulandı.
