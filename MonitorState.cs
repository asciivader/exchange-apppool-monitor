// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.Collections.Concurrent;

namespace ExchangeAppPoolMonitor;

/// <summary>
/// Latest view of one app pool, as shown on the status page and in /api/status.
/// Immutable (a record): the monitor loop builds a new one each poll and swaps it in, so the web
/// page can never read a half updated mix of old and new values.
/// </summary>
/// <param name="Name">App pool name.</param>
/// <param name="State">State reported by IIS (Started, Stopped, ...) or Unknown if it could not be read.</param>
/// <param name="QueueDepth">HTTP.sys request queue depth, or null if it could not be read.</param>
/// <param name="Threshold">Queue threshold in effect for this pool.</param>
/// <param name="ConsecutiveOver">How many polls in a row the queue has been over the threshold.</param>
/// <param name="LongRunningRequests">Requests running longer than OlderThanSeconds, or null if not read (trigger off, or failed).</param>
/// <param name="OldestRequest">How long the oldest of those requests has been running.</param>
/// <param name="LongRunningThreshold">Long running request threshold in effect for this pool.</param>
/// <param name="LongRunningConsecutiveOver">How many polls in a row the long running count has been over its threshold.</param>
/// <param name="LastChecked">When this pool was last checked.</param>
/// <param name="LastRecycled">When the monitor last recycled (or tried to recycle) this pool.</param>
/// <param name="RecycleCount">Successful recycles since the service started.</param>
/// <param name="RecentAspNetErrors">1309 errors still inside the counting window.</param>
/// <param name="LastAspNetError">Time of the most recent 1309 error for this pool.</param>
/// <param name="LastError">Problem from the last check (for example, could not read the counter), or null.</param>
public sealed record PoolStatus(
    string Name,
    string State,
    long? QueueDepth,
    int Threshold,
    int ConsecutiveOver,
    int? LongRunningRequests,
    TimeSpan? OldestRequest,
    int LongRunningThreshold,
    int LongRunningConsecutiveOver,
    DateTimeOffset? LastChecked,
    DateTimeOffset? LastRecycled,
    int RecycleCount,
    int RecentAspNetErrors,
    DateTimeOffset? LastAspNetError,
    string? LastError);

/// <summary>
/// Shared between the monitor loop (writer) and the status page (reader). Registered as a singleton.
/// The per-pool data lives in a ConcurrentDictionary of immutable records, which is safe without
/// locks. The simple properties are written by one thread and only displayed by the other, so a
/// read that is a moment out of date is harmless.
/// </summary>
public sealed class MonitorState
{
    private readonly ConcurrentDictionary<string, PoolStatus> _pools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the service started. Used for the uptime shown on the page.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    /// <summary>When the monitor loop last finished a poll. If this stops moving, the loop is stuck.</summary>
    public DateTimeOffset? LastPoll { get; set; }
    /// <summary>Starting, Running or Stopped.</summary>
    public string MonitorStatus { get; set; } = "Starting";
    /// <summary>Unexpected error from the last poll, or null if it went cleanly.</summary>
    public string? LastError { get; set; }

    /// <summary>Replaces the stored status for a pool.</summary>
    public void SetPool(PoolStatus status) => _pools[status.Name] = status;

    /// <summary>Returns the latest status for a pool, or null if it has not been checked yet.</summary>
    public PoolStatus? GetPool(string name) => _pools.TryGetValue(name, out var s) ? s : null;
}
