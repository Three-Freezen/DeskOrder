using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopZones.Helpers
{
    /// <summary>
    /// 小按钮统一的悬停高光 —— 分区 / 组件 / 面板 / 次级分区浮层共用同一个配方：
    /// 平时**没有**任何底盘方块；鼠标移上去铺一层半透明白 (<see cref="Brush"/>)，
    /// 移开立刻撤掉。**不改字色**（按钮颜色由用户配置，悬停时保持用户设的颜色）。
    ///
    /// 之所以收口成一个 helper 而不是各窗口各写一套：这些按钮过去散落着三套口径
    /// （常驻 #30FFFFFF 淡白方块 + 悬停变亮、常驻 #10FFFFFF，以及便签的
    /// “悬停换底色 + 字变纯白”），用户要的就是把它们统一。将来新增按钮只挂一个
    /// <c>helpers:HoverHighlight.IsEnabled="True"</c> 即可，不用再抄颜色值。
    ///
    /// 只用于 <see cref="Button"/> 与 <see cref="Border"/> 这两类“假按钮”；
    /// 元素需显式设 <c>Background="Transparent"</c>（Button 还要
    /// <c>Style="{x:Null}"</c>，避免 App 级隐式 Button 样式再加一层主题灰底/边框）。
    ///
    /// 尺寸口径（对齐便签标题栏那一排）：外框 <b>20×20</b>、字形 <b>FontSize=10</b>、
    /// 按钮间距 <b>3px</b>。
    /// </summary>
    public static class HoverHighlight
    {
        /// <summary>悬停底色 = 便签按钮原来那层 <c>#30FFFFFF</c>（≈18.8% 白），也是全应用唯一的按钮高光色。</summary>
        public static readonly SolidColorBrush Brush = Freeze(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

        /// <summary>
        /// 整排小按钮的统一不透明度。分区那边历史上把 <c>ControlPoint</c> 整排设在
        /// <c>ControlOpacity%</c>（默认 100% 外观设置 → 实际 0.4），于是同一个 #30FFFFFF
        /// 在分区上被压到 ≈7.5% 白、几乎看不见，而组件上直接 18.8% 白 —— 这就是
        /// “组件高光偏亮/偏色、分区像透明”的根因。现在所有窗口共用这一个值，
        /// 由各窗口在应用外观时写入（分区仍跟随 ControlOpacity 设置）。
        /// </summary>
        public static double ChromeOpacity { get; private set; } = DefaultChromeOpacity;

        public const double DefaultChromeOpacity = 0.4;

        /// <summary>设定统一不透明度，并给已挂接的元素套上（幂等，可在 ApplyStyle 里反复调用）。</summary>
        public static void SetChromeOpacity(double opacity, IEnumerable<UIElement>? targets = null)
        {
            ChromeOpacity = Math.Max(0.05, Math.Min(1.0, opacity));
            if (targets == null) return;
            foreach (var el in targets)
            {
                if (el != null) el.Opacity = ChromeOpacity;
            }
        }

        /// <summary>
        /// 把「按钮颜色」模型里的 ControlOpacity 百分比(0~100)折算成统一不透明度。
        /// 分区/时钟/日历/便签/面板都有这个设置项，各窗口 ApplyStyle 时往这里喂一次，
        /// 于是所有小按钮的明暗只跟一个数走（默认 40% 与分区历史观感一致）。
        /// </summary>
        public static void ApplyControlOpacity(double percent, IEnumerable<UIElement>? targets = null)
            => SetChromeOpacity(percent / 100.0, targets);

        static SolidColorBrush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // ── 挂载开关（XAML 一行搞定）────────────────────────────────────────────
        // helpers:HoverHighlight.IsEnabled="True"

        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled", typeof(bool), typeof(HoverHighlight),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);
        public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);

        static void OnIsEnabledChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            if (o is not UIElement fe) return;
            if ((bool)e.NewValue) Attach(fe);
            else Detach(fe);
        }

        // ── 代码挂载（Border 假按钮用）──────────────────────────────────────────

        static readonly DependencyProperty HookedProperty =
            DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(HoverHighlight),
                new PropertyMetadata(false));

        /// <summary>给任意“假按钮”元素挂上悬停高光（幂等），并套上统一的不透明度。</summary>
        public static void Attach(UIElement element)
        {
            if (element == null) return;
            if (element is FrameworkElement fe0) fe0.Opacity = ChromeOpacity;
            if ((bool)element.GetValue(HookedProperty)) return;
            element.SetValue(HookedProperty, true);
            element.MouseEnter += OnEnter;
            element.MouseLeave += OnLeave;
        }

        public static void Detach(UIElement element)
        {
            if (element == null || !(bool)element.GetValue(HookedProperty)) return;
            element.SetValue(HookedProperty, false);
            element.MouseEnter -= OnEnter;
            element.MouseLeave -= OnLeave;
        }

        static void OnEnter(object sender, MouseEventArgs e)
        {
            switch (sender)
            {
                case Button b: b.Background = Brush; break;
                case Border d: d.Background = Brush; break;
            }
        }

        static void OnLeave(object sender, MouseEventArgs e)
        {
            // 移开时回到「底盘色」：默认透明（绝大多数字按钮平时没有方块），
            // 但 ▦/≡ 这对切换图标要回到「当前选中态那个 #20FFFFFF」，
            // 否则一悬停就把选中标识抹掉了。见 BaseBackground。
            var back = (sender as DependencyObject) is { } dep
                ? GetBaseBackground(dep) ?? System.Windows.Media.Brushes.Transparent
                : System.Windows.Media.Brushes.Transparent;
            switch (sender)
            {
                case Button b: b.Background = back; break;
                case Border d: d.Background = back; break;
            }
        }

        // ── 底盘色（悬停结束后要恢复的 Background）───────────────────────────
        // 默认 null = 透明。目前只有 ▦/≡ 这一对「常驻选中方块」的按钮用得上：
        // 由窗口在切换视图时把「当前该有的底色」写进来。

        public static readonly DependencyProperty BaseBackgroundProperty =
            DependencyProperty.RegisterAttached(
                "BaseBackground", typeof(Brush), typeof(HoverHighlight),
                new PropertyMetadata(null));

        public static void SetBaseBackground(DependencyObject o, Brush? v) => o.SetValue(BaseBackgroundProperty, v);
        public static Brush? GetBaseBackground(DependencyObject o) => (Brush?)o.GetValue(BaseBackgroundProperty);

        // ── 圆角高光模板（Button 专用）──────────────────────────────────────────
        // 分区的小按钮是 Border + CornerRadius，高光天然被圆角裁掉；小组件是 Button，
        // 走 App 级隐式模板（里面那层 Border 的圆角写死、不跟随），高光会填满整个矩形
        // → 看到“尖角 vs 圆角”。这里给 Button 换一个「背景层自己带圆角」的模板，
        // 于是不管挂在谁身上，高光都是圆角，和分区一模一样。

        /// <summary>圆角半径，与分区小按钮（Border CornerRadius=4）保持一致。</summary>
        public const double CornerRadius = 4;

        static ControlTemplate? _rounded;

        /// <summary>取/建「圆角高光 + 内容居中」的 Button 模板。可在 XAML 里这样直接用：
        /// <c>Template="{x:Static h:HoverHighlight.RoundedButtonTemplate}"</c>。</summary>
        public static ControlTemplate RoundedButtonTemplate
        {
            get
            {
                if (_rounded != null) return _rounded;

                var border = new FrameworkElementFactory(typeof(Border), "Bd");
                border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(CornerRadius));
                border.SetValue(Border.SnapsToDevicePixelsProperty, true);

                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                border.AppendChild(presenter);

                _rounded = new ControlTemplate(typeof(ButtonBase)) { VisualTree = border };
                _rounded.Seal();
                return _rounded;
            }
        }
    }
}
