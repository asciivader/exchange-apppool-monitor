// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ExchangeAppPoolMonitor;

/// <summary>One line of the action log.</summary>
/// <param name="Timestamp">Local server time, with offset.</param>
/// <param name="Level">Info (routine), Action (a recycle or dry run), Warning (needs a look), or Error (something failed).</param>
/// <param name="AppPool">The pool the entry is about, or null for service level entries.</param>
/// <param name="Message">Human readable description.</param>
public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string? AppPool, string Message);

/// <summary>
/// Action log: the record of what the service did to the app pools. Writes one JSON object per line
/// to logs\actions-yyyy-MM-dd.log, keeps the most recent entries in memory for the status page, and
/// passes a plain text copy to the normal ILogger (the console when run interactively, and the
/// service log file, see <see cref="ServiceFileLoggerProvider"/>, and the Windows Application event log).
/// </summary>
/// <remarks>
/// <para>
/// One file per day keeps each file small and makes old ones easy to delete. JSON lines (one object
/// per line) is easy to read in a text editor and easy to load into PowerShell:
/// <c>Get-Content .\logs\actions-2026-11-05.log | ConvertFrom-Json</c>.
/// </para>
/// <para>
/// Thread safe: the monitor loop and the email notifier write, the web page reads, all under one lock.
/// The volume is tiny (a few lines per incident), so a simple lock and a synchronous file append
/// are plenty. Writing the file never throws to the caller; if the disk is full or the folder is
/// not writable, the problem goes to the service log and the in-memory list still updates.
/// </para>
/// </remarks>
public sealed class ActionLog
{
    /// <summary>How many entries the status page shows, and how many are kept in memory.</summary>
    public const int RecentCount = 25;

    private readonly IOptionsMonitor<MonitorSettings> _settings;
    private readonly ILogger<ActionLog> _logger;
    /// <summary>Most recent entries, newest first. Never longer than <see cref="RecentCount"/>.</summary>
    private readonly LinkedList<LogEntry> _recent = new();
    private readonly object _lock = new();
    /// <summary>The day old files were last cleaned up, so cleanup runs at most once a day.</summary>
    private DateOnly _lastCleanup;

    public ActionLog(IOptionsMonitor<MonitorSettings> settings, ILogger<ActionLog> logger)
    {
        _settings = settings;
        _logger = logger;
        // Fill the in-memory list from the files on disk, so the status page still shows
        // history straight after a service restart.
        LoadRecent();
    }

    // Convenience methods, one per level.

    public void Info(string message, string? pool = null) => Write("Info", pool, message);
    public void Action(string message, string? pool = null) => Write("Action", pool, message);
    public void Warning(string message, string? pool = null) => Write("Warning", pool, message);
    public void Error(string message, string? pool = null) => Write("Error", pool, message);

    /// <summary>Newest first.</summary>
    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_lock) return _recent.ToList();
    }

    /// <summary>Records one entry: ILogger copy, in-memory list, then today's actions file.</summary>
    private void Write(string level, string? pool, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, pool, message);
        var text = pool is null ? message : $"{pool}: {message}";
        switch (level)
        {
            case "Error": _logger.LogError("{Message}", text); break;
            case "Warning": _logger.LogWarning("{Message}", text); break;
            default: _logger.LogInformation("{Message}", text); break;
        }

        lock (_lock)
        {
            _recent.AddFirst(entry);
            while (_recent.Count > RecentCount) _recent.RemoveLast();

            try
            {
                var dir = LogDirectory();
                Directory.CreateDirectory(dir);
                File.AppendAllText(LogFiles.PathFor(dir, LogFiles.ActionPrefix, DateOnly.FromDateTime(entry.Timestamp.LocalDateTime)),
                    JsonSerializer.Serialize(entry) + Environment.NewLine);
                CleanupOldFiles(dir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not write to the action log file");
            }
        }
    }

    /// <summary>Reads the newest <see cref="RecentCount"/> entries from the existing log files.</summary>
    private void LoadRecent()
    {
        try
        {
            var dir = LogDirectory();
            if (!Directory.Exists(dir)) return;

            // File names sort by date, so walk newest first until we have enough entries.
            foreach (var file in Directory.GetFiles(dir, $"{LogFiles.ActionPrefix}-*.log").OrderByDescending(f => f))
            {
                foreach (var line in File.ReadAllLines(file).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<LogEntry>(line);
                        if (entry is not null) _recent.AddLast(entry);
                    }
                    catch (JsonException) { /* skip a damaged line */ }
                    if (_recent.Count >= RecentCount) return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read existing action log files");
        }
    }

    /// <summary>
    /// Deletes daily files (action and service logs) older than LogRetentionDays.
    /// Runs at most once per day, triggered by the first entry written that day.
    /// </summary>
    private void CleanupOldFiles(string dir)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_lastCleanup == today) return;
        _lastCleanup = today;
        LogFiles.Cleanup(dir, _settings.CurrentValue.LogRetentionDays);
    }

    private string LogDirectory() => LogFiles.Directory(_settings.CurrentValue.LogDirectory);
}
