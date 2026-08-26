using SmartFileLauncher.Core.Diagnostics;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Diagnostics;

public class DiagnosticsStartupOptionsTests
{
    [Fact]
    public void NoArgumentsRequestsNothing()
    {
        var options = DiagnosticsStartupOptions.Parse(["OmniSpot.exe"]);

        Assert.False(options.IsRequested);
        Assert.Null(options.Directory);
        Assert.Null(options.Error);
    }

    [Fact]
    public void ReadsDirectoryFromFollowingArgument()
    {
        var options = DiagnosticsStartupOptions.Parse(
            ["OmniSpot.exe", "--tanila", @"C:\olcum"]);

        Assert.True(options.IsRequested);
        Assert.Equal(@"C:\olcum", options.Directory);
        Assert.Null(options.Error);
    }

    [Fact]
    public void ReadsDirectoryFromEqualsForm()
    {
        var options = DiagnosticsStartupOptions.Parse(
            ["OmniSpot.exe", @"--tanila=C:\olcum"]);

        Assert.Equal(@"C:\olcum", options.Directory);
    }

    [Fact]
    public void MatchesSwitchCaseInsensitively()
    {
        var options = DiagnosticsStartupOptions.Parse(
            ["OmniSpot.exe", "--TANILA", @"C:\olcum"]);

        Assert.Equal(@"C:\olcum", options.Directory);
    }

    [Fact]
    public void ReportsErrorWhenValueMissingAtEnd()
    {
        var options = DiagnosticsStartupOptions.Parse(["OmniSpot.exe", "--tanila"]);

        Assert.False(options.IsRequested);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void ReportsErrorWhenNextArgumentIsAnotherSwitch()
    {
        var options = DiagnosticsStartupOptions.Parse(
            ["OmniSpot.exe", "--tanila", "--index-rebuild-failed"]);

        Assert.False(options.IsRequested);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void ReportsErrorWhenValueIsBlank()
    {
        var options = DiagnosticsStartupOptions.Parse(["OmniSpot.exe", "--tanila", "   "]);

        Assert.False(options.IsRequested);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void ReportsErrorWhenEqualsFormHasNoValue()
    {
        var options = DiagnosticsStartupOptions.Parse(["OmniSpot.exe", "--tanila="]);

        Assert.False(options.IsRequested);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void StripsSurroundingQuotes()
    {
        var options = DiagnosticsStartupOptions.Parse(
            ["OmniSpot.exe", "--tanila", "\"C:\\iki kelime\""]);

        Assert.Equal(@"C:\iki kelime", options.Directory);
    }

    [Fact]
    public void IgnoresUnrelatedSwitches()
    {
        var options = DiagnosticsStartupOptions.Parse(
            ["OmniSpot.exe", "--index-rebuild-failed", "--tanila", @"C:\olcum"]);

        Assert.Equal(@"C:\olcum", options.Directory);
    }

    [Fact]
    public void DoesNotMatchSwitchPrefix()
    {
        var options = DiagnosticsStartupOptions.Parse(
            ["OmniSpot.exe", "--tanilama", @"C:\olcum"]);

        Assert.False(options.IsRequested);
        Assert.Null(options.Error);
    }
}
