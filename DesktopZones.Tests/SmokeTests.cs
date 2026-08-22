using Xunit;
namespace DesktopZones.Tests;

public class SmokeTests
{
    [Fact]
    public void CanReferenceMainProject()
    {
        // 验证能引用主项目类型
        var svcType = typeof(DesktopZones.Services.LocalizationService);
        Assert.NotNull(svcType);
    }
}
