// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using Microsoft.Extensions.Options;

namespace ExchangeAppPoolMonitor;

/// <summary>
/// A minimal .NET logging provider that writes to the app's own daily file,
/// <c>logs\service-yyyy-MM-dd.log</c>. It runs alongside the Windows event log, so the app's own
/// files hold the full history even if the event log is cleared or rolls over.
/// </summary>
/// <remarks>
/// <para>
/// Everything that goes through the standard .NET <c>ILogger</c> ends up here: the service starting
/// and stopping, the web server (for example "address already in use"), unexpected errors, and a
/// plain text copy of each action log entry. That gives one complete timeline of what the service
/// did, which is useful when troubleshooting the service itself. The action log
/// (<c>actions-*.log</c>) stays the clean record of what was done to the app pools.
/// </para>
/// <para>
/// Which messages are written is controlled by the "Logging:LogLevel" section of
/// monitorsettings.json, like any other .NET logging provider.
/// </para>
/// <para>
/// Volume is low, so each line is appended directly under a lock. No buffering means nothing is
/// lost if the process is killed. A failed write is swallowed: logging must never take the service down.
/// </para>
/// </remarks>
[ProviderAlias("File")]
public sealed class ServiceFileLoggerProvider : ILoggerProvider
{
    private readonly IOptionsMonitor<MonitorSettings> _settings;
    private readonly object _lock = new();

    public ServiceFileLoggerProvider(IOptionsMonitor<MonitorSettings> settings) => _settings = settings;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    /// <summary>Formats and appends one line (plus the exception, if any) to today's service log.</summary>
    private void Write(LogLevel level, string category, string message, Exception? ex)
    {
        // Shorten "ExchangeAppPoolMonitor.MonitorWorker" to "MonitorWorker" to keep lines readable.
        var shortCategory = category.StartsWith("ExchangeAppPoolMonitor.", StringComparison.Ordinal)
            ? category["ExchangeAppPoolMonitor.".Length..]
            : category;
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {shortCategory}: {message}";
        if (ex is not null) line += Environment.NewLine + ex;

        lock (_lock)
        {
            try
            {
                var dir = LogFiles.Directory(_settings.CurrentValue.LogDirectory);
                Directory.CreateDirectory(dir);
                File.AppendAllText(LogFiles.PathFor(dir, LogFiles.ServicePrefix, DateOnly.FromDateTime(DateTime.Now)),
                    line + Environment.NewLine);
            }
            catch
            {
                // Disk full or folder not writable. Do not throw from logging.
            }
        }
    }

    /// <summary>Returned by BeginScope. Disposing it does nothing.</summary>
    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }

    /// <summary>The per-category logger handed out by the provider. All writes go back to the provider.</summary>
    private sealed class FileLogger : ILogger
    {
        private readonly ServiceFileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(ServiceFileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        // Scopes are not used by this app, so hand back a do-nothing scope.
        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        // Level filtering from monitorsettings.json is applied by the logging framework before this
        // is called, so this only has to rule out "None".
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _owner.Write(logLevel, _category, formatter(state, exception), exception);
        }
    }
}
