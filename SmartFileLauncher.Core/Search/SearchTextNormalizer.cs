using System.Globalization;
using System.Text;

namespace SmartFileLauncher.Core.Search;

/// <summary>
/// Arama metnini iki aşamada hazırlar.
///
/// <para><b>1. Unicode formu.</b> Aynı ad iki farklı kod noktası dizisiyle
/// yazılabilir: Windows genelde `İ`'yi tek kod noktası (`U+0130`), macOS ve
/// bazı ağ paylaşımları `I` + birleşen nokta olarak yazar. Gözle aynıdırlar,
/// ordinal karşılaştırmada değil. Normalize edilmezse ayrıştırılmış biçim
/// `tr-TR` küçültmesinden `ı` + birleşen nokta olarak çıkar ve **hiçbir
/// aramayla bulunamaz**.</para>
///
/// <para><b>2. Katlama (folding).</b> `ı/İ` ile `i/I` Türkçe'de ayrı harflerdir
/// ve `OrdinalIgnoreCase` bunları eşitlemez; indeksleme `tr-TR` ile
/// küçülttüğü için `ISTANBUL.txt` deposunda `ıstanbul` olarak durur ve
/// "istanbul" araması onu bulamaz. Katlama, aksanı sıyrılmış bir **ikinci**
/// biçim üretir. Aslı silinmez, yanına eklenir: böylece `görüşme` araması tam
/// eşleşmeyle üste çıkarken `gorusme` araması da aynı dosyayı bulur.</para>
/// </summary>
public static class SearchTextNormalizer
{
    /// <summary>
    /// Girdiyi birleşik (`FormC`) Unicode biçimine getirir. Geçersiz Unicode
    /// taşıyan adlarda normalize etmek yerine girdiyi olduğu gibi döndürür;
    /// bir dosya adı yüzünden tarama düşmemelidir.
    /// </summary>
    public static string ToComposedForm(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        try
        {
            return value.IsNormalized(NormalizationForm.FormC)
                ? value
                : value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary>
    /// Token'ın aksansız karşılığını üretir. `ğ ş ç ö ü` ve benzerleri Unicode
    /// ayrıştırmasıyla sıyrılır; `ı` ve `İ` ayrıştırılamadığı için elle
    /// eşlenir. Değişiklik yoksa aynı referans döner.
    /// </summary>
    public static string Fold(string token)
    {
        if (string.IsNullOrEmpty(token) || IsAscii(token))
        {
            return token;
        }

        string decomposed;
        try
        {
            decomposed = token.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return token;
        }

        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character switch
            {
                'ı' => 'i',
                'İ' => 'i',
                'I' => 'i',
                _ => character
            });
        }

        return builder.ToString();
    }

    private static bool IsAscii(string value)
    {
        foreach (var character in value)
        {
            if (character > 127)
            {
                return false;
            }
        }

        return true;
    }
}
