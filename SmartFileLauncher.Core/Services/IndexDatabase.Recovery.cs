using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Services;

public partial class IndexDatabase
{
    private const string RecoveryDirectoryScope = @"
        WITH RECURSIVE Scope(Id) AS (
            SELECT Id FROM Directories WHERE FullPath = @path
            UNION
            SELECT d.Id FROM Directories d JOIN Scope p ON d.ParentId = p.Id
        ) ";

    internal IEnumerable<IndexedDirectory> GetDirectoriesInScope(string path)
    {
        using var command = CreateCommand(RecoveryDirectoryScope +
            "SELECT * FROM Directories WHERE Id IN (SELECT Id FROM Scope) ORDER BY Depth, Name");
        command.Parameters.AddWithValue("@path", path);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return ReadDirectory(reader);
    }

    internal IEnumerable<IndexedFile> GetFilesInScope(string path)
    {
        using var command = CreateCommand(RecoveryDirectoryScope +
            "SELECT * FROM Files WHERE FullPath = @path OR DirectoryId IN (SELECT Id FROM Scope)");
        command.Parameters.AddWithValue("@path", path);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return ReadFile(reader);
    }
}
