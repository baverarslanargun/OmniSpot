using OmniSpot.Benchmarking.Profiling;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Profiling;

public sealed class ProfileCommandTests
{
    [Fact]
    public void FormatRootPreview_MasksUserProfileByDefault()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(userProfile));
        var rootPath = Path.Combine(userProfile, "PrivateRoot", "SensitiveFolder");
        var roots = new[]
        {
            new ProfileRootRequest(rootPath, ProfileRootKind.Custom, 1)
        };

        var preview = ProfileCommand.FormatRootPreview(roots, showPaths: false);

        Assert.DoesNotContain(userProfile, preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rootPath, preview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", preview, StringComparison.Ordinal);
        Assert.Contains("--show-paths", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRootPreview_HidesPathOutsideUserProfileByDefault()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(userProfile));
        var volumeRoot = Path.GetPathRoot(userProfile)
            ?? throw new InvalidOperationException("Kullanıcı profili volume kökü çözülemedi.");
        var rootPath = Path.Combine(volumeRoot, "OmniSpot-Outside-Profile", "SensitiveFolder");
        var roots = new[]
        {
            new ProfileRootRequest(rootPath, ProfileRootKind.Custom, 1)
        };

        var preview = ProfileCommand.FormatRootPreview(roots, showPaths: false);

        Assert.DoesNotContain(rootPath, preview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<gizli-path>", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRootPreview_ShowsFullPathOnlyWhenRequested()
    {
        var rootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "PrivateRoot");
        var roots = new[]
        {
            new ProfileRootRequest(rootPath, ProfileRootKind.Custom, 1)
        };

        var preview = ProfileCommand.FormatRootPreview(roots, showPaths: true);

        Assert.Contains(rootPath, preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<gizli-path>", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("Tam yollar gizlendi", preview, StringComparison.Ordinal);
    }
}
