using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class BasicTokenEnumeratorTests
{
    [Fact]
    public void SpanEnumerationMatchesPublicTokenizerIncludingTurkishAndInvalidUtf16()
    {
        var tokenizer = new BasicTokenizer();
        var samples = new List<string>
        {
            "", " \t\r\n", "I İ ı i", "INDEX.DOC", "I_I-I.I,I[I]I(I)", "a\tb\nC",
            "\t  A\r_\nB\t", "görüşme.pdf", "cafe\u0301.txt", "İş 📁.txt", "\ud800_a_\udfff", "a_a_A",
            "a\u00a0b", "\u00a0A\u00a0", new string('I', 1024) + ".pdf"
        };
        var random = new Random(1729);
        const string alphabet = "abcABCIZ _-.,[]()\t\r\nıİğöé\u0301\ud800\udfff";
        for (var n = 0; n < 500; n++) samples.Add(new string(Enumerable.Range(0, random.Next(80)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
        foreach (var text in samples)
        {
            var buffer = new char[text.Length];
            using var actual = new BasicTokenEnumerator(text, buffer, tokenizer);
            var tokens = new List<string>();
            while (actual.MoveNext()) tokens.Add(actual.Current.ToString());
            Assert.Equal(tokenizer.Tokenize(text).ToArray(), tokens);
        }
    }

    [Fact]
    public void LiveTokenPostingDedupAndFuzzyRulesMatchExistingSearchState()
    {
        using var workspace = new TemporaryDirectory();
        var root = PackedCatalogTests.Records()[0];
        var names = new[] { "rapor-rapor.PDF", "görüşme.txt", "cafe\u0301.txt", "INDEX.DOC", "abc.txt", "abd.txt", "ABC.log", "a\tb.txt", "İş 📁.txt", "Kelvin.txt", "ſample.txt", "\ud800x.txt", "x\udfff.txt" };
        var records = names.Select((name, i) => new PackedRecord(new(name, root.Item.FullPath + "\\" + name, false, i,
            null, null, 0, root.Item.FullPath), 0, 0, false, false, root.IndexedUtc)).Prepend(root).ToArray();
        var path = Path.Combine(workspace.Path, "tokens");
        using var live = new LiveCatalog(path, root.Item.FullPath);
        foreach (var record in records) live.Add(record);
        live.Seal();
        using var reopened = LiveCatalog.Open(path);
        var expected = CompactSearchState.Create(records.Select(record => record.Item), new BasicTokenizer());
        foreach (var state in new[] { live.Snapshot(), reopened.Snapshot() })
            foreach (var query in new[] { "rapor", "por", "gorusme", "görüşme", "café", "index", "ındex", "is", "abc", "ABX", "abx", "a\tb", "kelv", "ſamp", "samp", "\ud800", "\udfff", "" })
            {
                Assert.Equal(Order(expected.Get(query)), Order(state.Get(query)));
                Assert.Equal(Order(expected.GetPartial(query)), Order(state.GetPartial(query)));
                for (var distance = -1; distance <= 3; distance++)
                    Assert.Equal(Order(expected.GetFuzzy(query, distance)), Order(state.GetFuzzy(query, distance)));
            }
    }

    private static SearchItem[] Order(IEnumerable<SearchItem> items) => items.OrderBy(item => item.FullPath, StringComparer.Ordinal).ToArray();
}
