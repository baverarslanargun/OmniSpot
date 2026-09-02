using System.IO;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnStatePathIntegrityTests
{
    private const string RootPath = @"C:\Kok";
    private const ulong VolumeSerialNumber = 0x1234;
    private const ulong RootReference = 1;
    private const ulong JournalId = 7;

    [Theory]
    [InlineData(@"D:\Baskasinin")]
    [InlineData(@"\Windows\System32")]
    [InlineData(@"..\..\Baska")]
    [InlineData("alt\\derin")]
    [InlineData("alt/derin")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    public void DirectoryMap_RefusesANameThatIsNotASingleSegment(string name)
    {
        var map = new UsnDirectoryMap(RootPath, Reference(RootReference));

        Assert.Throws<ArgumentException>(() => map.Set(Reference(2), name, Reference(RootReference)));
    }

    [Fact]
    public void DirectoryMap_StillResolvesAPlainName()
    {
        var map = new UsnDirectoryMap(RootPath, Reference(RootReference));
        map.Set(Reference(2), "Projeler", Reference(RootReference));

        Assert.True(map.TryResolve(Reference(2), out var path));
        Assert.Equal(@"C:\Kok\Projeler", path);
    }

    [Fact]
    public void State_RefusesAPoisonedDirectoryEntry()
    {
        var failure = Assert.Throws<ArgumentException>(() => new UsnChangeFeedState(
            RootPath,
            Identity(),
            JournalId,
            100,
            new[] { new UsnDirectoryEntry(Reference(2), @"D:\Baskasinin", Reference(RootReference)) }));

        Assert.Contains("tek bir ad parçası", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StateStore_RejectsAPoisonedStateFileSoTheRootIsRebuilt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "durum.json");
        File.WriteAllText(path, PoisonedDocument(@"D:\\Baskasinin"));

        Assert.Throws<InvalidDataException>(() => new UsnChangeFeedStateStore(path).Read());
    }

    [Fact]
    public void StateStore_RejectsDuplicateRoots()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "durum.json");
        File.WriteAllText(path, DuplicateRootDocument());

        var failure = Assert.Throws<InvalidDataException>(() => new UsnChangeFeedStateStore(path).Read());
        Assert.Contains("yinelenen kök", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StateStore_RejectsAMissingDirectoryList()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "durum.json");
        File.WriteAllText(path, MissingDirectoriesDocument());

        var failure = Assert.Throws<InvalidDataException>(
            () => new UsnChangeFeedStateStore(path).Read());
        Assert.Contains("eksik kök veya dizin listesi", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Projection_RefusesADeviceNameThatEscapesTheRoot()
    {
        var projection = CreateProjection();

        var batch = projection.Project(new[]
        {
            new UsnRecord(
                150,
                Reference(50),
                Reference(RootReference),
                UsnReason.FileCreate,
                FileAttributes.Directory,
                "NUL")
        });

        Assert.Equal(ChangeFeedStatus.Gap, batch.Status);
        Assert.Equal(ChangeFeedGapReason.FeedStateInvalid, batch.GapReason);
        Assert.Empty(batch.Events);
    }

    [Fact]
    public void Projection_NeverReadsASubtreeForAnEscapingDirectory()
    {
        var subtrees = new FakeUsnSubtreeReader();
        var projection = CreateProjection(subtrees);

        projection.Project(new[]
        {
            new UsnRecord(
                150,
                Reference(50),
                Reference(RootReference),
                UsnReason.RenameNewName,
                FileAttributes.Directory,
                "NUL")
        });

        Assert.Empty(subtrees.Requests);
    }

    [Fact]
    public void Projection_StillDeliversAPlainPath()
    {
        var projection = CreateProjection();

        var batch = projection.Project(new[]
        {
            new UsnRecord(
                150,
                Reference(50),
                Reference(RootReference),
                UsnReason.DataExtend,
                FileAttributes.Normal,
                "rapor.docx")
        });

        Assert.Equal(ChangeFeedStatus.Ok, batch.Status);
        Assert.Equal(@"C:\Kok\rapor.docx", Assert.Single(batch.Events).FullPath);
    }

    private static UsnRootProjection CreateProjection(FakeUsnSubtreeReader? subtrees = null) =>
        new(
            new UsnChangeFeedState(
                RootPath,
                Identity(),
                JournalId,
                100,
                Array.Empty<UsnDirectoryEntry>()),
            new FakeUsnIdentityProbe().Set(RootPath, VolumeSerialNumber, RootReference),
            subtrees ?? new FakeUsnSubtreeReader());

    private static UsnNodeIdentity Identity() =>
        new(VolumeSerialNumber, Reference(RootReference));

    private static UsnFileReference Reference(ulong value) =>
        UsnFileReference.FromNtfs(value);

    private static string PoisonedDocument(string name) =>
        $$"""
        {
          "JournalId": 7,
          "NextUsn": 100,
          "Roots": [
            {
              "RootPath": "C:\\Kok",
              "VolumeSerialNumber": 4660,
              "ReferenceLow": 1,
              "ReferenceHigh": 0,
              "SynchronizedFromUsn": 0,
              "Directories": [
                { "Low": 2, "High": 0, "Name": "{{name}}", "ParentLow": 1, "ParentHigh": 0 }
              ]
            }
          ]
        }
        """;

    private static string DuplicateRootDocument() =>
        """
        {
          "JournalId": 7,
          "NextUsn": 100,
          "Roots": [
            { "RootPath": "C:\\Kok", "VolumeSerialNumber": 4660, "ReferenceLow": 1, "ReferenceHigh": 0, "SynchronizedFromUsn": 0, "Directories": [] },
            { "RootPath": "c:\\kok", "VolumeSerialNumber": 4660, "ReferenceLow": 1, "ReferenceHigh": 0, "SynchronizedFromUsn": 0, "Directories": [] }
          ]
        }
        """;

    private static string MissingDirectoriesDocument() =>
        """
        {
          "JournalId": 7,
          "NextUsn": 100,
          "Roots": [
            { "RootPath": "C:\\Kok", "VolumeSerialNumber": 4660, "ReferenceLow": 1, "ReferenceHigh": 0, "SynchronizedFromUsn": 0, "Directories": null }
          ]
        }
        """;
}
