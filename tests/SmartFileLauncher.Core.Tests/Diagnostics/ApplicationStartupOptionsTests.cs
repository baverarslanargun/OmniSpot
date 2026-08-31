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

    [Fact]
    public void LiveHeapIsEnabledWithoutAnyMeasurementProfile()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--tanila", @"C:\olcum\sizinti", "--canli-yigin", "60"]);

        Assert.Null(options.Error);
        Assert.Null(options.Profile);
        Assert.False(options.IsMeasurement);
        Assert.Equal(TimeSpan.FromSeconds(60), options.LiveHeapInterval);
    }

    [Fact]
    public void LiveHeapReadsEqualsForm()
    {
        var options = ApplicationStartupOptions.Parse(["--canli-yigin=90"]);

        Assert.Null(options.Error);
        Assert.Equal(TimeSpan.FromSeconds(90), options.LiveHeapInterval);
    }

    [Fact]
    public void LiveHeapStaysOffWhenNotRequested()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--tanila", @"C:\olcum\tur-1"]);

        Assert.Null(options.Error);
        Assert.Null(options.LiveHeapInterval);
    }

    [Fact]
    public void LiveHeapOverridesTheProfileDefault()
    {
        var options = ApplicationStartupOptions.Parse(
            [
                "--tanila", @"C:\olcum\tur-1",
                "--profil", "bos-uretim",
                "--canli-yigin", "15"
            ]);

        Assert.Null(options.Error);
        Assert.Equal(MeasurementProfile.EmptyProduction, options.Profile);
        Assert.Equal(TimeSpan.FromSeconds(15), options.LiveHeapInterval);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1,5")]
    public void LiveHeapRejectsNonNumericValue(string value)
    {
        var options = ApplicationStartupOptions.Parse(["--canli-yigin", value]);

        Assert.Null(options.LiveHeapInterval);
        Assert.Contains("--canli-yigin", options.Error);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("3601")]
    public void LiveHeapRejectsValueOutsideTheAcceptedRange(string value)
    {
        var options = ApplicationStartupOptions.Parse(["--canli-yigin", value]);

        Assert.Null(options.LiveHeapInterval);
        Assert.Contains("aralığında olmalı", options.Error);
    }

    [Fact]
    public void LiveHeapRejectsMissingValue()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--canli-yigin", "--tanila", @"C:\olcum\tur-1"]);

        Assert.Null(options.LiveHeapInterval);
        Assert.Contains("bir sayı bekliyor", options.Error);
    }

    [Fact]
    public void LiveHeapRejectsRepeatedSwitch()
    {
        var options = ApplicationStartupOptions.Parse(
            ["--canli-yigin", "60", "--canli-yigin", "90"]);

        Assert.Null(options.LiveHeapInterval);
        Assert.Contains("yalnız bir kez", options.Error);
    }
}
