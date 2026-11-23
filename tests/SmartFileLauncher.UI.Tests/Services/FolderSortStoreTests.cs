using System.IO;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class FolderSortStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "omnispot-folder-sorts-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_directory, "folder-views.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void MissingFileLoadsNothing()
    {
        var messages = new List<string>();

        Assert.Empty(new FolderSortStore(StorePath, messages.Add).Load());
        Assert.Empty(messages);
    }

    [Fact]
    public void SavedSortsLoadBackInOrder()
    {
        var folders = new[]
        {
            new FolderSort(@"C:\Is", new ItemSort(SortField.Modified)),
            new FolderSort(FolderViewMemory.RootKey, new ItemSort(SortField.Size, Descending: false)),
            new FolderSort(@"D:\Arşiv", new ItemSort(SortField.Name, Descending: true))
        };

        new FolderSortStore(StorePath).Save(folders);

        Assert.Equal(folders, new FolderSortStore(StorePath).Load());
        Assert.Equal([StorePath], Directory.GetFiles(_directory));
    }

    [Fact]
    public void UnreadableFileLoadsNothingAndIsReported()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, "{ bozuk");
        var messages = new List<string>();

        Assert.Empty(new FolderSortStore(StorePath, messages.Add).Load());
        Assert.Contains("okunamadı", Assert.Single(messages));
    }

    [Fact]
    public void UnknownOrSearchOnlyEntriesAreSkipped()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, """
            {
              "Folders": [
                { "Path": "C:\\Is", "Sort": "Modified", "Descending": true },
                { "Path": "C:\\Alaka", "Sort": "Relevance", "Descending": true },
                { "Path": "C:\\Bilinmez", "Sort": "Renk", "Descending": false },
                { "Path": "C:\\Sayi", "Sort": "99", "Descending": false },
                { "Sort": "Size", "Descending": true },
                null
              ]
            }
            """);

        var loaded = new FolderSortStore(StorePath).Load();

        Assert.Equal(new FolderSort(@"C:\Is", new ItemSort(SortField.Modified)), Assert.Single(loaded));
    }

    [Fact]
    public void AFailedSaveIsReportedInsteadOfThrown()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "dosya");
        File.WriteAllText(blocker, string.Empty);
        var messages = new List<string>();
        var store = new FolderSortStore(Path.Combine(blocker, "folder-views.json"), messages.Add);

        store.Save([new FolderSort(@"C:\Is", new ItemSort(SortField.Modified))]);

        Assert.Contains("kaydedilemedi", Assert.Single(messages));
    }
}
