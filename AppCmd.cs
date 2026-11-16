// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ExchangeAppPoolMonitor;

/// <summary>
/// Talks to IIS by running <c>%windir%\system32\inetsrv\appcmd.exe</c>, the command line tool that
/// ships with IIS.
/// </summary>
/// <remarks>
/// appcmd was chosen over the Microsoft.Web.Administration library because it is always present
/// where IIS is installed, behaves the same on every IIS version, and its commands are the same ones
/// an admin would type by hand, which makes the log entries easy to reproduce and check.
/// It needs administrator rights, which the service has when it runs as LocalSystem.
/// It does not need PowerShell or the Exchange Management Shell.
/// The class holds no state, so it is static: every call simply runs appcmd.exe.
/// </remarks>
public static class AppCmd
{
    private static readonly string ExePath = Path.Combine(Environment.SystemDirectory, "inetsrv", "appcmd.exe");

    /// <summary>
    /// appcmd normally answers in well under a second. Anything this slow means IIS itself is stuck.
    /// Kept short so a hung IIS holds up the monitor loop for as little time as possible.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Upper limit for any one regular expression match. The patterns are simple and the input is
    /// appcmd's own output, so matches take microseconds; the limit just guarantees a match can never hang.
    /// </summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Matches one line of <c>appcmd list wp</c> output, for example:
    /// <c>WP "1234" (applicationPool:MSExchangeAutodiscoverAppPool)</c>.
    /// Group 1 is the PID, group 2 the app pool name.
    /// </summary>
    private static readonly Regex WorkerProcessLine =
        new("WP \"(\\d+)\" \\(applicationPool:([^)]+)\\)", RegexOptions.Compiled, RegexTimeout);

    /// <summary>
    /// Matches the elapsed time in one line of <c>appcmd list requests</c> output, for example:
    /// <c>REQUEST "f20000018000000c" (url:POST /Autodiscover/Autodiscover.xml, time:45031 msec, client:10.0.0.5, stage:ExecuteRequestHandler, module:IsapiModule)</c>.
    /// Group 1 is the time in milliseconds.
    /// </summary>
    private static readonly Regex RequestTime = new("time:(\\d+) msec", RegexOptions.Compiled, RegexTimeout);

    /// <summary>
    /// Returns the pool's state as IIS reports it: Started, Stopped, Starting, Stopping or Unknown.
    /// Runs: <c>appcmd list apppool /name:&lt;pool&gt; /text:state</c>
    /// </summary>
    public static async Task<string> GetStateAsync(string pool, CancellationToken ct)
    {
        var (exit, output) = await RunAsync(ct, "list", "apppool", $"/name:{pool}", "/text:state");
        if (exit != 0)
            throw new InvalidOperationException($"appcmd list apppool failed (exit {exit}): {output}");
        return string.IsNullOrWhiteSpace(output) ? "NotFound" : output.Trim();
    }

    /// <summary>
    /// Recycles the pool and returns appcmd's output (normally <c>"&lt;pool&gt;" successfully recycled</c>).
    /// Throws if appcmd reports a failure, for example because the pool is stopped.
    /// Runs: <c>appcmd recycle apppool /apppool.name:&lt;pool&gt;</c>
    /// </summary>
    public static async Task<string> RecycleAsync(string pool, CancellationToken ct)
    {
        var (exit, output) = await RunAsync(ct, "recycle", "apppool", $"/apppool.name:{pool}");
        if (exit != 0)
            throw new InvalidOperationException($"appcmd recycle apppool failed (exit {exit}): {output}");
        return output.Trim();
    }

    /// <summary>
    /// Counts the requests that have been running inside the pool's worker process for at least
    /// <paramref name="olderThan"/>, and returns the age of the oldest one.
    /// Runs: <c>appcmd list requests /apppool.name:&lt;pool&gt; /elapsed:&lt;milliseconds&gt;</c>
    /// </summary>
    /// <remarks>
    /// These are requests IIS has already handed to w3wp.exe that have not finished, which is
    /// different from the HTTP.sys queue (requests still waiting to be handed over). When an Exchange
    /// pool hangs, requests stuck for 30 seconds or more pile up here into the thousands.
    /// Needs the IIS "Request Monitor" feature (Web-Request-Monitor), which Exchange setup installs.
    /// </remarks>
    public static async Task<(int Count, TimeSpan? Oldest)> CountLongRunningRequestsAsync(string pool, TimeSpan olderThan, CancellationToken ct)
    {
        var (exit, output) = await RunAsync(ct, "list", "requests", $"/apppool.name:{pool}",
            $"/elapsed:{(long)olderThan.TotalMilliseconds}");

        // Like "list wp": exit code 1 with no output simply means no matching requests.
        if (exit != 0 && !string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"appcmd list requests failed (exit {exit}): {output}");

        var count = 0;
        long oldestMs = -1;
        foreach (var line in output.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("REQUEST ", StringComparison.Ordinal)) continue;
            count++;
            var m = RequestTime.Match(line);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var ms) && ms > oldestMs) oldestMs = ms;
        }
        return (count, oldestMs >= 0 ? TimeSpan.FromMilliseconds(oldestMs) : null);
    }

    /// <summary>
    /// Maps each running w3wp.exe process ID to its app pool name, for every pool on the server.
    /// Runs: <c>appcmd list wp</c>
    /// </summary>
    public static async Task<Dictionary<int, string>> ListWorkerProcessesAsync(CancellationToken ct)
    {
        var (exit, output) = await RunAsync(ct, "list", "wp");
        var map = new Dictionary<int, string>();

        // appcmd exits with 1 and prints nothing when no worker processes are running, which is a
        // valid answer (an empty map), not an error. Only treat it as a failure if it printed a message.
        if (exit != 0 && !string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"appcmd list wp failed (exit {exit}): {output}");

        foreach (var groups in WorkerProcessLine.Matches(output).Select(m => m.Groups))
            map[int.Parse(groups[1].Value)] = groups[2].Value.Trim();
        return map;
    }

    /// <summary>
    /// Runs appcmd with the given arguments and returns its exit code and combined output.
    /// Kills appcmd if it runs longer than <see cref="Timeout"/>.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunAsync(CancellationToken ct, params string[] args)
    {
        if (!File.Exists(ExePath))
            throw new FileNotFoundException($"appcmd.exe not found at {ExePath}. Is IIS installed?");

        var psi = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ArgumentList quotes each argument correctly, so pool names with spaces are safe.
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start appcmd.exe");

        // Read both streams at the same time as waiting. If we waited first, appcmd could block
        // forever once its output buffer filled up, because nobody would be reading it.
        // (.NET 6 has no cancellable ReadToEndAsync; the streams end when appcmd exits or is killed below.)
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            if (ct.IsCancellationRequested) throw; // the service is stopping, not a timeout
            throw new TimeoutException($"appcmd.exe did not finish within {Timeout.TotalSeconds:0} seconds");
        }

        var text = (await stdout + await stderr).Trim();
        return (process.ExitCode, text);
    }
}
