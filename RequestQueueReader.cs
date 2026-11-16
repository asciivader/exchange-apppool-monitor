// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.Runtime.InteropServices;

namespace ExchangeAppPoolMonitor;

/// <summary>
/// Reads the HTTP.sys request queue depth for an app pool.
/// </summary>
/// <remarks>
/// <para>
/// Background: HTTP.sys is the Windows kernel driver that receives HTTP requests before IIS sees them.
/// It keeps one request queue per IIS app pool, named after the pool. A request waits in that queue
/// until a thread in the pool's w3wp.exe picks it up. In a healthy pool the queue is at or near zero.
/// When the pool hangs or runs out of threads, requests pile up here, and once the queue reaches its
/// limit (the pool's "Queue Length" setting, 1000 by default) HTTP.sys starts answering 503.
/// </para>
/// <para>
/// The depth is published as the performance counter
/// <c>\HTTP Service Request Queues(&lt;pool name&gt;)\CurrentQueueSize</c>, the same value you see with
/// <c>Get-Counter '\HTTP Service Request Queues(*)\CurrentQueueSize'</c> or in Performance Monitor.
/// </para>
/// <para>
/// How it is read: through the Performance Data Helper (PDH) functions in pdh.dll, which are part of
/// Windows and are what Performance Monitor and Get-Counter use. Calling pdh.dll directly means no extra
/// library (NuGet package) is needed. One PDH query is opened with a wildcard counter path that covers
/// every request queue on the server; each read collects a fresh sample and returns the value for the
/// requested pool. Queues that appear later (a new pool) are picked up automatically by the wildcard.
/// The English counter name is used, so it works on Windows in any language.
/// </para>
/// <para>Only used from the monitor loop thread, so it is not thread safe.</para>
/// </remarks>
public sealed class RequestQueueReader : IDisposable
{
    private const string CounterPath = @"\HTTP Service Request Queues(*)\CurrentQueueSize";

    private IntPtr _query;
    private IntPtr _counter;

    /// <summary>
    /// Returns the current queue depth, or null if HTTP.sys has no request queue for this pool
    /// (wrong name in the config, or the pool does not exist on this server).
    /// Throws if the counter cannot be read at all (for example the counter category is missing).
    /// </summary>
    public long? Read(string pool)
    {
        var values = ReadAll();
        return values.TryGetValue(pool, out var depth) ? depth : null;
    }

    /// <summary>Collects one sample and returns the queue depth of every request queue, by queue (pool) name.</summary>
    private Dictionary<string, long> ReadAll()
    {
        EnsureOpen();

        var status = PdhCollectQueryData(_query);
        if (status != ErrorSuccess) throw PdhError("collect the performance counter sample", status);

        // First call asks how big the buffer needs to be; the second fills it.
        uint bufferSize = 0;
        status = PdhGetFormattedCounterArray(_counter, FormatLarge | FormatNoScale, ref bufferSize, out _, IntPtr.Zero);
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (status == PdhNoData || status == PdhInvalidData) return result; // no request queues exist at all
        if (status != PdhMoreData) throw PdhError("size the counter results", status);

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArray(_counter, FormatLarge | FormatNoScale, ref bufferSize, out var itemCount, buffer);
            if (status != ErrorSuccess) throw PdhError("read the counter results", status);

            // The buffer holds itemCount PDH_FMT_COUNTERVALUE_ITEM_W entries (64-bit layout):
            //   offset 0  : pointer to the instance name (the queue / pool name)
            //   offset 8  : CStatus (uint), the status of this one value
            //   offset 16 : the value, as a 64-bit integer (PDH_FMT_LARGE)
            // Each entry is 24 bytes.
            for (var i = 0; i < itemCount; i++)
            {
                var item = buffer + i * ItemSize;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0));
                var valueStatus = (uint)Marshal.ReadInt32(item, 8);
                if (string.IsNullOrEmpty(name) || (valueStatus != CStatusValidData && valueStatus != CStatusNewData)) continue;
                result[name] = Marshal.ReadInt64(item, 16);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    /// <summary>Opens the PDH query and adds the wildcard counter, the first time it is needed.</summary>
    private void EnsureOpen()
    {
        if (_query != IntPtr.Zero) return;

        var status = PdhOpenQuery(null, IntPtr.Zero, out var query);
        if (status != ErrorSuccess) throw PdhError("open a performance counter query", status);

        status = PdhAddEnglishCounter(query, CounterPath, IntPtr.Zero, out var counter);
        if (status != ErrorSuccess)
        {
            PdhCloseQuery(query);
            if (status == CStatusNoObject)
                throw new InvalidOperationException(
                    "Performance counter category 'HTTP Service Request Queues' does not exist on this server. " +
                    "If IIS is installed, the counters may need rebuilding with 'lodctr /R' from an elevated prompt.");
            throw PdhError("add the counter " + CounterPath, status);
        }

        _query = query;
        _counter = counter;
    }

    private static InvalidOperationException PdhError(string action, uint status) =>
        new($"Could not {action} (PDH error 0x{status:X8}).");

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query); // also releases the counter
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ Windows API (pdh.dll)

    private const int ItemSize = 24;
    private const uint ErrorSuccess = 0;
    private const uint FormatLarge = 0x00000400;   // PDH_FMT_LARGE: 64-bit integer value
    private const uint FormatNoScale = 0x00001000; // PDH_FMT_NOSCALE: the raw count, no scaling
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhNoData = 0x800007D5;
    private const uint PdhInvalidData = 0xC0000BC6;
    private const uint CStatusValidData = 0x00000000;
    private const uint CStatusNewData = 0x00000001;
    private const uint CStatusNoObject = 0xC0000BB8;

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
