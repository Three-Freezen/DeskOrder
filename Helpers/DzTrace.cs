using System;
using System.IO;

namespace DesktopZones.Helpers;

/// <summary>
/// ponytail 2026-08-26: lightweight file trace for hover-expand show/hide diagnostics.
/// Writes to D:\BS\dz_trace.log (fixed path, like he_debug.log) so reproductions can
/// be grepped without absolute paths. All calls are try/catch-wrapped: tracing must
/// never affect app behaviour.
/// </summary>
public static class DzTrace
{
    static readonly object _lock = new();

    public static void Log(string msg)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(@"D:\BS\dz_trace.log",
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
            }
        }
        catch { /* tracing must never throw */ }
    }

    /// <summary>Clear the log once per session start.</summary>
    public static void Reset()
    {
        try { lock (_lock) File.WriteAllText(@"D:\BS\dz_trace.log", ""); } catch { }
    }
}
