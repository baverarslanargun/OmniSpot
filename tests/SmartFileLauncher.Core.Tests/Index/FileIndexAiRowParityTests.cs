using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class FileIndexAiRowParityTests
{
    private static readonly DateTime Origin = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] FolderNames =
    [
        "Belgeler", "MASAÜSTÜ", "Masaüstü", "baver.tr", "baver", "tr", "Projeler", "Resimler", "IŞIK", "Ayrık Matematik",
        "Çıkmış Sorular", "proje-2026", "locales", "node_modules", "Arşiv", "Is", "medya"
    ];

    private static readonly string[] FileNames =
    [
        "ışık raporu.docx", "İstanbul.jpg", "resim-kapak.jpg", "resimler.png", "rapor_2026.pdf", "Rapor 2026.PDF", "notlar.txt",
        "fatura_mart.pdf", "efatura2024.pdf", "kapak.HEIC", "logo.svg", "tr.json", "string.txt", "baver-tr.db", "trailer.mp4",
        "foto (1).jpg", "[eski] rapor.pdf", "README", "nokta.", "image-reference.d.ts", "baver.tr.json", "soru-1.pdf",
        "raporlar.xlsx", "Proje Özeti.docx", "ceviri.json", "IMG_0001.JPG", "masaüstü.ini", "matematik notlar.md"
    ];

    public static TheoryData<string> Cases => new(QueryCases().Keys);

    [Theory]
    [MemberData(nameof(Cases))]
    public void RowSearchMatchesTheGeneralEngineExactly(string name)
    {
        using var c = BuildVolume(@"C:\", 11);
        using var d = BuildVolume(@"D:\", 29);
        var reader = new FileIndexReader([(c, null), (d, [@"D:\Is", @"D:\Projeler"])]);
        var tokenizer = new BasicTokenizer();
        var scoring = new BasicScoringStrategy();
        var rows = new AdvancedSearchEngine(_ => reader, tokenizer, scoring);
        var general = new AdvancedSearchEngine(_ => new GeneralReader(reader), tokenizer, scoring);
        var query = QueryCases()[name];

        foreach (var limit in new[] { 4000, 7 })
        {
            var expected = general.Search(query(), limit, ResultView.SearchDefault).Select(Key).ToArray();
            var actual = rows.Search(query(), limit, ResultView.SearchDefault).Select(Key).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ParityCasesExerciseRealResults()
    {
        using var c = BuildVolume(@"C:\", 11);
        using var d = BuildVolume(@"D:\", 29);
        var reader = new FileIndexReader([(c, null), (d, [@"D:\Is", @"D:\Projeler"])]);
        var engine = new AdvancedSearchEngine(_ => reader, new BasicTokenizer(), new BasicScoringStrategy());

        var counts = QueryCases().Values.Select(query => engine.Search(query(), 4000, ResultView.SearchDefault).Count).ToArray();

        Assert.True(counts.Count(count => count > 0) >= counts.Length - 1);
        Assert.True(counts.Sum() > 500);
    }

    private static Dictionary<string, Func<StructuredQuery>> QueryCases() => new()
    {
        ["resim-kapsam"] = () => new StructuredQuery
        {
            OriginalText = "baver tr resim dosyaları",
            OriginalTerms = ["baver", "tr"],
            SearchTerms =
            [
                Term("baver", 1), Term("resim", 1, group: 1), Term("resimleri", 0.9, SearchTermCategory.Variant, group: 1),
                Term("image", 0.8, SearchTermCategory.Translation, group: 1), Term("photo", 0.75, SearchTermCategory.Translation, group: 1),
                Term("tr", 0.35, role: SearchTermRole.Context)
            ],
            HardExtensions = [".jpg", ".jpeg", ".png", ".gif", ".heic"],
            FileTypes = ["image"],
            TargetType = new TargetType { File = 1, Folder = 0 },
            FolderHints = [new FolderHint { Name = "baver tr", Weight = 1 }]
        },
        ["iki-terim-her-tur"] = () => new StructuredQuery
        {
            OriginalText = "baver tr",
            OriginalTerms = ["baver", "tr"],
            SearchTerms = [Term("baver", 1), Term("tr", 1, group: 1), Term("baver tr", 0.75, role: SearchTermRole.Phrase)],
            TargetType = new TargetType { File = 0.5, Folder = 0.5 }
        },
        ["klasor-hedef"] = () => new StructuredQuery
        {
            OriginalText = "projeler klasörü",
            OriginalTerms = ["projeler"],
            SearchTerms = [Term("proje", 1)],
            TargetType = new TargetType { File = 0, Folder = 1 }
        },
        ["tarih-boyut"] = () => new StructuredQuery
        {
            OriginalText = "geçen ay değiştirilen büyük raporlar",
            OriginalTerms = ["rapor"],
            SearchTerms = [Term("rapor", 1), Term("report", 0.8, SearchTermCategory.Translation)],
            DateFilter = new DateFilter { ModifiedAfter = "2026-10-05", ModifiedBeforeExclusive = "2026-10-28" },
            SizeFilter = new SizeFilter { MinMb = 1 },
            TargetType = new TargetType { File = 0.6, Folder = 0.4 }
        },
        ["olusturma-klasor-tarihi"] = () => new StructuredQuery
        {
            OriginalText = "eylülde oluşturulan proje klasörleri",
            OriginalTerms = ["proje"],
            SearchTerms = [Term("proje", 1)],
            DateFilter = new DateFilter { CreatedAfter = "2026-09-20", CreatedBeforeExclusive = "2026-10-20" },
            IncludeFolderContents = false,
            TargetType = new TargetType { File = 0.4, Folder = 0.6 }
        },
        ["zorunlu-klasor-ad"] = () => new StructuredQuery
        {
            OriginalText = "Belgeler klasöründeki rapor adlı pdf",
            OriginalTerms = ["rapor"],
            RequiredFolders = [new FolderHint { Name = "Belgeler", Weight = 1 }],
            RequiredName = "rapor",
            SearchTerms = [Term("rapor", 1)],
            HardExtensions = [".pdf"],
            TargetType = new TargetType { File = 1, Folder = 0 }
        },
        ["yumusak-uzanti"] = () => new StructuredQuery
        {
            OriginalText = "fatura belgeleri",
            OriginalTerms = ["fatura"],
            SearchTerms = [Term("fatura", 1), Term("invoice", 0.8, SearchTermCategory.Translation)],
            SoftExtensions = [".pdf", "docx", "xlsx"]
        },
        ["klasor-baglami"] = () => new StructuredQuery
        {
            OriginalText = "masaüstündeki kapak",
            OriginalTerms = ["kapak"],
            SearchTerms = [Term("kapak", 1), Term("cover", 0.8, SearchTermCategory.Translation)],
            FolderContextTerms =
            [
                new SearchTerm { Text = "masaüstü", AnchorGroup = 0, Weight = 0.9 },
                new SearchTerm { Text = "baver tr", AnchorGroup = 1, Weight = 1 },
                new SearchTerm { Text = "medya", AnchorGroup = 1, Weight = 0.5 }
            ],
            FolderHints = [new FolderHint { Name = "Masaüstü", Weight = 0.25 }, new FolderHint { Name = "medya", Weight = 0.8 }]
        },
        ["yazim-hatasi"] = () => new StructuredQuery
        {
            OriginalText = "raporr zzzzq",
            SearchTerms = [Term("raporr", 1), Term("zzzzq", 1, group: 1), Term("faturx", 0.9, SearchTermCategory.Variant)]
        },
        ["turkce-harfler"] = () => new StructuredQuery
        {
            OriginalText = "MASAÜSTÜ ışık İstanbul",
            OriginalTerms = ["ışık"],
            SearchTerms = [Term("işık", 1), Term("ISIK", 0.9, SearchTermCategory.Variant), Term("İstanbul", 0.35, role: SearchTermRole.Context)],
            FolderHints = [new FolderHint { Name = "MASAÜSTÜ", Weight = 1 }]
        },
        ["esit-puan"] = () => new StructuredQuery
        {
            OriginalText = "notlar",
            OriginalTerms = ["notlar"],
            SearchTerms = [Term("not", 1)]
        },
        ["adli-klasor"] = () => new StructuredQuery
        {
            OriginalText = "çıkmış sorular ayrık matematik",
            OriginalTerms = ["çıkmış", "sorular", "ayrık", "matematik"],
            SearchTerms = [Term("soru", 1), Term("matematik", 0.35, role: SearchTermRole.Context)]
        },
        ["eski-anahtarlar"] = () => new StructuredQuery
        {
            OriginalText = "tr baver",
            Keywords = ["tr", "baver"]
        },
        ["baglam-kirpma"] = () => new StructuredQuery
        {
            OriginalText = "logo string",
            SearchTerms = [Term("logo", 1), Term("string", 0.9, role: SearchTermRole.Context), Term("svg", 0.3, role: SearchTermRole.Context)],
            TargetType = new TargetType { File = 0.6, Folder = 0.4 }
        },
        ["icerik-kapali"] = () => new StructuredQuery
        {
            OriginalText = "baver resim",
            OriginalTerms = ["baver"],
            SearchTerms = [Term("resim", 1), Term("medya", 0.65, role: SearchTermRole.Context)],
            IncludeFolderContents = false,
            FolderHints = [new FolderHint { Name = "Resimler", Weight = 1 }]
        }
    };

    private static SearchTerm Term(string text, double weight, SearchTermCategory category = SearchTermCategory.Exact,
        SearchTermRole role = SearchTermRole.Anchor, int group = 0) =>
        new() { Text = text, Weight = weight, Category = category, Role = role, AnchorGroup = group };

    private static (string, string, bool, long?, DateTime?, double) Key(SearchResult result) =>
        (result.Name, result.FullPath, result.IsDirectory, result.SizeBytes, result.LastWriteTime, result.Score);

    private static FileIndex BuildVolume(string root, int seed)
    {
        var random = new Random(seed);
        var rootReference = Reference(5);
        var entries = new List<IndexEntry> { new(rootReference, rootReference, ".", FileAttributes.Directory, 0, Ticks(-90), Ticks(-90)) };
        var folders = new List<ulong> { rootReference };
        var used = new HashSet<(ulong, string)>();
        var next = 16UL;
        foreach (var top in new[] { "Is", "Projeler", "Belgeler" })
        {
            var id = Reference(next++);
            used.Add((rootReference, top.ToUpperInvariant()));
            entries.Add(new(id, rootReference, top, FileAttributes.Directory, 0, Ticks(-30), Ticks(-40)));
            folders.Add(id);
        }
        for (var i = 0; i < 140; i++)
        {
            var parent = folders[random.Next(folders.Count)];
            var name = FolderNames[random.Next(FolderNames.Length)];
            if (!used.Add((parent, name.ToUpperInvariant()))) continue;
            var id = Reference(next++);
            var days = -random.Next(0, 80);
            entries.Add(new(id, parent, name, FileAttributes.Directory, 0, Ticks(days), Ticks(days - random.Next(0, 10))));
            folders.Add(id);
        }
        for (var i = 0; i < 900; i++)
        {
            var parent = folders[random.Next(folders.Count)];
            var name = FileNames[random.Next(FileNames.Length)];
            if (!used.Add((parent, name.ToUpperInvariant()))) continue;
            var modified = -random.Next(0, 80);
            var size = random.Next(0, 4) switch { 0 => 0L, 1 => random.Next(1, 1024), 2 => random.Next(1, 3) * 1048576L, _ => random.Next(3, 9) * 1048576L };
            entries.Add(new(Reference(next++), parent, name, FileAttributes.Archive, size, Ticks(modified), Ticks(modified - random.Next(0, 30))));
        }
        return FileIndex.Build(root, rootReference, entries);
    }

    private static ulong Reference(ulong record) => 1UL << 48 | record;

    private static long Ticks(int days) => Origin.AddDays(days).AddMinutes(days * 37 % 600).Ticks;

    private sealed class GeneralReader(FileIndexReader inner) : IQueryCatalogSnapshot, ITokenIndexSnapshot, ITokenPartialSnapshot
    {
        public int ItemCount => inner.ItemCount;
        public int TokenCount => inner.TokenCount;
        public bool IsTokenIndexReady => true;
        public IReadOnlyCollection<SearchItem> Get(string token, CancellationToken cancellationToken = default) => inner.Get(token, cancellationToken);
        public IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken cancellationToken = default) =>
            inner.GetPartial(token, cancellationToken);
        public IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2, CancellationToken cancellationToken = default) =>
            inner.GetFuzzy(token, maxDistance, cancellationToken);
        public IReadOnlyList<(SearchItem Item, bool Exact)> GetWithPartial(string token, QueryCatalogFilter? filter, out bool found,
            CancellationToken cancellationToken) => inner.GetWithPartial(token, filter, out found, cancellationToken);
        public IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken cancellationToken = default) => inner.GetAllItems(cancellationToken);
        public IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken cancellationToken = default) =>
            inner.GetDescendants(item, cancellationToken);
        public bool TryGetItem(string path, out SearchItem item) => inner.TryGetItem(path, out item);
        public IReadOnlyCollection<SearchItem> GetRoots(CancellationToken cancellationToken = default) => inner.GetRoots(cancellationToken);
        public IReadOnlyCollection<SearchItem> GetChildren(string path, CancellationToken cancellationToken = default) =>
            inner.GetChildren(path, cancellationToken);
        public IEnumerable<SearchItem> GetItems(QueryCatalogFilter filter, CancellationToken cancellationToken = default) =>
            inner.GetItems(filter, cancellationToken);
        public bool ContainsPath(string path) => inner.ContainsPath(path);
        public ISearchStateReader WithUpserts(IEnumerable<FileSystemNode> nodes, ITokenizer tokenizer) => throw new NotSupportedException();
        public ISearchStateReader WithoutPathAndDescendants(string path) => throw new NotSupportedException();
        public ISearchStateReader WithChanges(IEnumerable<string> removedPaths, IEnumerable<FileSystemNode> upserts, ITokenizer tokenizer) =>
            throw new NotSupportedException();
    }
}
