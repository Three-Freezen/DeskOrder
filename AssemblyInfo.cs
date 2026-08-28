using System.Windows;

// 本应用为纯 Windows WPF 程序(到处是 Win32 P/Invoke)。声明最低平台版本 6.1
// 以匹配 System.Drawing.Common 等 API 的平台要求 — CA1416 据此放行全部调用点。
// 不能用 csproj 的 <SupportedOSPlatformVersion> 生成: Directory.Build.props 里
// GenerateAssemblyInfo=false 把特性生成整体关掉了, 只能手写在这里。
[assembly:System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
