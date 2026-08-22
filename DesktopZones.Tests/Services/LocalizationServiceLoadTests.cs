using System.IO;
using Xunit;
using DesktopZones.Services;
namespace DesktopZones.Tests.Services;

public class LocalizationServiceLoadTests
{
    [Fact]
    public void LoadFromDisk_SourceZh_Returns_234_Keys()
    {
        var dict = LocalizationService.LoadFromDiskForTest(
            Path.Combine(AppContext.BaseDirectory, "i18n", "source.zh.json"));
        Assert.Equal(234, dict.Count);
        Assert.Equal("最小化分区", dict["Zone.Hide"]);
    }

    [Fact]
    public void LoadFromDisk_Skips_Meta_Keys()
    {
        var dict = LocalizationService.LoadFromDiskForTest(
            Path.Combine(AppContext.BaseDirectory, "i18n", "source.zh.json"));
        Assert.DoesNotContain("_meta", dict.Keys);
    }

    [Fact]
    public void AvailableLanguages_Are_9()
    {
        Assert.Equal(9, LocalizationService.Instance.AvailableLanguages.Count);
        Assert.Contains("ja", LocalizationService.Instance.AvailableLanguages);
        Assert.Contains("zh", LocalizationService.Instance.AvailableLanguages);
    }
}
