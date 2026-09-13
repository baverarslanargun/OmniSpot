using System.Buffers;

namespace SmartFileLauncher.Core.Search;

internal ref struct BasicTokenEnumerator
{
    private static readonly SearchValues<char> Delimiters = SearchValues.Create(" _-.,[]()");
    private ReadOnlySpan<char> _remaining;
    private readonly Span<char> _scratch;
    private readonly IEnumerator<string>? _fallback;
    private bool _foldPending;
    internal ReadOnlySpan<char> Current { get; private set; }

    internal BasicTokenEnumerator(string input, Span<char> scratch, BasicTokenizer tokenizer)
    {
        if (scratch.Length < input.Length) throw new ArgumentException("Token tamponu kısa.");
        _remaining = input; _scratch = scratch; _fallback = null; _foldPending = false; Current = default;
        foreach (var ch in input)
            if (ch > 127) { _fallback = tokenizer.Tokenize(input).GetEnumerator(); _remaining = default; break; }
    }

    internal bool MoveNext()
    {
        if (_fallback is not null)
        {
            if (!_fallback.MoveNext()) return false;
            Current = _fallback.Current.AsSpan(); return true;
        }
        if (_foldPending)
        {
            var folded = _scratch[..Current.Length];
            for (var i = 0; i < folded.Length; i++) if (folded[i] == 'ı') folded[i] = 'i';
            _foldPending = false; Current = folded; return true;
        }
        while (!_remaining.IsEmpty)
        {
            var delimiter = _remaining.IndexOfAny(Delimiters);
            var part = (delimiter < 0 ? _remaining : _remaining[..delimiter]).Trim();
            _remaining = delimiter < 0 ? default : _remaining[(delimiter + 1)..];
            if (part.IsEmpty) continue;
            for (var i = 0; i < part.Length; i++)
            {
                var ch = part[i];
                if (ch == 'I') { ch = 'ı'; _foldPending = true; }
                else if (ch is >= 'A' and <= 'Z') ch = (char)(ch + 32);
                _scratch[i] = ch;
            }
            Current = _scratch[..part.Length]; return true;
        }
        return false;
    }

    public void Dispose() => _fallback?.Dispose();
}
