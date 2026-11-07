using SmartFileLauncher.Core.Application.Files;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Files;

public sealed class NavigationHistoryTests
{
    [Fact]
    public void BackAndForwardReturnAlongTheVisitedPath()
    {
        var history = new NavigationHistory<string>(10);
        history.Record("arama");
        history.Record("A");

        Assert.True(history.TryPeekBack(out var back));
        Assert.Equal("A", back);
        history.CompleteBack("B");
        Assert.True(history.TryPeekBack(out back));
        Assert.Equal("arama", back);
        history.CompleteBack("A");

        Assert.False(history.CanGoBack);
        Assert.Equal(["B", "A"], history.ForwardEntries);
        Assert.True(history.TryPeekForward(out var forward));
        Assert.Equal("A", forward);
        history.CompleteForward("arama");
        Assert.Equal(["arama"], history.BackEntries);
    }

    [Fact]
    public void RecordingANewStepClearsForwardEntries()
    {
        var history = new NavigationHistory<string>(10);
        history.Record("A");
        history.CompleteBack("B");

        history.Record("A");

        Assert.False(history.CanGoForward);
        Assert.Equal(["A"], history.BackEntries);
    }

    [Fact]
    public void CapacityDropsTheOldestEntries()
    {
        var history = new NavigationHistory<int>(3);
        for (var step = 1; step <= 5; step++)
        {
            history.Record(step);
        }

        Assert.Equal([3, 4, 5], history.BackEntries);
    }

    [Fact]
    public void DiscardRemovesAnUnreachableEntryWithoutMovingForward()
    {
        var history = new NavigationHistory<string>(10);
        history.Record("ev");
        history.Record("silinen");

        history.DiscardBack();

        Assert.True(history.TryPeekBack(out var back));
        Assert.Equal("ev", back);
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void CompletingWithoutAnEntryFails()
    {
        var history = new NavigationHistory<string>(2);

        Assert.False(history.TryPeekBack(out _));
        Assert.Throws<InvalidOperationException>(() => history.CompleteBack("A"));
        Assert.Throws<InvalidOperationException>(() => history.DiscardForward());
    }
}
