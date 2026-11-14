namespace DesktopZones.Helpers;

/// <summary>
/// 进程级「正在关闭」闩锁。只置位、永不复位 —— 与 WPF 内部的 <c>_appIsShutdown</c> 同语义：
/// 一旦 <see cref="System.Windows.Application.Shutdown()"/> 开始，这次进程生命周期就结束了。
///
/// 为什么需要它：关闭流程是「关窗口 + 置标志」，不是「停代码」。模态对话框的
/// <c>ShowDialog()</c> 要在关闭已经开始之后才返回，调用方随后继续跑的同步代码、以及
/// <c>Dispatcher.BeginInvoke</c> 队列项 / <c>await</c> 续体，都可能落在关闭之后；这时再
/// <c>new PluginCard()</c>（BAML 加载）就会撞上框架的 shutting-down 守卫并抛
/// <c>InvalidOperationException("应用程序对象正在关闭。")</c>。
///
/// 消费方式：凡是「新建 WPF 对象 / 加载 BAML / 重建视觉树 / 弹模态框」之前先判
/// <see cref="IsShuttingDown"/>。置位只发生在 UI 线程的退出路径上。
/// </summary>
public static class AppLifetime
{
    // 读取可能发生在后台续体里（fire-and-forget 的 await 之后），用 volatile 保证可见性。
    static volatile bool _isShuttingDown;

    /// <summary>进程是否已经开始关闭（只置不复位）。</summary>
    public static bool IsShuttingDown => _isShuttingDown;

    /// <summary>幂等；必须赶在 teardown 之前调用（退出路径的第一行）。</summary>
    public static void MarkShuttingDown() => _isShuttingDown = true;
}
