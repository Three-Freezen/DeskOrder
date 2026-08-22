using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Markup;
using DesktopZones.Services;

namespace DesktopZones.Helpers;

/// <summary>
/// XAML markup extension shorthand for <c>{Binding [Key], Source={x:Static ...}}</c> against
/// <see cref="LocalizationService"/>. Resolves at load time via the indexer; rerenders on
/// <see cref="LocalizationService.LanguageChanged"/> because the indexer is a notify property.
///
/// Usage:
///   <code>{loc:Loc Zone.Hide}</code>                                  — literal key
///   <code>{loc:Loc Dialog.DeleteZoneMsg, Arg0={Binding Name}}</code>  — with format args
///
/// ponytail: positional ctor (matches the spec). Multi-segment keys with dots (e.g.
/// <c>Motion.Origin.ButtonCenter</c>) need to use property syntax
/// <c>{loc:Loc Key=Motion.Origin.ButtonCenter, ...}</c> because the WPF XAML parser
/// interprets dots after the positional arg as member access — a structural limitation,
/// not something this extension can paper over.
/// </summary>
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public BindingBase? Arg0 { get; set; }
    public BindingBase? Arg1 { get; set; }

    public LocExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider sp)
    {
        var multi = new MultiBinding();
        multi.Bindings.Add(new Binding($"[{Key}]") { Source = LocalizationService.Instance });
        if (Arg0 != null) multi.Bindings.Add(Arg0);
        if (Arg1 != null) multi.Bindings.Add(Arg1);
        multi.Converter = new LocFormatConverter();
        return multi;
    }
}

public class LocFormatConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null || values.Length == 0 || values[0] is not string template)
            return string.Empty;
        var args = values.Skip(1).ToArray();
        return args.Length == 0 ? template : string.Format(template, args);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}