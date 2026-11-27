using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SmartFileLauncher.UI.Services;
using Xunit;
using static SmartFileLauncher.UI.Tests.Views.MainWindowNavigationTests;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class CardNameRevealTests
{
    private const string LongName = "Miraculous - Chanson de Ladybug (Original Soundtrack) Remastered.wav";

    [Fact]
    public Task RevealingTheNameNeverChangesTheCardLayout() => RunSta(() =>
    {
        var (root, card, icon, name) = Card(LongName);
        var before = name.Text;
        var size = card.RenderSize;
        var nameHeight = name.ActualHeight;
        Assert.Contains("…", before);

        CardNameReveal.Open(card, animate: false);
        Layout(root);

        Assert.Equal(1, CardNameReveal.Progress(card));
        Assert.Equal(LongName, CardNameReveal.RevealedText(card));
        Assert.Equal(0, name.Opacity);
        Assert.Equal(before, name.Text);
        Assert.Equal(size, card.RenderSize);
        Assert.Equal(nameHeight, name.ActualHeight);
        var scale = Assert.IsType<ScaleTransform>(icon.RenderTransform);
        Assert.True(scale.ScaleY is < 1 and >= 0.35, scale.ScaleY.ToString());
        Assert.Null(icon.LayoutTransform as ScaleTransform);

        CardNameReveal.Close(card, animate: false);
        Layout(root);

        Assert.Equal(0, CardNameReveal.Progress(card));
        Assert.Null(CardNameReveal.RevealedText(card));
        Assert.Equal(1, name.Opacity);
        Assert.Equal(before, name.Text);
        Assert.Equal(1, scale.ScaleY);
        Assert.Equal(size, card.RenderSize);
        Assert.Empty(AdornerLayer.GetAdornerLayer(name)!.GetAdorners(name) ?? []);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AHeldCardStaysClosedAndLeavesTheNameAloneUntilReleased() => RunSta(() =>
    {
        var (_, card, icon, name) = Card(LongName);
        CardNameReveal.SetEnabled(card, true);
        CardNameReveal.Open(card, animate: false);
        Assert.Equal(1, CardNameReveal.Progress(card));

        CardNameReveal.Hold(card);
        Assert.Equal(0, CardNameReveal.Progress(card));
        Assert.Null(CardNameReveal.RevealedText(card));
        Assert.Equal(1, Assert.IsType<ScaleTransform>(icon.RenderTransform).ScaleY);
        Assert.Empty(AdornerLayer.GetAdornerLayer(name)!.GetAdorners(name) ?? []);

        name.Opacity = 0;
        CardNameReveal.Open(card, animate: false);
        CardNameReveal.SetCurrent(card);
        CardNameReveal.Close(card, animate: false);
        Assert.Equal(0, CardNameReveal.Progress(card));
        Assert.Equal(0, name.Opacity);

        name.ClearValue(UIElement.OpacityProperty);
        CardNameReveal.Release(card, animate: false);
        Assert.Equal(1, CardNameReveal.Progress(card));
        Assert.Equal(LongName, CardNameReveal.RevealedText(card));
        CardNameReveal.SetCurrent(null);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ANameThatAlreadyFitsDoesNotMove() => RunSta(() =>
    {
        var (_, card, icon, name) = Card("baver.txt");

        CardNameReveal.Open(card, animate: false);

        Assert.Equal(0, CardNameReveal.Progress(card));
        Assert.Equal(1, name.Opacity);
        Assert.False(icon.RenderTransform is ScaleTransform { ScaleY: < 1 });
        return Task.CompletedTask;
    });

    [Fact]
    public void HiddenLettersAppearOneByOneBetweenTheVisibleStartAndTheExtension()
    {
        const string full = "https_studio.youtube.com_0.indexeddb.blob";
        const string shown = "https_studio.yout…eddb.blob";
        const int head = 17;
        const string tail = "eddb.blob";

        Assert.Equal(shown, CardNameReveal.Partial(full, shown, head, tail, 0));
        var previous = shown.Length;
        foreach (var progress in new[] { 0.2, 0.4, 0.6, 0.8 })
        {
            var text = CardNameReveal.Partial(full, shown, head, tail, progress);
            Assert.StartsWith("https_studio.yout", text);
            Assert.EndsWith("…eddb.blob", text);
            Assert.True(text.Length > previous, $"{progress}: {text}");
            Assert.StartsWith(text[..text.IndexOf('…')], full);
            previous = text.Length;
        }
        Assert.Equal(full, CardNameReveal.Partial(full, shown, head, tail, 1));
    }

    [Fact]
    public void ANameWithoutAKnownCutSwapsAtOnce()
    {
        Assert.Equal("kisa…", CardNameReveal.Partial("kisaltilmis ad", "kisa…", -1, "", 0));
        Assert.Equal("kisaltilmis ad", CardNameReveal.Partial("kisaltilmis ad", "kisa…", -1, "", 0.3));
    }

    [Fact]
    public Task HighlightedNamesStillRevealOnlyWhenTrimmed() => RunSta(() =>
    {
        var (_, shortCard, _, shortName) = Card("rapor.pdf", "rapor");
        Assert.Equal("rapor.pdf", FileNameTrimming.ShownText(shortName));
        CardNameReveal.Open(shortCard, animate: false);
        Assert.Null(CardNameReveal.RevealedText(shortCard));

        var (_, longCard, _, longName) = Card(LongName, "ladybug");
        Assert.Contains("…", FileNameTrimming.ShownText(longName));
        CardNameReveal.Open(longCard, animate: false);
        Assert.Equal(LongName, CardNameReveal.RevealedText(longCard));
        return Task.CompletedTask;
    });

    private static (AdornerDecorator Root, Button Card, Border Icon, TextBlock Name) Card(string fileName, string? highlight = null)
    {
        var icon = new Border { Name = CardNameReveal.IconPart, Width = 72, Height = 72, Margin = new Thickness(0, 0, 0, 12) };
        var name = new TextBlock
        {
            Name = CardNameReveal.NamePart, FontSize = 12, LineHeight = 16, MaxHeight = 36,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        FileNameTrimming.SetHighlight(name, highlight);
        FileNameTrimming.SetFileName(name, fileName);
        var content = new StackPanel { Width = 100 };
        content.Children.Add(icon);
        content.Children.Add(name);
        var card = new Button { Content = content, Template = PlainTemplate() };
        var root = new AdornerDecorator { Child = card };
        Layout(root);
        return (root, card, icon, name);
    }

    private static ControlTemplate PlainTemplate()
    {
        var template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        template.Seal();
        return template;
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(140, 200));
        element.Arrange(new Rect(0, 0, 140, element.DesiredSize.Height));
        element.UpdateLayout();
    }
}
