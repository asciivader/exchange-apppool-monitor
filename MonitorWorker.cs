// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using Microsoft.Extensions.Options;

namespace ExchangeAppPoolMonitor;

/// <summary>
/// The monitor loop. This is the part of the service that actually makes decisions.
/// </summary>
/// <remarks>
/// <para>
/// The goal is to catch a pool while it is starting to back up and recycle it within seconds,
/// before the server reaches the state where it has to go into maintenance mode and be rebooted.
/// So the loop runs often (default every 10 seconds) and the time-critical work comes first.
/// </para>
/// <para>
/// Every poll it does the following, in order:
/// </para>
/// <list type="number">
///   <item>For each watched pool:
///     <list type="bullet">
///       <item>read its HTTP.sys request queue depth (a fast performance counter read, no external process);</item>
///       <item>count its long running requests (<c>appcmd list requests /elapsed:...</c>);</item>
///       <item>decide whether it needs a recycle: the queue or the long running count has been over its
///             threshold for enough polls in a row, or the pool has logged enough 1309 errors recently;</item>
///       <item>if so, and the pool is not in its cooldown period, recycle it, write the action log, and email;</item>
///       <item>only then read its state with appcmd (for the status page), and publish its status.</item>
///     </list>
///   </item>
///   <item>If the 1309 trigger is on: refresh the PID to app pool map with <c>appcmd list wp</c>,
///         read any new ASP.NET 1309 events from the Application log, and assign each one to a pool.
///         This runs on its own interval (Event1309.CheckIntervalSeconds). Errors it finds are acted
///         on at the next pool check, at most PollIntervalSeconds later.</item>
/// </list>
/// <para>
/// Why this order: appcmd talks to IIS, and on a struggling server IIS can be slow to answer. The
/// queue counter is read first because nothing can delay it. The long running request count needs
/// appcmd, but it is a trigger, so it is read before deciding. The pool state is only for display,
/// so it is read last, after any recycle.
/// </para>
/// <para>
/// Error handling rule: nothing that goes wrong for one pool, or for the event log, is allowed to
/// stop the checks for the other pools or end the loop. Each failure is caught, shown on the status
/// page, and written to the action log once (repeats of the same error are not logged again, so a
/// persistent problem does not flood the log with one line every few seconds).
/// </para>
/// <para>
/// This class runs on a single background thread, so its private fields need no locking. The only
/// data shared with the web page goes through <see cref="MonitorState"/> and <see cref="ActionLog"/>,
/// which are thread safe.
/// </para>
/// </remarks>
public sealed class MonitorWorker : BackgroundService
{
    private readonly IOptionsMonitor<MonitorSettings> _settings;
    private readonly MonitorState _state;
    private readonly ActionLog _log;
    private readonly EmailNotifier _email;

    // These two are created here rather than injected because they hold per-loop state
    // (cached perf counter handles, and the position reached in the event log).
    private readonly RequestQueueReader _queues = new();
    private readonly AspNetErrorReader _aspNetErrors = new();

    /// <summary>Per-pool decision state, keyed by app pool name (case insensitive, like IIS).</summary>
    private readonly Dictionary<string, Tracking> _tracking = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Worker process ID to app pool name, as reported by <c>appcmd list wp</c>.
    /// Entries are kept for an hour after they were last seen, because a 1309 event is often
    /// written by a w3wp.exe that has crashed or been recycled by the time we read the event.
    /// Remembering recent PIDs lets us still attribute that event to the right pool.
    /// </summary>
    private readonly Dictionary<int, (string Pool, DateTimeOffset Seen)> _pidToPool = new();

    /// <summary>How long a PID stays in <see cref="_pidToPool"/> after appcmd last listed it.</summary>
    private static readonly TimeSpan PidMemory = TimeSpan.FromHours(1);

    // The last error message logged for the main loop and for the 1309 check, used to log each
    // distinct problem only once instead of on every poll.
    private string? _lastLoopError;
    private string? _last1309Error;

    // When each kind of check last ran. The loop wakes up often enough for whichever interval is
    // shorter, and each check only runs when its own interval has passed.
    private DateTimeOffset _lastPoolCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _last1309Check = DateTimeOffset.MinValue;

    /// <summary>Everything the loop needs to remember about one pool between polls.</summary>
    private sealed class Tracking
    {
        /// <summary>How many polls in a row the queue has been over the threshold. Reset to 0 when it drops.</summary>
        public int ConsecutiveOver;

        /// <summary>How many polls in a row the long running request count has been over its threshold.</summary>
        public int LongRunningConsecutiveOver;

        /// <summary>When we last recycled (or tried to recycle) this pool. Drives the cooldown.</summary>
        public DateTimeOffset? LastRecycled;

        /// <summary>Successful recycles since the service started. Shown on the status page.</summary>
        public int RecycleCount;

        /// <summary>
        /// True once we have logged "trigger hit but in cooldown" for the current problem,
        /// so that message appears once per incident rather than once per poll.
        /// </summary>
        public bool CooldownNoted;

        /// <summary>The last state or queue read error logged for this pool (log each distinct error once).</summary>
        public string? LastErrorLogged;

        /// <summary>Timestamps of 1309 errors for this pool that are still inside the counting window.</summary>
        public readonly List<DateTimeOffset> AspNetErrors = new();

        /// <summary>Time of the most recent 1309 error for this pool. Shown on the status page.</summary>
        public DateTimeOffset? LastAspNetError;
    }

    public MonitorWorker(IOptionsMonitor<MonitorSettings> settings, MonitorState state, ActionLog log,
        EmailNotifier email)
    {
        _settings = settings;
        _state = state;
        _log = log;
        _email = email;
    }

    /// <summary>
    /// Called once by the host when the service starts. Runs until the service is stopped,
    /// at which point <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var start = _settings.CurrentValue;
        _log.Info($"Monitor started. Watching {string.Join(", ", start.EffectivePools().Select(p => p.Name))}." +
                  (start.DryRun ? " DRY RUN mode: pools will not actually be recycled." : ""));
        _state.MonitorStatus = "Running";

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Read the settings fresh on every poll. IOptionsMonitor reloads them when
                // monitorsettings.json is saved, so threshold changes apply without a restart.
                var s = _settings.CurrentValue;
                try
                {
                    await PollAsync(s, stoppingToken);
                    _state.LastError = null;
                    _lastLoopError = null;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Safety net. PollAsync already handles expected failures per pool, so reaching
                    // here means something unexpected; record it and keep looping.
                    _state.LastError = ex.Message;
                    if (ex.Message != _lastLoopError) _log.Error($"Poll failed: {ex.Message}");
                    _lastLoopError = ex.Message;
                }
                _state.LastPoll = DateTimeOffset.Now;

                // Sleep until the next check of either kind is due, so each runs on its own interval
                // (for example queues every 4 seconds and 1309s every 10, not every 12). Intervals have
                // a floor of 2 seconds, so a typo like 0 cannot turn this into a busy loop.
                var nextDue = _lastPoolCheck + TimeSpan.FromSeconds(Math.Max(2, s.PollIntervalSeconds));
                if (s.Event1309.Enabled)
                {
                    var next1309 = _last1309Check + TimeSpan.FromSeconds(Math.Max(2, s.Event1309.CheckIntervalSeconds));
                    if (next1309 < nextDue) nextDue = next1309;
                }
                var wait = nextDue - DateTimeOffset.Now;
                if (wait < TimeSpan.FromMilliseconds(200)) wait = TimeSpan.FromMilliseconds(200);
                await Task.Delay(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the service is stopping and Task.Delay (or appcmd) was cancelled.
        }
        finally
        {
            _state.MonitorStatus = "Stopped";
            _log.Info("Monitor stopped.");
            _queues.Dispose();
        }
    }

    /// <summary>
    /// One pass of the loop: check and act on each pool if PollIntervalSeconds has passed, then
    /// collect new 1309 errors if Event1309.CheckIntervalSeconds has passed.
    /// </summary>
    private async Task PollAsync(MonitorSettings s, CancellationToken ct)
    {
        var pools = s.EffectivePools();
        var now = DateTimeOffset.Now;
        // A little slack, because Task.Delay can wake a few milliseconds early.
        static bool Due(DateTimeOffset last, DateTimeOffset now, int seconds) =>
            now - last >= TimeSpan.FromSeconds(Math.Max(2, seconds) - 0.1);

        if (Due(_lastPoolCheck, now, s.PollIntervalSeconds))
        {
            _lastPoolCheck = now;
            // Pools are checked one after another, not in parallel. There are only two, and doing them
            // in sequence means two recycles can never be started at the same instant.
            foreach (var pool in pools)
            {
                ct.ThrowIfCancellationRequested();
                await CheckPoolAsync(s, pool, ct);
            }
        }

        if (s.Event1309.Enabled && Due(_last1309Check, now, s.Event1309.CheckIntervalSeconds))
        {
            _last1309Check = now;
            // Kept in its own try block so an event log problem never stops the queue depth checks below.
            try
            {
                await CollectAspNetErrorsAsync(pools, ct);
                _last1309Error = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = $"Could not check for ASP.NET 1309 errors: {ex.Message}";
                if (msg != _last1309Error) _log.Error(msg);
                _last1309Error = msg;
            }
        }
    }

    /// <summary>
    /// Reads new 1309 events and records each one against the pool it came from.
    /// The decision to recycle is made later, in <see cref="CheckPoolAsync"/>.
    /// </summary>
    private async Task CollectAspNetErrorsAsync(IReadOnlyList<AppPoolSettings> pools, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;

        // Step 1: refresh the PID map. This must happen BEFORE reading the events, so that an
        // event written by a process that is still running can be matched on this same poll.
        try
        {
            foreach (var (pid, pool) in await AppCmd.ListWorkerProcessesAsync(ct))
                _pidToPool[pid] = (pool, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not fatal: still read the events, which can be matched by PIDs seen on earlier
            // polls or by the pool's EventMatches text.
            if (_last1309Error is null) _log.Warning($"Could not list worker processes: {ex.Message}");
        }

        // Forget PIDs not seen for a while. Windows reuses PIDs, so keeping them forever could
        // eventually attribute an event to the wrong pool.
        foreach (var stale in _pidToPool.Where(kv => now - kv.Value.Seen > PidMemory).Select(kv => kv.Key).ToList())
            _pidToPool.Remove(stale);

        // Step 2: read and assign the new events.
        foreach (var error in _aspNetErrors.ReadNew())
        {
            var pool = MatchPool(error, pools);
            if (pool is null) continue; // From some other site or pool we are not watching. Ignore it.

            var t = TrackingFor(pool.Name);
            t.AspNetErrors.Add(error.TimeCreated);
            t.LastAspNetError = error.TimeCreated;
            _log.Warning($"ASP.NET 1309 error logged (event record {error.RecordId}" +
                         (error.ProcessId is { } p ? $", w3wp.exe PID {p}" : "") + ").", pool.Name);
        }
    }

    /// <summary>
    /// Works out which watched pool a 1309 event belongs to, or null if none.
    /// The worker process ID is tried first because it is exact. The configured EventMatches text
    /// is only a fallback, because the same virtual path (for example /Autodiscover) exists on
    /// both Exchange web sites and so text alone can point at the wrong pool.
    /// </summary>
    private AppPoolSettings? MatchPool(AspNetError error, IReadOnlyList<AppPoolSettings> pools)
    {
        if (error.ProcessId is { } pid && _pidToPool.TryGetValue(pid, out var owner))
        {
            // We know the owner. If it is not one of our pools the event is not ours, even if its
            // text happens to match something, so do not fall through to text matching.
            return pools.FirstOrDefault(p => p.Name.Equals(owner.Pool, StringComparison.OrdinalIgnoreCase));
        }

        return pools.FirstOrDefault(p => p.EventMatches.Any(m =>
            !string.IsNullOrWhiteSpace(m) && error.Text.Contains(m, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Checks one pool, decides whether to recycle it, and publishes its status.</summary>
    private async Task CheckPoolAsync(MonitorSettings s, AppPoolSettings pool, CancellationToken ct)
    {
        var t = TrackingFor(pool.Name);
        var threshold = s.ThresholdFor(pool);
        var longThreshold = s.LongRunningThresholdFor(pool);
        var olderThan = TimeSpan.FromSeconds(Math.Max(1, s.LongRunningRequests.OlderThanSeconds));
        var errors = new List<string>();

        // ----- Gather: queue depth. A cheap counter read, done first so nothing can delay it. -----

        long? depth = null;
        try
        {
            depth = _queues.Read(pool.Name);
            // Null means HTTP.sys has no queue for this pool name: usually a typo in the config,
            // or the pool does not exist on this server.
            if (depth is null) errors.Add("No request queue counter instance found for this pool.");
        }
        catch (Exception ex) { errors.Add($"Could not read request queue: {ex.Message}"); }

        // ----- Gather: long running requests. Needs appcmd, but read before deciding because it is a trigger. -----

        int? longRunning = null;
        TimeSpan? oldest = null;
        if (s.LongRunningRequests.Enabled)
        {
            try { (longRunning, oldest) = await AppCmd.CountLongRunningRequestsAsync(pool.Name, olderThan, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"Could not list long running requests: {ex.Message}"); }
        }

        // ----- Decide: does any trigger fire? The first one that fires gives the reason. -----

        // Trigger 1: queue depth. "Deeper than" the threshold, so a depth equal to it does not count.
        // If the depth could not be read (null) the comparison is false and the streak resets.
        var over = depth > threshold;
        t.ConsecutiveOver = over ? t.ConsecutiveOver + 1 : 0;
        string? reason = null;
        if (over && t.ConsecutiveOver >= Math.Max(1, s.ConsecutiveSamplesBeforeRecycle))
            reason = $"Request queue depth {depth} is over the threshold of {threshold} " +
                     $"for {t.ConsecutiveOver} consecutive check(s).";

        // Trigger 2: long running requests, same idea as the queue: over the threshold for N checks in a row.
        var longOver = longRunning > longThreshold;
        t.LongRunningConsecutiveOver = longOver ? t.LongRunningConsecutiveOver + 1 : 0;
        if (reason is null && longOver && t.LongRunningConsecutiveOver >= Math.Max(1, s.ConsecutiveSamplesBeforeRecycle))
            reason = $"{longRunning} request(s) have been running {olderThan.TotalSeconds:0} seconds or longer " +
                     $"(oldest {oldest?.TotalSeconds ?? 0:0} s), over the threshold of {longThreshold}, " +
                     $"for {t.LongRunningConsecutiveOver} consecutive check(s).";

        // Trigger 3: 1309 errors. Drop errors that have aged out of the window, then count the rest.
        // Pruning happens even when the trigger is off, so the status page count stays accurate.
        var window = TimeSpan.FromMinutes(Math.Max(1, s.Event1309.WindowMinutes));
        t.AspNetErrors.RemoveAll(e => DateTimeOffset.Now - e > window);
        if (reason is null && s.Event1309.Enabled && t.AspNetErrors.Count >= Math.Max(1, s.Event1309.ErrorCount))
            reason = $"{t.AspNetErrors.Count} ASP.NET 1309 error(s) in the last {window.TotalMinutes:0} minute(s).";

        // ----- Act -----

        if (reason is null)
        {
            // Problem has cleared, so the next incident gets its own "in cooldown" log line.
            t.CooldownNoted = false;
        }
        else
        {
            var cooldown = TimeSpan.FromMinutes(Math.Max(0, s.RecycleCooldownMinutes));
            if (t.LastRecycled is { } last && DateTimeOffset.Now - last < cooldown)
            {
                // A recycle happened recently and did not fix things (or the problem came back fast).
                // Recycling again straight away would just interrupt users repeatedly, so wait.
                if (!t.CooldownNoted)
                    _log.Warning($"{reason} Not recycling: last recycle was at {last:HH:mm:ss}, " +
                                 $"cooldown ends at {last + cooldown:HH:mm:ss}.", pool.Name);
                t.CooldownNoted = true;
            }
            else
            {
                await RecycleAsync(s, pool.Name, reason, t, ct);
            }
        }

        // ----- Pool state, for the status page only. Read after acting, so a slow IIS cannot delay a
        // recycle, and so the page shows the state after any recycle that just happened. -----

        string state;
        try { state = await AppCmd.GetStateAsync(pool.Name, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { state = "Unknown"; errors.Add(ex.Message); }

        // Log the error only when it changes, so a persistent problem is logged once, not every poll.
        var error = errors.Count > 0 ? string.Join(" ", errors) : null;
        if (error != t.LastErrorLogged && error is not null) _log.Error(error, pool.Name);
        t.LastErrorLogged = error;

        // ----- Publish: a new immutable snapshot for the web page. -----
        _state.SetPool(new PoolStatus(pool.Name, state, depth, threshold, t.ConsecutiveOver,
            longRunning, oldest, longThreshold, t.LongRunningConsecutiveOver, DateTimeOffset.Now,
            t.LastRecycled, t.RecycleCount, t.AspNetErrors.Count, t.LastAspNetError, error));
    }

    /// <summary>
    /// Recycles the pool (or pretends to, in dry run mode), logs the result, and emails it.
    /// Exactly one email is sent per call, whether the recycle worked, failed, or was a dry run.
    /// </summary>
    private async Task RecycleAsync(MonitorSettings s, string pool, string reason, Tracking t, CancellationToken ct)
    {
        var server = Environment.MachineName;
        string subject, body;

        if (s.DryRun)
        {
            _log.Action($"[DRY RUN] Would recycle. {reason}", pool);
            subject = $"[DRY RUN] Would recycle {pool} on {server}";
            body = $"DRY RUN: {pool} on {server} would have been recycled at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}.\r\n\r\nReason: {reason}\r\n";
        }
        else
        {
            try
            {
                // A recycle is graceful: IIS starts a new w3wp.exe, hands it new requests, and lets the
                // old one finish its in-flight requests before shutting it down. The HTTP.sys queue
                // belongs to the pool, not the process, so queued requests are not lost.
                var output = await AppCmd.RecycleAsync(pool, ct);
                t.RecycleCount++;
                _log.Action($"Recycled. {reason} appcmd: {output}", pool);
                subject = $"Recycled {pool} on {server}";
                body = $"{pool} on {server} was recycled at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}.\r\n\r\nReason: {reason}\r\n\r\nappcmd output: {output}\r\n";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error($"Recycle FAILED. {reason} Error: {ex.Message}", pool);
                subject = $"FAILED to recycle {pool} on {server}";
                body = $"An attempt to recycle {pool} on {server} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} failed.\r\n\r\nReason for recycle: {reason}\r\n\r\nError: {ex.Message}\r\n";
            }
        }

        // Start the cooldown even after a failure or a dry run. Otherwise a pool that cannot be
        // recycled would trigger a fresh attempt, and a fresh email, on every single poll.
        t.LastRecycled = DateTimeOffset.Now;

        // Reset both triggers so the new process starts with a clean slate. Errors from the old
        // process should not count towards recycling the new one.
        t.ConsecutiveOver = 0;
        t.LongRunningConsecutiveOver = 0;
        t.AspNetErrors.Clear();
        t.CooldownNoted = false;

        await _email.SendAsync(subject, body, pool, ct);
    }

    /// <summary>Gets the tracking record for a pool, creating it the first time the pool is seen.</summary>
    private Tracking TrackingFor(string pool)
    {
        if (!_tracking.TryGetValue(pool, out var t)) _tracking[pool] = t = new Tracking();
        return t;
    }
}
