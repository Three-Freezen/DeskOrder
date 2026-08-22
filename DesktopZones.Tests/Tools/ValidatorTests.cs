using Xunit;
using Seed;
using Seed.Models;
namespace DesktopZones.Tests.Tools;

public class ValidatorTests
{
    [Fact]
    public void Missing_Keys_Are_Errors()
    {
        var source = new LanguagePack();
        source.Strings["A"] = "a"; source.Strings["B"] = "b";
        var ja = new LanguagePack(); ja.Strings["A"] = "a";
        var report = Validator.Validate(source, new() { ["ja"] = ja });
        Assert.Equal("error", report.Results["ja"].Status);
        Assert.Contains("B", report.Results["ja"].Missing);
    }

    [Fact]
    public void Extra_Keys_Are_Errors()
    {
        var source = new LanguagePack(); source.Strings["A"] = "a";
        var ja = new LanguagePack(); ja.Strings["A"] = "a"; ja.Strings["Z"] = "z";
        var report = Validator.Validate(source, new() { ["ja"] = ja });
        Assert.Equal("error", report.Results["ja"].Status);
        Assert.Contains("Z", report.Results["ja"].Extra);
    }

    [Fact]
    public void Placeholder_Mismatch_Is_Error()
    {
        var source = new LanguagePack(); source.Strings["X"] = "Hello {0}";
        var ja = new LanguagePack(); ja.Strings["X"] = "こんにちは";
        var report = Validator.Validate(source, new() { ["ja"] = ja });
        Assert.Equal("error", report.Results["ja"].Status);
        Assert.Contains(report.Results["ja"].Warnings, w => w.Contains("placeholder"));
    }

    [Fact]
    public void Empty_Value_Is_Warning()
    {
        var source = new LanguagePack(); source.Strings["A"] = "a";
        var ja = new LanguagePack(); ja.Strings["A"] = "";
        var report = Validator.Validate(source, new() { ["ja"] = ja });
        Assert.Equal("warn", report.Results["ja"].Status);
    }

    [Fact]
    public void All_Match_Is_Ok()
    {
        var source = new LanguagePack();
        source.Strings["A"] = "a"; source.Strings["B"] = "b {0}";
        var ja = new LanguagePack();
        ja.Strings["A"] = "ア"; ja.Strings["B"] = "び {0}";
        var report = Validator.Validate(source, new() { ["ja"] = ja });
        Assert.Equal("ok", report.Results["ja"].Status);
    }
}
