using System;
using System.Globalization;
using System.Linq;
using System.Windows;
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

    // ponytail: explicit default ctor required for property syntax {loc:Loc Key=X}.
    // Without it the implicit default is suppressed by the (string) ctor and BAML's
    // BindToMethod NRE's when it tries to instantiate via property syntax. The XAML
    // parser sees dotted keys like "Manage.Zones" as member access on the positional
    // arg, so multi-segment keys MUST go through the property form — and that
    // property form needs this ctor to exist.
    public LocExtension() { }

    public LocExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider sp)
    {
        // ponytail: WPF can't assign MultiBinding to string properties (TextBlock.Text,
        // ContentControl.Content, etc.) — it raises "MultiBinding is not a valid value
        // for Text". Standard fix is to fetch the target via IProvideValueTarget and
        // call BindingOperations.SetBinding ourselves, then return a one-shot fallback
        // string. The binding fires on LocalizationService.LanguageChanged because the
        // indexer raises PropertyChanged, so the live update path still works.
        var valueService = sp.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
        var targetObject = valueService?.TargetObject as DependencyObject;
        var targetProperty = valueService?.TargetProperty as DependencyProperty;

        if (targetObject != null && targetProperty != null)
        {
            var multi = new MultiBinding();
            multi.Bindings.Add(new Binding($"[{Key}]") { Source = LocalizationService.Instance });
            if (Arg0 != null) multi.Bindings.Add(Arg0);
            if (Arg1 != null) multi.Bindings.Add(Arg1);
            multi.Converter = new LocFormatConverter();
            BindingOperations.SetBinding(targetObject, targetProperty, multi);
        }

        // Fallback for the brief window before the binding pushes its first value, and
        // for non-DP targets (templates, ResourceDictionary entries). The indexer is
        // synchronous so this is the actual current value, not a placeholder.
        return LocalizationService.Instance[Key] ?? "";
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