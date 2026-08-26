using System.Globalization;
using System.Text;

namespace SmartFileLauncher.Core.Diagnostics;

public sealed class DiagnosticsMetricLog : IDisposable
{
    public const string EventGroup = "OLAY";

    private const string Header = "zaman;bölüm;etiket;değer;sayısal";
    private const string LineTimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const string FileTimestampFormat = "yyyyMMdd-HHmmss";

    private static readonly UTF8Encoding FileEncoding = new(false);

    private readonly object _sync = new();
    private readonly Func<DateTime> _clock;

    private FileStream? _stream;
    private string? _currentFilePath;
    private long _writtenRows;
    private string? _lastError;
    private bool _disposed;

    public DiagnosticsMetricLog(Func<DateTime>? clock = null)
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

    public long WrittenRows
    {
        get
        {
            lock (_sync)
            {
                return _writtenRows;
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

    public bool Start(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();

            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(
                    directory,
                    $"omnispot-{_clock().ToString(FileTimestampFormat)}-metrik.csv");

                _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _currentFilePath = path;
                _writtenRows = 0;
                _lastError = null;

                if (_stream.Length == 0)
                {
                    WriteRaw(Header + Environment.NewLine);
                }

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

    public void WriteSample(IReadOnlyList<DiagnosticsGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        lock (_sync)
        {
            if (_stream == null) return;

            var stamp = _clock().ToString(LineTimestampFormat);
            var builder = new StringBuilder();
            var rows = 0;

            foreach (var group in groups)
            {
                foreach (var reading in group.Readings)
                {
                    AppendRow(builder, stamp, group.Title, reading.Label, reading.Value, reading.Numeric);
                    rows++;
                }
            }

            if (rows == 0) return;

            try
            {
                WriteRaw(builder.ToString());
                _writtenRows += rows;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                StopCore();
            }
        }
    }

    public void WriteEvent(string name, string detail, double? numeric = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(detail);

        lock (_sync)
        {
            if (_stream == null) return;

            var builder = new StringBuilder();
            AppendRow(
                builder,
                _clock().ToString(LineTimestampFormat),
                EventGroup,
                name,
                detail,
                numeric);

            try
            {
                WriteRaw(builder.ToString());
                _writtenRows++;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                StopCore();
            }
        }
    }

    private static void AppendRow(
        StringBuilder builder,
        string stamp,
        string group,
        string label,
        string value,
        double? numeric)
    {
        builder.Append(stamp).Append(';')
            .Append(Sanitize(group)).Append(';')
            .Append(Sanitize(label)).Append(';')
            .Append(Sanitize(value)).Append(';')
            .Append(numeric?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty)
            .Append(Environment.NewLine);
    }

    private static string Sanitize(string value)
    {
        return value
            .Replace(';', ',')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
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

        _stream.Dispose();
        _stream = null;
    }

    private void WriteRaw(string text)
    {
        var bytes = FileEncoding.GetBytes(text);
        _stream!.Write(bytes, 0, bytes.Length);
        _stream.Flush();
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
