using System;
using System.Runtime.InteropServices;

namespace DesktopZones.Helpers;

/// <summary>
/// 当前进程的完整性级别（Mandatory Integrity Level）。
///
/// 为什么需要它：这个程序被从**受限/沙箱环境**（低完整性令牌、AppContainer）启动时，
/// 表面上一切正常，但三件事会静默失败：
/// 1. <c>Shell_NotifyIcon</c> 被 shell 拒绝（托盘图标不出来）；
/// 2. 写入 <c>%APPDATA%</c> / <c>%LOCALAPPDATA%</c> 被拒（配置存不下来 → "重启后组件全没了"）；
/// 3. 部分 COM 激活（CoreAudio 会话枚举）拿不到接口（音频识别失效）。
///
/// 这三种现象混在一起极易被误判成"程序 BUG"。启动时把级别写进日志，
/// 并在低完整性下给出明确提示，比事后猜半天有用。
/// </summary>
public static class ProcessIntegrity
{
    public enum Level
    {
        Untrusted = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        System = 4,
        Unknown = -1,
    }

    private static readonly Lazy<Level> _current = new(ReadLevel);
    private static readonly Lazy<bool> _appContainer = new(ReadAppContainer);

    /// <summary>当前进程的完整性级别。</summary>
    public static Level Current => _current.Value;

    /// <summary>是否跑在 AppContainer（MSIX / 沙箱）里。</summary>
    public static bool IsAppContainer => _appContainer.Value;

    /// <summary>低完整性或更低 —— 托盘图标 / 外部路径写入 / 部分 COM 都会失败。</summary>
    public static bool IsRestricted => Current is Level.Untrusted or Level.Low;

    public static string Describe => Current switch
    {
        Level.Untrusted => "Untrusted",
        Level.Low => "Low",
        Level.Medium => "Medium",
        Level.High => "High",
        Level.System => "System",
        _ => "Unknown",
    };

    /// <summary>给日志用的一行说明：正常时为空串。</summary>
    public static string RestrictionHint => IsRestricted
        ? "以低完整性(沙箱)运行：托盘图标 / %APPDATA% 写入 / 音频会话枚举都会失败。" +
          "请从资源管理器双击 exe、或用自己的终端启动，不要经沙箱/托管进程启动"
        : "";

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int TokenIsAppContainer = 29;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int informationClass, IntPtr information, int informationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static Level ReadLevel()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr info = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return Level.Unknown;

            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return Level.Unknown;

            info = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, TokenIntegrityLevel, info, needed, out _)) return Level.Unknown;

            // TOKEN_MANDATORY_LABEL = { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
            IntPtr sid = Marshal.ReadIntPtr(info);
            if (sid == IntPtr.Zero) return Level.Unknown;

            // SID 布局：Revision(1) SubAuthorityCount(1) IdentifierAuthority(6) SubAuthority[](4×N)
            int count = Marshal.ReadByte(sid, 1);
            if (count < 1) return Level.Unknown;
            int rid = Marshal.ReadInt32(sid, 8 + (count - 1) * 4);
            return rid switch
            {
                0x0000 => Level.Untrusted,
                0x1000 => Level.Low,
                0x2000 => Level.Medium,
                0x3000 => Level.High,
                0x4000 => Level.System,
                _ => Level.Unknown,
            };
        }
        catch
        {
            return Level.Unknown;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            if (info != IntPtr.Zero) LocalFree(info);
        }
    }

    private static bool ReadAppContainer()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr info = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return false;
            info = Marshal.AllocHGlobal(4);
            if (!GetTokenInformation(token, TokenIsAppContainer, info, 4, out _)) return false;
            return Marshal.ReadInt32(info) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            if (info != IntPtr.Zero) LocalFree(info);
        }
    }
}
