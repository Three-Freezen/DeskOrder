using System;
using System.Diagnostics;
using System.IO;

namespace DesktopZones.Helpers;

/// <summary>
/// ponytail 2026-08-26: lightweight file trace for hover-expand show/hide diagnostics.
/// 2026-08-28 perf pass: [Conditional("DEBUG")] 让 Release 构建把调用点连同字符串插值
/// 参数一起编译掉(探针在 DEBUG 跑，行为不变)；文件句柄常驻，替代原先每次 Log 都
/// AppendAllText 的打开→写→关(UI 线程同步磁盘 IO)；路径从开发机硬编码 D:\BS\ 移到
/// %LOCALAPPDATA%\DeskOrder\logs\，并带 16MB 单文件上限。所有调用 try/catch 包裹：
/// tracing must never affect app behaviour.
/// </summary>
public static class DzTrace
{
    static readonly object _lock = new();
    static StreamWriter? _writer;
    static bool _createBroken;

    // ponytail 2026-08-29: 落点随 DataLocator(标准 %LOCALAPPDATA%\DeskOrder\logs /
    // 便携 安装目录 Data\logs)。
    static string TraceDir => Services.DataLocator.LogsRoot;

    static string TracePath => Path.Combine(TraceDir, "dz_trace.log");

    [Conditional("DEBUG")]
    public static void Log(string msg)
    {
        try
        {
            lock (_lock)
            {
                if (_createBroken) return;
                if (_writer == null)
                {
                    Directory.CreateDirectory(TraceDir);
                    // 超过 16MB 就从本次会话重新开始，避免跨会话无限膨胀。
                    var fresh = File.Exists(TracePath) && new FileInfo(TracePath).Length > 16 * 1024 * 1024;
                    _writer = new StreamWriter(TracePath, append: !fresh) { AutoFlush = true };
                }
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
            }
        }
        catch (IOException)
        {
            // 排查者正用独占方式读日志等瞬时占用 — 丢弃本条，下次调用重建句柄重试。
            _writer?.Dispose();
            _writer = null;
        }
        catch
        {
            // 建句柄级失败(路径/权限) — 熔断，本会话不再尝试。
            _createBroken = true;
        }
    }

    /// <summary>Clear the log once per session start.</summary>
    [Conditional("DEBUG")]
    public static void Reset()
    {
        try
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
                Directory.CreateDirectory(TraceDir);
                File.WriteAllText(TracePath, "");
            }
        }
        catch
        {
            // tracing must never throw
        }
    }
}
