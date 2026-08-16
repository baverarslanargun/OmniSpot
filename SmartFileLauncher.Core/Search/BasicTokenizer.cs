using System.Globalization;
namespace SmartFileLauncher.Core.Search;
/// <summary>
/// Basic tokenizer splitting on common delimiters. Turkish culture lowercasing example.
/// TODO: Replace/extend with morphological analyzer (Zemberek) in future.
///
/// Her parça iki biçimde üretilebilir: `tr-TR` küçültülmüş aslı ve —farklıysa—
/// aksansız katlanmış biçimi (bkz. <see cref="SearchTextNormalizer"/>). Aynı
/// kural hem indeksleme hem sorgu tarafında çalışır; böylece `görüşme` araması
/// tam eşleşmeyle üste çıkarken `gorusme` araması da aynı dosyayı bulur.
/// </summary>
public class BasicTokenizer : ITokenizer {
    private static readonly char[] _delims = new[] { ' ', '_', '-', '.', ',', '[', ']', '(', ')' };
    private readonly CultureInfo _culture = new("tr-TR");
    public IEnumerable<string> Tokenize(string input) {
        if (string.IsNullOrWhiteSpace(input)) yield break;
        var parts = SearchTextNormalizer.ToComposedForm(input)
            .Split(_delims, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts) {
            var token = p.ToLower(_culture);
            yield return token;
            var folded = SearchTextNormalizer.Fold(token);
            if (folded.Length > 0 && !string.Equals(folded, token, StringComparison.Ordinal))
                yield return folded;
        }
    }
}
