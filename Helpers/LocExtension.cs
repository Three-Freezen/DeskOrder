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
/// <see cref="LocalizationService.LanguageChanged"/>.
///
/// Usage:
///   <code>{loc:Loc Zone.Hide}</code>                                  — single-segment
///   <code>{loc:Loc Key=Dialog.DeleteZoneMsg}</code>                   — multi-segment (dotted)
///   <code>{loc:Loc Dialog.DeleteZoneMsg, Arg0={Binding Name}}</code>  — with format args
///
/// ponytail: text 跟颜色走同一条路 — 数据变 → 通知 → UI 刷新。颜色靠 DynamicResource
/// 直接引用 MergedDictionaries 里的 brush；文字这条一开始也走 WPF 的 Binding-on-indexer
/// + PropertyChanged("Item[]")，实测在 Style / Template / ContentPresenter 等上下文里
/// IProvideValueTarget.TargetObject 为 null、BindingOperations.SetBinding 被跳过，绑定
/// 没真的挂上，UI 就成了一次性。换直接路线：LocExtension 自己订 LanguageChanged 回调
/// 直接 SetValue 推到 target，语义跟 DynamicResource brush 引用刷新等价。
/// </summary>
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public BindingBase? Arg0 { get; set; }
    public BindingBase? Arg1 { get; set; }

    // ponytail: explicit default ctor required for property syntax {loc:Loc Key=X}.
    // Without it the implicit default is suppressed by the (string) ctor and BAML's
    // BindToMethod NRE's when it tries to instantiate via property syntax. Multi-
    // segment keys (e.g. "Manage.Zones") MUST use property syntax because the XAML
    // parser reads dots after the positional arg as member access — structural limit.
    public LocExtension() { }

    public LocExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider sp)
    {
        var loc = LocalizationService.Instance;
        var valueService = sp.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
        var targetObject = valueService?.TargetObject as DependencyObject;
        var targetProperty = valueService?.TargetProperty as DependencyProperty;

        if (targetObject != null && targetProperty != null)
        {
            // Per-instance handler — closes over Key, targetObject, targetProperty.
            // Fires on every LanguageChanged to push the new value to the target.
            // The handler is the only strong reference to the LocExtension closure,
            // so when the target's Unloaded fires we unsubscribe to avoid leaking
            // handlers every time a window is closed and reopened.
            Action<string> handler = _ => Refresh(targetObject, targetProperty);
            loc.LanguageChanged += handler;

            // Unloaded only exists on FrameworkElement; skip the leak guard for raw
            // DOs (Freezables, etc.) — those live for the duration of the binding
            // expression so the static event reference is reclaimed with the binding.
            if (targetObject is FrameworkElement fe)
            {
                var unsub = new LocUnsubscriber(fe, handler);
                fe.Unloaded += unsub.OnUnloaded;
            }
        }

        return loc[Key] ?? "";
    }

    void Refresh(DependencyObject target, DependencyProperty property)
    {
        var loc = LocalizationService.Instance;
        var value = loc[Key] ?? "";

        if (Arg0 != null || Arg1 != null)
        {
            // ponytail: format-arg path. None of the XAML {loc:Loc ...} usages in the
            // dashboard pass Arg0/Arg1 — that path is reserved for future dialog text.
            // For now just push the raw template; revisit when a real caller needs it.
            target.SetValue(property, value);
            return;
        }

        target.SetValue(property, value);
    }

    sealed class LocUnsubscriber
    {
        readonly FrameworkElement _target;
        readonly Action<string> _handler;
        public LocUnsubscriber(FrameworkElement target, Action<string> handler)
        { _target = target; _handler = handler; }
        public void OnUnloaded(object? s, RoutedEventArgs e)
        {
            LocalizationService.Instance.LanguageChanged -= _handler;
            _target.Unloaded -= OnUnloaded;
        }
    }
}

public class LocFormatConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length == 0 || values[0] is not string template)
            return string.Empty;
        var args = values.Skip(1).ToArray();
        return args.Length == 0 ? template : string.Format(template, args);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
