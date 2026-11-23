using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class FileNameTrimmingTests
{
    [Theory]
    [InlineData("MiraculousTale-E0158_OV_V3051_20190722.mp4", 24, ".mp4")]
    [InlineData("MiraculousTale-E0158_OV_V3051_20190722.srt", 24, ".srt")]
    [InlineData("Proje sunumu son hali gercekten son.pptx", 20, ".pptx")]
    public void LongNamesKeepTheirStartAndExtension(string name, int limit, string extension)
    {
        var trimmed = FileNameTrimming.Trim(name, candidate => candidate.Length <= limit);

        Assert.True(trimmed.Length <= limit, trimmed);
        Assert.EndsWith(extension, trimmed);
        Assert.Contains("…", trimmed);
        Assert.StartsWith(name[..5], trimmed);
    }

    [Fact]
    public void TwinsThatDifferOnlyByExtensionStayDistinct()
    {
        static bool Fits(string candidate) => candidate.Length <= 24;
        Assert.NotEqual(
            FileNameTrimming.Trim("MiraculousTale-E0158_OV_V3051_20190722.mp4", Fits),
            FileNameTrimming.Trim("MiraculousTale-E0158_OV_V3051_20190722.srt", Fits));
    }

    [Theory]
    [InlineData("baver")]
    [InlineData("kisa.txt")]
    public void NamesThatFitAreLeftAlone(string name) =>
        Assert.Equal(name, FileNameTrimming.Trim(name, candidate => candidate.Length <= 24));

    [Fact]
    public void FolderNamesWithoutExtensionKeepTheirEnding()
    {
        var trimmed = FileNameTrimming.Trim("GameJam2025 yedek kopyasi taslak", candidate => candidate.Length <= 18);
        Assert.True(trimmed.Length <= 18, trimmed);
        Assert.EndsWith("taslak", trimmed);
        Assert.StartsWith("GameJam", trimmed);
    }

    [Fact]
    public Task ACardSizedTextBlockKeepsTheExtensionVisible() => SmartFileLauncher.UI.Tests.Views.MainWindowNavigationTests.RunSta(() =>
    {
        var block = new System.Windows.Controls.TextBlock
        {
            Width = 100, FontSize = 12, LineHeight = 16, MaxHeight = 36,
            TextWrapping = System.Windows.TextWrapping.Wrap, TextAlignment = System.Windows.TextAlignment.Center
        };
        FileNameTrimming.SetFileName(block, "MiraculousTale-E0158_OV_V3051_20190722_ENG.mp4");
        block.Measure(new System.Windows.Size(100, 100));
        block.Arrange(new System.Windows.Rect(0, 0, 100, 36));
        block.UpdateLayout();

        Assert.EndsWith(".mp4", block.Text);
        Assert.Contains("…", block.Text);
        Assert.StartsWith("Miraculous", block.Text);

        FileNameTrimming.SetFileName(block, "baver");
        Assert.Equal("baver", block.Text);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("MiraculousTale-E0158_OV_V30518571_RD542453-TIM51_SUBREF.mp4.sfk", ".mp4.sfk")]
    [InlineData("yedek.tar.gz", ".tar.gz")]
    [InlineData("rapor.final.pdf", ".pdf")]
    [InlineData("baver", "")]
    [InlineData(".gitignore", "")]
    public void ShortInnerExtensionsStayWithTheLastOne(string name, string extension) =>
        Assert.Equal(extension, name[^FileNameTrimming.ExtensionLength(name)..]);

    [Fact]
    public Task AListRowKeepsTheWholeExtensionOnOneLine() => SmartFileLauncher.UI.Tests.Views.MainWindowNavigationTests.RunSta(() =>
    {
        string Row(string name)
        {
            var block = new System.Windows.Controls.TextBlock { Width = 320, FontSize = 14, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
            FileNameTrimming.SetFileName(block, name);
            block.Measure(new System.Windows.Size(320, 40));
            block.Arrange(new System.Windows.Rect(0, 0, 320, 40));
            block.UpdateLayout();
            return block.Text;
        }

        var video = Row("MiraculousTale-E0158_OV_V30518571_RD542453-TIM51_SUBREF.mp4");
        var sidecar = Row("MiraculousTale-E0158_OV_V30518571_RD542453-TIM51_SUBREF.mp4.sfk");
        Assert.EndsWith(".mp4", video);
        Assert.EndsWith(".mp4.sfk", sidecar);
        Assert.Contains("…", sidecar);
        Assert.StartsWith("MiraculousTale-E0158", sidecar);
        Assert.NotEqual(video, sidecar);
        Assert.Equal("baver", Row("baver"));
        return Task.CompletedTask;
    });

    [Fact]
    public void WhenNothingFitsTheNameIsLeftForTheTextBlockToTrim()
    {
        const string name = "uzun-bir-dosya-adi.docx";
        Assert.Equal(name, FileNameTrimming.Trim(name, _ => false));
    }
}
