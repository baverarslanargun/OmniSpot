using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Services;

public partial class IndexDatabase
{
    internal PreparedWriteBatch CreatePreparedWriteBatch() => new(this);

    private SqliteCommand CreateDirectoryInsertCommand()
    {
        var command = CreateCommand(@"
            INSERT INTO Directories (FullPath, Name, ParentId, Depth, LastWriteTimeUtc, LastIndexedTimeUtc, IsHidden)
            VALUES (@path, @name, @parentId, @depth, @lastWrite, @lastIndexed, @hidden)
            ON CONFLICT(FullPath) DO UPDATE SET
                Name = excluded.Name,
                ParentId = excluded.ParentId,
                Depth = excluded.Depth,
                LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                LastIndexedTimeUtc = excluded.LastIndexedTimeUtc,
                IsHidden = excluded.IsHidden
            RETURNING Id;");
        command.Parameters.Add("@path", SqliteType.Text);
        command.Parameters.Add("@name", SqliteType.Text);
        command.Parameters.Add("@parentId", SqliteType.Integer);
        command.Parameters.Add("@depth", SqliteType.Integer);
        command.Parameters.Add("@lastWrite", SqliteType.Integer);
        command.Parameters.Add("@lastIndexed", SqliteType.Integer);
        command.Parameters.Add("@hidden", SqliteType.Integer);
        return command;
    }

    private static long ExecuteDirectoryInsert(SqliteCommand command, IndexedDirectory directory)
    {
        command.Parameters["@path"].Value = directory.FullPath;
        command.Parameters["@name"].Value = directory.Name;
        command.Parameters["@parentId"].Value = directory.ParentId.HasValue ? directory.ParentId.Value : DBNull.Value;
        command.Parameters["@depth"].Value = directory.Depth;
        command.Parameters["@lastWrite"].Value = directory.LastWriteTimeUtc;
        command.Parameters["@lastIndexed"].Value = directory.LastIndexedTimeUtc;
        command.Parameters["@hidden"].Value = directory.IsHidden ? 1 : 0;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private const int FileInsertParameterCount = 11;

    private SqliteCommand CreateFileInsertCommand(bool returnId = true, int rowCount = 1)
    {
        var values = Enumerable.Range(0, rowCount).Select(row =>
            "(" + string.Join(", ", Enumerable.Range(row * FileInsertParameterCount, FileInsertParameterCount)
                .Select(index => $"@p{index}")) + ")");
        var command = CreateCommand(@"
            INSERT INTO Files (FullPath, FileName, Extension, DirectoryId, SizeBytes, CreatedTimeUtc, LastWriteTimeUtc, LastIndexedTimeUtc, OpenCount, IsHidden, IsSystem)
            VALUES " + string.Join(", ", values) + @"
            ON CONFLICT(FullPath) DO UPDATE SET
                FileName = excluded.FileName,
                Extension = excluded.Extension,
                DirectoryId = excluded.DirectoryId,
                SizeBytes = excluded.SizeBytes,
                LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                LastIndexedTimeUtc = excluded.LastIndexedTimeUtc,
                IsHidden = excluded.IsHidden,
                IsSystem = excluded.IsSystem" + (returnId ? " RETURNING Id;" : ";"));
        for (var index = 0; index < rowCount * FileInsertParameterCount; index++)
            command.Parameters.Add($"@p{index}",
                index % FileInsertParameterCount < 3 ? SqliteType.Text : SqliteType.Integer);
        return command;
    }

    private static long ExecuteFileInsert(SqliteCommand command, IndexedFile file)
    {
        BindFileInsert(command, file);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void BindFileInsert(SqliteCommand command, IndexedFile file, int offset = 0)
    {
        command.Parameters[offset].Value = file.FullPath;
        command.Parameters[offset + 1].Value = file.FileName;
        command.Parameters[offset + 2].Value = file.Extension;
        command.Parameters[offset + 3].Value = file.DirectoryId;
        command.Parameters[offset + 4].Value = file.SizeBytes;
        command.Parameters[offset + 5].Value = file.CreatedTimeUtc;
        command.Parameters[offset + 6].Value = file.LastWriteTimeUtc;
        command.Parameters[offset + 7].Value = file.LastIndexedTimeUtc;
        command.Parameters[offset + 8].Value = file.OpenCount;
        command.Parameters[offset + 9].Value = file.IsHidden ? 1 : 0;
        command.Parameters[offset + 10].Value = file.IsSystem ? 1 : 0;
    }

    internal sealed class PreparedWriteBatch : IDisposable
    {
        internal const int FileBatchSize = 64;
        private readonly IndexDatabase _database;
        private readonly SqliteCommand _directoryCommand;
        private readonly SqliteCommand _fileCommand;
        private SqliteCommand? _filesCommand;
        private bool _disposed;

        internal PreparedWriteBatch(IndexDatabase database)
        {
            _database = database;
            _directoryCommand = database.CreateDirectoryInsertCommand();
            try
            {
                _fileCommand = database.CreateFileInsertCommand(returnId: false);
            }
            catch
            {
                _directoryCommand.Dispose();
                throw;
            }
        }

        public long InsertDirectory(IndexedDirectory directory)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ExecuteDirectoryInsert(_directoryCommand, directory);
        }

        public void InsertFile(IndexedFile file)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            BindFileInsert(_fileCommand, file);
            _fileCommand.ExecuteNonQuery();
        }

        public void InsertFiles(IReadOnlyList<IndexedFile> files, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(files);
            var offset = 0;
            while (files.Count - offset >= FileBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var command = _filesCommand ??=
                    _database.CreateFileInsertCommand(returnId: false, rowCount: FileBatchSize);
                for (var row = 0; row < FileBatchSize; row++)
                {
                    ct.ThrowIfCancellationRequested();
                    BindFileInsert(command, files[offset + row], row * FileInsertParameterCount);
                }
                ct.ThrowIfCancellationRequested();
                command.ExecuteNonQuery();
                offset += FileBatchSize;
            }
            for (; offset < files.Count; offset++)
            {
                ct.ThrowIfCancellationRequested();
                InsertFile(files[offset]);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _filesCommand?.Dispose();
            _fileCommand.Dispose();
            _directoryCommand.Dispose();
        }
    }
}
