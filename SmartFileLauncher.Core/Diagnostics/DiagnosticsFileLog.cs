using System.Text;

namespace SmartFileLauncher.Core.Diagnostics;

public sealed class DiagnosticsFileLog : IDisposable
{
    private const string LineTimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const string FileTimestampFormat = "yyyyMMdd-HHmmss";

    private static readonly UTF8Encoding FileEncoding = new(false);

    private readonly object _sync = new();
    private readonly Func<DateTime> _clock;

    private FileStream? _stream;
    private string? _currentFilePath;
    private long _writtenLines;
    private string? _lastError;
    private bool _disposed;

    public DiagnosticsFileLog(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.Now);
    }

    public bool IsWriting
    {
        get
        {
            lock (_sync)
            {
                return _stream != null;
            }
        }
    }

    public string? CurrentFilePath
    {
        get
        {
            lock (_sync)
            {
                return _currentFilePath;
            }
        }
    }

    public long WrittenLines
    {
        get
        {
            lock (_sync)
            {
                return _writtenLines;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_sync)
            {
                return _lastError;
            }
        }
    }

    public bool Start(string directory, IReadOnlyList<KeyValuePair<string, string>>? stamps = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();

            try
            {
                Directory.CreateDirectory(directory);
                var started = _clock();
                var path = Path.Combine(
                    directory,
                    $"omnispot-{started.ToString(FileTimestampFormat)}.log");

                _stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                _currentFilePath = path;
                _writtenLines = 0;
                _lastError = null;

                var header = new StringBuilder();
                header.Append("=== OmniSpot tanılama günlüğü ===").Append(Environment.NewLine);
                header.Append("başlangıç       : ")
                    .Append(started.ToString(LineTimestampFormat))
                    .Append(Environment.NewLine);
                if (stamps != null)
                {
                    foreach (var stamp in stamps)
                    {
                        header.Append(stamp.Key.PadRight(16))
                            .Append(": ")
                            .Append(stamp.Value)
                            .Append(Environment.NewLine);
                    }
                }

                header.Append(new string('-', 60)).Append(Environment.NewLine);
                WriteRaw(header.ToString());
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _stream?.Dispose();
                _stream = null;
                _currentFilePath = null;
                return false;
            }
        }
    }

    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_sync)
        {
            if (_stream == null) return;

            try
            {
                WriteRaw(message + Environment.NewLine);
                _writtenLines++;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                StopCore();
            }
        }
    }

    private void WriteRaw(string text)
    {
        var bytes = FileEncoding.GetBytes(text);
        _stream!.Write(bytes, 0, bytes.Length);
        _stream.Flush();
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
        }
    }

    private void StopCore()
    {
        if (_stream == null) return;

        try
        {
            WriteRaw(
                $"=== kapandı {_clock().ToString(LineTimestampFormat)} · {_writtenLines} satır ==="
                + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }

        _stream.Dispose();
        _stream = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            StopCore();
        }
    }
}
