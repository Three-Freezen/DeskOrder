using System.Globalization;
using Xunit;
using DesktopZones.Helpers;
namespace DesktopZones.Tests.Services;

public class LocFormatConverterTests
{
    [Fact]
    public void No_Args_Returns_Template()
    {
        var c = new LocFormatConverter();
        var result = c.Convert(new object[] { "hello" }, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void With_Args_Formats()
    {
        var c = new LocFormatConverter();
        var result = c.Convert(new object[] { "Hello {0}", "World" }, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("Hello World", result);
    }
}
