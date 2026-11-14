using System.Runtime.CompilerServices;

// 宿主需要给 PluginWidgetContext 的 SaveSettings/Notify 回调赋值；
// 这些是实现细节，不暴露给插件作者。
[assembly: InternalsVisibleTo("DeskOrder")]
