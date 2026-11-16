// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.Globalization;

namespace ExchangeAppPoolMonitor;

/// <summary>
/// Shared helpers for the app's own log files. The app keeps a complete record in its own folder,
/// independent of the Windows event log (which it also writes to, see Program.cs).
/// </summary>
/// <remarks>
/// Two kinds of daily file live in the log folder:
/// <list type="bullet">
///   <item><c>actions-yyyy-MM-dd.log</c>: the action log (recycles, triggers, emails), one JSON object per line.
///         Written by <see cref="ActionLog"/>. This is what the status page shows.</item>
///   <item><c>service-yyyy-MM-dd.log</c>: the service's own diagnostic log in plain text (startup, shutdown,
///         web server errors, anything unexpected). Written by <see cref="ServiceFileLoggerProvider"/>.</item>
/// </list>
/// Every file name ends in the date, which is what retention cleanup uses to decide what to delete.
/// </remarks>
public static class LogFiles
{
    public const string ActionPrefix = "actions";
    public const string ServicePrefix = "service";

    /// <summary>
    /// The log folder as a full path. Relative paths are relative to the exe folder, not the working
    /// directory, because a Windows service starts with its working directory set to System32.
    /// </summary>
    public static string Directory(string? configured)
    {
        var dir = string.IsNullOrWhiteSpace(configured) ? "logs" : configured;
        return Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
    }

    /// <summary>Full path of the file for a given prefix and day, for example logs\actions-2026-11-05.log.</summary>
    public static string PathFor(string dir, string prefix, DateOnly day) =>
        Path.Combine(dir, $"{prefix}-{day:yyyy-MM-dd}.log");

    /// <summary>
    /// Deletes this app's daily files (both kinds) older than <paramref name="keepDays"/> days.
    /// Does nothing when keepDays is 0 or less. Files it cannot delete are left for next time.
    /// Only files named prefix-yyyy-MM-dd.log are touched, so anything else in the folder is safe.
    /// </summary>
    public static void Cleanup(string dir, int keepDays)
    {
        if (keepDays <= 0 || !System.IO.Directory.Exists(dir)) return;
        var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-keepDays);

        foreach (var prefix in new[] { ActionPrefix, ServicePrefix })
        {
            foreach (var file in System.IO.Directory.GetFiles(dir, $"{prefix}-*.log"))
            {
                // The last 10 characters before ".log" are the date.
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length < 10) continue;
                if (!DateOnly.TryParseExact(name[^10..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    continue;
                if (day < cutoff)
                {
                    try { File.Delete(file); } catch { /* in use or locked; try again tomorrow */ }
                }
            }
        }
    }

    /// <summary>
    /// Last resort for a failure before or outside the normal logging, such as the service failing to
    /// start. Appends straight to today's service log and never throws.
    /// </summary>
    public static void WriteFatal(string? configuredDir, string message, Exception ex)
    {
        try
        {
            var dir = Directory(configuredDir);
            System.IO.Directory.CreateDirectory(dir);
            File.AppendAllText(PathFor(dir, ServicePrefix, DateOnly.FromDateTime(DateTime.Now)),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [Critical] {message}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // Nowhere left to write. Nothing more can be done.
        }
    }
}
