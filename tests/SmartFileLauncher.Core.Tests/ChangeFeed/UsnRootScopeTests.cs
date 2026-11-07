using SmartFileLauncher.Core.ChangeFeed.Usn;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnRootScopeTests
{
    [Theory]
    [InlineData(@"C:\", @"C:\Users\ornek", @"C:\Users\ornek")]
    [InlineData(@"C:\", @"C:\", @"C:\")]
    [InlineData(@"C:\Kok", @"C:\Kok\alt\", @"C:\Kok\alt")]
    [InlineData(@"C:\Kok\", @"C:\Kok\alt", @"C:\Kok\alt")]
    public void TryCanonicalize_AcceptsPathsUnderTheRootIncludingAVolumeRoot(
        string root, string candidate, string expected)
    {
        Assert.True(UsnRootScope.TryCanonicalize(root, candidate, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData(@"C:\Kok", @"C:\Kokler\alt")]
    [InlineData(@"C:\Kok", @"D:\Kok\alt")]
    [InlineData(@"C:\", @"D:\alt")]
    public void TryCanonicalize_RejectsPathsOutsideTheRoot(string root, string candidate)
    {
        Assert.False(UsnRootScope.TryCanonicalize(root, candidate, out _));
    }
}
