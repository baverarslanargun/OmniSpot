using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Indexing;
using SmartFileLauncher.Core.Indexing.Ntfs;
using System.Diagnostics;
using System.Text.Json;

namespace SmartFileLauncher.Core.Application.Indexing;

public sealed class ChangeFeedInventorySource(
    IChangeFeedRequestChannel channel, Action<string>? diagnostic = null) : IIndexInventorySource
{
    private const int MaxStartAttempts = 12;
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromSeconds(2);

    public async Task<IIndexInventorySession?> ReadAsync(IReadOnlyList<string> roots,
        Action<IndexInventoryEntry> receive, CancellationToken cancellationToken)
    {
        var session = new Session(channel, diagnostic);
        var entries = 0;
        var startAttempts = 0;
        long requestTicks = 0, processingTicks = 0;
        var pages = 0;
        try
        {
            foreach (var root in roots)
            {
                var admitted = await channel.SendAsync(new ChangeFeedRequest(ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.AddRoot, root), cancellationToken).ConfigureAwait(false);
                if (admitted.Status != ChangeFeedResponseStatus.Ok)
                {
                    diagnostic?.Invoke($"MFT kök kabulü başarısız: {admitted.Status}; {admitted.Message}");
                    return null;
                }
            }
            while (true)
            {
                var requestStarted = Stopwatch.GetTimestamp();
                var response = await channel.SendAsync(new ChangeFeedRequest(ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.Inventory, Token: session.Token,
                    InventoryRoots: session.Token is null ? roots : null), cancellationToken).ConfigureAwait(false);
                requestTicks += Stopwatch.GetTimestamp() - requestStarted;
                pages++;
                if (response.Version != ChangeFeedProtocol.Version ||
                    response.Status != ChangeFeedResponseStatus.Ok || response.Inventory is not { } page ||
                    string.IsNullOrWhiteSpace(page.Token))
                {
                    if (response.Status == ChangeFeedResponseStatus.Unavailable &&
                        session.Token is null && entries == 0 && startAttempts < MaxStartAttempts)
                    {
                        startAttempts++;
                        diagnostic?.Invoke($"MFT envanteri geçici olarak yanıt vermedi; yeniden deneniyor ({startAttempts}/{MaxStartAttempts}); kayıt={entries}: {response.Status}; {response.Message}");
                        await Task.Delay(StartRetryDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    diagnostic?.Invoke($"MFT aktarımı başarısız: {response.Status}; sürüm={response.Version}; kayıt={entries}; {response.Message}");
                    return null;
                }
                session.Token = page.Token;
                var processingStarted = Stopwatch.GetTimestamp();
                foreach (var dto in page.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = dto.ToEntry();
                    if (!Path.IsPathFullyQualified(entry.Path) ||
                        !roots.Any(root => IsWithin(entry.Path, root)) ||
                        entry.SizeBytes < 0 || entry.CreatedTimeUtc < 0 ||
                        entry.CreatedTimeUtc > DateTime.MaxValue.Ticks || entry.LastWriteTimeUtc < 0 ||
                        entry.LastWriteTimeUtc > DateTime.MaxValue.Ticks)
                        throw new InvalidDataException("Envanter kaydı geçersiz.");
                    receive(entry);
                    entries++;
                }
                processingTicks += Stopwatch.GetTimestamp() - processingStarted;
                if (page.Completed)
                {
                    session.Keep = true;
                    diagnostic?.Invoke($"MFT aktarımı tamamlandı: {entries} kayıt.");
                    return session;
                }
                if (page.Entries.Count == 0)
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            diagnostic?.Invoke($"MFT aktarım hatası: {failure.GetType().Name}; kayıt={entries}; {failure.Message}");
            return null;
        }
        finally
        {
            diagnostic?.Invoke("MFT ölçümü: " + JsonSerializer.Serialize(new
            {
                Entries = entries, Pages = pages, Completed = session.Keep,
                RequestMilliseconds = requestTicks * 1000.0 / Stopwatch.Frequency,
                ProcessingMilliseconds = processingTicks * 1000.0 / Stopwatch.Frequency
            }));
            if (!session.Keep) await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static bool IsWithin(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Session(IChangeFeedRequestChannel channel, Action<string>? diagnostic) : IIndexInventorySession
    {
        public string? Token { get; set; }
        public bool Keep { get; set; }

        public async Task<bool> ValidateAsync(CancellationToken cancellationToken)
        {
            try
            {
                var response = await channel.SendAsync(new ChangeFeedRequest(ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.ValidateInventory, Token: Token), cancellationToken).ConfigureAwait(false);
                var valid = response.Version == ChangeFeedProtocol.Version && response.Status == ChangeFeedResponseStatus.Ok;
                if (!valid) diagnostic?.Invoke($"MFT oturumu doğrulanamadı: {response.Status}; {response.Message}");
                return valid;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception failure)
            {
                diagnostic?.Invoke($"MFT oturumu doğrulama hatası: {failure.GetType().Name}; {failure.Message}");
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Token is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await channel.SendAsync(new ChangeFeedRequest(ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.CancelInventory, Token: Token), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) { }
            Token = null;
        }
    }
}
