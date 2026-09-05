using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedPathAuthorizerTests
{
    private const string Root = @"C:\Kok";
    private const string Open = @"C:\Kok\Acik";
    private const string Closed = @"C:\Kok\Kapali";

    [Fact]
    public void AVisibleName_IsPublished()
    {
        var projection = Project(Created(@"C:\Kok\Acik\a.txt"));

        Assert.Equal(@"C:\Kok\Acik\a.txt", Assert.Single(projection.Events).FullPath);
        Assert.False(projection.Withheld);
    }

    [Fact]
    public void ANameInsideAClosedDirectory_IsWithheldWithoutLeakingIt()
    {
        var projection = Project(Created(@"C:\Kok\Kapali\gizli.txt"));

        Assert.Empty(projection.Events);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void TheClosedDirectoryOwnName_IsPublishedBecauseItsParentIsListable()
    {
        var projection = Project(Created(Closed));

        Assert.Equal(Closed, Assert.Single(projection.Events).FullPath);
        Assert.False(projection.Withheld);
    }

    [Fact]
    public void SiblingsOutsideTheClosedDirectory_StayVisible()
    {
        var projection = Project(
            Created(@"C:\Kok\Kapali\gizli.txt"),
            Created(@"C:\Kok\Acik\gorunur.txt"));

        Assert.Equal(@"C:\Kok\Acik\gorunur.txt", Assert.Single(projection.Events).FullPath);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void ADeeperNameUnderAClosedAncestor_IsWithheld()
    {
        var projection = Project(Created(@"C:\Kok\Kapali\Alt\Daha\derin.txt"));

        Assert.Empty(projection.Events);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void APathOutsideTheRoot_IsNeverPublished()
    {
        var projection = Project(Created(@"C:\Baska\a.txt"));

        Assert.Empty(projection.Events);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void TheRootItself_IsPublishedWithoutCheckingAboveIt()
    {
        var projection = Project(Created(Root));

        Assert.Equal(Root, Assert.Single(projection.Events).FullPath);
        Assert.False(projection.Withheld);
    }

    [Fact]
    public void Rename_BetweenTwoVisibleNames_StaysARename()
    {
        var projection = Project(Renamed(@"C:\Kok\Acik\yeni.txt", @"C:\Kok\Acik\eski.txt"));

        var published = Assert.Single(projection.Events);
        Assert.Equal(ChangeFeedEventKind.Renamed, published.Kind);
        Assert.Equal(@"C:\Kok\Acik\yeni.txt", published.FullPath);
        Assert.Equal(@"C:\Kok\Acik\eski.txt", published.OldPath);
        Assert.False(projection.Withheld);
    }

    [Fact]
    public void Rename_FromVisibleToHidden_BecomesADeleteOfTheVisibleName()
    {
        var projection = Project(Renamed(@"C:\Kok\Kapali\gizli.txt", @"C:\Kok\Acik\eski.txt"));

        var published = Assert.Single(projection.Events);
        Assert.Equal(ChangeFeedEventKind.Deleted, published.Kind);
        Assert.Equal(@"C:\Kok\Acik\eski.txt", published.FullPath);
        Assert.Null(published.OldPath);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void Rename_FromHiddenToVisible_BecomesACreateOfTheVisibleName()
    {
        var projection = Project(Renamed(@"C:\Kok\Acik\yeni.txt", @"C:\Kok\Kapali\gizli.txt"));

        var published = Assert.Single(projection.Events);
        Assert.Equal(ChangeFeedEventKind.Created, published.Kind);
        Assert.Equal(@"C:\Kok\Acik\yeni.txt", published.FullPath);
        Assert.Null(published.OldPath);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void Rename_BetweenTwoHiddenNames_PublishesNothingAndOnlyReportsAGap()
    {
        var projection = Project(Renamed(@"C:\Kok\Kapali\b.txt", @"C:\Kok\Kapali\a.txt"));

        Assert.Empty(projection.Events);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void AListingFailure_IsTreatedAsUnauthorized()
    {
        var authorizer = new ChangeFeedPathAuthorizer(
            Root,
            _ => throw new UnauthorizedAccessException());

        var projection = authorizer.Project(new[] { Created(@"C:\Kok\Acik\a.txt") });

        Assert.Empty(projection.Events);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void EachDirectory_IsProbedOnceWithinASinglePull()
    {
        var probes = new List<string>();
        var authorizer = new ChangeFeedPathAuthorizer(
            Root,
            directory =>
            {
                probes.Add(directory);
                return true;
            });

        authorizer.Project(new[]
        {
            Created(@"C:\Kok\Acik\a.txt"),
            Created(@"C:\Kok\Acik\b.txt"),
            Created(@"C:\Kok\Acik\c.txt")
        });

        Assert.Equal(new[] { Root, Open }, probes.Distinct().OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(probes.Count, probes.Distinct().Count());
    }

    [Fact]
    public void ANewAuthorizer_DoesNotInheritTheEarlierDecision()
    {
        var allowed = true;
        Func<string, bool> probe = _ => allowed;

        Assert.Single(new ChangeFeedPathAuthorizer(Root, probe)
            .Project(new[] { Created(@"C:\Kok\Acik\a.txt") })
            .Events);

        allowed = false;

        Assert.Empty(new ChangeFeedPathAuthorizer(Root, probe)
            .Project(new[] { Created(@"C:\Kok\Acik\a.txt") })
            .Events);
    }

    [Fact]
    public void DirectoriesDifferingOnlyInCase_AreProbedSeparately()
    {
        var probes = new List<string>();
        var authorizer = new ChangeFeedPathAuthorizer(
            Root,
            directory =>
            {
                probes.Add(directory);
                return !string.Equals(directory, @"C:\Kok\acik", StringComparison.Ordinal);
            });

        var projection = authorizer.Project(new[]
        {
            Created(@"C:\Kok\Acik\gorunur.txt"),
            Created(@"C:\Kok\acik\gizli.txt")
        });

        Assert.Contains(@"C:\Kok\Acik", probes);
        Assert.Contains(@"C:\Kok\acik", probes);
        Assert.Equal(@"C:\Kok\Acik\gorunur.txt", Assert.Single(projection.Events).FullPath);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void ANonRenameEventCarryingAnOldPath_IsRefusedRatherThanPublished()
    {
        var malformed = new ChangeFeedEvent(
            ChangeFeedEventKind.Created,
            @"C:\Kok\Acik\gorunur.txt",
            false,
            @"C:\Kok\Kapali\gizli.txt");

        var projection = Project(malformed);

        Assert.Empty(projection.Events);
        Assert.True(projection.Withheld);
    }

    [Fact]
    public void APublishedEvent_IsRebuiltFromCheckedFieldsOnly()
    {
        var projection = Project(Created(@"C:\Kok\Acik\a.txt"));

        var published = Assert.Single(projection.Events);
        Assert.Null(published.OldPath);
        Assert.Equal(ChangeFeedEventKind.Created, published.Kind);
    }

    [Fact]
    public void ADriveRoot_DoesNotRejectEveryChild()
    {
        var authorizer = new ChangeFeedPathAuthorizer(@"C:\", _ => true);

        var projection = authorizer.Project(new[] { Created(@"C:\Klasor\a.txt") });

        Assert.Equal(@"C:\Klasor\a.txt", Assert.Single(projection.Events).FullPath);
        Assert.False(projection.Withheld);
    }

    private static ChangeFeedRootProjection Project(params ChangeFeedEvent[] events) =>
        new ChangeFeedPathAuthorizer(Root, CanList).Project(events);

    private static bool CanList(string directory) =>
        !string.Equals(directory, Closed, StringComparison.OrdinalIgnoreCase);

    private static ChangeFeedEvent Created(string path) =>
        new(ChangeFeedEventKind.Created, path, false);

    private static ChangeFeedEvent Renamed(string path, string oldPath) =>
        new(ChangeFeedEventKind.Renamed, path, false, oldPath);
}
