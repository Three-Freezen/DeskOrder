using System.Reflection;

namespace DesktopZones.Helpers;

/// <summary>全项目唯一版本来源：csproj 的 &lt;Version&gt; 经生成属性写入程序集，
/// 这里只读。Directory.Build.props 关闭了属性生成，csproj 里单独重新打开。</summary>
public static class AppVersion
{
    public static string Current { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "0.0.0";
}
