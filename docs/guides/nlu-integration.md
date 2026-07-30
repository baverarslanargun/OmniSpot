# Doğal Dil Arama Test Rehberi

Bu rehber aktif Groq + rule-based fallback akışının manuel doğrulamasını kapsar. Testler kişisel dosyalar yerine ayrı bir örnek klasörde yapılmalıdır.

## Ön koşullar

- Windows 10/11
- .NET 8 SDK
- Güncel bir build
- Groq başarı senaryosu için geçerli bir API anahtarı ve ağ erişimi

Varsayılan cache-backed akış Desktop, Documents, Downloads, Pictures, Music ve Videos klasörlerini indeksler. Cache kapalı eski fallback yalnız Desktop'ı taradığı için bu rehber test verisini Desktop altında oluşturur.

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

## Senaryo 2 — Groq destekli doğal dil arama

Uygulamayı geçerli anahtarla başlatın:

```powershell
$env:OMNISPOT_GROQ_API_KEY = "gsk_..."
dotnet run --project .\SmartFileLauncher.UI\SmartFileLauncher.UI.csproj
```

1. Doğal Dil modunu açın.
2. `workplace safety klasöründeki videoları göster` sorgusunu çalıştırın.
3. Logda intent ve keyword çağrılarının tamamlandığını doğrulayın.
4. Sonuçlarda örnek `.mp4` dosyalarının bulunduğunu kontrol edin.
5. `budget ile ilgili excel dosyasını bul` sorgusunda `.xlsx` örneğinin döndüğünü kontrol edin.

Model çıktısı deterministik olmadığı için token listesi veya skorlar birebir sabit kabul edilmemelidir. Doğrulama; uygun dosya türünün, klasör ipucunun ve beklenen örneklerin sonuçlara yansımasına odaklanır.

## Senaryo 3 — Rule-based fallback

Aynı PowerShell oturumunda üç Groq değişkenini kaldırıp uygulamayı yeniden başlatın:

```powershell
Remove-Item Env:OMNISPOT_GROQ_API_KEY -ErrorAction SilentlyContinue
Remove-Item Env:OMNISPOT_GROQ_INTENT_API_KEY -ErrorAction SilentlyContinue
Remove-Item Env:OMNISPOT_GROQ_KEYWORD_API_KEY -ErrorAction SilentlyContinue
dotnet run --project .\SmartFileLauncher.UI\SmartFileLauncher.UI.csproj
```

1. Doğal Dil modunu açın.
2. `excel budget` sorgusunu çalıştırın.
3. UI'da fallback uyarısının gösterildiğini doğrulayın.
4. Logda Groq hatasının ardından rule-based parser'a geçildiğini kontrol edin.
5. Temel keyword ve dosya türü eşleşmesinin sonuç üretmeye devam ettiğini doğrulayın.

Anahtar makine veya kullanıcı ortamında kalıcı tanımlıysa yeni PowerShell sürecine yeniden gelebilir. Bu durumda test için temiz bir süreç ortamı kullanın; kalıcı anahtarı silmeden önce nerede kullanıldığını kontrol edin.

## Senaryo 4 — Keyword isteği tek başına başarısız

Bu senaryo için geçerli intent anahtarı ve geçersiz keyword anahtarı kullanın:

```powershell
$env:OMNISPOT_GROQ_INTENT_API_KEY = "gsk_gecerli_intent_anahtari"
$env:OMNISPOT_GROQ_KEYWORD_API_KEY = "gecersiz"
```

Doğal dil sorgusunun tamamen fallback'e düşmeden intent sonucuyla devam ettiğini, logda keyword uyarısı bulunduğunu ve sorgu metninin basit keyword olarak kullanıldığını doğrulayın. Test bittikten sonra süreç değişkenlerini temizleyin.

## Kontrol listesi

- [ ] Standart arama API anahtarı ve ağ olmadan sonuç üretiyor.
- [ ] Doğal Dil modu geçerli anahtarla intent ve keyword çağrılarını tamamlıyor.
- [ ] Intent çağrısı başarısız olduğunda rule-based fallback devreye giriyor.
- [ ] Keyword çağrısı tek başına başarısız olduğunda intent sonucu korunuyor.
- [ ] Fallback nedeni kullanıcıya ve loga yansıyor.
- [ ] Test dosyaları kontrollü biçimde temizlendi.

## Otomasyon kapsamı

Core test paketi arama snapshot'ı, eşzamanlı mutasyon ve cancellation davranışlarını kapsar. Groq başarı/fallback sözleşmesi için deterministik fake HTTP client tabanlı otomatik testler henüz bulunmadığından bu rehber manuel doğrulama sağlar.
