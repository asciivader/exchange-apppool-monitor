// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.Diagnostics.Eventing.Reader;

namespace ExchangeAppPoolMonitor;

/// <summary>One ASP.NET Event ID 1309 entry from the Application log.</summary>
/// <param name="TimeCreated">When Windows wrote the event.</param>
/// <param name="RecordId">The event's record number in the log. Unique and always increasing.</param>
/// <param name="ProcessId">The w3wp.exe process ID from the event data, or null if it could not be found.</param>
/// <param name="Text">All of the event's data fields joined by newlines, used for EventMatches text matching.</param>
public sealed record AspNetError(DateTimeOffset TimeCreated, long RecordId, int? ProcessId, string Text);

/// <summary>
/// Returns ASP.NET 1309 events written to the Application log since the last call.
/// </summary>
/// <remarks>
/// <para>
/// Event ID 1309 is a Warning written by an "ASP.NET 4.0.30319.0" source (the exact version in the
/// name varies) when an ASP.NET application hits an unhandled exception, shown in the event text as
/// "Event code: 3005". On Exchange, a run of these from a pool is a common sign that the pool is in
/// a bad state and needs a recycle.
/// </para>
/// <para>
/// Only events written after the service started are returned, so starting the service does not
/// immediately recycle a pool because of old events. Each event is returned once: the reader
/// remembers the highest record ID it has seen and skips anything at or below it.
/// Only used from the monitor loop thread, so it is not thread safe.
/// </para>
/// </remarks>
public sealed class AspNetErrorReader
{
    private const string LogName = "Application";

    /// <summary>Only ask the log for events at or after this time. Starts at service start time.</summary>
    private DateTime _sinceUtc = DateTime.UtcNow;

    /// <summary>Highest record ID already returned. Guards against returning the same event twice.</summary>
    private long _lastRecordId;

    public List<AspNetError> ReadNew()
    {
        // The event log query language is a subset of XPath. Filtering on ID and time here means
        // Windows does the searching, rather than us reading the whole Application log each poll.
        // The time filter uses >= (not >) so that an event written in the same millisecond as the
        // last one we saw is not missed; the record ID check below stops it being counted twice.
        var since = _sinceUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var query = new EventLogQuery(LogName, PathType.LogName,
            $"*[System[(EventID=1309) and TimeCreated[@SystemTime>='{since}']]]");

        var results = new List<AspNetError>();
        using var reader = new EventLogReader(query);
        for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
        {
            using (record)
            {
                var id = record.RecordId ?? 0;
                if (id <= _lastRecordId) continue;
                _lastRecordId = id;
                if (record.TimeCreated is { } t && t.ToUniversalTime() > _sinceUtc)
                    _sinceUtc = t.ToUniversalTime();

                // Event ID 1309 is not unique to ASP.NET; other software can use the same number.
                // Only count events whose source name starts with "ASP.NET".
                if (record.ProviderName?.StartsWith("ASP.NET", StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                var values = record.Properties.Select(p => p.Value?.ToString() ?? "").ToList();
                results.Add(new AspNetError(
                    record.TimeCreated is { } tc ? new DateTimeOffset(tc) : DateTimeOffset.Now,
                    id,
                    FindProcessId(values),
                    string.Join(Environment.NewLine, values)));
            }
        }
        return results;
    }

    /// <summary>
    /// Pulls the worker process ID out of the event's data fields.
    /// </summary>
    /// <remarks>
    /// A 1309 event's data is a fixed list of values. In the "Process information" part of the
    /// event text they appear as "Process ID: 1234" then "Process name: w3wp.exe", and in the raw
    /// data the PID is the value directly before "w3wp.exe". Searching for "w3wp.exe" rather than
    /// using a fixed position keeps this working if the number of fields before it differs between
    /// .NET versions. Returns null if no PID is found; the event can then still be matched by text.
    /// </remarks>
    private static int? FindProcessId(List<string> values)
    {
        var i = values.FindIndex(v => v.Equals("w3wp.exe", StringComparison.OrdinalIgnoreCase));
        return i > 0 && int.TryParse(values[i - 1], out var pid) ? pid : null;
    }
}
