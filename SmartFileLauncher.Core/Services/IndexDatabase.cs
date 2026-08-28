using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.IO;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Services;

/// <summary>
/// SQLite-based index database for persistent file system caching.
/// Provides O(1) path lookups via hash-indexed tables.
/// 
/// Database location: %APPDATA%\OmniSpot\index.db
/// </summary>
public class IndexDatabase : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private bool _disposed;

    public IndexDatabase(string? customPath = null)
    {
        if (customPath != null)
        {
            _dbPath = customPath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var omniSpotDir = Path.Combine(appData, "OmniSpot");
            Directory.CreateDirectory(omniSpotDir);
            _dbPath = Path.Combine(omniSpotDir, "index.db");
        }
    }

    public string DatabasePath => _dbPath;
    public bool IsOpen => _connection != null;

    #region Connection Management

    public static void ValidateSeed(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var pathGuard = FileSystemPathGuard.Default;
        var canonicalDatabasePath = pathGuard.Canonicalize(databasePath);
        if (!File.Exists(canonicalDatabasePath))
        {
            throw new InvalidDataException(
                "Ölçüm index.db dosyası bulunamadı.");
        }

        if (File.Exists(canonicalDatabasePath + "-wal") ||
            File.Exists(canonicalDatabasePath + "-shm"))
        {
            throw new InvalidDataException(
                "Ölçüm index.db temiz ve checkpoint edilmiş olmalıdır; WAL/SHM sidecar kabul edilmez.");
        }

        try
        {
            if (pathGuard.FindReparsePointInExistingPath(canonicalDatabasePath) != null)
            {
                throw new InvalidDataException(
                    "Ölçüm index.db yeniden yönlendirilmiş bir dosya olamaz.");
            }

            var indexDirectory = Path.GetDirectoryName(canonicalDatabasePath);
            if (string.IsNullOrWhiteSpace(indexDirectory))
            {
                throw new InvalidDataException(
                    "Ölçüm index.db doğrulama dizini bulunamadı.");
            }

            var before = CaptureSeedFingerprint(indexDirectory, pathGuard);
            string? validationDirectory = null;
            Exception? validationError = null;
            Exception? cleanupError = null;
            try
            {
                validationDirectory = CreateValidationDirectory(indexDirectory, pathGuard);
                var clonePath = Path.Combine(validationDirectory, "index.db");
                File.Copy(canonicalDatabasePath, clonePath);
                ValidateClone(clonePath);
            }
            catch (Exception ex)
            {
                validationError = ex;
            }
            finally
            {
                if (validationDirectory != null)
                {
                    try
                    {
                        DeleteValidationDirectory(validationDirectory);
                    }
                    catch (Exception ex)
                    {
                        cleanupError = ex;
                    }
                }
            }

            var after = CaptureSeedFingerprint(indexDirectory, pathGuard);
            if (!FingerprintsEqual(before, after))
            {
                throw new InvalidDataException(
                    "Ölçüm index.db doğrulaması sırasında seed değişti.");
            }

            if (cleanupError != null)
            {
                throw new InvalidDataException(
                    "Ölçüm index.db doğrulama geçici dizini temizlenemedi.",
                    cleanupError);
            }

            if (validationError != null)
            {
                ExceptionDispatchInfo.Capture(validationError).Throw();
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or UnauthorizedAccessException or
                InvalidOperationException)
        {
            throw new InvalidDataException(
                "Ölçüm index.db geçerli ve doğrulanabilir bir SQLite dosyası değil.",
                ex);
        }
    }

    private static void ValidateClone(string clonePath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = clonePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        connection.Open();

        ValidateCheck(connection, "quick_check");
        ValidateCheck(connection, "integrity_check");
        ValidateSchemaSignature(connection);
    }

    private static void ValidateSchemaSignature(SqliteConnection connection)
    {
        var requiredColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directories"] = new[]
            {
                "Id", "FullPath", "Name", "ParentId", "Depth",
                "LastWriteTimeUtc", "LastIndexedTimeUtc", "IsHidden"
            },
            ["Files"] = new[]
            {
                "Id", "FullPath", "FileName", "Extension", "DirectoryId",
                "SizeBytes", "CreatedTimeUtc", "LastWriteTimeUtc",
                "LastIndexedTimeUtc", "OpenCount", "IsHidden", "IsSystem"
            },
            ["Tokens"] = new[] { "Id", "Token" },
            ["FileTokens"] = new[] { "FileId", "TokenId" },
            ["Metadata"] = new[] { "Key", "Value" },
            ["ExcludedPaths"] = new[] { "Id", "Pattern", "IsRegex" }
        };

        foreach (var table in requiredColumns)
        {
            var foundColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table.Key}\");";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                foundColumns.Add(reader.GetString(1));
            }

            if (!table.Value.All(foundColumns.Contains))
            {
                throw new InvalidDataException(
                    "Ölçüm index.db OmniSpot şema imzasını karşılamıyor.");
            }
        }

        using var schemaVersionCommand = connection.CreateCommand();
        schemaVersionCommand.CommandText =
            "SELECT Value FROM Metadata WHERE Key = 'schema_version';";
        var schemaVersion = schemaVersionCommand.ExecuteScalar()?.ToString();
        if (!string.Equals(
                schemaVersion,
                CurrentSchemaVersion.ToString(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Ölçüm index.db desteklenmeyen OmniSpot şema sürümünü kullanıyor.");
        }
    }

    private static string CreateValidationDirectory(
        string indexDirectory,
        FileSystemPathGuard pathGuard)
    {
        var canonicalDirectory = pathGuard.Canonicalize(indexDirectory);
        var physicalDirectory = pathGuard.ResolvePhysicalPath(canonicalDirectory);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Path.Combine(
                canonicalDirectory,
                ".validate-" + Guid.NewGuid().ToString("N"));
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                continue;
            }

            Directory.CreateDirectory(candidate);
            if (pathGuard.FindReparsePointInExistingPath(candidate) != null ||
                !IsSameOrDescendant(
                    pathGuard.ResolvePhysicalPath(candidate),
                    physicalDirectory))
            {
                DeleteValidationDirectory(candidate);
                throw new InvalidDataException(
                    "Ölçüm index.db doğrulama dizini fiziksel olarak içeride değil.");
            }

            return candidate;
        }

        throw new InvalidDataException(
            "Ölçüm index.db doğrulama dizini oluşturulamadı.");
    }

    private static void DeleteValidationDirectory(string directory)
    {
        if (!Directory.Exists(directory) && !File.Exists(directory))
        {
            return;
        }

        if (File.Exists(directory))
        {
            throw new IOException("Doğrulama yolu dizin değil.");
        }

        Directory.Delete(directory, recursive: true);
        if (Directory.Exists(directory) || File.Exists(directory))
        {
            throw new IOException("Doğrulama yolu silinemedi.");
        }
    }

    private static SeedFingerprint CaptureSeedFingerprint(
        string indexDirectory,
        FileSystemPathGuard pathGuard)
    {
        var entries = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(indexDirectory))
        {
            var canonicalEntry = pathGuard.Canonicalize(entry);
            if (Directory.Exists(canonicalEntry))
            {
                throw new InvalidDataException(
                    "Ölçüm index.db dizininde beklenmeyen dizin var.");
            }

            if (!File.Exists(canonicalEntry))
            {
                throw new InvalidDataException(
                    "Ölçüm index.db seed dosya kümesi okunamadı.");
            }

            entries.Add(
                Path.GetFileName(canonicalEntry),
                CaptureFileFingerprint(canonicalEntry, pathGuard));
        }

        return new SeedFingerprint(entries);
    }

    private static FileFingerprint CaptureFileFingerprint(
        string path,
        FileSystemPathGuard pathGuard)
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
        var hash = Convert.ToHexString(sha256.ComputeHash(stream));
        return new FileFingerprint(
            hash,
            info.Length,
            info.LastWriteTimeUtc,
            pathGuard.GetFileIdentity(path));
    }

    private static bool FingerprintsEqual(
        SeedFingerprint first,
        SeedFingerprint second)
    {
        if (first.Files.Count != second.Files.Count)
        {
            return false;
        }

        foreach (var entry in first.Files)
        {
            if (!second.Files.TryGetValue(entry.Key, out var other) ||
                entry.Value != other)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record SeedFingerprint(
        IReadOnlyDictionary<string, FileFingerprint> Files);

    private sealed record FileFingerprint(
        string Hash,
        long Length,
        DateTime LastWriteUtc,
        FileSystemPathGuard.FileIdentity Identity);

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;

        return candidate.StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens the database connection and ensures schema exists.
    /// </summary>
    public void Open()
    {
        if (_connection != null) return;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        // SQLite foreign key enforcement is connection-scoped and disabled by default.
        ExecuteNonQuery("PRAGMA foreign_keys=ON;");

        // Enable WAL mode for better concurrent performance
        ExecuteNonQuery("PRAGMA journal_mode=WAL;");
        ExecuteNonQuery("PRAGMA synchronous=NORMAL;");

        EnsureSchema();
        RepairOrphanedRows();
    }

    /// <summary>
    /// Closes the database connection.
    /// </summary>
    public void Close()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
    }

    /// <summary>
    /// Deletes the database file (for fresh start).
    /// </summary>
    public void DeleteDatabase()
    {
        Close();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
            // Also delete WAL and SHM files
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
    }

    #endregion

    #region Schema Management

    private void EnsureSchema()
    {
        var version = GetSchemaVersion();
        if (version < CurrentSchemaVersion)
        {
            CreateSchema();
            SetMetadata(IndexMetadata.Keys.SchemaVersion, CurrentSchemaVersion.ToString());
        }
    }

    /// <summary>
    /// Repairs rows that may have been written by older versions while SQLite
    /// foreign-key enforcement was disabled. Future writes are protected by the
    /// connection-level PRAGMA; this makes existing caches safe to reuse too.
    /// </summary>
    private void RepairOrphanedRows()
    {
        using var transaction = BeginTransaction();
        try
        {
            ExecuteNonQuery(@"
                DELETE FROM FileTokens
                WHERE NOT EXISTS (SELECT 1 FROM Files WHERE Files.Id = FileTokens.FileId)
                   OR NOT EXISTS (SELECT 1 FROM Tokens WHERE Tokens.Id = FileTokens.TokenId);

                DELETE FROM Files
                WHERE NOT EXISTS (SELECT 1 FROM Directories WHERE Directories.Id = Files.DirectoryId);

                DELETE FROM Directories
                WHERE ParentId IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM Directories AS Parent WHERE Parent.Id = Directories.ParentId);
            ");
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private int GetSchemaVersion()
    {
        try
        {
            var value = GetMetadata(IndexMetadata.Keys.SchemaVersion);
            return int.TryParse(value, out var v) ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void CreateSchema()
    {
        // Directories table
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS Directories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FullPath TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL,
                ParentId INTEGER,
                Depth INTEGER NOT NULL DEFAULT 0,
                LastWriteTimeUtc INTEGER NOT NULL,
                LastIndexedTimeUtc INTEGER NOT NULL,
                IsHidden INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (ParentId) REFERENCES Directories(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_directories_path ON Directories(FullPath);
            CREATE INDEX IF NOT EXISTS idx_directories_parent ON Directories(ParentId);
        ");

        // Files table
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FullPath TEXT NOT NULL UNIQUE,
                FileName TEXT NOT NULL,
                Extension TEXT NOT NULL,
                DirectoryId INTEGER NOT NULL,
                SizeBytes INTEGER NOT NULL,
                CreatedTimeUtc INTEGER NOT NULL,
                LastWriteTimeUtc INTEGER NOT NULL,
                LastIndexedTimeUtc INTEGER NOT NULL,
                OpenCount INTEGER NOT NULL DEFAULT 0,
                IsHidden INTEGER NOT NULL DEFAULT 0,
                IsSystem INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (DirectoryId) REFERENCES Directories(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_files_path ON Files(FullPath);
            CREATE INDEX IF NOT EXISTS idx_files_directory ON Files(DirectoryId);
            CREATE INDEX IF NOT EXISTS idx_files_extension ON Files(Extension);
            CREATE INDEX IF NOT EXISTS idx_files_name ON Files(FileName);
        ");

        // Tokens table (inverted index persistence)
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS Tokens (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Token TEXT NOT NULL UNIQUE
            );
            CREATE INDEX IF NOT EXISTS idx_tokens_token ON Tokens(Token);
        ");

        // File-Token mapping (many-to-many)
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS FileTokens (
                FileId INTEGER NOT NULL,
                TokenId INTEGER NOT NULL,
                PRIMARY KEY (FileId, TokenId),
                FOREIGN KEY (FileId) REFERENCES Files(Id) ON DELETE CASCADE,
                FOREIGN KEY (TokenId) REFERENCES Tokens(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_filetokens_token ON FileTokens(TokenId);
        ");

        // Metadata table (key-value store)
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS Metadata (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
        ");

        // Excluded paths table
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS ExcludedPaths (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Pattern TEXT NOT NULL UNIQUE,
                IsRegex INTEGER NOT NULL DEFAULT 0
            );
        ");
    }

    #endregion

    #region Metadata Operations

    public void SetMetadata(string key, string value)
    {
        using var cmd = CreateCommand(
            "INSERT OR REPLACE INTO Metadata (Key, Value) VALUES (@key, @value)");
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    public string? GetMetadata(string key)
    {
        using var cmd = CreateCommand("SELECT Value FROM Metadata WHERE Key = @key");
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    #endregion

    #region Directory Operations

    public long InsertDirectory(IndexedDirectory dir)
    {
        using var cmd = CreateCommand(@"
            INSERT INTO Directories (FullPath, Name, ParentId, Depth, LastWriteTimeUtc, LastIndexedTimeUtc, IsHidden)
            VALUES (@path, @name, @parentId, @depth, @lastWrite, @lastIndexed, @hidden)
            ON CONFLICT(FullPath) DO UPDATE SET
                Name = excluded.Name,
                ParentId = excluded.ParentId,
                LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                LastIndexedTimeUtc = excluded.LastIndexedTimeUtc,
                IsHidden = excluded.IsHidden
            RETURNING Id;");
        
        cmd.Parameters.AddWithValue("@path", dir.FullPath);
        cmd.Parameters.AddWithValue("@name", dir.Name);
        cmd.Parameters.AddWithValue("@parentId", dir.ParentId.HasValue ? dir.ParentId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@depth", dir.Depth);
        cmd.Parameters.AddWithValue("@lastWrite", dir.LastWriteTimeUtc);
        cmd.Parameters.AddWithValue("@lastIndexed", dir.LastIndexedTimeUtc);
        cmd.Parameters.AddWithValue("@hidden", dir.IsHidden ? 1 : 0);
        
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public IndexedDirectory? GetDirectoryByPath(string path)
    {
        using var cmd = CreateCommand("SELECT * FROM Directories WHERE FullPath = @path");
        cmd.Parameters.AddWithValue("@path", path);
        
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return ReadDirectory(reader);
        }
        return null;
    }

    public void DeleteDirectory(string path)
    {
        using var cmd = CreateCommand("DELETE FROM Directories WHERE FullPath = @path");
        cmd.Parameters.AddWithValue("@path", path);
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<IndexedDirectory> GetAllDirectories()
    {
        using var cmd = CreateCommand("SELECT * FROM Directories ORDER BY Depth, Name");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return ReadDirectory(reader);
        }
    }

    private static IndexedDirectory ReadDirectory(SqliteDataReader reader)
    {
        return new IndexedDirectory
        {
            Id = reader.GetInt64(0),
            FullPath = reader.GetString(1),
            Name = reader.GetString(2),
            ParentId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            Depth = reader.GetInt32(4),
            LastWriteTimeUtc = reader.GetInt64(5),
            LastIndexedTimeUtc = reader.GetInt64(6),
            IsHidden = reader.GetInt32(7) == 1
        };
    }

    #endregion

    #region File Operations

    public long InsertFile(IndexedFile file)
    {
        using var cmd = CreateCommand(@"
            INSERT INTO Files (FullPath, FileName, Extension, DirectoryId, SizeBytes, CreatedTimeUtc, LastWriteTimeUtc, LastIndexedTimeUtc, OpenCount, IsHidden, IsSystem)
            VALUES (@path, @name, @ext, @dirId, @size, @created, @lastWrite, @lastIndexed, @openCount, @hidden, @system)
            ON CONFLICT(FullPath) DO UPDATE SET
                FileName = excluded.FileName,
                Extension = excluded.Extension,
                DirectoryId = excluded.DirectoryId,
                SizeBytes = excluded.SizeBytes,
                LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                LastIndexedTimeUtc = excluded.LastIndexedTimeUtc,
                IsHidden = excluded.IsHidden,
                IsSystem = excluded.IsSystem
            RETURNING Id;");
        
        cmd.Parameters.AddWithValue("@path", file.FullPath);
        cmd.Parameters.AddWithValue("@name", file.FileName);
        cmd.Parameters.AddWithValue("@ext", file.Extension);
        cmd.Parameters.AddWithValue("@dirId", file.DirectoryId);
        cmd.Parameters.AddWithValue("@size", file.SizeBytes);
        cmd.Parameters.AddWithValue("@created", file.CreatedTimeUtc);
        cmd.Parameters.AddWithValue("@lastWrite", file.LastWriteTimeUtc);
        cmd.Parameters.AddWithValue("@lastIndexed", file.LastIndexedTimeUtc);
        cmd.Parameters.AddWithValue("@openCount", file.OpenCount);
        cmd.Parameters.AddWithValue("@hidden", file.IsHidden ? 1 : 0);
        cmd.Parameters.AddWithValue("@system", file.IsSystem ? 1 : 0);
        
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public IndexedFile? GetFileByPath(string path)
    {
        using var cmd = CreateCommand("SELECT * FROM Files WHERE FullPath = @path");
        cmd.Parameters.AddWithValue("@path", path);
        
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return ReadFile(reader);
        }
        return null;
    }

    public void DeleteFile(string path)
    {
        using var cmd = CreateCommand("DELETE FROM Files WHERE FullPath = @path");
        cmd.Parameters.AddWithValue("@path", path);
        cmd.ExecuteNonQuery();
    }

    public void IncrementOpenCount(string path)
    {
        using var cmd = CreateCommand("UPDATE Files SET OpenCount = OpenCount + 1 WHERE FullPath = @path");
        cmd.Parameters.AddWithValue("@path", path);
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<IndexedFile> GetAllFiles()
    {
        using var cmd = CreateCommand("SELECT * FROM Files ORDER BY FileName");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return ReadFile(reader);
        }
    }

    public IEnumerable<IndexedFile> GetFilesByDirectory(long directoryId)
    {
        using var cmd = CreateCommand("SELECT * FROM Files WHERE DirectoryId = @dirId ORDER BY FileName");
        cmd.Parameters.AddWithValue("@dirId", directoryId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return ReadFile(reader);
        }
    }

    public int GetFileCount()
    {
        using var cmd = CreateCommand("SELECT COUNT(*) FROM Files");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetDirectoryCount()
    {
        using var cmd = CreateCommand("SELECT COUNT(*) FROM Directories");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static IndexedFile ReadFile(SqliteDataReader reader)
    {
        return new IndexedFile
        {
            Id = reader.GetInt64(0),
            FullPath = reader.GetString(1),
            FileName = reader.GetString(2),
            Extension = reader.GetString(3),
            DirectoryId = reader.GetInt64(4),
            SizeBytes = reader.GetInt64(5),
            CreatedTimeUtc = reader.GetInt64(6),
            LastWriteTimeUtc = reader.GetInt64(7),
            LastIndexedTimeUtc = reader.GetInt64(8),
            OpenCount = reader.GetInt32(9),
            IsHidden = reader.GetInt32(10) == 1,
            IsSystem = reader.GetInt32(11) == 1
        };
    }

    #endregion

    #region Token Operations (Inverted Index Persistence)

    public long GetOrCreateToken(string token)
    {
        using var cmd = CreateCommand(@"
            INSERT INTO Tokens (Token) VALUES (@token)
            ON CONFLICT(Token) DO UPDATE SET Token = Token
            RETURNING Id;");
        cmd.Parameters.AddWithValue("@token", token);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void LinkFileToToken(long fileId, long tokenId)
    {
        using var cmd = CreateCommand(@"
            INSERT OR IGNORE INTO FileTokens (FileId, TokenId) VALUES (@fileId, @tokenId)");
        cmd.Parameters.AddWithValue("@fileId", fileId);
        cmd.Parameters.AddWithValue("@tokenId", tokenId);
        cmd.ExecuteNonQuery();
    }

    public void UnlinkFileTokens(long fileId)
    {
        using var cmd = CreateCommand("DELETE FROM FileTokens WHERE FileId = @fileId");
        cmd.Parameters.AddWithValue("@fileId", fileId);
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<long> GetFileIdsByToken(string token)
    {
        using var cmd = CreateCommand(@"
            SELECT ft.FileId FROM FileTokens ft
            JOIN Tokens t ON t.Id = ft.TokenId
            WHERE t.Token = @token");
        cmd.Parameters.AddWithValue("@token", token);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return reader.GetInt64(0);
        }
    }

    #endregion

    #region Excluded Paths

    public void AddExcludedPath(string pattern, bool isRegex = false)
    {
        using var cmd = CreateCommand(@"
            INSERT OR IGNORE INTO ExcludedPaths (Pattern, IsRegex) VALUES (@pattern, @isRegex)");
        cmd.Parameters.AddWithValue("@pattern", pattern);
        cmd.Parameters.AddWithValue("@isRegex", isRegex ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<(string Pattern, bool IsRegex)> GetExcludedPaths()
    {
        using var cmd = CreateCommand("SELECT Pattern, IsRegex FROM ExcludedPaths");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return (reader.GetString(0), reader.GetInt32(1) == 1);
        }
    }

    #endregion

    #region Batch Operations

    /// <summary>
    /// Begins a transaction for batch operations.
    /// </summary>
    public SqliteTransaction BeginTransaction()
    {
        EnsureConnection();
        return _connection!.BeginTransaction();
    }

    /// <summary>
    /// Clears all indexed data (files, directories, tokens).
    /// Keeps metadata and excluded paths.
    /// </summary>
    public void ClearIndex()
    {
        ExecuteNonQuery("DELETE FROM FileTokens;");
        ExecuteNonQuery("DELETE FROM Files;");
        ExecuteNonQuery("DELETE FROM Directories;");
        ExecuteNonQuery("DELETE FROM Tokens;");
    }

    #endregion

    #region Helpers

    private void EnsureConnection()
    {
        if (_connection == null)
            throw new InvalidOperationException("Database is not open. Call Open() first.");
    }

    private SqliteCommand CreateCommand(string sql)
    {
        EnsureConnection();
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private void ExecuteNonQuery(string sql)
    {
        using var cmd = CreateCommand(sql);
        cmd.ExecuteNonQuery();
    }

    private static void ValidateCheck(SqliteConnection connection, string check)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {check};";
        var result = command.ExecuteScalar()?.ToString();
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SQLite {check} doğrulaması başarısız.");
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (!_disposed)
        {
            Close();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~IndexDatabase()
    {
        Dispose();
    }

    #endregion
}
