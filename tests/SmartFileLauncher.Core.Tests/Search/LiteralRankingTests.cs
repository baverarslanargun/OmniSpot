using System.Text;
using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class LiteralRankingTests
{
    [Theory]
    [InlineData("CMD.EXE", "cmd.exe", 5)]
    [InlineData("cmd.exe", "CMD", 4)]
    [InlineData("cmd-tools.exe", "cmd", 3)]
    [InlineData("cmd.tar.gz", "cmd", 3)]
    [InlineData("cmd.tar.gz", "cmd.tar", 3)]
    [InlineData("cmd.", "cmd", 3)]
    [InlineData("xcmd y cmd.exe", "CMD", 2)]
    [InlineData("old_cmd.exe", "cmd", 2)]
    [InlineData("old-cmd.exe", "cmd", 2)]
    [InlineData("old.cmd.exe", "cmd", 2)]
    [InlineData("oldcmd.exe", "CMD", 1)]
    [InlineData("misc.txt", "cmd", 0)]
    [InlineData("a", "aaa", 0)]
    [InlineData("misc.txt", "", 0)]
    public void RanksLiteralMatchesInTheSameOrderForAsciiAndChar(string name, string query, int expected)
    {
        Assert.Equal(expected, LiteralRanking.Classify(name, query));
        Assert.Equal(expected, LiteralRanking.ClassifyAscii(
            Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(query), !query.Contains('.')));
    }

    [Theory]
    [InlineData("Şevval.png", "şevval", 4)]
    [InlineData("Şevval.png", "sevval", 0)]
    [InlineData("İstanbul.pdf", "istanbul", 0)]
    [InlineData("12-İstanbul.pdf", "İstanbul", 2)]
    public void UsesOrdinalIgnoreCaseForUnicode(string name, string query, int expected) =>
        Assert.Equal(expected, LiteralRanking.Classify(name, query));

    [Fact]
    public void OpenedPathMatchesOnlyTheLastComponent()
    {
        const string file = @"C:\Klasör\Dosya.txt";
        var ranking = new LiteralRanking([(file, 0.3d)]);

        Assert.Equal(0d, LiteralRanking.Empty.Boost(file));
        Assert.Equal(0.3d, ranking.Boost(@"c:\KLASÖR\dosya.TXT"));
        Assert.Empty(ranking.MatchingOpenedPaths("Klasör"));
        Assert.Empty(ranking.MatchingOpenedPaths(" "));
        Assert.Equal(file, Assert.Single(ranking.MatchingOpenedPaths("DOSYA")));
    }

    [Fact]
    public async Task RecordsAreVisibleImmediatelyAndPersistedAtDispose()
    {
        using var workspace = new TemporaryDirectory();
        var databasePath = Path.Combine(workspace.Path, "open-history.db");
        const string favorite = @"C:\Work\Çizim.dwg";
        const string other = @"C:\Other\Çizim.dwg";
        double firstBoost;
        using (var history = new LocalOpenHistory(databasePath))
        {
            await history.FlushAsync();
            Assert.False(File.Exists(databasePath));

            history.RecordOpened(favorite);
            var initial = history.Snapshot;
            firstBoost = initial.Boost(favorite);
            Assert.InRange(firstBoost, 0.01d, 0.5d);

            history.RecordOpened(other);
            history.RecordOpened(@"c:\WORK\çizim.DWG");
            var updated = history.Snapshot;
            Assert.Equal(2, updated.MatchingOpenedPaths("çizim").Count);
            Assert.True(updated.Boost(favorite) > updated.Boost(other));
            Assert.Equal(firstBoost, initial.Boost(favorite));
            Assert.Equal(0d, initial.Boost(other));
        }

        using var reopened = new LocalOpenHistory(databasePath);
        await reopened.FlushAsync();
        Assert.Equal(2, reopened.Snapshot.MatchingOpenedPaths("ÇIZIM").Count);
        Assert.True(reopened.Snapshot.Boost(favorite) > firstBoost);
        Assert.True(reopened.Snapshot.Boost(favorite) > reopened.Snapshot.Boost(other));
    }

    [Fact]
    public async Task ConcurrentCaseVariantsIncrementOnePersistedCount()
    {
        using var workspace = new TemporaryDirectory();
        var databasePath = Path.Combine(workspace.Path, "open-history.db");
        using var history = new LocalOpenHistory(databasePath);
        Parallel.For(0, 24, i => history.RecordOpened(i % 2 == 0
            ? @"C:\İşler\Özet.txt"
            : @"c:\İŞLER\özet.TXT"));
        await history.FlushAsync();

        Assert.Single(history.Snapshot.MatchingOpenedPaths("ÖZET"));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT open_count FROM opened_paths";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(24, reader.GetInt32(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task LimitsTheNumberOfTrackedPathsAcrossRestart()
    {
        using var workspace = new TemporaryDirectory();
        var databasePath = Path.Combine(workspace.Path, "open-history.db");
        using (var history = new LocalOpenHistory(databasePath))
        {
            for (var i = 0; i < LocalOpenHistory.MaxPaths + 3; i++)
                history.RecordOpened($@"C:\Work\history-{i}.txt");
            Assert.Equal(LocalOpenHistory.MaxPaths, history.Snapshot.MatchingOpenedPaths("history-").Count);
        }

        using var reopened = new LocalOpenHistory(databasePath);
        await reopened.FlushAsync();
        Assert.Equal(LocalOpenHistory.MaxPaths, reopened.Snapshot.MatchingOpenedPaths("history-").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyWriteRemainsQueuedForNextOpenOrShutdown(bool retryAtShutdown)
    {
        using var workspace = new TemporaryDirectory();
        var databasePath = Path.Combine(workspace.Path, "open-history.db");
        const string waiting = @"C:\Work\Waiting.txt";
        var warnings = new List<string>();
        using (var history = new LocalOpenHistory(databasePath, warnings.Add))
        {
            history.RecordOpened(@"C:\Work\Seed.txt");
            await history.FlushAsync();

            using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            blocker.Open();
            using (var transaction = blocker.BeginTransaction(deferred: false))
            {
                history.RecordOpened(waiting);
                await history.FlushAsync();
                var warning = Assert.Single(warnings);
                Assert.DoesNotContain(waiting, warning);
                Assert.DoesNotContain(databasePath, warning);
                Assert.True(history.Snapshot.Boost(waiting) > 0);
                transaction.Commit();
            }

            if (!retryAtShutdown)
            {
                history.RecordOpened(@"C:\Work\Next.txt");
                await history.FlushAsync();

                using var liveReload = new LocalOpenHistory(databasePath);
                await liveReload.FlushAsync();
                Assert.InRange(liveReload.Snapshot.Boost(waiting), 0.25d, 0.27d);
                Assert.True(liveReload.Snapshot.Boost(@"C:\Work\Next.txt") > 0);
            }
        }

        using var reopened = new LocalOpenHistory(databasePath);
        await reopened.FlushAsync();
        Assert.InRange(reopened.Snapshot.Boost(waiting), 0.25d, 0.27d);
    }


    [Fact]
    public async Task CorruptDatabaseLeavesInMemoryRankingAvailable()
    {
        using var workspace = new TemporaryDirectory();
        var contents = new string('x', 512);
        var databasePath = workspace.CreateFile("open-history.db", contents);
        const string file = @"C:\Work\Opened.txt";
        using (var history = new LocalOpenHistory(databasePath))
        {
            await history.FlushAsync();
            Assert.Equal(0d, history.Snapshot.Boost(file));
            history.RecordOpened(file);
            Assert.InRange(history.Snapshot.Boost(file), 0.01d, 0.5d);
        }
        Assert.Equal(contents, File.ReadAllText(databasePath));
    }
}
