using SmartFileLauncher.Core.Diagnostics;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Diagnostics;

public sealed class ApplicationStartupOptionsTests
{
    [Fact]
    public void NoProfileKeepsNormalDiagnosticsBehavior()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--tanila", @"C:\olcum\tur-1"]);

        Assert.False(options.IsMeasurement);
        Assert.Null(options.Profile);
        Assert.Null(options.Error);
        Assert.Equal(@"C:\olcum\tur-1", options.Diagnostics.Directory);
    }

    [Fact]
    public void NoProfileKeepsExistingFirstDiagnosticsSwitchBehavior()
    {
        var options = ApplicationStartupOptions.Parse(
            [
                "--tanila", @"C:\olcum\tur-1",
                "--tanila", @"C:\olcum\tur-2"
            ]);

        Assert.False(options.IsMeasurement);
        Assert.Null(options.Error);
        Assert.Equal(@"C:\olcum\tur-1", options.Diagnostics.Directory);
    }

    [Fact]
    public void ReadsEmptyProductionProfileFromFollowingArgument()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--tanila", @"C:\olcum\tur-1", "--profil", "bos-uretim"]);

        Assert.Equal(MeasurementProfile.EmptyProduction, options.Profile);
        Assert.Equal("bos-uretim", options.ProfileName);
        Assert.Null(options.Error);
    }

    [Fact]
    public void ReadsProfileFromEqualsFormCaseInsensitively()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--PROFIL=BOS-URETIM", "--tanila", @"C:\olcum\tur-1"]);

        Assert.Equal(MeasurementProfile.EmptyProduction, options.Profile);
    }

    [Fact]
    public void ReadsProductionCopyProfile()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--tanila", @"C:\olcum\tur-1", "--profil", "uretim-kopya"]);

        Assert.Equal(MeasurementProfile.ProductionCopy, options.Profile);
        Assert.Equal("uretim-kopya", options.ProfileName);
        Assert.Null(options.Error);
    }

    [Fact]
    public void ProductionCopyProfileRequiresExactlyOneDiagnosticsDirectory()
    {
        var options = ApplicationStartupOptions.Parse(
            [
                "--tanila", @"C:\olcum\tur-1",
                "--tanila", @"C:\olcum\tur-2",
                "--profil", "uretim-kopya"
            ]);

        Assert.Null(options.Profile);
        Assert.Contains("yalnız bir kez", options.Error);
    }

    [Fact]
    public void RejectsTurkishProfileSpelling()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--tanila", @"C:\olcum\tur-1", "--profil", "boş-üretim"]);

        Assert.Null(options.Profile);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void RejectsUnknownProfile()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--tanila", @"C:\olcum\tur-1", "--profil", "bilinmeyen"]);

        Assert.Null(options.Profile);
        Assert.Contains("bos-uretim", options.Error);
    }

    [Fact]
    public void ProfileRequiresDiagnosticsDirectory()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--profil", "bos-uretim"]);

        Assert.Null(options.Profile);
        Assert.NotNull(options.Error);
        Assert.Contains("--tanila", options.Error);
    }

    [Fact]
    public void RejectsMissingProfileValue()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--tanila", @"C:\olcum\tur-1", "--profil"]);

        Assert.Null(options.Profile);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void RejectsDuplicateProfileSwitch()
    {
        var options = ApplicationStartupOptions.Parse(
            [
                "--tanila", @"C:\olcum\tur-1",
                "--profil", "bos-uretim",
                "--profil=bos-uretim"
            ]);

        Assert.Null(options.Profile);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void MeasurementProfileRejectsDuplicateDiagnosticsSwitch()
    {
        var options = ApplicationStartupOptions.Parse(
            [
                "--tanila", @"C:\olcum\tur-1",
                "--tanila=C:\\olcum\\tur-2",
                "--profil", "bos-uretim"
            ]);

        Assert.Null(options.Profile);
        Assert.Contains("yalnız bir kez", options.Error);
    }

    [Fact]
    public void MeasurementProfileRejectsMalformedLaterDiagnosticsSwitch()
    {
        var options = ApplicationStartupOptions.Parse(
            [
                "--tanila", @"C:\olcum\tur-1",
                "--tanila",
                "--profil", "bos-uretim"
            ]);

        Assert.Null(options.Profile);
        Assert.Contains("bir dizin bekliyor", options.Error);
    }
}
