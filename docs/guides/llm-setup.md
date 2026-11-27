# Doğal Dil Arama Yapılandırması

## Güncel durum

OmniSpot'un aktif doğal dil akışı Celeris-1 ile intent analizi ve Groq üzerindeki Qwen ile keyword üretimini paralel çalıştırır. Sonuçlar `StructuredQuery` nesnesine dönüştürülür. Yalnız filtre içeren sorgularda da iki çağrı yapılır ve yerel ayrıştırıcı sorgunun hangi bölümlerinin filtre olduğunu belirler. Geçici 429/5xx için her çağrı en fazla bir kez yinelenebildiğinden normalde iki, en kötü durumda dört POST oluşabilir. İstek boyutu veya kalıcı kota kaynaklı 429 aynı payload ile yinelenmez. İnternet yoksa arama standard/local yola düşer. Intent isteği başarısız olursa uygulama kural tabanlı yerel parser'a geçer. Keyword isteği tek başına başarısız olursa intent sonucu ve sorgu metniyle devam eder.

Standart arama dış AI servisi kullanmaz ve yerel indeks üzerinde anlık çalışır. AI modunda metin girişi son tuştan sonra 600 ms sessizlik bekler. Bu sırada yeni tuş, metni temizleme, mod değiştirme veya kapanış önceki beklemeyi iptal eder. AI modu kapatıldığında mevcut metinle standart arama hemen başlar. Otomatik indeks yenilemesi daha önce çözülmüş AI sorgusunu yerel olarak çalıştırır; bu beklemeyi ve sağlayıcı çağrılarını tekrarlamaz.

AI düğmesine fareyle gelindiğinde veya klavyeyle odaklanıldığında yanında efor ibresi görünür. İbreye her tıklama Low → Medium → High → Low döngüsünde ilerler. Varsayılan Orta düzeyidir; seçim kaydedilir ve yalnız Qwen keyword isteğine uygulanır. Celeris doğrudan yanıt verir; düşünme kapalıdır. Efor değiştiğinde mevcut sorgu yeniden aranır. Her istek seçimini ayrı taşır; sonraki seçim devam eden çağrının profilini değiştirmez. Yüksek efor daha uzun sürebilir; daha iyi sonuç veya kota sorunlarının çözülmesini garanti etmez.

## Akış

1. Kullanıcı arayüzünde Doğal Dil modu açılır.
2. `IntentParser` aynı sorgu için Celeris intent ve Groq keyword isteklerini başlatır. Mevcut `ParseWithGroqAsync` / `ParseWithGroqEffortAsync` adları çağıranlarla uyumluluk için korunmuştur.
3. Yerel ayrıştırıcı sorgu metnindeki açık tür ve uzantı, klasör kapsamı, tarih, boyut ve ad koşullarını uygular. Örneğin `JPG dosyaları`, `Arşiv klasöründe` ve `rapor adlı` sonuç kümesini daraltabilir. `sunum`, `excel` ve `fotoğraf` gibi tür adları ilgili uzantı ailesini zorunlu kılar; fotoğraf ailesi HEIC'i de kapsar. `dünkü` ve `geçen ayın başında` gibi çekimli zaman ifadeleri tarih olarak tanınır. Model aralığı yanlış tarih alanına koyarsa aralık, metindeki fiilin gösterdiği oluşturma veya değiştirme alanına taşınır. `ZXQ-9917` gibi harf ve rakamı tire ya da alt çizgiyle birleştiren tek bir kimlik ad içinde bütün olarak aranır; ayırıcı farkı (`ZXQ_9917`) sonucu değiştirmez. `hani`, `şu`, `şey`, `vardı ya` gibi konuşma dolguları ad kanıtı sayılmaz.
4. Kullanıcının yazdığı ad terimleriyle modelin ürettiği anahtar kelimeler aday bulur. Tek terim eşleşmesi sonuç için yeterlidir. Tam ad, birden fazla eşleşme ve uyuşan üst klasörler sıralamada daha çok katkı sağlar. Modelin tür ve klasör tahminleri de puana katkı sağlar.
5. Sorgu yalnız açık filtrelerden oluşuyorsa koşulları karşılayan katalog kayıtları listelenir. `Aç` isteğinde en üst adayın dosya adı ayrıca doğrulanır.
6. Intent isteği, bağlantı veya zaman aşımı nedeniyle başarısızsa `ParseIntent` kural tabanlı fallback'i çalışır.

Sorgudaki iki ad indekslenmiş bir klasöre ve onun doğrudan üst klasörüne tam olarak uyduğunda AI araması klasörü gösterir. Örneğin `çıkmış sorular ayrık matematik` sorgusu `Ayrık Matematik\Çıkmış Sorular` klasörünü bulabilir. Türkçe karakterlerin ASCII karşılıkları ve `altındaki` gibi konum ifadeleri desteklenir; `altındaki` ile adı verilen klasörün daha derindeki aynı adlı alt klasörleri doğrudan alt klasörden sonra gelir. Sözcüklere bölünerek yazılmış tek bir klasör adı da bulunur: `lale tr` sorgusu `lale.tr` klasörünü, `lale` adlı başka bir klasörün derinindeki `tr` klasörlerinin önüne koyar.

Tek parçalı bir ad sorgusunda, açık filtre veya açma komutu bulunmadığında normal ad aramasının eşleşmeleri kullanılır. Örneğin `bav`, `baver` ve `baver.tr` sorguları AI modu açıkken aynı klasörü bulabilir. Ad eşleşmesi yoksa AI terimleriyle arama devam eder.

AI aramasında aday bulmak için kullanılan anahtar kelimeler, uzunluklarına bakılmadan ad içinde kısmi eşleşebilir. Örneğin AI'nin ürettiği `Abc` terimi `Abc.pdf` ve `Abcd.pdf` adlarını eşleştirir. Tam kelime eşleşmesi daha yüksek puan alır.

`baver tr resim dosyaları` sorgusunda resim türü zorunludur. `baver.tr` eşleşmesi, bu klasörün altındaki `banner.png` gibi resimleri öne çıkarır. `baver.tr klasöründeki resim dosyaları` sorgusu aynı klasörü kapsam filtresi olarak uygular.

Aktif endpoint ve modeller:

| Amaç | Model |
|---|---|
| Intent analizi | `celeris-1` (`enable_thinking=false`) |
| Keyword üretimi | `qwen/qwen3.8-27b` (`reasoning_effort=medium`) |

Celeris `https://inference.celeris.ai/celeris-1/v1/chat/completions` adresine `temperature=0`, `max_tokens=512`, `stream=false`, `chat_template_kwargs.enable_thinking=false` ve `json_schema` ile çağrılır. [Celeris API sözleşmesi](https://docs.celeris.ai/making-requests) uyarınca `reasoning_effort` gönderilmez. C# tarafı 12 alanın eksiksiz ve tekil olmasını, liste/open türlerini ve `finish_reason=stop` değerini kontrol eder; mevcut tarih/boyut/enum doğrulaması devam eder.

Qwen keyword `https://api.groq.com/openai/v1/chat/completions` adresinde varsayılan `medium`, `temperature=0.6`, `top_p=0.95`, `max_completion_tokens=768`, `reasoning_format=hidden` ve [Groq Structured Outputs](https://console.groq.com/docs/structured-outputs) (`json_schema`, `strict=true`) kullanır. Low/Medium/High aynı 768 üst sınırını kullanır; açık `none` profilinin sınırı 350'dir. `finish_reason=length` yanıtı tamamlanmış keyword sonucu kabul edilmez; intent korunarak keyword fallback uygulanır. Her çağrı kendi 30 saniyelik linked-timeout sınırına sahiptir ve paralel başlar. Intent ve keyword promptları sağlayıcı değişiminde korunmuştur; yeni modelin arama kalitesi ve hız üstünlüğü ayrıca değerlendirilmelidir. Varsayılan profilde araç yoktur. Explicit Groq intent modelleri karşılaştırma için önceki bütçeleriyle desteklenmeye devam eder.

Geçici 429 yanıtında `Retry-After` saniye veya tarih biçiminde okunur; sunucunun süresi iki saniyeye kırpılmaz. Bekleme Groq ve Celeris için ayrı tutulur. Sorguyu iptal etmek sağlayıcının bekleme bitişini silmez; yeni sorgu o süre dolmadan aynı sağlayıcıya HTTP göndermez ve kalan süreyi uyarıda gösterir. 30 saniye veya daha uzun bekleme bildirilirse aynı çağrı içinde erken retry yapılmaz, 429 nedeni korunur. Tanılama günlüğündeki `Intent API denemesi` / `Keyword API denemesi` satırları sağlayıcıya yapılan HTTP denemelerini gösterir; anahtar ve istek gövdesi yazılmaz.

## API anahtarı

OmniSpot ayarlarındaki **AI anahtarları** bölümünde sağlayıcıların kayıt durumu listelenir. **Değiştir** veya **Anahtar ekle** düğmesiyle ilgili giriş alanını açın; **Anahtarı kaydet** yalnız o sağlayıcının anahtarını mevcut DPAPI dosyasına kaydeder. **Vazgeç** veya giriş alanındaki `Esc`, mevcut anahtarı değiştirmeden düzenlemeyi kapatır; boş alan kayıtlı anahtarı silmez. Kaydettikten sonra görünen **Yeniden başlat** düğmesiyle yeni anahtarı kullanabilirsiniz. Mevcut anahtar yükleme sırası ve aşağıdaki script/ortam değişkeni yöntemleri korunur; ortam değişkenleri dosyalardan önce kullanılır. Ortam değişkeni varken **Yerel anahtar ekle** ile yapılan kayıt bu önceliği değiştirmez. Groq kaydındaki mevcut intent anahtarı korunur; ilk kayıtta eski başlatıcıyla uyumluluk için aynı Groq anahtarı intent alanına da yazılır.

Önerilen yerel geliştirme akışı, anahtarları Windows DPAPI ile şifreleyip kullanıcı profili altında tutar. Bir kez çalıştırın:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\configure-ai-shortcut.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\configure-celeris.ps1
```

İlk script Groq anahtarlarını görünmeden ister, `%LOCALAPPDATA%\OmniSpot\groq-keys.json` dosyasına DPAPI ile şifreli yazar ve Masaüstünde `OmniSpot AI` kısayolu oluşturur. Mevcut dosyası olanların bunu tekrar çalıştırması gerekmez; hibrit akış bu dosyadaki keyword anahtarını kullanır. İkinci script Celeris anahtarını ayrı `%LOCALAPPDATA%\OmniSpot\celeris-key.json` dosyasına aynı Windows kullanıcısıyla çözülebilen DPAPI biçiminde kaydeder.

Normal `OmniSpot.exe` ve Windows oturum açılışındaki uygulama artık bu iki şifreli dosyayı okuyabilir. Ortam değişkenleri varsa dosyalardan önce kullanılır. Ölçüm profilleri yerel anahtar dosyalarını otomatik yüklemez. Probe için `scripts\start-with-ai.ps1` anahtarları yalnız alt süreç ortamına aktarır.

Kısayol:

- şifreli anahtarları yalnız mevcut Windows kullanıcısı için çözer,
- anahtarları OmniSpot alt sürecine ortam değişkeni olarak verir,
- başlatıcı süreçteki geçici değerleri hemen temizler,
- Release çıktısı yoksa uygulamayı bir kez derler.

Anahtarları güncellemek için ilgili yapılandırma komutunu yeniden çalıştırın. Groq anahtarlarını ve kısayolu kaldırmak için:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\configure-ai-shortcut.ps1 -Remove
```

Celeris kaydı ayrı `celeris-key.json` dosyasındadır; yukarıdaki kaldırma komutu onu silmez. Şifreli dosyalar repo dışında kalır ve yalnız aynı Windows kullanıcısı tarafından aynı bilgisayarda çözülebilir. Anahtarları kaynak koda, `appsettings` dosyalarına, loglara veya Git geçmişine yazmayın.

Geçici manuel başlatma gerektiğinde ortam değişkenleri hâlâ kullanılabilir:

```powershell
$env:OMNISPOT_CELERIS_API_KEY = "ck_..."
$env:OMNISPOT_GROQ_KEYWORD_API_KEY = "gsk_..."
dotnet run --project .\SmartFileLauncher.UI\SmartFileLauncher.UI.csproj
```

## Fallback davranışı

Intent API anahtarı yoksa uygulama ağ isteği göndermeden kural tabanlı parser'a döner. Celeris anahtarı geçersizse, sağlayıcıya erişilemiyorsa veya yanıt sözleşmeye uymuyorsa intent isteğinin hatası üzerine aynı fallback çalışır. Fallback:

- sorgudan temel anahtar kelimeleri çıkarır,
- açık takvim tarihlerini, boyutları ve tanınan klasör kapsamlarını yerelde ayrıştırır,
- bilinen dosya türlerini uzantılara eşler,
- klasör içeriğini aramaya dahil eder,
- AI intent sonucundaki gelişmiş tarih, boyut ve eylem semantiğini garanti etmez.

Fallback kullanıldığında UI uyarı gösterir ve neden `StructuredQuery.FallbackReason` alanında taşınır. Kullanıcının başlattığı cancellation fallback'e çevrilmez; işlem iptal edilir.

## Gizlilik ve ağ davranışı

Doğal Dil modu geçerli intent yapılandırması ve internet bağlantısıyla çalıştırıldığında sorgu metni Celeris'e intent, Groq'a keyword analizi için gönderilir. Intent payload'ı sorguya ek olarak gün ve saat dilimini; keyword payload'ı sorguyu taşır. Intent anahtarı eksikse uygulama ağ isteği yapmadan fallback'e geçer. Ağ üzerinden sorgu göndermek istemiyorsanız standart arama modunu kullanın.

OmniSpot bu çağrı zincirinde indekslenmiş dosya adlarını, path'leri, içerikleri, snippet'leri veya sonucu sağlayıcılara göndermez; dinamik veri sorgu (intent isteğinde ayrıca gün/saat dilimi) ve kodda tanımlı sistem istemleridir. Kullanıcının sorguda yazdığı dosya adı veya yol da sorgu metninin parçasıdır.

Bu, provider'ın kendi retention/processing politikasını değerlendirme gereğini ortadan kaldırmaz; yalnız güncel uygulama payload sınırını tanımlar.

## Sorun giderme

### Sürekli fallback kullanılıyor

1. `Celeris intent API anahtarı yapılandırılmamış` uyarısında `configure-celeris.ps1` ile şifreli anahtarı kaydedip uygulamayı yeniden başlatın. Anahtar dosyasının aynı Windows kullanıcısı tarafından oluşturulmuş olması gerekir.
2. Geçici manuel başlatma kullanıyorsanız anahtarların uygulamayı başlatan PowerShell sürecinde göründüğünü doğrulayın:

   ```powershell
   Test-Path Env:OMNISPOT_GROQ_API_KEY
   Test-Path Env:OMNISPOT_CELERIS_API_KEY
   Test-Path Env:OMNISPOT_GROQ_KEYWORD_API_KEY
   ```

3. Ortak anahtar yerine aşamaya özel değişkenler kullanıyorsanız ikisinin de boş olmadığını kontrol edin.
4. `https://api.groq.com` ve `https://inference.celeris.ai` erişimini, sistem saatini ve VPN durumunu doğrulayın. TCP 443 bağlantısının açık olması sağlayıcının kullanılan VPN çıkışını kabul edeceğini tek başına garanti etmez.
5. Uygulama logunda intent ve keyword çağrılarının ayrı hata mesajlarını ve HTTP durum kodlarını inceleyin.

### Yerel model ayarları çalışmıyor

`OMNISPOT_MODEL_PATH`, Phi-3 GGUF dosyaları, LLamaSharp paketleri ve geçmişte belgelenen `TestLLM` projesi aktif uygulama yolunun parçası değildir. `SmartFileLauncher.Core/Legacy` altındaki yardımcı sınıflar güncel doğal dil çağrı zincirine bağlı değildir.

## Hızlı prompt probe

Prompt değişikliklerini UI açmadan, üretimdeki `IntentParser` ve .NET 8 HTTP zinciriyle denemek için:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1 -ProbeQuery "bu yaza ait biletler"
```

Qwen keyword eforunu yalnız probe çağrısında değiştirmek için:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1 -ProbeQuery "bu yaza ait biletler" -ProbeReasoningEffort default
```

Probe varsayılanları üretimle aynıdır: Celeris-1 doğrudan intent ve Qwen 3.8 27B `medium` keyword. Varsayılan Celeris profilinde `-ProbeReasoningEffort` yalnız Qwen'i değiştirir. `-ProbeModel qwen/qwen3.8-27b` eski iki-Qwen karşılaştırmasını seçer; explicit Groq intent profillerinde bu parametre intent seviyesini değiştirir ve keyword `medium` kalır. Qwen için `none` reasoning'i kapatır; `default`, `low`, `medium` ve `high` açar. Thinking açıkken `temperature=0.6`, `top_p=0.95`, `max_completion_tokens=2048` ve `reasoning_format=hidden` kullanılır; `none` profilinde `temperature=0.3` korunur.

GPT-OSS 20B veya 120B intent karşılaştırması için `low`, `medium` veya `high` kullanın:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1 -ProbeQuery "bu yaza ait biletler" -ProbeModel openai/gpt-oss-120b -ProbeReasoningEffort medium
```

`-ProbeModel openai/gpt-oss-20b` değeriyle aynı profil 20B modelinde çalıştırılabilir. OSS profilinde intent çağrısı `temperature=1`, `top_p=1` ve `max_completion_tokens=2048` kullanır. Keyword modeli Qwen 3.8 `medium` olarak kalır.

Groq Compound veya Llama 3.3 70B intent karşılaştırması için reasoning değeri `none` bırakılır:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1 -ProbeQuery "bu yaza ait biletler" -ProbeModel groq/compound -ProbeReasoningEffort none
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1 -ProbeQuery "bu yaza ait biletler" -ProbeModel llama-3.3-70b-versatile -ProbeReasoningEffort none
```

İki profil de `temperature=1`, `top_p=1` ve `max_completion_tokens=2048` kullanır. Llama çağrısında JSON Object Mode açıktır. Compound çağrısında `Groq-Model-Version: latest` başlığı ile yalnız `code_interpreter` aracı açılır; `web_search` ve `visit_website` kapalıdır, `response_format` gönderilmez. Compound intent çağrısı araç bağlamını küçük tutmak için aynı JSON sözleşmesinin kısa prompt sürümünü kullanır.

Bu mod:

- DPAPI ile korunan iki anahtarı yalnız probe alt sürecine aktarır,
- intent ve keyword çağrılarını uygulamayla aynı biçimde paralel çalıştırır,
- süre, fallback durumu, yapılandırılmış intent ve ağırlıklı arama terimlerini JSON olarak yazdırır,
- çalışan OmniSpot sürecini kapatmayı gerektirmez.

Probe gerçek Celeris/Groq istekleri gönderir ve hesap kotasını kullanır. Anahtarları veya dosya içeriklerini çıktıya yazmaz.

## Manuel doğrulama

Hibrit başarı ve fallback senaryolarını adım adım doğrulamak için [doğal dil arama test rehberini](nlu-integration.md) kullanın.
