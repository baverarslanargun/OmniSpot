using System.Text;
using SmartFileLauncher.Core.Index;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class IndexNamesScanTests
{
    private static readonly string[] Stems =
    [
        "report", "Readme", "IMG", "setup", "data", "index", "notes", "a", "e", "zz", "x-1", "final draft", "copy (2)", "e.e.e",
        "ışık", "İstanbul", "Straße", "ÇAĞRI", "😀 emoji", "wide-\ud800", "Karışık Ad"
    ];

    private static readonly string[] Extensions = [".txt", ".PDF", ".dll", "", ".e", ".tar.gz", ".Jpg"];

    private static readonly string[] Patterns =
    [
        "e", "a", ".", "re", "ee", "txt", ".pdf", "report", "img_", "zz9", "x-1", "draft", "(2)", "00", "9.tar", "nomatch-q",
        "README", "readme", "ReadMe", "ışık", "IŞIK", "i", "ı", "İ", "straße", "ss", "ß", "çağrı", "😀", "karışık", ".JPG", "wide"
    ];

    [Fact]
    public void MatchAgreesWithCheckingEveryNameWhenThePoolIsSplitAcrossWorkers()
    {
        var names = Populate(90_000, out var poolBytes);
        Assert.True(poolBytes > 1 << 20, $"Havuz tek parçada kaldı: {poolBytes}");
        foreach (var pattern in Patterns)
        {
            var folded = IndexNames.FoldQuery(pattern);
            var bits = names.Match(folded, CancellationToken.None);
            for (var id = 0; id < names.Count; id++)
            {
                var expected = Folded(names, id).AsSpan().IndexOf(folded) >= 0;
                var actual = (bits[id >> 6] & 1UL << (id & 63)) != 0;
                Assert.True(expected == actual, $"'{pattern}' ad {id} '{names.Text(id)}': beklenen {expected}");
            }
        }
    }

    [Fact]
    public void WholeWordSearchAgreesWithCheckingEveryName()
    {
        var names = Populate(90_000, out _);
        foreach (var pattern in new[] { "e", "report", "REPORT", "draft", "2", "txt", "x", "final draft", "zz", "readme", "img" })
        {
            var folded = IndexNames.FoldQuery(pattern);
            var bits = names.Find(folded, folded, wholeWord: true, CancellationToken.None);
            for (var id = 0; id < names.Count; id++)
            {
                if (!names.IsAscii(id)) continue;
                var expected = ContainsWholeWord(Folded(names, id), folded);
                var actual = (bits[id >> 6] & 1UL << (id & 63)) != 0;
                Assert.True(expected == actual, $"'{pattern}' ad {id} '{names.Text(id)}': beklenen {expected}");
            }
        }
    }

    [Fact]
    public void NamesKeepTheirExactSpellingAndOrderAcrossASnapshot()
    {
        var names = Populate(4_000, out _);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) names.Write(writer);
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var restored = IndexNames.Read(reader);

        Assert.Equal(stream.Length, stream.Position);
        Assert.Equal(names.Count, restored.Count);
        var random = new Random(17);
        for (var id = 0; id < names.Count; id++)
        {
            Assert.Equal(names.Text(id), restored.Text(id));
            Assert.Equal(names.IsAscii(id), restored.IsAscii(id));
            Assert.Equal(id, restored.Intern(names.Text(id)));
            var other = random.Next(names.Count);
            Assert.Equal(Math.Sign(ReferenceCompare(names, id, other)), Math.Sign(names.Compare(id, other)));
            Assert.Equal(Math.Sign(names.Compare(id, other)), Math.Sign(restored.Compare(id, other)));
        }
        foreach (var pattern in Patterns)
        {
            var folded = IndexNames.FoldQuery(pattern);
            Assert.Equal(names.Match(folded, CancellationToken.None), restored.Match(folded, CancellationToken.None));
        }
    }

    private static IndexNames Populate(int count, out int poolBytes)
    {
        var names = new IndexNames();
        var random = new Random(3);
        poolBytes = 0;
        for (var i = 0; i < count; i++)
        {
            var separator = random.Next(3) switch { 0 => "_", 1 => "-", _ => " " };
            var text = $"{Stems[random.Next(Stems.Length)]}{separator}{i}{Extensions[random.Next(Extensions.Length)]}";
            Assert.Equal(i, names.Intern(text));
            Assert.Equal(i, names.Intern(text));
            poolBytes += text.Length + 1;
        }
        Assert.Equal(count, names.Count);
        return names;
    }

    private static byte[] Folded(IndexNames names, int id) => Encoding.UTF8.GetBytes(names.Text(id).ToUpperInvariant());

    private static int ReferenceCompare(IndexNames names, int left, int right)
    {
        if (left == right) return 0;
        var byFolded = FoldedForOrder(names.Text(left)).AsSpan().SequenceCompareTo(FoldedForOrder(names.Text(right)));
        if (byFolded != 0) return byFolded;
        return names.Original(left).SequenceCompareTo(names.Original(right));
    }

    private static byte[] FoldedForOrder(string text)
    {
        var upper = text.ToUpperInvariant();
        var bytes = new byte[upper.Length * 3];
        System.Text.Unicode.Utf8.FromUtf16(upper, bytes, out _, out var written, replaceInvalidSequences: true);
        return bytes[..written];
    }

    private static bool ContainsWholeWord(ReadOnlySpan<byte> name, ReadOnlySpan<byte> pattern)
    {
        for (var at = 0; at + pattern.Length <= name.Length; at++)
        {
            if (!name.Slice(at, pattern.Length).SequenceEqual(pattern)) continue;
            var startsWord = at == 0 || IndexNames.IsWordBreak(name[at - 1]);
            var endsWord = at + pattern.Length == name.Length || IndexNames.IsWordBreak(name[at + pattern.Length]);
            if (startsWord && endsWord) return true;
        }
        return false;
    }
}
