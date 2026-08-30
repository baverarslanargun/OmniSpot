using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnDirectoryMapBuilderTests
{
    [Fact]
    public void Build_LinksEveryDirectoryToItsParent()
    {
        using var root = new TemporaryDirectory();
        root.CreateDirectory("alt");
        root.CreateDirectory(Path.Combine("alt", "derin"));
        root.CreateDirectory("yan");
        root.CreateFile("rapor.txt");

        var probe = new UsnFileSystemIdentityProbe();
        var result = UsnDirectoryMapBuilder.Build(root.Path, probe);

        Assert.Equal(0, result.SkippedDirectoryCount);
        Assert.Equal(3, result.Directories.Count);

        var map = new UsnDirectoryMap(root.Path, result.RootIdentity.FileReference);
        foreach (var entry in result.Directories)
        {
            map.Set(entry.Reference, entry.Name, entry.ParentReference);
        }

        foreach (var relativePath in new[] { "alt", Path.Combine("alt", "derin"), "yan" })
        {
            var fullPath = Path.Combine(root.Path, relativePath);
            Assert.True(probe.TryReadIdentity(fullPath, out var identity));
            Assert.True(map.TryResolve(identity.FileReference, out var resolved));
            Assert.Equal(fullPath, resolved);
        }
    }

    [Fact]
    public void Build_CountsDirectoriesWithoutIdentity()
    {
        using var root = new TemporaryDirectory();
        root.CreateDirectory("gorunur");
        root.CreateDirectory("gizli");

        var probe = new PartialIdentityProbe(Path.Combine(root.Path, "gizli"));
        var result = UsnDirectoryMapBuilder.Build(root.Path, probe);

        Assert.Equal(1, result.SkippedDirectoryCount);
        Assert.Equal("gorunur", Assert.Single(result.Directories).Name);
    }

    [Fact]
    public void Build_RejectsAReparsePointRoot()
    {
        using var target = new TemporaryDirectory();
        using var parent = new TemporaryDirectory();
        var junction = Path.Combine(parent.Path, "baglanti");
        WindowsDirectoryLink.CreateJunction(junction, target.Path);

        try
        {
            Assert.Throws<NotSupportedException>(
                () => UsnDirectoryMapBuilder.Build(junction, new UsnFileSystemIdentityProbe()));
        }
        finally
        {
            WindowsDirectoryLink.Delete(junction);
        }
    }

    [Fact]
    public void Build_DoesNotDescendIntoReparsePoints()
    {
        using var target = new TemporaryDirectory();
        target.CreateDirectory("hedefAlt");
        using var root = new TemporaryDirectory();
        root.CreateDirectory("gercek");
        var junction = Path.Combine(root.Path, "baglanti");
        WindowsDirectoryLink.CreateJunction(junction, target.Path);

        try
        {
            var result = UsnDirectoryMapBuilder.Build(
                root.Path,
                new UsnFileSystemIdentityProbe());

            Assert.Equal("gercek", Assert.Single(result.Directories).Name);
            Assert.Equal(1, result.SkippedDirectoryCount);
        }
        finally
        {
            WindowsDirectoryLink.Delete(junction);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_CountsDirectoriesWhoseListingFails(bool failWhenTheListingIsCreated)
    {
        using var root = new TemporaryDirectory();
        root.CreateDirectory("gorunur");
        root.CreateDirectory("kapali");
        root.CreateDirectory(Path.Combine("gorunur", "derin"));

        var blocked = Path.Combine(root.Path, "kapali");
        string[] Listing(string path)
        {
            if (!string.Equals(path, blocked, StringComparison.OrdinalIgnoreCase))
            {
                return Directory.GetDirectories(path);
            }

            throw failWhenTheListingIsCreated
                ? new UnauthorizedAccessException(path)
                : new IOException(path);
        }

        var result = UsnDirectoryMapBuilder.Build(
            root.Path,
            new UsnFileSystemIdentityProbe(),
            Listing,
            CancellationToken.None);

        Assert.Equal(1, result.SkippedDirectoryCount);
        Assert.Equal(
            new[] { "derin", "gorunur", "kapali" },
            result.Directories.Select(entry => entry.Name).Order());
    }

    [Fact]
    public void Build_LetsUnexpectedListingFailuresSurface()
    {
        using var root = new TemporaryDirectory();
        root.CreateDirectory("gorunur");

        Assert.Throws<InvalidOperationException>(
            () => UsnDirectoryMapBuilder.Build(
                root.Path,
                new UsnFileSystemIdentityProbe(),
                _ => throw new InvalidOperationException("beklenmeyen"),
                CancellationToken.None));
    }

    [Fact]
    public void Build_RejectsAMissingRoot()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => UsnDirectoryMapBuilder.Build(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                new UsnFileSystemIdentityProbe()));
    }

    private sealed class PartialIdentityProbe : IUsnIdentityProbe
    {
        private readonly UsnFileSystemIdentityProbe _inner = new();
        private readonly HashSet<string> _blocked;

        public PartialIdentityProbe(params string[] blockedPaths)
        {
            _blocked = new HashSet<string>(blockedPaths, StringComparer.OrdinalIgnoreCase);
        }

        public bool TryReadIdentity(string path, out UsnNodeIdentity identity)
        {
            if (_blocked.Contains(path))
            {
                identity = default;
                return false;
            }

            return _inner.TryReadIdentity(path, out identity);
        }
    }
}
