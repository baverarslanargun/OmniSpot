using SmartFileLauncher.Core.Application.Files;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Files;

public sealed class BatchRenamePlannerTests
{
    private static readonly Func<string, bool> NothingExists = _ => false;

    [Fact]
    public void FindAndReplaceKeepsTheExtensionAndIgnoresCase()
    {
        var plan = BatchRenamePlanner.Plan(
            [File(@"C:\Fotoğraf\IMG_001.jpg"), File(@"C:\Fotoğraf\img_002.JPG")],
            new BatchRenameOptions(Find: "img_", Replace: "Tatil-"),
            NothingExists);

        Assert.Equal(["Tatil-001.jpg", "Tatil-002.JPG"], plan.Select(item => item.NewName));
        Assert.All(plan, item => Assert.Equal(BatchRenameStatus.Ready, item.Status));
        Assert.True(BatchRenamePlanner.CanApply(plan));
    }

    [Fact]
    public void TemplateNumbersItemsWithPaddingAndKeepsFolderNamesWhole()
    {
        var sources = Enumerable.Range(1, 11)
            .Select(index => File($@"C:\A\eski{index}.png"))
            .Append(new BatchRenameSource(@"C:\A\klasör.v2", true))
            .ToArray();

        var plan = BatchRenamePlanner.Plan(sources, new BatchRenameOptions(Template: "Tatil {n}"), NothingExists);

        Assert.Equal("Tatil 01.png", plan[0].NewName);
        Assert.Equal("Tatil 11.png", plan[10].NewName);
        Assert.Equal("Tatil 12", plan[11].NewName);
    }

    [Fact]
    public void TemplateWithoutTokenAppendsTheNumber()
    {
        var plan = BatchRenamePlanner.Plan(
            [File(@"C:\A\x.txt"), File(@"C:\A\y.txt")],
            new BatchRenameOptions(Template: "Rapor", StartNumber: 7),
            NothingExists);

        Assert.Equal(["Rapor 7.txt", "Rapor 8.txt"], plan.Select(item => item.NewName));
    }

    [Fact]
    public void CaseChangesUseTurkishRules()
    {
        var plan = BatchRenamePlanner.Plan(
            [File(@"C:\A\istanbul ılık.txt"), File(@"C:\A\İZMİR KIŞ.txt")],
            new BatchRenameOptions(Case: BatchRenameCase.Upper),
            NothingExists);
        Assert.Equal(["İSTANBUL ILIK.txt", "İZMİR KIŞ.txt"], plan.Select(item => item.NewName));
        Assert.Equal(BatchRenameStatus.Unchanged, plan[1].Status);

        var title = BatchRenamePlanner.Plan(
            [File(@"C:\A\İZMİR KIŞ.txt")],
            new BatchRenameOptions(Case: BatchRenameCase.Title),
            NothingExists);
        Assert.Equal("İzmir Kış.txt", Assert.Single(title).NewName);

        var replaced = BatchRenamePlanner.Plan(
            [File(@"C:\A\İZMİR gezi.jpg"), File(@"C:\A\Izmir yaz.jpg")],
            new BatchRenameOptions(Find: "izmir", Replace: "Ege"),
            NothingExists);
        Assert.Equal(["Ege gezi.jpg", "Ege yaz.jpg"], replaced.Select(item => item.NewName));
    }

    [Fact]
    public void AnExistingTargetBlocksApplying()
    {
        var plan = BatchRenamePlanner.Plan(
            [File(@"C:\A\a1.txt"), File(@"C:\A\a2.txt"), File(@"C:\A\b.txt")],
            new BatchRenameOptions(Find: "a", Replace: "c"),
            path => path == @"C:\A\b.txt" || path == @"C:\A\c2.txt");

        Assert.Equal(
            [BatchRenameStatus.Ready, BatchRenameStatus.Exists, BatchRenameStatus.Unchanged],
            plan.Select(item => item.Status));
        Assert.False(BatchRenamePlanner.CanApply(plan));
    }

    [Fact]
    public void TwoItemsTargetingTheSameNameInOneFolderAreDuplicates()
    {
        var plan = BatchRenamePlanner.Plan(
            [File(@"C:\A\a-1.txt"), File(@"C:\A\a-2.txt")],
            new BatchRenameOptions(Find: "-1", Replace: "-2"),
            NothingExists);

        Assert.Equal(BatchRenameStatus.Duplicate, plan[0].Status);
        Assert.Equal(BatchRenameStatus.Unchanged, plan[1].Status);
        Assert.False(BatchRenamePlanner.CanApply(plan));
    }

    [Fact]
    public void TheSameNameInDifferentFoldersIsAllowed()
    {
        var plan = BatchRenamePlanner.Plan(
            [File(@"C:\A\rapor-1.txt"), File(@"C:\B\rapor-1.txt")],
            new BatchRenameOptions(Find: "rapor-1", Replace: "özet"),
            NothingExists);

        Assert.All(plan, item => Assert.Equal(BatchRenameStatus.Ready, item.Status));
        Assert.Equal(["özet.txt", "özet.txt"], plan.Select(item => item.NewName));
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("CON")]
    [InlineData("nokta.")]
    public void InvalidNamesAreRejected(string replacement)
    {
        var plan = BatchRenamePlanner.Plan(
            [new BatchRenameSource(@"C:\A\eski", true)],
            new BatchRenameOptions(Find: "eski", Replace: replacement),
            NothingExists);

        Assert.Equal(BatchRenameStatus.Invalid, Assert.Single(plan).Status);
    }

    [Fact]
    public void CaseOnlyRenameIsNotAConflictWithItself()
    {
        var plan = BatchRenamePlanner.Plan(
            [File(@"C:\A\rapor.txt")],
            new BatchRenameOptions(Case: BatchRenameCase.Upper),
            _ => true);

        Assert.Equal(BatchRenameStatus.Ready, Assert.Single(plan).Status);
        Assert.Equal("RAPOR.txt", plan[0].NewName);
    }

    private static BatchRenameSource File(string path) => new(path, false);
}
