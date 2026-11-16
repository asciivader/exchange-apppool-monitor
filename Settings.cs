// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

namespace ExchangeAppPoolMonitor;

// These classes are filled in from monitorsettings.json by the .NET configuration system.
// Property names match the JSON keys exactly. The values after "=" are the defaults used when a
// key is missing from the file. The service reads them through IOptionsMonitor, which reloads
// them whenever the file is saved, so most changes take effect on the next poll without a restart.
//
// Note: list defaults are deliberately empty. The configuration binder ADDS JSON list items to
// whatever a list already contains, so a non-empty default list would end up with duplicates.

/// <summary>Bound from the "Monitor" section of monitorsettings.json.</summary>
public sealed class MonitorSettings
{
    /// <summary>Pools watched when the AppPools list in the config is empty.</summary>
    public static readonly string[] DefaultPools = { "MSExchangeAutodiscoverAppPool", "MSExchangeDefaultAppPool" };

    /// <summary>
    /// Seconds between checks. Minimum 2. Short by default because the point is to catch a backlog
    /// early; one check costs a counter read per pool plus a few quick appcmd calls.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>Recycle when the request queue is deeper than this. A pool can override it.</summary>
    public int QueueThreshold { get; set; } = 100;

    /// <summary>
    /// How many polls in a row must be over the threshold before recycling. 1 = act on the first one.
    /// With the default 10 second interval, 2 means the backlog has lasted at least about 10 to 20 seconds.
    /// </summary>
    public int ConsecutiveSamplesBeforeRecycle { get; set; } = 2;

    /// <summary>Minimum minutes between recycles of the same pool, so a stuck queue cannot cause a recycle loop.</summary>
    public int RecycleCooldownMinutes { get; set; } = 10;

    /// <summary>The pools to watch. Leave empty to use the two default Exchange pools.</summary>
    public List<AppPoolSettings> AppPools { get; set; } = new();

    /// <summary>Folder for the app's own log files (action and service logs). Relative paths are relative to the application folder.</summary>
    public string LogDirectory { get; set; } = "logs";

    /// <summary>Daily log files older than this are deleted. 0 keeps them forever.</summary>
    public int LogRetentionDays { get; set; } = 90;

    /// <summary>URL the status page listens on. Read at startup only.</summary>
    public string StatusPageUrl { get; set; } = "http://localhost:8085";

    /// <summary>When true, log and email what would happen but do not actually recycle.</summary>
    public bool DryRun { get; set; }

    /// <summary>Recycle when too many requests have been running too long inside a pool's worker process.</summary>
    public LongRunningRequestSettings LongRunningRequests { get; set; } = new();

    /// <summary>Recycle on ASP.NET Event ID 1309 errors in the Application event log.</summary>
    public Event1309Settings Event1309 { get; set; } = new();

    /// <summary>The pools to actually watch: the configured list (skipping blank names), or the defaults if it is empty.</summary>
    public IReadOnlyList<AppPoolSettings> EffectivePools() =>
        AppPools.Count > 0
            ? AppPools.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList()
            : DefaultPools.Select(n => new AppPoolSettings { Name = n }).ToList();

    /// <summary>The queue threshold for a pool: its own override if set, otherwise the global one.</summary>
    public int ThresholdFor(AppPoolSettings pool) => pool.QueueThreshold ?? QueueThreshold;

    /// <summary>The long running request threshold for a pool: its own override if set, otherwise the global one.</summary>
    public int LongRunningThresholdFor(AppPoolSettings pool) => pool.LongRunningThreshold ?? LongRunningRequests.Threshold;
}

/// <summary>One entry in Monitor:AppPools.</summary>
public sealed class AppPoolSettings
{
    /// <summary>The IIS app pool name, exactly as shown in IIS Manager (case does not matter).</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional per-pool threshold. Falls back to MonitorSettings.QueueThreshold.</summary>
    public int? QueueThreshold { get; set; }

    /// <summary>Optional per-pool long running request threshold. Falls back to LongRunningRequests.Threshold.</summary>
    public int? LongRunningThreshold { get; set; }

    /// <summary>
    /// Fallback for assigning a 1309 event to this pool when its worker process ID cannot be matched
    /// (the process already exited). Case insensitive text found in the event, such as an application
    /// domain fragment like "/LM/W3SVC/2/ROOT/Autodiscover".
    /// </summary>
    public List<string> EventMatches { get; set; } = new();
}

/// <summary>
/// Bound from Monitor:LongRunningRequests. Controls the long running request trigger, the same
/// measure as running <c>appcmd list requests /elapsed:30000</c> by hand.
/// </summary>
public sealed class LongRunningRequestSettings
{
    /// <summary>Turns this trigger on or off. The other triggers are not affected.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A request counts once it has been running at least this many seconds.</summary>
    public int OlderThanSeconds { get; set; } = 30;

    /// <summary>
    /// Recycle when MORE than this many requests are that old, for ConsecutiveSamplesBeforeRecycle
    /// checks in a row. A pool can override it.
    /// </summary>
    public int Threshold { get; set; } = 50;
}

/// <summary>Bound from Monitor:Event1309. Controls the ASP.NET 1309 error trigger.</summary>
public sealed class Event1309Settings
{
    /// <summary>Turns the 1309 trigger on or off. The queue depth trigger is not affected.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds between checks of the Application log for new 1309 errors. Minimum 2. Independent of
    /// PollIntervalSeconds, so the event log can be checked more or less often than the queues.
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 10;

    /// <summary>Recycle a pool when it logs at least this many 1309 errors...</summary>
    public int ErrorCount { get; set; } = 1;

    /// <summary>...within this many minutes.</summary>
    public int WindowMinutes { get; set; } = 10;
}

/// <summary>Bound from the "Email" section of monitorsettings.json.</summary>
public sealed class EmailSettings
{
    /// <summary>Turns notifications off without removing the rest of the settings.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>SMTP server host name or IP. "localhost" uses the Exchange server's own transport service.</summary>
    public string SmtpServer { get; set; } = "localhost";

    /// <summary>25 for an internal relay, 587 for authenticated submission.</summary>
    public int Port { get; set; } = 25;

    /// <summary>Use STARTTLS. Usually true with port 587, false for an internal relay on 25.</summary>
    public bool EnableSsl { get; set; }

    /// <summary>Leave empty for an anonymous relay.</summary>
    public string Username { get; set; } = "";
    /// <summary>Stored in plain text in the JSON file. Lock the folder down if you use it.</summary>
    public string Password { get; set; } = "";

    /// <summary>Sender address. Must be one the SMTP server will accept or relay for.</summary>
    public string From { get; set; } = "";

    /// <summary>Recipients. Every notification goes to all of them.</summary>
    public List<string> To { get; set; } = new();

    /// <summary>Put at the start of every subject line, to make inbox rules easy.</summary>
    public string SubjectPrefix { get; set; } = "[AppPool Monitor]";
}
