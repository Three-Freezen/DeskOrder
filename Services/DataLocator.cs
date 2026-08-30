using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopZones.Services;

/// <summary>
/// ponytail 2026-08-29: 用户数据根目录定位。安装器(DeskOrder.iss)向导里可选
/// "系统 AppData(推荐)" 或 "软件安装文件夹(便携模式)";选便携时安装器在
/// 安装目录写 Data\portable.flag,本类据此把所有用户数据(config.json / Notes /
/// Presets / lang / 日志)从 %APPDATA%\DesktopZones 切到 安装目录\Data。
/// 各服务一律经 Root / LogsRoot 取根,不再散落 SpecialFolder 调用。
/// 便携模式首启把 AppData 里的既有数据搬进 Data(幂等:目标已有同名文件则跳过),
/// 实现"选择后自动创建对应文件夹并接管既有用户数据"。标准模式行为与历史版本一致。
/// 注意:in-app 更新走 Setup.exe /SILENT(跳过选择页),Data 文件夹与 marker 不被
/// 安装器触碰(Inno 只删 [Files] 清单内文件),模式跨升级保持。
/// </summary>
public static class DataLocator
{
    static bool? _portable;

    /// <summary>便携判定标记:安装器在用户选择"保存在软件文件夹"后写入。</summary>
    public static string PortableFlagPath => Path.Combine(
        AppContext.BaseDirectory, "Data", "portable.flag");

    static bool? _packaged;

    /// <summary>
    /// MSIX/Store 打包运行判定(kernel32.GetCurrentPackageFullName:未打包进程
    /// 返回 APPMODEL_ERROR_NO_PACKAGE=15700;打包进程先回 ERROR_INSUFFICIENT_
    /// BUFFER=122,再成功取到包全名)。打包进程装在只读 WindowsApps,便携模式
    /// 与应用内自更新(下载 Setup 覆盖安装)均不适用。
    /// </summary>
    public static bool IsPackaged
    {
        get
        {
            if (_packaged.HasValue) return _packaged.Value;
            try
            {
                uint len = 0;
                if (GetCurrentPackageFullName(ref len, null) != 122)
                {
                    _packaged = false;
                    return false;
                }
                var sb = new StringBuilder((int)len);
                _packaged = GetCurrentPackageFullName(ref len, sb) == 0;
            }
            catch
            {
                _packaged = false;
            }
            return _packaged.Value;
        }
    }

    [DllImport("kernel32.dll")]
    static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, [Out] StringBuilder? packageFullName);

    static string? _identityName;

    /// <summary>MSIX 包身份 Name(如 Three-Freezen.DeskOrder);未打包返回 null,
    /// 打包但解析失败返回空串。包全名格式 Name_Version_Arch__PublisherHash,身份名
    /// 不含下划线(只允许字母/数字/点/连字符),按 '__' 截断再取 '_' 前段。</summary>
    public static string? PackageIdentityName
    {
        get
        {
            if (!IsPackaged) return null;
            if (_identityName != null) return _identityName;
            try
            {
                uint len = 0;
                if (GetCurrentPackageFullName(ref len, null) != 122)
                {
                    _identityName = "";
                    return _identityName;
                }
                var sb = new StringBuilder((int)len);
                if (GetCurrentPackageFullName(ref len, sb) != 0)
                {
                    _identityName = "";
                    return _identityName;
                }
                _identityName = sb.ToString().Split("__")[0].Split('_')[0];
            }
            catch { _identityName = ""; }
            return _identityName;
        }
    }

    /// <summary>true = 数据存安装目录 Data;false = 存 %APPDATA%\DesktopZones。
    /// 进程内缓存首判结果(运行中安装目录结构不会变化)。MSIX 包内容只读,
    /// 便携标记写不进去,再防御一刀保证打包态恒走 AppData。</summary>
    public static bool IsPortable => !IsPackaged && (_portable ??= File.Exists(PortableFlagPath));

    /// <summary>用户数据根目录(config.json / Notes / Presets / lang 的共同父目录)。</summary>
    public static string Root => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopZones");

    /// <summary>日志根目录(debug.log / dz_trace.log 的父目录)。便携模式随 Data 走;
    /// 标准模式维持历史路径 %LOCALAPPDATA%\DeskOrder\logs 不变。</summary>
    public static string LogsRoot => IsPortable
        ? Path.Combine(Root, "logs")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskOrder", "logs");

    /// <summary>
    /// App 启动最早处调用一次(先于任何 ConfigService / LocalizationService 访问):
    /// 确保根目录存在("自动创建对应文件夹");便携模式且 Data 还是空壳时把 AppData
    /// 里的既有用户数据搬过来。失败(权限/占用)只放弃迁移,不阻塞启动。
    /// </summary>
    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(Root);
            if (IsPortable) MigrateFromAppData();
        }
        catch
        {
            // 落点创建失败是致命环境问题,但各服务自己 CreateDirectory 时仍会给
            // 出二次机会;这里吞掉以保证启动不被日志/迁移类故障卡死。
        }
    }

    /// <summary>便携首启接管:把标准模式 AppData 根下的既有数据复制到 Data。
    /// 幂等 — 只搬目标缺失的文件,已有数据(可能已在使用中)永不覆盖。</summary>
    static void MigrateFromAppData()
    {
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopZones");
        if (!Directory.Exists(legacy)) return;
        // 根目录是 AppData 根的镜像(Root == legacy 时无需搬,保险防御)。
        if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(Root),
                StringComparison.OrdinalIgnoreCase)) return;

        CopyFileIfMissing(Path.Combine(legacy, "config.json"), Path.Combine(Root, "config.json"));
        CopyDirIfMissing(Path.Combine(legacy, "Notes"), Path.Combine(Root, "Notes"));
        CopyDirIfMissing(Path.Combine(legacy, "Presets"), Path.Combine(Root, "Presets"));
        CopyDirIfMissing(Path.Combine(legacy, "lang"), Path.Combine(Root, "lang"));
    }

    static void CopyFileIfMissing(string src, string dst)
    {
        try
        {
            if (!File.Exists(src) || File.Exists(dst)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: false);
        }
        catch { }
    }

    static void CopyDirIfMissing(string srcDir, string dstDir)
    {
        try
        {
            if (!Directory.Exists(srcDir)) return;
            Directory.CreateDirectory(dstDir);
            foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcDir, file);
                var dst = Path.Combine(dstDir, rel);
                if (File.Exists(dst)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: false);
            }
        }
        catch { }
    }
}
