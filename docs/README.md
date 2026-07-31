# Dokümantasyon

OmniSpot belgeleri konuya göre aşağıdaki klasörlerde düzenlenir.

## Rehberler

- [Derleme ve yayınlama](guides/build.md)
- [Doğal dil arama yapılandırması](guides/llm-setup.md)
- [Doğal dil arama testi](guides/nlu-integration.md)

## Mimari

- [Teknik referans](architecture/technical-reference.md)
- [Veri yapıları](architecture/data-structures.md)
- [UI mimarisi](architecture/ui-architecture.md)

## Performans

- [Büyük klasör optimizasyonu](performance/large-folder-optimization.md)

## Referans dosyaları

Aktif çalışma zamanı istemlerinin okunabilir kopyaları:

- [Intent ve metadata istemi](prompts/intent-analyzer.txt)
- [Keyword istemi](prompts/keyword-generator.txt)

Çalışan kaynak `SmartFileLauncher.Core/Services/IntentParser.cs` dosyasıdır. Prompt davranışı değiştirildiğinde kaynak ile bu iki referans birlikte güncellenir. `prompts/metadata-analyzer.txt` eski dosya adına bakan bağlantılar için yönlendirme olarak tutulur. Eski teknik notlar `archive/` altındadır.
