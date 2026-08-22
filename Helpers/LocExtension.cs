using System.Windows.Data;
using System.Windows.Markup;
using DesktopZones.Services;

namespace DesktopZones.Helpers;

public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public BindingBase? Arg0 { get; set; }
    public BindingBase? Arg1 { get; set; }

    public LocExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var multi = new MultiBinding { Converter = new LocFormatConverter() };
        multi.Bindings.Add(new Binding($"[{Key}]") { Source = LocalizationService.Instance });
        if (Arg0 != null) multi.Bindings.Add(Arg0);
        if (Arg1 != null) multi.Bindings.Add(Arg1);
        return multi.ProvideValue(serviceProvider);
    }
}
