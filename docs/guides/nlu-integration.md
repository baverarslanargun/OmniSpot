# Doğal Dil Arama Test Rehberi

Bu rehber paralel Celeris intent + Groq keyword ve rule-based fallback akışının manuel doğrulamasını kapsar. Anahtar yönetimi ve başlatma davranışının tek kaynağı [doğal dil arama yapılandırma rehberidir](llm-setup.md). Testler kişisel dosyalar yerine ayrı bir örnek klasörde yapılmalıdır.

## Ön koşullar

- Windows 10/11
- .NET 8 SDK
- Güncel bir build
- Hibrit başarı senaryosu için yapılandırma rehberine göre kaydedilmiş Celeris ve Groq keyword anahtarları, iki sağlayıcıya erişen ağ bağlantısı

OmniSpot Desktop, Documents, Downloads, Pictures, Music ve Videos klasörlerini tek kalıcı indeks üzerinden tarar ve canlı izler. Bu rehber, test verisini kolayca ayırmak ve temizlemek için Desktop altında oluşturur.

## Test verisini hazırla

```powershell
$testRoot = Join-Path ([Environment]::GetFolderPath("Desktop")) "OmniSpotNluTest"
New-Item -ItemType Directory -Force -Path (Join-Path $testRoot "workplace-safety") | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $testRoot "workplace-safety\lesson-01.mp4") | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $testRoot "workplace-safety\lesson-02.mp4") | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $testRoot "budget-2026.xlsx") | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $testRoot "team-photo.jpg") | Out-Null
```

Test sonunda aynı PowerShell oturumunda temizleyebilirsiniz:

```powershell
Remove-Item -LiteralPath $testRoot -Recurse -Force
```

Silme işleminden önce `$testRoot` değerinin beklediğiniz Desktop test klasörünü gösterdiğini doğrulayın.

## Senaryo 1 — Standart arama yerel çalışır

1. Uygulamayı API anahtarı olmadan başlatın.
2. Doğal Dil modunu kapalı tutun.
3. `budget` ve `team photo` sorgularını ayrı ayrı arayın.
4. Beklenen dosyaların göründüğünü ve Groq çağrı logu oluşmadığını doğrulayın.

Bu senaryo standart arama için ağ bağımlılığı olmadığını kontrol eder.

## Senaryo 2 — Hibrit doğal dil arama

AI kısayolu daha önce hazırlanmadıysa bir kez yapılandırın:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\configure-ai-shortcut.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\configure-celeris.ps1
```

Önce çalışan tüm OmniSpot süreçlerini kapatın. Ardından Masaüstündeki `OmniSpot AI` kısayoluyla veya aşağıdaki eşdeğer komutla başlatın:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-with-ai.ps1
```

Güncel Release EXE de şifreli anahtarları doğrudan okuyabilir. Efor seçimi yalnız Qwen keyword isteğini değiştirir; Celeris düşünme kapalı kalır.

UI testinden önce aynı sorgunun yapılandırılmış sonucunu terminalde görmek isterseniz [yapılandırma rehberindeki hızlı prompt probe](llm-setup.md#hızlı-prompt-probe) komutunu kullanabilirsiniz.

1. Doğal Dil modunu açın.
2. `workplace safety klasöründeki videoları göster` sorgusunu çalıştırın.
3. Logda intent ve keyword çağrılarının tamamlandığını doğrulayın.
4. Sonuçlarda örnek `.mp4` dosyalarının bulunduğunu kontrol edin.
5. `budget ile ilgili excel dosyasını bul` sorgusunda `.xlsx` örneğinin döndüğünü kontrol edin.
6. `yaz dönemine ait biletler` sorgusunda logda ana hedefin `bilet`, yardımcı bağlamın `yaz` olduğunu ve tarih aralığının çalıştırıldığı yıl için 1 Haziran–1 Eylül üretildiğini kontrol edin.
7. `Yaza merhaba` veya `altyazı` içeren fakat `bilet` içermeyen örneklerin yalnız `yaz` benzerliği nedeniyle sonuçlara girmediğini doğrulayın.

Model çıktısı deterministik olmadığı için token listesi veya skorlar birebir sabit kabul edilmemelidir. Doğrulama; uygun dosya türünün, klasör ipucunun ve beklenen örneklerin sonuçlara yansımasına odaklanır.

## Senaryo 3 — Rule-based fallback

Yerel anahtar dosyalarına dokunmadan anahtarsız parser yolunu probe ile kontrol edin. Probe, başlatıcı kullanılmadığında şifreli dosyaları otomatik okumaz:

```powershell
Remove-Item Env:OMNISPOT_CELERIS_API_KEY -ErrorAction SilentlyContinue
Remove-Item Env:CELERIS_API_KEY -ErrorAction SilentlyContinue
dotnet run --project .\Tools\PromptProbe -- "excel budget"
```

Çıktıda `used_fallback=true`, eksik Celeris anahtarı nedeni ve temel keyword/uzantıların korunduğunu doğrulayın. UI uyarısını denemek için geçici süreç ortamında geçersiz Celeris anahtarıyla güncel uygulamayı başlatabilirsiniz; kayıtlı DPAPI dosyasını değiştirmeyin. Bu UI senaryosu gerçek HTTP isteği üretir.

Anahtar makine veya kullanıcı ortamında kalıcı tanımlıysa yeni PowerShell sürecine yeniden gelebilir. Bu durumda test için temiz bir süreç ortamı kullanın; kalıcı anahtarı silmeden önce nerede kullanıldığını kontrol edin.

## Senaryo 4 — Keyword isteği tek başına başarısız

Bu senaryo için geçerli intent anahtarı ve geçersiz keyword anahtarı kullanın:

```powershell
$env:OMNISPOT_CELERIS_API_KEY = "ck_gecerli_intent_anahtari"
$env:OMNISPOT_GROQ_KEYWORD_API_KEY = "gecersiz"
```

Doğal dil sorgusunun tamamen fallback'e düşmeden intent sonucuyla devam ettiğini, logda keyword uyarısı bulunduğunu ve sorgu metninin basit keyword olarak kullanıldığını doğrulayın. Test bittikten sonra süreç değişkenlerini temizleyin.

## Senaryo 5 — İndeks değişirken açık aramanın korunması

1. Bir doğal dil araması başlatın. Yanıt beklenirken örnek klasörde bir dosya ekleyin veya yeniden adlandırın.
2. AI isteğinin indeks bildirimiyle iptal edilmediğini ve sonuçların standart dosya adı aramasına dönüşmediğini doğrulayın.
3. İlk yanıt geldikten sonra aynı örnek klasörde sorguya uyan bir dosya ekleyin. Sonuçlar mevcut intent ve keyword çözümlemesiyle yerel olarak yenilenmeli; yeni sağlayıcı çağrısı yapılmamalıdır.
4. Standart dosya adı aramasında sorguyla ilgisiz bir dosyayı değiştirin. MFT indeksinin eski/yeni ad özeti sayesinde yerel sorgu da tekrarlanmamalıdır. AI sorguları, klasör yolu değişiklikleri, tarih filtreleri ve kapsamı bilinmeyen bildirimlerde yerel sonuç kontrolü devam eder. Sonuçlar aynı kaldığında liste ve arama logu tekrar sunulmamalıdır.
5. Otomatik yenilemenin filtreyi, sıralamayı, sonuç limitini ve fallback uyarısını koruduğunu; `aç` içeren önceki bir sorgu nedeniyle dosyayı tekrar açmadığını kontrol edin.

## Kontrol listesi

- [ ] Standart arama API anahtarı ve ağ olmadan sonuç üretiyor.
- [ ] Doğal Dil modu geçerli anahtarla intent ve keyword çağrılarını tamamlıyor.
- [ ] Intent çağrısı başarısız olduğunda rule-based fallback devreye giriyor.
- [ ] Keyword çağrısı tek başına başarısız olduğunda intent sonucu korunuyor.
- [ ] Fallback nedeni kullanıcıya ve loga yansıyor.
- [ ] İndeks yenilemesi AI sorgusunu koruyor, tekrar API çağrısı veya otomatik dosya açma üretmiyor.
- [ ] Değişmeyen sonuçlar listeye ve arama loguna tekrar yazılmıyor.
- [ ] AI modunda hızlı yazılan metin yalnız 600 ms sessizlikten sonra gönderiliyor; standart arama anlık kalıyor.
- [ ] Kalıcı 429 aynı payload ile tekrarlanmıyor; geçici 429 beklemesi yeni sorguyla sıfırlanmıyor.
- [ ] Test dosyaları kontrollü biçimde temizlendi.

## Otomasyon kapsamı

Core test paketi arama snapshot'ı, eşzamanlı mutasyon ve cancellation davranışlarını kapsar. `IntentParserTests` mevcut Groq profillerini, rol ve anchor grubu eşlemesini, açık komut korumasını, HTTP retry ve kısmi fallback sözleşmelerini fake HTTP yanıtlarıyla doğrular. `HybridIntentParserTests` ayrı sağlayıcı/anahtar/endpoint, paralel başlangıç, yalnız Qwen eforu, yanlış filter düzeltmesi, eksik/yinelenmiş/yarım intent yanıtı, kısmi hata ve iptali kapsar. UI'daki `AiCredentialsTests` DPAPI okuma ve bozuk yapılandırmanın anahtar içeriğini loglamadan reddini sınar. Gerçek sağlayıcı hesabı, model kalitesi, ağ kabulü ve uçtan uca UI davranışı ayrı manuel doğrulama gerektirir.

`SearchRefreshTests` saklanan intent ile API çağrısı ve otomatik dosya açma olmadan yenilemeyi doğrular. `FileIndexChangeNotificationTests` ve `VolumeIndexLifecycleTests` aynı indeks durumunun yeniden bildirilmesini, gerçek değişiklikleri kaybetmeden eler. `MainWindowSearchRefreshTests` gerçek WPF pencere sınıfını sentetik servislerle kullanarak bekleyen AI isteği, yeni kullanıcı sorgusu, kapanış, yenileme hatası ve değişmeyen sonuçların sunumunu sınar.

`IntentParserRateLimitTests` sentetik HTTP ve denetlenebilir saatle kalıcı/geçici 429 ayrımını, saniye/tarih `Retry-After` süresini, iptal sonrası sağlayıcı beklemesini, uzun beklemede erken retry yapılmamasını ve yarım keyword yanıtının reddini doğrular. `MainWindowSearchRefreshTests` ayrıca denetlenebilir zamanlayıcıyla hızlı yazmada tek AI çağrısı, standart aramada bekleme olmaması, silme/mod değiştirme/kapanış iptali ve indeks bildiriminin debounce'u atlayamamasını kapsar.

## Güncelleme notu — 2026-07-31

- AI ve anahtarsız smoke başlatma yolları açıkça ayrıldı.
- Yapılandırma rehberi tek kaynak olarak bağlandı.
- Eklenen fake HTTP tabanlı otomatik test kapsamı belgeye işlendi.
- Anchor/phrase/context arama davranışı ve yaz bileti gerileme senaryosu eklendi.
