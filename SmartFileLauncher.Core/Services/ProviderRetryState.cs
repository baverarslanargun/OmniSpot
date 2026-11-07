using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartFileLauncher.Core.Services;

internal sealed class ProviderRetryState(TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new(StringComparer.Ordinal);

    internal TimeProvider Clock { get; } = clock;

    internal TimeSpan Remaining(string provider)
    {
        lock (_gate)
            return _notBefore.TryGetValue(provider, out var until) && until > Clock.GetUtcNow()
                ? until - Clock.GetUtcNow() : TimeSpan.Zero;
    }

    internal void Defer(string provider, TimeSpan delay)
    {
        var until = Clock.GetUtcNow() + delay;
        lock (_gate)
            if (!_notBefore.TryGetValue(provider, out var previous) || until > previous)
                _notBefore[provider] = until;
    }

    internal void ThrowIfCoolingDown(string provider, string operation)
    {
        var remaining = Remaining(provider);
        if (remaining <= TimeSpan.Zero) return;
        var failure = new HttpRequestException($"{operation} API bekleme süresi", null, HttpStatusCode.TooManyRequests);
        failure.Data["ProviderMessage"] = $"{provider} istek sınırı: yeniden denemeden önce {Math.Ceiling(remaining.TotalSeconds):0} sn bekleyin.";
        throw failure;
    }

    internal async Task WaitAsync(string provider, CancellationToken cancellationToken)
    {
        while (Remaining(provider) is var remaining && remaining > TimeSpan.Zero)
            await Task.Delay(remaining, Clock, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var delay = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date ? date - Clock.GetUtcNow() :
                response.StatusCode == HttpStatusCode.TooManyRequests ? TimeSpan.FromSeconds(1) :
                TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    internal static bool IsPermanentRateLimit(string responseBody)
    {
        string message = responseBody;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in new[] { "code", "type" })
                    if (error.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
                        value.GetString() is "insufficient_quota" or "billing_hard_limit_reached" or "quota_exceeded") return true;
                if (error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
                    message = text.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { }

        if (message.Contains("request too large", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("insufficient quota", StringComparison.OrdinalIgnoreCase)) return true;
        var limit = Regex.Match(message, @"\bLimit\s*:?\s*([\d,.]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var requested = Regex.Match(message, @"\bRequested\s*:?\s*([\d,.]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return limit.Success && requested.Success &&
            double.TryParse(limit.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var maximum) &&
            double.TryParse(requested.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount > maximum;
    }
}
