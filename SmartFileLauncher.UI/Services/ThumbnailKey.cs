namespace SmartFileLauncher.UI.Services;

public record ThumbnailKey(string Path, int Size, long LastWriteTicks)
{
    public string GetCacheFileName()
    {
        // Hash kullanarak disk cache dosya adı üret
        var hashInput = $"{Path}|{Size}|{LastWriteTicks}";
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashInput));
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant() + ".png";
    }
}
