using System.Text;
using System.Text.Unicode;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class FileIndexTests
{
    private const string RootPath = @"C:\Root";
    private static readonly ulong RootReference = Reference(5, 5);

    private static readonly string[] Queries =
    [
        "read", "READ", "md", "rapor", "2024", "ışık", "IŞIK", "ı", "i", "ss", "ß", "straße", ".", "x1", "😀",
        "dup", "zzz", "a", "e.j", "_20", "index", "json", "no_ext", "git"
    ];

    private static readonly ResultView[] Views = new[]
        {
            ItemFilter.None,
            new ItemFilter(Kind: FilterItemKind.FilesOnly),
            new ItemFilter(Kind: FilterItemKind.FoldersOnly),
            new ItemFilter(Size: FilterSizeRange.Large),
            new ItemFilter(Date: FilterDateRange.LastMonth),
            new ItemFilter(Categories: FileFilterCategory.Document)
        }
        .SelectMany(filter => Enum.GetValues<SortField>()
            .SelectMany(field => new[] { false, true }.Select(descending => new ResultView(filter, new ItemSort(field, descending)))))
        .ToArray();

    [Fact]
    public void FreshBuildMatchesTheReferenceForEveryQueryFilterSortAndLimit()
    {
        var model = Model.Random(new Random(11), folders: 300, files: 2500);
        using var index = FileIndex.Build(RootPath, RootReference, model.Entries());
        AssertMatches(model, index);
    }

    [Fact]
    public void SuccessiveChangeBatchesMatchTheReferenceAndAFreshRebuild()
    {
        var random = new Random(23);
        var model = Model.Random(random, folders: 250, files: 2000);
        using var index = FileIndex.Build(RootPath, RootReference, model.Entries());
        foreach (var view in Views.Where(view => view.Sort.Field is SortField.Size or SortField.Modified or SortField.Kind))
            index.Search("a", 3, view);
        for (var batch = 0; batch < 8; batch++)
        {
            var changes = model.RandomChanges(random, 60 + batch * 40);
            index.Apply(changes);
            AssertMatches(model, index);
            using var rebuilt = FileIndex.Build(RootPath, RootReference, model.Entries());
            AssertSameResults(rebuilt, index);
        }
        index.CompactNow();
        AssertMatches(model, index);
    }

    [Fact]
    public void ReconcilingCurrentRecordStatesMatchesTheReference()
    {
        var random = new Random(41);
        var model = Model.Random(random, folders: 200, files: 1500);
        using var index = FileIndex.Build(RootPath, RootReference, model.Entries());
        foreach (var view in Views.Where(view => view.Sort.Field is SortField.Size or SortField.Kind))
            index.Search("e", 3, view);
        for (var batch = 0; batch < 6; batch++)
        {
            var changes = model.RandomChanges(random, 80 + batch * 30);
            var touched = changes.Select(change => change.Entry.FileReference).Distinct().ToList();
            index.Reconcile(model.States(touched));
            AssertMatches(model, index);
        }
        var file = model.Files().First();
        var folder = model.Folders().First(candidate => candidate != file.Parent);
        model.AddLink(file.Reference, folder, "second-name.bin");
        index.Reconcile(model.States([file.Reference]));
        AssertMatches(model, index);
        model.DeleteLink(file.Reference, folder, "second-name.bin");
        index.Reconcile(model.States([file.Reference]));
        AssertMatches(model, index);
    }

    [Fact]
    public void SnapshotRoundTripKeepsResultsAndAcceptsLaterChanges()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "index.db");
        var random = new Random(31);
        var model = Model.Random(random, folders: 120, files: 900);
        using var index = FileIndex.Build(RootPath, RootReference, model.Entries());
        index.Apply(model.RandomChanges(random, 120));
        var checkpoint = new IndexCheckpoint(0x1234, 77, 4096);
        index.Save(path, checkpoint);

        using var loaded = FileIndex.Load(path, out var restored);
        Assert.NotNull(loaded);
        Assert.Equal(checkpoint, restored);
        AssertSameResults(index, loaded!);

        var unsortedPath = Path.Combine(directory.Path, "unsorted.db");
        index.Save(unsortedPath, checkpoint, withSortOrders: false);
        Assert.True(new FileInfo(unsortedPath).Length < new FileInfo(path).Length);
        using var unsorted = FileIndex.Load(unsortedPath, out var unsortedCheckpoint);
        Assert.NotNull(unsorted);
        Assert.Equal(checkpoint, unsortedCheckpoint);
        AssertSameResults(index, unsorted!);
        loaded!.Apply(model.RandomChanges(random, 120));
        AssertMatches(model, loaded);

        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x5A;
        File.WriteAllBytes(path, bytes);
        Assert.Null(FileIndex.Load(path, out _));
        Assert.Null(FileIndex.Load(Path.Combine(directory.Path, "missing.db"), out _));
    }

    [Fact]
    public void AnUnknownParentIsSkippedWithoutTouchingTheIndex()
    {
        var model = Model.Random(new Random(5), folders: 10, files: 30);
        using var index = FileIndex.Build(RootPath, RootReference, model.Entries());
        var orphan = new IndexEntry(Reference(900_000, 1), Reference(800_000, 1), "orphan.txt", FileAttributes.Archive, 1, 1);
        var file = model.Files().First();
        var hidden = new IndexEntry(file.Reference, Reference(800_001, 1), file.Name, FileAttributes.Archive, 1, 1);
        var result = index.Apply([IndexChange.Upsert(orphan), IndexChange.Upsert(hidden, file.Parent, file.Name)]);
        Assert.Equal(new IndexApplyResult(0, 2), result);
        Assert.Empty(index.Search("orphan", 10, ResultView.SearchDefault));
        AssertMatches(model, index);
    }

    [Fact]
    public void AChildWhoseFolderArrivesLaterInTheSameBatchLandsInsideIt()
    {
        var model = Model.Random(new Random(9), folders: 10, files: 30);
        using var index = FileIndex.Build(RootPath, RootReference, model.Entries());
        var file = model.Files().First();
        var folder = new IndexEntry(Reference(700_000, 1), RootReference, "yeni-klasor", FileAttributes.Directory, 0, 1);
        var moved = new IndexEntry(file.Reference, folder.FileReference, "tasindi-qz.bin", FileAttributes.Archive, 1, 1);
        var result = index.Apply([IndexChange.Upsert(moved, file.Parent, file.Name), IndexChange.Upsert(folder)]);
        Assert.Equal(new IndexApplyResult(2, 0), result);
        var found = Assert.Single(index.Search("tasindi-qz", 10, ResultView.SearchDefault));
        Assert.Equal(Path.Combine(RootPath, "yeni-klasor", "tasindi-qz.bin"), found.FullPath);
    }

    [Fact]
    public void HardLinksShareMetadataAndRenameOrDeleteIndependently()
    {
        var model = Model.Random(new Random(7), folders: 20, files: 50);
        using var index = FileIndex.Build(RootPath, RootReference, model.Entries());
        var changes = new List<IndexChange>();
        var file = model.Files().First();
        var folder = model.Folders().First(candidate => candidate != file.Parent);
        changes.Add(model.AddLink(file.Reference, folder, "linked-copy.bin"));
        index.Apply(changes);
        AssertMatches(model, index);
        index.Apply([model.Touch(file.Reference, size: 123_456_789_000, modified: DateTime.UtcNow.Ticks)]);
        AssertMatches(model, index);
        index.Apply([model.Rename(file.Reference, folder, "linked-copy.bin", "renamed-link.bin")]);
        AssertMatches(model, index);
        index.Apply([model.DeleteLink(file.Reference, folder, "renamed-link.bin")]);
        AssertMatches(model, index);
    }

    private static readonly string[] WordTokens =
    [
        "fatura", "FATURA", "efatura2024", "isik", "ışık", "IŞIK", "rapor", "raporu", "çağrı", "cagri", "kopya", "readme", "md",
        "x1", "straße", "strasse", "😀", "data", "istanbul", "faturx", "raporr", "isk", "cagr", "notlari", "nots"
    ];

    [Fact]
    public void WordQueriesMatchTheReferenceAcrossChangesCompactionAndSnapshots()
    {
        using var directory = new TemporaryDirectory();
        var random = new Random(61);
        var model = Model.Random(random, folders: 150, files: 1200);
        using var index = FileIndex.Build(RootPath, RootReference, model.Entries());
        AssertWords(model, index);
        index.Apply(model.RandomChanges(random, 60));
        AssertWords(model, index);
        index.CompactNow();
        AssertWords(model, index);
        var path = Path.Combine(directory.Path, "words.db");
        index.Save(path, new IndexCheckpoint(1, 2, 3));
        using var loaded = FileIndex.Load(path, out _);
        Assert.NotNull(loaded);
        AssertWords(model, loaded!);
        index.PrepareWords(CancellationToken.None);
        AssertWords(model, index);
    }

    private static void AssertWords(Model model, FileIndex index)
    {
        var items = model.Items();
        var created = items.GroupBy(item => item.FullPath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Created, StringComparer.Ordinal);
        var seen = 0;
        foreach (var token in WordTokens)
        foreach (var (mode, distance) in new[] { (TokenMatch.Exact, 0), (TokenMatch.Partial, 0), (TokenMatch.Fuzzy, 1), (TokenMatch.Fuzzy, 2) })
        {
            var want = WordReference.Expected(items, token, mode, distance);
            var found = index.TokenItems(token, mode, distance, null, CancellationToken.None);
            var got = found.Select(item => item.FullPath).ToHashSet(StringComparer.Ordinal);
            seen += want.Count;
            Assert.True(want.SetEquals(got),
                $"'{token}' {mode} {distance}:{Environment.NewLine}want [{string.Join(", ", want.Order().Take(12))}]{Environment.NewLine}" +
                $"got  [{string.Join(", ", got.Order().Take(12))}]");
            foreach (var item in found)
            {
                var ticks = created[item.FullPath];
                Assert.Equal(ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime(), item.CreatedTime);
            }
        }
        Assert.True(seen > 500, $"Kelime referansı çok az sonuç üretti: {seen}");
    }

    private static class WordReference
    {
        private static readonly char[] Breaks = [' ', '_', '-', '.', ',', '[', ']', '(', ')'];

        internal static HashSet<string> Expected(List<(string FullPath, string Name, long Created)> items, string token, TokenMatch mode,
            int distance)
        {
            var whole = mode != TokenMatch.Partial;
            var result = items.Where(item => Has(item.Name, token, whole)).Select(item => item.FullPath).ToHashSet(StringComparer.Ordinal);
            if (mode != TokenMatch.Fuzzy || result.Count > 0) return result;
            var target = Encoding.UTF8.GetBytes(Plain(token));
            var near = items.SelectMany(item => item.Name.Split(Breaks, StringSplitOptions.RemoveEmptyEntries))
                .Select(Plain).Distinct(StringComparer.Ordinal)
                .Where(word => Encoding.UTF8.GetByteCount(word) is >= 3 and <= 64 && !word.Any(c => c is >= '0' and <= '9') &&
                    Distance(Encoding.UTF8.GetBytes(word), target) <= distance)
                .ToList();
            Assert.True(near.Count <= 64, $"'{token}' için referans {near.Count} yakın kelime üretti.");
            return items.Where(item => near.Any(word => Has(item.Name, word, true))).Select(item => item.FullPath)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool Has(string name, string token, bool whole) =>
            Contains(name.ToUpperInvariant(), token.ToUpperInvariant(), whole) ||
            Contains(name.ToUpperInvariant(), Plain(token), whole) ||
            Contains(Plain(name), Plain(token), whole);

        private static bool Contains(string text, string needle, bool whole)
        {
            if (needle.Length == 0) return false;
            for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
                 at = at + 1 < text.Length ? text.IndexOf(needle, at + 1, StringComparison.Ordinal) : -1)
            {
                if (!whole) return true;
                var before = at == 0 || Breaks.Contains(text[at - 1]);
                var after = at + needle.Length == text.Length || Breaks.Contains(text[at + needle.Length]);
                if (before && after) return true;
            }
            return false;
        }

        private static string Plain(string text) => SearchTextNormalizer.Fold(text).ToUpperInvariant();

        private static int Distance(byte[] left, byte[] right)
        {
            var table = new int[left.Length + 1, right.Length + 1];
            for (var i = 0; i <= left.Length; i++) table[i, 0] = i;
            for (var j = 0; j <= right.Length; j++) table[0, j] = j;
            for (var i = 1; i <= left.Length; i++)
            for (var j = 1; j <= right.Length; j++)
                table[i, j] = Math.Min(Math.Min(table[i - 1, j] + 1, table[i, j - 1] + 1),
                    table[i - 1, j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            return table[left.Length, right.Length];
        }
    }

    private static void AssertMatches(Model model, FileIndex index)
    {
        var now = DateTime.Now;
        var seen = 0;
        foreach (var query in Queries)
        foreach (var view in Views)
        {
            var expected = model.Expected(query, view, now);
            seen += expected.Count;
            foreach (var limit in new[] { 1, 7, 100, 100_000 })
            {
                var want = expected.Take(limit).ToArray();
                var got = index.Search(query, limit, view).Select(result => result.FullPath).ToArray();
                Assert.True(want.SequenceEqual(got),
                    $"'{query}' {view.Sort.Field} desc={view.Sort.Descending} {view.Filter} limit={limit}:{Environment.NewLine}" +
                    $"want [{string.Join(", ", want.Take(12))}]{Environment.NewLine}got  [{string.Join(", ", got.Take(12))}]");
            }
        }
        Assert.True(seen > 1000, $"Referans çok az sonuç üretti: {seen}");
    }

    private static void AssertSameResults(FileIndex expected, FileIndex actual)
    {
        foreach (var query in Queries)
        foreach (var view in Views)
        {
            var want = expected.Search(query, 100_000, view).Select(result => result.FullPath).ToArray();
            var got = actual.Search(query, 100_000, view).Select(result => result.FullPath).ToArray();
            Assert.True(want.SequenceEqual(got), $"'{query}' {view.Sort.Field} desc={view.Sort.Descending} {view.Filter}");
        }
    }

    private static ulong Reference(ulong record, ulong sequence) => sequence << 48 | record;

    private sealed class Model
    {
        private readonly Dictionary<ulong, Node> _nodes = [];
        private ulong _nextRecord = 16;

        private sealed class Node
        {
            public required ulong Reference;
            public required bool Directory;
            public long Size;
            public long Modified;
            public long Created;
            public FileAttributes Attributes;
            public List<(ulong Parent, string Name)> Links = [];
        }

        internal readonly record struct FileHandle(ulong Reference, ulong Parent, string Name);

        private static readonly string[] Vocabulary =
        [
            "readme.md", "README.md", "index.js", "Index.JS", "rapor.pdf", "rapor_2024.pdf", "ışık.txt", "İstanbul.jpg",
            "straße.doc", "STRASSE.doc", "a", "b.c.d", ".gitignore", "no_ext", "dup", "data.json", "Data.JSON",
            "😀.png", "x1", "x10", "image.jpg", "notes.txt", "e.js", "line.", "wide-\ud800.txt",
            "fatura_mart.pdf", "efatura2024.pdf", "Işık raporu.docx", "isik-notlari.txt", "Çağrı (kopya).txt", "cagri.log"
        ];

        internal static Model Random(Random random, int folders, int files)
        {
            var model = new Model();
            model._nodes[RootReference] = new Node { Reference = RootReference, Directory = true, Attributes = FileAttributes.Directory, Links = { (RootReference, ".") } };
            for (var i = 0; i < folders; i++) model.Create(random, directory: true);
            for (var i = 0; i < files; i++) model.Create(random, directory: false);
            return model;
        }

        internal IEnumerable<IndexEntry> Entries()
        {
            foreach (var node in _nodes.Values)
            foreach (var link in node.Links)
                yield return Entry(node, link.Parent, link.Name);
        }

        internal List<IndexRecordState> States(IEnumerable<ulong> references) => references
            .Select(reference => new IndexRecordState(reference, _nodes.TryGetValue(reference, out var node)
                ? node.Links.Select(link => Entry(node, link.Parent, link.Name)).ToList()
                : []))
            .ToList();

        internal IEnumerable<FileHandle> Files() =>
            _nodes.Values.Where(node => !node.Directory).SelectMany(node => node.Links.Select(link => new FileHandle(node.Reference, link.Parent, link.Name)));

        internal IEnumerable<ulong> Folders() => _nodes.Values.Where(node => node.Directory && node.Reference != RootReference).Select(node => node.Reference);

        private static IndexEntry Entry(Node node, ulong parent, string name) =>
            new(node.Reference, parent, name, node.Attributes, node.Directory ? 0 : node.Size, node.Modified, node.Created);

        private ulong RandomFolder(Random random)
        {
            var folders = _nodes.Values.Where(node => node.Directory).ToArray();
            return folders[random.Next(folders.Length)].Reference;
        }

        private string UniqueName(Random random, ulong parent, string? avoid = null)
        {
            for (var attempt = 0; ; attempt++)
            {
                var name = Vocabulary[random.Next(Vocabulary.Length)];
                if (attempt > 3) name = $"{Path.GetFileNameWithoutExtension(name)}-{random.Next(10_000)}{Path.GetExtension(name)}";
                if (name == avoid) continue;
                if (!Children(parent).Any(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
            }
        }

        private IEnumerable<(Node Node, string Name)> Children(ulong parent) =>
            _nodes.Values.SelectMany(node => node.Links.Where(link => link.Parent == parent && node.Reference != RootReference)
                .Select(link => (node, link.Name)));

        private Node Create(Random random, bool directory)
        {
            var parent = RandomFolder(random);
            var node = new Node
            {
                Reference = Reference(_nextRecord++, 1),
                Directory = directory,
                Attributes = directory ? FileAttributes.Directory : FileAttributes.Archive,
                Size = directory ? 0 : random.Next(4) == 0 ? 200L * 1024 * 1024 + random.Next(1000) : random.Next(5000),
                Modified = DateTime.UtcNow.AddDays(-random.Next(90)).Ticks,
                Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(random.Next(40_000_000)).Ticks
            };
            node.Links.Add((parent, UniqueName(random, parent)));
            _nodes[node.Reference] = node;
            return node;
        }

        internal List<IndexChange> RandomChanges(Random random, int count)
        {
            var changes = new List<IndexChange>();
            for (var i = 0; i < count; i++)
            {
                var roll = random.Next(100);
                if (roll < 25)
                {
                    var node = Create(random, directory: random.Next(4) == 0);
                    changes.Add(IndexChange.Upsert(Entry(node, node.Links[0].Parent, node.Links[0].Name)));
                    continue;
                }
                var candidates = _nodes.Values.Where(node => node.Reference != RootReference).ToArray();
                if (candidates.Length == 0) continue;
                var target = candidates[random.Next(candidates.Length)];
                var link = target.Links[0];
                if (roll < 40)
                {
                    changes.Add(IndexChange.Delete(target.Reference, link.Parent, link.Name));
                    RemoveLink(target, link);
                }
                else if (roll < 60)
                {
                    var name = UniqueName(random, link.Parent, link.Name);
                    target.Links[0] = (link.Parent, name);
                    changes.Add(IndexChange.Upsert(Entry(target, link.Parent, name), link.Parent, link.Name));
                }
                else if (roll < 75)
                {
                    var parent = RandomFolder(random);
                    if (parent == target.Reference || IsInside(parent, target.Reference)) continue;
                    var name = UniqueName(random, parent);
                    target.Links[0] = (parent, name);
                    changes.Add(IndexChange.Upsert(Entry(target, parent, name), link.Parent, link.Name));
                }
                else if (roll < 92 && !target.Directory)
                {
                    target.Size = random.Next(3) == 0 ? 300L * 1024 * 1024 : random.Next(9000);
                    target.Modified = DateTime.UtcNow.AddDays(-random.Next(90)).Ticks;
                    changes.Add(IndexChange.Upsert(Entry(target, link.Parent, link.Name)));
                }
                else if (!target.Directory && target.Links.Count == 1)
                {
                    changes.Add(IndexChange.Delete(target.Reference, link.Parent, link.Name));
                    RemoveLink(target, link);
                    var reused = new Node
                    {
                        Reference = Reference(target.Reference & 0xFFFF_FFFF_FFFF, (target.Reference >> 48) + 1),
                        Directory = false,
                        Attributes = FileAttributes.Archive,
                        Size = random.Next(100),
                        Modified = DateTime.UtcNow.Ticks
                    };
                    reused.Links.Add((link.Parent, UniqueName(random, link.Parent)));
                    _nodes[reused.Reference] = reused;
                    changes.Add(IndexChange.Upsert(Entry(reused, reused.Links[0].Parent, reused.Links[0].Name)));
                }
            }
            return changes;
        }

        private bool IsInside(ulong folder, ulong ancestor)
        {
            for (var at = folder; at != RootReference;)
            {
                if (at == ancestor) return true;
                at = _nodes[at].Links[0].Parent;
            }
            return false;
        }

        private void RemoveLink(Node node, (ulong Parent, string Name) link)
        {
            node.Links.Remove(link);
            if (node.Links.Count > 0) return;
            _nodes.Remove(node.Reference);
            if (!node.Directory) return;
            foreach (var child in _nodes.Values.Where(child => child.Links.Any(item => item.Parent == node.Reference)).ToArray())
                foreach (var childLink in child.Links.Where(item => item.Parent == node.Reference).ToArray())
                    RemoveLink(child, childLink);
        }

        internal IndexChange AddLink(ulong reference, ulong parent, string name)
        {
            var node = _nodes[reference];
            node.Links.Add((parent, name));
            return IndexChange.AddLink(Entry(node, parent, name));
        }

        internal IndexChange Touch(ulong reference, long size, long modified)
        {
            var node = _nodes[reference];
            node.Size = size;
            node.Modified = modified;
            return IndexChange.Upsert(Entry(node, node.Links[0].Parent, node.Links[0].Name), node.Links[0].Parent, node.Links[0].Name);
        }

        internal IndexChange Rename(ulong reference, ulong parent, string oldName, string newName)
        {
            var node = _nodes[reference];
            var at = node.Links.IndexOf((parent, oldName));
            node.Links[at] = (parent, newName);
            return IndexChange.Upsert(Entry(node, parent, newName), parent, oldName);
        }

        internal IndexChange DeleteLink(ulong reference, ulong parent, string name)
        {
            var node = _nodes[reference];
            node.Links.Remove((parent, name));
            return IndexChange.Delete(reference, parent, name);
        }

        private sealed record Item(Node Node, string Name, string[] Chain, string FullPath);

        internal List<(string FullPath, string Name, long Created)> Items() => _nodes.Values
            .Where(node => node.Reference != RootReference)
            .SelectMany(node => node.Links.Select(link =>
                (RootPath + "\\" + string.Join("\\", Chain(link.Parent).Append(link.Name)), link.Name, node.Created)))
            .ToList();

        internal List<string> Expected(string query, ResultView view, DateTime now)
        {
            var needle = query.ToUpperInvariant();
            var items = new List<Item>();
            foreach (var node in _nodes.Values)
            {
                if (node.Reference == RootReference) continue;
                foreach (var link in node.Links)
                {
                    if (!link.Name.ToUpperInvariant().Contains(needle, StringComparison.Ordinal)) continue;
                    var chain = Chain(link.Parent);
                    var item = new Item(node, link.Name, chain, RootPath + "\\" + string.Join("\\", chain.Append(link.Name)));
                    if (view.Filter.IsActive && !view.Filter.Matches(link.Name, node.Directory, node.Directory ? null : node.Size,
                            node.Directory ? null : new DateTime(node.Modified, DateTimeKind.Utc).ToLocalTime(), now)) continue;
                    items.Add(item);
                }
            }
            Comparison<Item> byName = (left, right) =>
            {
                var compared = CompareName(left.Name, right.Name);
                return compared != 0 ? compared : CompareChains(left.Chain, right.Chain);
            };
            var sort = view.Sort;
            List<Item> ordered;
            switch (sort.Field)
            {
                case SortField.Size:
                case SortField.Modified:
                {
                    long Key(Item item) => sort.Field == SortField.Size ? item.Node.Size : item.Node.Modified;
                    var files = items.Where(item => !item.Node.Directory).ToList();
                    files.Sort((left, right) =>
                    {
                        var compared = Key(left).CompareTo(Key(right));
                        if (sort.Descending) compared = -compared;
                        return compared != 0 ? compared : byName(left, right);
                    });
                    var folders = items.Where(item => item.Node.Directory).ToList();
                    folders.Sort(byName);
                    ordered = files.Concat(folders).ToList();
                    break;
                }
                case SortField.Kind:
                    ordered = items.ToList();
                    ordered.Sort((left, right) =>
                    {
                        var compared = Kind(left).AsSpan().SequenceCompareTo(Kind(right));
                        if (sort.Descending) compared = -compared;
                        return compared != 0 ? compared : byName(left, right);
                    });
                    break;
                case SortField.Name when sort.Descending:
                    ordered = items.ToList();
                    ordered.Sort((left, right) =>
                    {
                        var compared = -CompareName(left.Name, right.Name);
                        return compared != 0 ? compared : CompareChains(left.Chain, right.Chain);
                    });
                    break;
                default:
                    ordered = items.ToList();
                    ordered.Sort(byName);
                    break;
            }
            return ordered.Select(item => item.FullPath).ToList();
        }

        private static byte[] Kind(Item item)
        {
            if (item.Node.Directory) return [];
            var folded = Folded(item.Name);
            var dot = Array.LastIndexOf(folded, (byte)'.');
            return dot < 0 || dot == folded.Length - 1 ? [] : folded[dot..];
        }

        private string[] Chain(ulong folder)
        {
            var names = new List<string>();
            for (var at = folder; at != RootReference;)
            {
                var link = _nodes[at].Links[0];
                names.Add(link.Name);
                at = link.Parent;
            }
            names.Reverse();
            return names.ToArray();
        }

        private static int CompareChains(string[] left, string[] right)
        {
            for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                var compared = CompareName(left[i], right[i]);
                if (compared != 0) return compared;
            }
            return left.Length.CompareTo(right.Length);
        }

        private static byte[] Folded(string name) => Encoding.UTF8.GetBytes(name.ToUpperInvariant());

        private static int CompareName(string left, string right)
        {
            var byFolded = Folded(left).AsSpan().SequenceCompareTo(Folded(right));
            if (byFolded != 0) return byFolded;
            var (leftBytes, leftWide) = Original(left);
            var (rightBytes, rightWide) = Original(right);
            var byOriginal = leftBytes.AsSpan().SequenceCompareTo(rightBytes);
            return byOriginal != 0 ? byOriginal : leftWide.CompareTo(rightWide);
        }

        private static (byte[] Bytes, bool Wide) Original(string name)
        {
            var buffer = new byte[name.Length * 3];
            return Utf8.FromUtf16(name, buffer, out _, out var written, replaceInvalidSequences: false) == System.Buffers.OperationStatus.Done
                ? (buffer[..written], false)
                : (System.Runtime.InteropServices.MemoryMarshal.AsBytes(name.AsSpan()).ToArray(), true);
        }
    }
}
