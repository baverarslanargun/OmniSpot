using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class SearchStateTokenSharingTests
{
    private static readonly BasicTokenizer Tokenizer = new();

    [Fact]
    public void CreateSharesOneTokenInstanceAcrossItems()
    {
        var state = SearchState.Create(
            new[]
            {
                Node("rapor-ocak.txt"),
                Node("rapor-subat.txt"),
                Node("rapor-mart.txt")
            },
            Tokenizer);

        var instances = new[]
        {
            TokenInstance(state, @"C:\kok\rapor-ocak.txt", "rapor"),
            TokenInstance(state, @"C:\kok\rapor-subat.txt", "rapor"),
            TokenInstance(state, @"C:\kok\rapor-mart.txt", "rapor")
        };

        Assert.Same(instances[0], instances[1]);
        Assert.Same(instances[0], instances[2]);
    }

    [Fact]
    public void WithUpsertsReusesExistingCanonicalTokenInstance()
    {
        var state = SearchState.Create(new[] { Node("rapor-ocak.txt") }, Tokenizer);
        var canonical = TokenInstance(state, @"C:\kok\rapor-ocak.txt", "rapor");

        var updated = state.WithUpserts(new[] { Node("rapor-nisan.txt") }, Tokenizer);

        Assert.Same(canonical, TokenInstance(updated, @"C:\kok\rapor-nisan.txt", "rapor"));
        Assert.Same(canonical, TokenInstance(updated, @"C:\kok\rapor-ocak.txt", "rapor"));
    }

    [Fact]
    public void RepeatedUpsertsDoNotErodeTokenSharing()
    {
        var state = SearchState.Create(
            new[] { Node("rapor-ocak.txt"), Node("rapor-subat.txt") },
            Tokenizer);
        var canonical = TokenInstance(state, @"C:\kok\rapor-ocak.txt", "rapor");

        for (var round = 0; round < 25; round++)
        {
            state = state.WithUpserts(new[] { Node($"rapor-{round}.txt") }, Tokenizer);
        }

        var paths = new List<string>
        {
            @"C:\kok\rapor-ocak.txt",
            @"C:\kok\rapor-subat.txt"
        };
        for (var round = 0; round < 25; round++)
        {
            paths.Add($@"C:\kok\rapor-{round}.txt");
        }

        foreach (var path in paths)
        {
            Assert.Same(canonical, TokenInstance(state, path, "rapor"));
        }
    }

    [Fact]
    public void ReupsertingSameItemKeepsCanonicalTokenInstance()
    {
        var state = SearchState.Create(
            new[] { Node("rapor-ocak.txt"), Node("rapor-subat.txt") },
            Tokenizer);
        var canonical = TokenInstance(state, @"C:\kok\rapor-subat.txt", "rapor");

        for (var round = 0; round < 25; round++)
        {
            state = state.WithUpserts(new[] { Node("rapor-ocak.txt") }, Tokenizer);
        }

        Assert.Same(canonical, TokenInstance(state, @"C:\kok\rapor-ocak.txt", "rapor"));
        Assert.Same(canonical, TokenInstance(state, @"C:\kok\rapor-subat.txt", "rapor"));
    }

    [Fact]
    public void SharingDoesNotChangeLookupResults()
    {
        var nodes = new[]
        {
            Node("rapor-ocak.txt"),
            Node("rapor-subat.txt"),
            Node("bulten-mart.txt")
        };

        var shared = SearchState.Create(nodes, Tokenizer);
        var unshared = SearchState.Create(nodes, Tokenizer, shareTokens: false);

        Assert.Equal(unshared.ItemCount, shared.ItemCount);
        foreach (var token in new[] { "rapor", "ocak", "subat", "bulten", "mart", "txt" })
        {
            Assert.Equal(
                unshared.Get(token).Select(item => item.FullPath).OrderBy(path => path),
                shared.Get(token).Select(item => item.FullPath).OrderBy(path => path));
        }
    }

    private static string TokenInstance(SearchState state, string path, string token)
    {
        var tokens = state.TokensFor(path);
        Assert.False(tokens.IsDefaultOrEmpty, $"token yok: {path}");
        var match = tokens.SingleOrDefault(
            candidate => string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(match);
        return match!;
    }

    private static FileSystemNode Node(string name) =>
        new(name, $@"C:\kok\{name}", false);
}
