using System.Globalization;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed class IndexProgressPresentationTests
{
    [Fact]
    public void UnknownInventoryTotalShowsCountAndTimeWithoutInventingAPercentage()
    {
        var text = IndexProgressPresentation.Format(new IndexProgress {
            Status = "MFT dosya listesi alınıyor...", ItemCount = 1500, IsIndeterminate = true
        }, 42000, CultureInfo.GetCultureInfo("tr-TR"));
        Assert.Contains("1.500 kayıt", text);
        Assert.Contains("00:42", text);
        Assert.DoesNotContain("%", text);
    }

    [Fact]
    public void KnownWorkShowsCompletedTotalAndStagePercentage()
    {
        var text = IndexProgressPresentation.Format(new IndexProgress {
            Status = "İndeks kaydediliyor...", ItemCount = 1500, TotalItemCount = 3000, Percentage = 50
        }, 61000, CultureInfo.GetCultureInfo("tr-TR"));
        Assert.Contains("1.500/3.000 kayıt", text);
        Assert.Contains("Bu aşama %50", text);
        Assert.Contains("01:01", text);
    }

    [Fact]
    public void CatalogStageKeepsItsLiveStatusAndLongElapsedTime()
    {
        var text = IndexProgressPresentation.Format(new IndexProgress {
            Status = "Arama hazırlanıyor...", IsCatalogBuild = true, IsIndeterminate = true
        }, 3601000);
        Assert.Contains("Arama hazırlanıyor...", text);
        Assert.Contains("60:01", text);
        Assert.DoesNotContain("%", text);
    }
}
