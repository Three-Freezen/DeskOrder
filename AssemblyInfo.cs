using System.Windows;

// 本应用为纯 Windows WPF 程序(到处是 Win32 P/Invoke)。最低平台版本声明为
// Windows 10 1809(10.0.17763) —— 与 csproj 的 <SupportedOSPlatformVersion>、
// MSIX 清单/Inno 安装器的最低系统要求保持一致。
//
// ponytail 2026-11-14: 这里曾写 windows6.1(当年为了让 System.Drawing.Common 等
// 6.1 API 放行)。但 CA1416 会把程序集最低平台当作"程序要跑 Win7", 于是把
// SMTC(要求 10.0.17763) / DataReader(要求 10.0.10240) 这类 Win10 API 全部报成
// 平台不支持 —— 历史 45 条 CA1416 警告的来源。6.1 的 API 在 17763 上同样受支持,
// 所以抬到 17763 不会让老调用点反报; 反而以后误用更高版本(Win11)API 时能提醒。
// 不能用 csproj 的 <SupportedOSPlatformVersion> 生成: Directory.Build.props 里
// GenerateAssemblyInfo=false 把特性生成整体关掉了, 只能手写在这里。
[assembly:System.Runtime.Versioning.SupportedOSPlatform("windows10.0.17763.0")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
