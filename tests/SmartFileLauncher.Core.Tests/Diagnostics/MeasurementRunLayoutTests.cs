using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.Core.IO;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Diagnostics;

public sealed class MeasurementRunLayoutTests
{
    [Fact]
    public void PrepareCreatesIsolatedEmptyProductionLayout()
    {
        using var workspace = new TemporaryDirectory();
        var runRoot = Path.Combine(workspace.Path, "run");
        var productionRoaming = workspace.CreateDirectory("production-roaming");
        var productionLocal = workspace.CreateDirectory("production-local");

        using var layout = MeasurementRunLayout.Prepare(
            runRoot,
            productionRoaming,
            productionLocal);

        Assert.Equal(Path.GetFullPath(runRoot), layout.RunRoot);
        Assert.True(Directory.Exists(layout.SettingsDirectory));
        Assert.True(Directory.Exists(layout.IndexDirectory));
        Assert.True(Directory.Exists(layout.ThumbnailCachePath));
        Assert.True(Directory.Exists(layout.CorpusPath!));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.CorpusPath!));
        Assert.All(
            new[]
            {
                layout.SettingsPath,
                layout.DatabasePath,
                layout.DatabaseWalPath,
                layout.DatabaseShmPath,
                layout.ThumbnailCachePath,
                layout.CorpusPath!,
                layout.LeasePath
            },
            path => Assert.StartsWith(
                layout.DataRoot + Path.DirectorySeparatorChar,
                path,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PrepareAllowsExistingFilesOutsideManagedDataDirectory()
    {
        using var workspace = new TemporaryDirectory();
        var runRoot = workspace.CreateDirectory("run");
        File.WriteAllText(Path.Combine(runRoot, "önceki.log"), "kanıt");

        using var layout = MeasurementRunLayout.Prepare(
            runRoot,
            workspace.CreateDirectory("production-roaming"),
            workspace.CreateDirectory("production-local"));

        Assert.True(File.Exists(Path.Combine(runRoot, "önceki.log")));
        Assert.True(Directory.Exists(layout.CorpusPath!));
    }

    [Fact]
    public void PrepareProductionCopyConsumesOnlyPreseededFilesAndLeavesSourcesUntouched()
    {
        using var workspace = new TemporaryDirectory();
        var productionRoaming = workspace.CreateDirectory("production-roaming");
        var productionLocal = workspace.CreateDirectory("production-local");
        File.WriteAllText(Path.Combine(productionRoaming, "settings.json"), "production-settings");
        File.WriteAllText(Path.Combine(productionRoaming, "index.db"), "production-index");
        Directory.CreateDirectory(Path.Combine(productionLocal, "thumbcache"));
        File.WriteAllText(
            Path.Combine(productionLocal, "thumbcache", "old.png"),
            "old");
        var runRoot = CreatePreseededRun(workspace, "run", includeSettings: true);
        File.WriteAllText(
            Path.Combine(runRoot, MeasurementRunLayout.ProductionCopyDataDirectoryName, "settings", "settings.json"),
            "{}");

        using var layout = MeasurementRunLayout.Prepare(
            runRoot,
            productionRoaming,
            productionLocal,
            MeasurementProfile.ProductionCopy);

        Assert.Equal(MeasurementProfile.ProductionCopy, layout.Profile);
        Assert.EndsWith(
            Path.Combine("run", MeasurementRunLayout.ProductionCopyDataDirectoryName),
            layout.DataRoot,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("{}", File.ReadAllText(layout.SettingsPath));
        Assert.True(File.Exists(layout.DatabasePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.ThumbnailCachePath));
        Assert.False(File.Exists(Path.Combine(layout.ThumbnailCachePath, "old.png")));
        Assert.Equal("production-settings", File.ReadAllText(Path.Combine(productionRoaming, "settings.json")));
        Assert.Equal("production-index", File.ReadAllText(Path.Combine(productionRoaming, "index.db")));
    }

    [Fact]
    public void PrepareProductionCopyAllowsMissingSettingsButRequiresIndex()
    {
        using var workspace = new TemporaryDirectory();
        var productionRoaming = workspace.CreateDirectory("production-roaming");
        var productionLocal = workspace.CreateDirectory("production-local");

        Assert.Throws<InvalidOperationException>(() =>
            MeasurementRunLayout.Prepare(
                Path.Combine(workspace.Path, "missing-index"),
                productionRoaming,
                productionLocal,
                MeasurementProfile.ProductionCopy));

        var runRoot = CreatePreseededRun(workspace, "without-settings", includeSettings: false);
        using var layout = MeasurementRunLayout.Prepare(
            runRoot,
            productionRoaming,
            productionLocal,
            MeasurementProfile.ProductionCopy);

        Assert.False(File.Exists(layout.SettingsPath));
        Assert.True(File.Exists(layout.DatabasePath));
    }

    [Fact]
    public void PrepareProductionCopyRejectsInvalidSettingsAndNonSqliteIndex()
    {
        using var workspace = new TemporaryDirectory();
        var productionRoaming = workspace.CreateDirectory("production-roaming");
        var productionLocal = workspace.CreateDirectory("production-local");
        var runRoot = CreatePreseededRun(workspace, "run", includeSettings: true);
        var databasePath = Path.Combine(
            runRoot,
            MeasurementRunLayout.ProductionCopyDataDirectoryName,
            "index",
            "index.db");
        var databaseSnapshot = CaptureSnapshot(databasePath);
        var settingsPath = Path.Combine(
            runRoot,
            MeasurementRunLayout.ProductionCopyDataDirectoryName,
            "settings",
            "settings.json");
        File.WriteAllText(settingsPath, "not-json");

        Assert.Throws<InvalidDataException>(() => MeasurementRunLayout.Prepare(
            runRoot,
            productionRoaming,
            productionLocal,
            MeasurementProfile.ProductionCopy));
        Assert.Equal(databaseSnapshot, CaptureSnapshot(databasePath));

        File.WriteAllText(settingsPath, "{}");
        File.WriteAllText(databasePath, "not-sqlite");
        var corruptSnapshot = CaptureSnapshot(databasePath);

        Assert.Throws<InvalidDataException>(() => MeasurementRunLayout.Prepare(
            runRoot,
            productionRoaming,
            productionLocal,
            MeasurementProfile.ProductionCopy));
        Assert.Equal(corruptSnapshot, CaptureSnapshot(databasePath));
    }

    [Fact]
    public void PrepareProductionCopyRejectsUnexpectedArtifactsAndHardlinks()
    {
        using var workspace = new TemporaryDirectory();
        var productionRoaming = workspace.CreateDirectory("production-roaming");
        var productionLocal = workspace.CreateDirectory("production-local");
        var runRoot = CreatePreseededRun(workspace, "run", includeSettings: true);
        File.WriteAllText(Path.Combine(runRoot, "crash.log"), "old");

        Assert.Throws<InvalidOperationException>(() => MeasurementRunLayout.Prepare(
            runRoot,
            productionRoaming,
            productionLocal,
            MeasurementProfile.ProductionCopy));

        File.Delete(Path.Combine(runRoot, "crash.log"));
        var databasePath = Path.Combine(
            runRoot,
            MeasurementRunLayout.ProductionCopyDataDirectoryName,
            "index",
            "index.db");
        var sourcePath = Path.Combine(workspace.Path, "source.db");
        File.Copy(databasePath, sourcePath);
        File.Delete(databasePath);
        CreateHardLink(databasePath, sourcePath);

        Assert.Throws<InvalidOperationException>(() => MeasurementRunLayout.Prepare(
            runRoot,
            productionRoaming,
            productionLocal,
            MeasurementProfile.ProductionCopy));
    }

    [Fact]
    public void PrepareProductionCopyValidatesMainDatabaseWithoutWal()
    {
        using var workspace = new TemporaryDirectory();
        var source = CreateSqliteSeedFiles(workspace, "main-only", keepWal: false);
        var runRoot = CreatePreseededRunFromSeed(workspace, "main-only-run", source);
        var sourceSnapshot = CaptureSnapshots(source);

        using var layout = MeasurementRunLayout.Prepare(
            runRoot,
            workspace.CreateDirectory("production-roaming"),
            workspace.CreateDirectory("production-local"),
            MeasurementProfile.ProductionCopy);

        AssertSeedUnchanged(source, sourceSnapshot);
    }

    [Fact]
    public void PrepareProductionCopyRejectsCommittedWalAndShmAndLeavesSeedUnchanged()
    {
        using var workspace = new TemporaryDirectory();
        var source = CreateSqliteSeedFiles(workspace, "committed-wal", keepWal: true);
        Assert.NotNull(source.Wal);
        Assert.NotNull(source.Shm);
        var runRoot = CreatePreseededRunFromSeed(
            workspace,
            "committed-wal-run",
            source,
            wal: source.Wal,
            shm: source.Shm);
        var sourceSnapshot = CaptureSnapshots(source);

        Assert.Throws<InvalidOperationException>(() => MeasurementRunLayout.Prepare(
            runRoot,
            workspace.CreateDirectory("production-roaming"),
            workspace.CreateDirectory("production-local"),
            MeasurementProfile.ProductionCopy));

        AssertSeedUnchanged(source, sourceSnapshot);
    }

    [Fact]
    public void PrepareProductionCopyRejectsWalWithoutShm()
    {
        using var workspace = new TemporaryDirectory();
        var source = CreateSqliteSeedFiles(workspace, "missing-shm", keepWal: true);
        Assert.NotNull(source.Wal);
        Assert.NotNull(source.Shm);
        var runRoot = CreatePreseededRunFromSeed(
            workspace,
            "missing-shm-run",
            source,
            wal: source.Wal,
            includeShm: false);
        var sourceSnapshot = CaptureSnapshots(source);

        Assert.Throws<InvalidOperationException>(() => MeasurementRunLayout.Prepare(
            runRoot,
            workspace.CreateDirectory("production-roaming"),
            workspace.CreateDirectory("production-local"),
            MeasurementProfile.ProductionCopy));

        AssertSeedUnchanged(source, sourceSnapshot);
    }

    [Fact]
    public void PrepareProductionCopyRejectsMismatchedWal()
    {
        using var workspace = new TemporaryDirectory();
        var main = CreateSqliteSeedFiles(workspace, "mismatched-main", keepWal: false);
        var stale = CreateSqliteSeedFiles(workspace, "mismatched-wal", keepWal: true);
        Assert.NotNull(stale.Wal);
        Assert.NotNull(stale.Shm);
        var runRoot = CreatePreseededRunFromSeed(
            workspace,
            "mismatched-run",
            main,
            wal: stale.Wal,
            shm: stale.Shm);
        var sourceSnapshots = new[] { main, stale }
            .Select(seed => (Seed: seed, Snapshot: CaptureSnapshots(seed)))
            .ToArray();

        Assert.Throws<InvalidOperationException>(() => MeasurementRunLayout.Prepare(
            runRoot,
            workspace.CreateDirectory("production-roaming"),
            workspace.CreateDirectory("production-local"),
            MeasurementProfile.ProductionCopy));
        Assert.All(
            sourceSnapshots,
            snapshot => AssertSeedUnchanged(snapshot.Seed, snapshot.Snapshot));
    }

    [Fact]
    public void PrepareProductionCopyRejectsForeignAndUnsupportedSchemaDatabases()
    {
        using var workspace = new TemporaryDirectory();
        var foreign = CreateForeignSqliteSeed(workspace, "foreign");
        var foreignRun = CreatePreseededRunFromSeed(workspace, "foreign-run", foreign);
        var foreignSnapshot = CaptureSnapshots(foreign);

        Assert.Throws<InvalidDataException>(() => MeasurementRunLayout.Prepare(
            foreignRun,
            workspace.CreateDirectory("foreign-production-roaming"),
            workspace.CreateDirectory("foreign-production-local"),
            MeasurementProfile.ProductionCopy));
        AssertSeedUnchanged(foreign, foreignSnapshot);

        var unsupported = CreateSqliteSeedFiles(workspace, "unsupported", keepWal: false);
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = unsupported.Database,
                       Mode = SqliteOpenMode.ReadWrite,
                       Pooling = false
                   }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE Metadata SET Value = '99' WHERE Key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        var unsupportedRun = CreatePreseededRunFromSeed(
            workspace,
            "unsupported-run",
            unsupported);
        var unsupportedSnapshot = CaptureSnapshots(unsupported);
        Assert.Throws<InvalidDataException>(() => MeasurementRunLayout.Prepare(
            unsupportedRun,
            workspace.CreateDirectory("unsupported-production-roaming"),
            workspace.CreateDirectory("unsupported-production-local"),
            MeasurementProfile.ProductionCopy));
        AssertSeedUnchanged(unsupported, unsupportedSnapshot);
    }

    [Fact]
    public void PrepareRejectsNonEmptyManagedDataWithoutDeletingIt()
    {
        using var workspace = new TemporaryDirectory();
        var runRoot = workspace.CreateDirectory("run");
        var existingFile = workspace.CreateFile(
            Path.Combine("run", MeasurementRunLayout.DataDirectoryName, "keep.txt"),
            "koru");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MeasurementRunLayout.Prepare(
                runRoot,
                workspace.CreateDirectory("production-roaming"),
                workspace.CreateDirectory("production-local")));

        Assert.Contains("boş değil", exception.Message);
        Assert.True(File.Exists(existingFile));
    }

    [Fact]
    public void PrepareRejectsProductionPathOverlap()
    {
        using var workspace = new TemporaryDirectory();
        var productionRoaming = workspace.CreateDirectory("production-roaming");
        var runRoot = Path.Combine(productionRoaming, "run");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MeasurementRunLayout.Prepare(
                runRoot,
                productionRoaming,
                workspace.CreateDirectory("production-local")));

        Assert.Contains("production", exception.Message);
        Assert.False(Directory.Exists(runRoot));
    }

    [Fact]
    public void PrepareRejectsRelativeRunRoot()
    {
        using var workspace = new TemporaryDirectory();

        Assert.Throws<InvalidOperationException>(() =>
            MeasurementRunLayout.Prepare(
                "relative-run",
                workspace.CreateDirectory("production-roaming"),
                workspace.CreateDirectory("production-local")));
    }

    [Fact]
    public void PrepareRejectsUncAndDevicePaths()
    {
        using var workspace = new TemporaryDirectory();
        var productionRoaming = workspace.CreateDirectory("production-roaming");
        var productionLocal = workspace.CreateDirectory("production-local");

        Assert.Throws<InvalidOperationException>(() =>
            MeasurementRunLayout.Prepare(
                @"\\server\share\run",
                productionRoaming,
                productionLocal));
        Assert.Throws<InvalidOperationException>(() =>
            MeasurementRunLayout.Prepare(
                @"\\?\C:\olcum\run",
                productionRoaming,
                productionLocal));
    }

    [Fact]
    public void PrepareHoldsExclusiveLeaseUntilDisposed()
    {
        using var workspace = new TemporaryDirectory();
        var layout = MeasurementRunLayout.Prepare(
            Path.Combine(workspace.Path, "run"),
            workspace.CreateDirectory("production-roaming"),
            workspace.CreateDirectory("production-local"));
        var leasePath = layout.LeasePath;

        Assert.Throws<IOException>(() =>
        {
            using var competingHandle = new FileStream(
                leasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
        });

        layout.Dispose();
        layout.Dispose();
        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public void PrepareRejectsReparsePointInRunRootAncestor()
    {
        using var workspace = new TemporaryDirectory();
        var runRoot = Path.Combine(workspace.Path, "run");
        var guard = CreateGuard(path =>
        {
            var attributes = ReadAttributes(path);
            return attributes.HasValue && path.Equals(
                workspace.Path,
                StringComparison.OrdinalIgnoreCase)
                ? attributes.Value | FileAttributes.ReparsePoint
                : attributes;
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MeasurementRunLayout.Prepare(
                runRoot,
                workspace.CreateDirectory("production-roaming"),
                workspace.CreateDirectory("production-local"),
                guard));

        Assert.Contains("yönlendirilmiş", exception.Message);
        Assert.False(Directory.Exists(runRoot));
    }

    [Fact]
    public void PrepareRejectsPhysicalAliasOverlappingProductionData()
    {
        using var workspace = new TemporaryDirectory();
        var aliasRoot = workspace.CreateDirectory("alias-root");
        var productionRoaming = workspace.CreateDirectory("production-roaming");
        var runRoot = Path.Combine(aliasRoot, "run");
        var guard = CreateGuard(
            resolveExistingPath: path => path.Equals(
                aliasRoot,
                StringComparison.OrdinalIgnoreCase)
                ? productionRoaming
                : path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MeasurementRunLayout.Prepare(
                runRoot,
                productionRoaming,
                workspace.CreateDirectory("production-local"),
                guard));

        Assert.Contains("production", exception.Message);
        Assert.False(Directory.Exists(runRoot));
    }

    private static FileSystemPathGuard CreateGuard(
        Func<string, FileAttributes?>? readAttributes = null,
        Func<string, string>? resolveExistingPath = null)
    {
        return new FileSystemPathGuard(
            readAttributes ?? ReadAttributes,
            path => Directory.GetFileSystemEntries(path),
            resolveExistingPath ?? (path => path));
    }

    private static FileAttributes? ReadAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string CreatePreseededRun(
        TemporaryDirectory workspace,
        string name,
        bool includeSettings)
    {
        workspace.CreateDirectory(
            Path.Combine(name, MeasurementRunLayout.ProductionCopyDataDirectoryName));
        var settingsDirectory = workspace.CreateDirectory(Path.Combine(
            name,
            MeasurementRunLayout.ProductionCopyDataDirectoryName,
            "settings"));
        var indexDirectory = workspace.CreateDirectory(Path.Combine(
            name,
            MeasurementRunLayout.ProductionCopyDataDirectoryName,
            "index"));
        workspace.CreateDirectory(Path.Combine(
            name,
            MeasurementRunLayout.ProductionCopyDataDirectoryName,
            "thumbcache"));
        if (includeSettings)
        {
            File.WriteAllText(Path.Combine(settingsDirectory, "settings.json"), "{}");
        }

        var databasePath = Path.Combine(indexDirectory, "index.db");
        using var database = new IndexDatabase(databasePath);
        database.Open();
        database.Close();
        File.Delete(databasePath + "-wal");
        File.Delete(databasePath + "-shm");
        return Path.Combine(workspace.Path, name);
    }

    private static string CreatePreseededRunFromSeed(
        TemporaryDirectory workspace,
        string name,
        SqliteSeedFiles seed,
        string? wal = null,
        string? shm = null,
        bool includeSettings = true,
        bool includeShm = true)
    {
        workspace.CreateDirectory(
            Path.Combine(name, MeasurementRunLayout.ProductionCopyDataDirectoryName));
        var settingsDirectory = workspace.CreateDirectory(Path.Combine(
            name,
            MeasurementRunLayout.ProductionCopyDataDirectoryName,
            "settings"));
        var indexDirectory = workspace.CreateDirectory(Path.Combine(
            name,
            MeasurementRunLayout.ProductionCopyDataDirectoryName,
            "index"));
        workspace.CreateDirectory(Path.Combine(
            name,
            MeasurementRunLayout.ProductionCopyDataDirectoryName,
            "thumbcache"));
        if (includeSettings)
        {
            File.WriteAllText(Path.Combine(settingsDirectory, "settings.json"), "{}");
        }

        var databasePath = Path.Combine(indexDirectory, "index.db");
        File.Copy(seed.Database, databasePath);
        if (wal != null)
        {
            File.Copy(wal, databasePath + "-wal");
        }

        if (includeShm && shm != null)
        {
            File.Copy(shm, databasePath + "-shm");
        }

        return Path.Combine(workspace.Path, name);
    }

    private static SqliteSeedFiles CreateSqliteSeedFiles(
        TemporaryDirectory workspace,
        string name,
        bool keepWal)
    {
        var sourcePath = Path.Combine(workspace.Path, name + ".db");
        using (var setup = new IndexDatabase(sourcePath))
        {
            setup.Open();
            setup.Close();
        }

        File.Delete(sourcePath + "-wal");
        File.Delete(sourcePath + "-shm");
        var seedDatabase = Path.Combine(workspace.Path, name + ".seed.db");
        var copiedWal = seedDatabase + "-wal";
        var copiedShm = seedDatabase + "-shm";
        if (keepWal)
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false
                }.ToString());
            connection.Open();
            ExecuteSqlite(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT OR REPLACE INTO Metadata (Key, Value) VALUES ('wal-seed', 'committed');";
            command.ExecuteNonQuery();
            transaction.Commit();

            Assert.True(File.Exists(sourcePath + "-wal"));
            Assert.True(File.Exists(sourcePath + "-shm"));
            File.Copy(sourcePath, seedDatabase, overwrite: true);
            File.Copy(sourcePath + "-wal", copiedWal, overwrite: true);
            File.Copy(sourcePath + "-shm", copiedShm, overwrite: true);
        }
        else
        {
            File.Copy(sourcePath, seedDatabase, overwrite: true);
        }


        return new SqliteSeedFiles(
            seedDatabase,
            keepWal ? copiedWal : null,
            keepWal ? copiedShm : null);
    }

    private static SqliteSeedFiles CreateForeignSqliteSeed(
        TemporaryDirectory workspace,
        string name)
    {
        var databasePath = Path.Combine(workspace.Path, name + ".db");
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = databasePath,
                       Mode = SqliteOpenMode.ReadWriteCreate,
                       Pooling = false
                   }.ToString()))
        {
            connection.Open();
            ExecuteSqlite(connection, "CREATE TABLE ForeignData (Value TEXT NOT NULL);");
            ExecuteSqlite(connection, "INSERT INTO ForeignData (Value) VALUES ('foreign');");
        }

        var seedDatabase = Path.Combine(workspace.Path, name + ".seed.db");
        File.Copy(databasePath, seedDatabase, overwrite: true);
        return new SqliteSeedFiles(seedDatabase, null, null);
    }

    private static void ExecuteSqlite(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyDictionary<string, FileSnapshot> CaptureSnapshots(SqliteSeedFiles seed)
    {
        var files = new[] { seed.Database, seed.Wal, seed.Shm }
            .Where(path => path != null)
            .Select(path => path!)
            .ToDictionary(path => path, CaptureSnapshot, StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static FileSnapshot CaptureSnapshot(string path)
    {
        var info = new FileInfo(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        return new FileSnapshot(
            Convert.ToHexString(sha256.ComputeHash(stream)),
            info.Length,
            info.LastWriteTimeUtc);
    }

    private static void AssertSeedUnchanged(
        SqliteSeedFiles seed,
        IReadOnlyDictionary<string, FileSnapshot> snapshots)
    {
        var current = CaptureSnapshots(seed);
        AssertSeedUnchanged(current, snapshots);
    }

    private static void AssertSeedUnchanged(
        IReadOnlyDictionary<string, FileSnapshot> current,
        IReadOnlyDictionary<string, FileSnapshot> expected)
    {
        Assert.Equal(expected.Keys.OrderBy(key => key), current.Keys.OrderBy(key => key));
        foreach (var path in expected.Keys)
        {
            Assert.Equal(expected[path], current[path]);
        }
    }

    private sealed record SqliteSeedFiles(string Database, string? Wal, string? Shm);

    private sealed record FileSnapshot(string Hash, long Length, DateTime LastWriteUtc);

    private static void CreateHardLink(string path, string existingPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (!CreateHardLinkNative(path, existingPath, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLinkNative(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
