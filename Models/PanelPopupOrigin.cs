namespace DesktopZones.Models;

/// <summary>
/// 面板弹出动画的展开原点 — 取「当前焦点显示器工作区」的四角之一
/// (即桌面的角,不是面板窗口自身的角)。
/// </summary>
public enum PanelPopupOrigin
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}
