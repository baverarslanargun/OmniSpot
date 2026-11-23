using System.IO;
using System.Reflection;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class FileOperationServiceTests
{
    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("C:", true)]
    [InlineData("c:/", true)]
    [InlineData(@"\\sunucu\paylasim", true)]
    [InlineData(@"\\sunucu\paylasim\", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:\Users\", false)]
    [InlineData("", false)]
    public void VolumeRootsAreRecognized(string path, bool expected) =>
        Assert.Equal(expected, FileOperationService.IsVolumeRoot(path));

    [Fact]
    public void AVolumeRootIsNeverSentToTheShellForDeleteOrRename()
    {
        var service = new FileOperationService(DispatchProxy.Create<IIndexLifecycleService, Unused>());

        Assert.Equal("Sürücü kökü silinemez.", Assert.Throws<IOException>(() => service.DeleteToRecycleBin(@"Q:\")).Message);
        Assert.Equal("Sürücü kökü yeniden adlandırılamaz.", Assert.Throws<IOException>(() => service.Rename(@"Q:\", "yeni")).Message);
    }

    public class Unused : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }
}
