using System;
using System.Text.RegularExpressions;

namespace DesktopZones.Helpers;

/// <summary>
/// Parses user-friendly date/time strings into DateTime/TimeSpan.
/// Supports fuzzy input like "7-22", "2026.7.22", "1430", "2:30pm", etc.
/// </summary>
public static partial class FuzzyDateTimeParser
{
    /// <summary>
    /// Parse a fuzzy date string. Returns null if unparseable.
    /// Examples: "2026-7-22", "7.22", "7/22", "7月22日", "722", "0722"
    /// </summary>
    public static DateTime? ParseDate(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // Remove Chinese characters like 月, 日, 年, 号
        var s = Regex.Replace(input.Trim(), @"[年月日号]", " ").Trim();
        // Replace common separators with space
        s = Regex.Replace(s, @"[-./\\_~]+", " ").Trim();
        // Collapse multiple spaces
        s = Regex.Replace(s, @"\s+", " ").Trim();

        // Extract all number segments
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int year = DateTime.Now.Year;
        int month = 0, day = 0;

        if (parts.Length == 3)
        {
            // 年 月 日
            if (int.TryParse(parts[0], out var y) && y > 99) year = y;
            if (int.TryParse(parts[1], out month) && int.TryParse(parts[2], out day)) { }
            else return null;
        }
        else if (parts.Length == 2)
        {
            // 月 日
            if (int.TryParse(parts[0], out month) && int.TryParse(parts[1], out day)) { }
            else return null;
        }
        else if (parts.Length == 1)
        {
            var digits = parts[0];
            if (digits.Length == 8)
            {
                // YYYYMMDD
                if (int.TryParse(digits[..4], out var y)) year = y;
                if (int.TryParse(digits[4..6], out month) && int.TryParse(digits[6..8], out day)) { }
                else return null;
            }
            else if (digits.Length == 6)
            {
                // YYMMDD or MMDDMM — assume YYMMDD
                if (int.TryParse(digits[..2], out var yy)) year = 2000 + yy;
                if (int.TryParse(digits[2..4], out month) && int.TryParse(digits[4..6], out day)) { }
                else return null;
            }
            else if (digits.Length == 4)
            {
                // MMDD
                if (int.TryParse(digits[..2], out month) && int.TryParse(digits[2..4], out day)) { }
                else return null;
            }
            else if (digits.Length == 3)
            {
                // MDD
                if (int.TryParse(digits[..1], out month) && int.TryParse(digits[1..3], out day)) { }
                else return null;
            }
            else if (digits.Length == 2)
            {
                // MM only → assume current month, day=1
                if (int.TryParse(digits, out month)) day = 1;
                else return null;
            }
            else if (digits.Length == 1)
            {
                // M only
                if (int.TryParse(digits, out month)) day = 1;
                else return null;
            }
            else return null;
        }
        else return null;

        // Validate
        if (month < 1 || month > 12 || day < 1 || day > 31) return null;
        if (year < 2000 || year > 2099) return null;

        try { return new DateTime(year, month, day); }
        catch { return null; }
    }

    /// <summary>
    /// Parse a fuzzy time string. Returns null if unparseable.
    /// Examples: "14:30", "14.30", "2:30pm", "1430", "230"
    /// </summary>
    public static TimeSpan? ParseTime(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var s = input.Trim().ToLowerInvariant();
        bool isPM = s.Contains("pm");
        bool isAM = s.Contains("am");

        // Remove am/pm markers and common separators
        s = Regex.Replace(s, @"[ap]m?", "").Trim();
        s = Regex.Replace(s, @"[:.;h]\s*", " ").Trim();
        s = Regex.Replace(s, @"[-./\\_~]+", " ").Trim();
        s = Regex.Replace(s, @"\s+", " ").Trim();

        // Extract all number segments
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int hour = 0, minute = 0;

        if (parts.Length == 2)
        {
            // 时 分
            if (int.TryParse(parts[0], out hour) && int.TryParse(parts[1], out minute)) { }
            else return null;
        }
        else if (parts.Length == 1)
        {
            var digits = parts[0];
            if (digits.Length == 4)
            {
                // HHMM
                if (int.TryParse(digits[..2], out hour) && int.TryParse(digits[2..4], out minute)) { }
                else return null;
            }
            else if (digits.Length == 3)
            {
                // HMM
                if (int.TryParse(digits[..1], out hour) && int.TryParse(digits[1..3], out minute)) { }
                else return null;
            }
            else if (digits.Length == 2)
            {
                // HH only → assume :00
                if (int.TryParse(digits, out hour)) minute = 0;
                else return null;
            }
            else if (digits.Length == 1)
            {
                // H only
                if (int.TryParse(digits, out hour)) minute = 0;
                else return null;
            }
            else return null;
        }
        else return null;

        // Apply AM/PM
        if (isPM && hour < 12) hour += 12;
        if (isAM && hour == 12) hour = 0;

        // Validate
        if (hour < 0 || hour > 23 || minute < 0 || minute > 59) return null;

        return new TimeSpan(hour, minute, 0);
    }

    /// <summary>
    /// Format a DateTime for display in the date TextBox (yyyy-MM-dd).
    /// </summary>
    public static string FormatDate(DateTime dt) => dt.ToString("yyyy-MM-dd");

    /// <summary>
    /// Format a TimeSpan for display in the time TextBox (HH:mm).
    /// </summary>
    public static string FormatTime(TimeSpan ts) => ts.ToString(@"hh\:mm");
}
