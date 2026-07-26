using System.Globalization;
namespace SmartFileLauncher.Core.Search;
/// <summary>
/// Basic tokenizer splitting on common delimiters. Turkish culture lowercasing example.
/// TODO: Replace/extend with morphological analyzer (Zemberek) in future.
/// </summary>
public class BasicTokenizer : ITokenizer {
    private static readonly char[] _delims = new[] { ' ', '_', '-', '.', ',', '[', ']', '(', ')' };
    private readonly CultureInfo _culture = new("tr-TR");
    public IEnumerable<string> Tokenize(string input) {
        if (string.IsNullOrWhiteSpace(input)) yield break;
        var parts = input.Split(_delims, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts) {
            yield return p.ToLower(_culture);
        }
    }
}