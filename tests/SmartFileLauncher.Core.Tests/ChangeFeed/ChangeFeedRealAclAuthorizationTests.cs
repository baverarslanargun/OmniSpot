using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedRealAclAuthorizationTests : IDisposable
{
    private readonly TemporaryDirectory _workspace = new();
    private readonly List<string> _denied = new();

    public void Dispose()
    {
        foreach (var path in _denied)
        {
            Undeny(path);
        }

        _workspace.Dispose();
    }

    [Fact]
    public void ANameUnderARealDeniedDirectory_IsWithheld()
    {
        var root = _workspace.CreateDirectory("Kok");
        var open = Directory.CreateDirectory(Path.Combine(root, "Acik")).FullName;
        var closed = Directory.CreateDirectory(Path.Combine(root, "Kapali")).FullName;
        Deny(closed);

        var projection = ChangeFeedPathAuthorizer
            .ForCurrentCaller(root)
            .Project(new[]
            {
                Created(Path.Combine(closed, "gizli.txt")),
                Created(Path.Combine(open, "gorunur.txt"))
            });

        Assert.Equal(
            Path.Combine(open, "gorunur.txt"),
            Assert.Single(projection.Events).FullPath);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void TheDeniedDirectoryOwnName_StaysVisibleBecauseItsParentIsReadable()
    {
        var root = _workspace.CreateDirectory("Kok");
        var closed = Directory.CreateDirectory(Path.Combine(root, "Kapali")).FullName;
        Deny(closed);

        var projection = ChangeFeedPathAuthorizer
            .ForCurrentCaller(root)
            .Project(new[] { Created(closed) });

        Assert.Equal(closed, Assert.Single(projection.Events).FullPath);
        Assert.False(projection.Withheld);
    }

    [Fact]
    public void ADeniedAncestorAboveTheRoot_DoesNotBlockDelivery()
    {
        var outer = _workspace.CreateDirectory("Ust");
        var root = Directory.CreateDirectory(Path.Combine(outer, "Kok")).FullName;
        var child = Path.Combine(root, "belge.txt");
        File.WriteAllText(child, "veri");
        Detach(root);
        Deny(outer);

        var projection = ChangeFeedPathAuthorizer
            .ForCurrentCaller(root)
            .Project(new[] { Created(child) });

        Assert.Equal(child, Assert.Single(projection.Events).FullPath);
        Assert.False(projection.Withheld);
    }

    [Fact]
    public void AReadableDirectoryInsideADeniedOne_StillHidesItsContents()
    {
        var root = _workspace.CreateDirectory("Kok");
        var closed = Directory.CreateDirectory(Path.Combine(root, "Kapali")).FullName;
        var readable = Directory.CreateDirectory(Path.Combine(closed, "Alt")).FullName;
        var secret = Path.Combine(readable, "gizli.txt");
        File.WriteAllText(secret, "veri");

        Detach(readable);
        Deny(closed);

        Assert.True(CanEnumerate(readable), "Alt dizin doğrudan okunabilir olmalı.");

        var projection = ChangeFeedPathAuthorizer
            .ForCurrentCaller(root)
            .Project(new[] { Created(secret) });

        Assert.Empty(projection.Events);
        Assert.True(projection.Withheld);
    }

    private static bool CanEnumerate(string directory)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void ADeletedParent_IsFailClosed()
    {
        var root = _workspace.CreateDirectory("Kok");
        var gone = Path.Combine(root, "Silinen");

        var projection = ChangeFeedPathAuthorizer
            .ForCurrentCaller(root)
            .Project(new[] { Created(Path.Combine(gone, "belge.txt")) });

        Assert.Empty(projection.Events);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void ARealRenameOutOfADeniedDirectory_BecomesACreate()
    {
        var root = _workspace.CreateDirectory("Kok");
        var open = Directory.CreateDirectory(Path.Combine(root, "Acik")).FullName;
        var closed = Directory.CreateDirectory(Path.Combine(root, "Kapali")).FullName;
        Deny(closed);

        var projection = ChangeFeedPathAuthorizer
            .ForCurrentCaller(root)
            .Project(new[]
            {
                new ChangeFeedEvent(
                    ChangeFeedEventKind.Renamed,
                    Path.Combine(open, "yeni.txt"),
                    false,
                    Path.Combine(closed, "eski.txt"))
            });

        var published = Assert.Single(projection.Events);
        Assert.Equal(ChangeFeedEventKind.Created, published.Kind);
        Assert.Equal(Path.Combine(open, "yeni.txt"), published.FullPath);
        Assert.Null(published.OldPath);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void NoWithheldNameAppearsAnywhereInTheProjection()
    {
        var root = _workspace.CreateDirectory("Kok");
        var closed = Directory.CreateDirectory(Path.Combine(root, "Kapali")).FullName;
        var secret = Path.Combine(closed, "cok-gizli-ad.txt");
        Deny(closed);

        var projection = ChangeFeedPathAuthorizer
            .ForCurrentCaller(root)
            .Project(new[] { Created(secret) });

        var rendered = string.Join(
            "|",
            projection.Events.Select(change => $"{change.Kind}{change.FullPath}{change.OldPath}"));

        Assert.DoesNotContain("cok-gizli-ad", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cok-gizli-ad", projection.RootPath, StringComparison.OrdinalIgnoreCase);
    }

    private static ChangeFeedEvent Created(string path) =>
        new(ChangeFeedEventKind.Created, path, false);

    private static void Detach(string path)
    {
        var directory = new DirectoryInfo(path);
        var access = directory.GetAccessControl();
        access.SetAccessRuleProtection(true, true);
        directory.SetAccessControl(access);
    }

    private void Deny(string path)
    {
        var directory = new DirectoryInfo(path);
        var access = directory.GetAccessControl();
        access.SetAccessRuleProtection(true, false);
        access.AddAccessRule(new FileSystemAccessRule(
            CurrentSid(),
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        directory.SetAccessControl(access);
        _denied.Add(path);
    }

    private static void Undeny(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            var access = directory.GetAccessControl();
            access.SetAccessRuleProtection(false, true);
            access.RemoveAccessRuleAll(new FileSystemAccessRule(
                CurrentSid(),
                FileSystemRights.ListDirectory | FileSystemRights.ReadData,
                AccessControlType.Deny));
            directory.SetAccessControl(access);
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException)
        {
        }
    }

    private static SecurityIdentifier CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }
}
