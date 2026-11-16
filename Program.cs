// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

// Entry point. Wires the pieces together and starts two things in one process:
//   1. MonitorWorker, the background loop that checks the pools and recycles them, and
//   2. a small Kestrel web server for the status page.
// The same exe runs as a Windows service (installed with sc.exe) or from a console for testing.
// WindowsServiceLifetime handles the service case; from a console it runs until Ctrl+C.
//
// Logging: the app keeps its own log files in the "logs" folder (see LogFiles.cs for the two kinds
// of file) and also writes to the Windows Application event log, so entries show in Event Viewer.

using ExchangeAppPoolMonitor;
using Microsoft.Extensions.Options;

// Installer helper mode: "--export-settings" / "--import-settings" convert between the installer's
// answers and monitorsettings.json, then exit without starting the service. See SettingsCli.cs.
if (SettingsCli.TryRun(args, out var settingsExitCode)) return settingsExitCode;

// Remembered so a startup failure can be written to the configured log folder, once it is known.
string? logDirectory = null;

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        // A Windows service starts in C:\Windows\System32, so anchor everything to the exe folder.
        ContentRootPath = AppContext.BaseDirectory,
    });

    // The settings file is required (optional: false), so a missing file stops the service at startup
    // with a clear error instead of running silently on defaults. reloadOnChange picks up edits live.
    builder.Configuration.AddJsonFile("monitorsettings.json", optional: false, reloadOnChange: true);
    logDirectory = builder.Configuration["Monitor:LogDirectory"];

    // When started by the Service Control Manager, report start and stop to Windows (see
    // WindowsServiceLifetime.cs). When started from a console, .NET's default lifetime is kept:
    // the app runs until Ctrl+C.
    if (WindowsServiceLifetime.IsRunningAsService)
        builder.Services.AddSingleton<IHostLifetime, WindowsServiceLifetime>();

    // Set the logging destinations explicitly rather than relying on the .NET defaults.
    // The destinations:
    //   - the console, only seen when run interactively for testing;
    //   - the Windows Application event log, source "ExchangeAppPoolMonitor" (Event Viewer). Which
    //     levels go there is set by Logging:EventLog:LogLevel in monitorsettings.json; set its
    //     Default to "None" to turn it off;
    //   - the app's own service log file (ServiceFileLoggerProvider).
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");
    if (OperatingSystem.IsWindows()) // lets the app still start for a quick test on a non-Windows dev box
    {
        builder.Logging.AddEventLog(o =>
        {
            o.LogName = "Application";
            o.SourceName = "ExchangeAppPoolMonitor";
        });
    }
    builder.Services.AddSingleton<ILoggerProvider, ServiceFileLoggerProvider>();
    builder.Services.Configure<MonitorSettings>(builder.Configuration.GetSection("Monitor"));
    builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
    // Singletons: one instance shared by the monitor loop and the web page for the life of the process.
    builder.Services.AddSingleton<MonitorState>();
    builder.Services.AddSingleton<ActionLog>();
    builder.Services.AddSingleton<EmailNotifier>();
    builder.Services.AddHostedService<MonitorWorker>();

    // Where the status page listens. Read once here, so changing it needs a service restart.
    builder.WebHost.UseUrls(builder.Configuration["Monitor:StatusPageUrl"] ?? new MonitorSettings().StatusPageUrl);

    var app = builder.Build();

    // The HTML status page.
    app.MapGet("/", (MonitorState state, ActionLog log, IOptionsMonitor<MonitorSettings> s, IOptionsMonitor<EmailSettings> e) =>
        Results.Content(StatusPage.Render(state, log, s.CurrentValue, e.CurrentValue), "text/html; charset=utf-8"));

    // The same information as JSON, for scripts or another monitoring system.
    app.MapGet("/api/status", (MonitorState state, ActionLog log, IOptionsMonitor<MonitorSettings> s) => new
{
        Server = Environment.MachineName,
        state.MonitorStatus,
        state.StartedAt,
        state.LastPoll,
        state.LastError,
        s.CurrentValue.DryRun,
        Pools = s.CurrentValue.EffectivePools().Select(p => (object?)state.GetPool(p.Name) ?? new { p.Name, State = "Pending" }),
        Log = log.Recent(),
    });

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // The host failed to start or crashed (for example the status page port is already in use, or
    // monitorsettings.json is missing or invalid). Normal logging may not be running yet, so write
    // straight to the service log, then rethrow so Windows marks the service as failed.
    LogFiles.WriteFatal(logDirectory, "Service failed to start or stopped unexpectedly", ex);
    throw;
}
