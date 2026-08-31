using System.ComponentModel;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnVolumeJournalReaderTests
{
    [Fact]
    public void TranslateFailure_ReportsAnInvalidParameterAsAProtocolRejection()
    {
        var failure = UsnVolumeJournalReader.TranslateFailure(87, "FSCTL_READ_USN_JOURNAL");

        var rejection = Assert.IsType<UsnProtocolRejectedException>(failure);
        Assert.Equal(87, rejection.ErrorCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(1178)]
    [InlineData(1179)]
    [InlineData(1181)]
    public void TranslateFailure_ReportsJournalStateCodesAsUnavailable(int errorCode)
    {
        Assert.IsType<UsnJournalUnavailableException>(
            UsnVolumeJournalReader.TranslateFailure(errorCode, "FSCTL_QUERY_USN_JOURNAL"));
    }

    [Fact]
    public void TranslateFailure_LetsUnknownCodesSurfaceAsWin32Failures()
    {
        Assert.IsType<Win32Exception>(
            UsnVolumeJournalReader.TranslateFailure(5, "FSCTL_QUERY_USN_JOURNAL"));
    }

    [Fact]
    public void ResolveVolumeRoot_RejectsNetworkPaths()
    {
        Assert.Throws<NotSupportedException>(
            () => UsnVolumeJournalReader.ResolveVolumeRoot(@"\\sunucu\pay\klasor"));
    }
}
