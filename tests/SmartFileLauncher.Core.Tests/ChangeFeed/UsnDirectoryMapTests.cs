using SmartFileLauncher.Core.ChangeFeed.Usn;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnDirectoryMapTests
{
    private static readonly UsnFileReference Root = UsnFileReference.FromNtfs(1);

    [Fact]
    public void TryResolve_ReturnsRootPathForRootReference()
    {
        var map = CreateMap();

        Assert.True(map.TryResolve(Root, out var path));
        Assert.Equal(@"C:\Kok", path);
    }

    [Fact]
    public void TryResolve_WalksNestedDirectories()
    {
        var map = CreateMap();
        map.Set(Reference(2), "alt", Root);
        map.Set(Reference(3), "derin", Reference(2));

        Assert.True(map.TryResolve(Reference(3), out var path));
        Assert.Equal(@"C:\Kok\alt\derin", path);
    }

    [Fact]
    public void TryResolve_FailsForUnknownReference()
    {
        var map = CreateMap();

        Assert.False(map.TryResolve(Reference(99), out var path));
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void TryResolve_FailsAfterAnAncestorIsRemoved()
    {
        var map = CreateMap();
        map.Set(Reference(2), "alt", Root);
        map.Set(Reference(3), "derin", Reference(2));

        Assert.True(map.Remove(Reference(2)));

        Assert.False(map.TryResolve(Reference(3), out _));
    }

    [Fact]
    public void TryResolve_FailsOnACycleInsteadOfLooping()
    {
        var map = CreateMap();
        map.Set(Reference(2), "a", Reference(3));
        map.Set(Reference(3), "b", Reference(2));

        Assert.False(map.TryResolve(Reference(2), out _));
    }

    [Fact]
    public void Set_IgnoresTheRootItself()
    {
        var map = CreateMap();

        map.Set(Root, "baska", Reference(9));

        Assert.Equal(0, map.Count);
        Assert.True(map.TryResolve(Root, out var path));
        Assert.Equal(@"C:\Kok", path);
    }

    [Fact]
    public void Constructor_RejectsEmptyRootIdentity()
    {
        Assert.Throws<ArgumentException>(
            () => new UsnDirectoryMap(@"C:\Kok", UsnFileReference.None));
    }

    private static UsnDirectoryMap CreateMap() => new(@"C:\Kok\", Root);

    private static UsnFileReference Reference(ulong value) => UsnFileReference.FromNtfs(value);
}
