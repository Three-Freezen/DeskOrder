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
            RegisterTarget(loc, targetObject, targetProperty, Key);

        return loc[Key] ?? "";
    }

    // ── 语言变化重刷（ponytail 2026-11-14，审查 P1-11）──
    // 旧实现每个 {loc:Loc} 都直接订阅静态 LanguageChanged，退订只靠 FrameworkElement.Unloaded：
    // 非 FE 目标永远不退订、Popup/ContextMenu/模板内容可能不触发 Unloaded → 静态事件把已关闭的
    // 局部 UI 树钉住。现在只订阅一次语言事件，内部维护**弱引用**目标表，语言变化时逐个 SetValue
    // 并顺手清掉已回收目标 —— 不再强引用任何元素。
    sealed class LocalizedTarget
    {
        public WeakReference<DependencyObject> Target = null!;
        public DependencyProperty Property = null!;
        public string Key = "";
    }

    static readonly object _targetsLock = new();
    static readonly System.Collections.Generic.List<LocalizedTarget> _targets = new();
    static bool _languageHookInstalled;

    static void RegisterTarget(LocalizationService loc, DependencyObject target, DependencyProperty property, string key)
    {
        if (!_languageHookInstalled)
        {
            _languageHookInstalled = true;
            loc.LanguageChanged += OnLanguageChanged;
        }
        lock (_targetsLock)
        {
            // 同一元素/属性/Key 只登记一次（模板重挂载会重复 ProvideValue）。
            foreach (var existing in _targets)
            {
                if (existing.Property != property || existing.Key != key) continue;
                if (existing.Target.TryGetTarget(out var t) && ReferenceEquals(t, target)) return;
            }
            _targets.Add(new LocalizedTarget
            {
                Target = new WeakReference<DependencyObject>(target),
                Property = property,
                Key = key,
            });
        }
    }

    static void OnLanguageChanged(string lang)
    {
        var loc = LocalizationService.Instance;
        System.Collections.Generic.List<LocalizedTarget> alive;
        lock (_targetsLock)
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                if (!_targets[i].Target.TryGetTarget(out _)) _targets.RemoveAt(i);
            }
            alive = new System.Collections.Generic.List<LocalizedTarget>(_targets);
        }
        foreach (var entry in alive)
        {
            if (!entry.Target.TryGetTarget(out var target)) continue;
            try
            {
                var value = loc[entry.Key] ?? "";
                if (target is System.Windows.Threading.DispatcherObject d && !d.CheckAccess())
                {
                    d.Dispatcher.BeginInvoke(new Action(() => target.SetValue(entry.Property, value)));
                }
                else
                {
                    target.SetValue(entry.Property, value);
                }
            }
            catch { }
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
