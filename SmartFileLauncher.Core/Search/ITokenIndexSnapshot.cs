namespace SmartFileLauncher.Core.Search;

internal interface ITokenIndexSnapshot
{
    bool IsTokenIndexReady { get; }
}

internal sealed class TokenIndexNotReadyException() : InvalidOperationException("AI indeksi hazırlanıyor; ad araması kullanıldı.");
