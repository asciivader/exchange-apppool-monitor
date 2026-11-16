// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace ExchangeAppPoolMonitor;

/// <summary>
/// Renders the status page at "/" as one self-contained HTML document.
/// </summary>
/// <remarks>
/// Built as a plain string on purpose: no Razor, no JavaScript, no external CSS or fonts, so it works
/// on a locked down server with no internet access and nothing else to deploy. A meta refresh tag
/// reloads it every 15 seconds. Every value that comes from outside the code (pool names, error
/// text, log messages) goes through HtmlEncode, so odd characters cannot break or inject into the page.
/// The page has three sections: application status, a table of the watched pools, and the last
/// 25 action log entries, newest first.
/// </remarks>
public static class StatusPage
{
    public static string Render(MonitorState state, ActionLog log, MonitorSettings s, EmailSettings email)
    {
        // H = HTML encode any value. T = format a timestamp, or "-" if there is none.
        static string H(object? v) => WebUtility.HtmlEncode(v?.ToString() ?? "");
        static string T(DateTimeOffset? t) => t is { } v ? v.ToString("yyyy-MM-dd HH:mm:ss") : "-";

        var uptime = DateTimeOffset.Now - state.StartedAt;
        var sb = new StringBuilder();
        sb.Append(@"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<meta http-equiv=""refresh"" content=""15"">
<title>AppPool Monitor</title>
<style>
  body { font-family: Segoe UI, Arial, sans-serif; margin: 24px; color: #1b1b1b; background: #f6f7f9; }
  h1 { font-size: 22px; margin: 0 0 4px; } h2 { font-size: 17px; margin: 28px 0 8px; }
  .sub { color: #666; font-size: 13px; }
  table { border-collapse: collapse; width: 100%; background: #fff; font-size: 14px; }
  th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid #e3e5e8; vertical-align: top; }
  th { background: #eef0f3; font-weight: 600; }
  .kv td:first-child { width: 260px; color: #555; }
  .badge { display: inline-block; padding: 1px 8px; border-radius: 10px; font-size: 12px; font-weight: 600; }
  .ok { background: #dff3e4; color: #1d6b34; } .bad { background: #fbe0e0; color: #9b1c1c; }
  .warn { background: #fff1d6; color: #8a5a00; } .info { background: #e3ecfa; color: #24477a; }
  .over { color: #9b1c1c; font-weight: 600; } .err { color: #9b1c1c; font-size: 13px; }
</style></head><body>
<h1>Exchange AppPool Monitor</h1>
");
        sb.Append($"<div class=\"sub\">{H(Environment.MachineName)} &middot; page generated {T(DateTimeOffset.Now)} &middot; refreshes every 15 seconds</div>");

        // Application status
        var running = state.MonitorStatus == "Running";
        sb.Append("<h2>Application</h2><table class=\"kv\">");
        sb.Append($"<tr><td>Monitor</td><td><span class=\"badge {(running ? "ok" : "bad")}\">{H(state.MonitorStatus)}</span>");
        if (s.DryRun) sb.Append(" <span class=\"badge warn\">DRY RUN</span>");
        sb.Append("</td></tr>");
        sb.Append($"<tr><td>Started</td><td>{T(state.StartedAt)} (up {(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m)</td></tr>");
        sb.Append($"<tr><td>Last check</td><td>{T(state.LastPoll)}</td></tr>");
        sb.Append($"<tr><td>Check interval</td><td>{s.PollIntervalSeconds} seconds</td></tr>");
        sb.Append($"<tr><td>Queue trigger</td><td>Deeper than {s.QueueThreshold} (default) for {s.ConsecutiveSamplesBeforeRecycle} check(s) in a row</td></tr>");
        sb.Append($"<tr><td>Long running request trigger</td><td>{(s.LongRunningRequests.Enabled ? $"More than {s.LongRunningRequests.Threshold} (default) running {s.LongRunningRequests.OlderThanSeconds}+ seconds, for {s.ConsecutiveSamplesBeforeRecycle} check(s) in a row" : "Off")}</td></tr>");
        sb.Append($"<tr><td>ASP.NET 1309 trigger</td><td>{(s.Event1309.Enabled ? $"{s.Event1309.ErrorCount} error(s) within {s.Event1309.WindowMinutes} minute(s), checked every {s.Event1309.CheckIntervalSeconds} seconds" : "Off")}</td></tr>");
        sb.Append($"<tr><td>Recycle cooldown</td><td>{s.RecycleCooldownMinutes} minutes</td></tr>");
        sb.Append($"<tr><td>Email notifications</td><td>{(email.Enabled ? $"On, to {H(string.Join(", ", email.To))} via {H(email.SmtpServer)}:{email.Port}" : "Off")}</td></tr>");
        if (state.LastError is not null)
            sb.Append($"<tr><td>Last error</td><td class=\"err\">{H(state.LastError)}</td></tr>");
        sb.Append("</table>");

        // App pools. Rows follow the config order. A pool that has not been checked yet (just after
        // startup, or just added to the config) shows as Pending.
        sb.Append("<h2>App pools</h2><table><tr><th>App pool</th><th>State</th><th>Queue depth</th><th>Queue threshold</th>" +
                  $"<th>Requests {s.LongRunningRequests.OlderThanSeconds}s+</th><th>Oldest</th><th>Long running threshold</th>" +
                  "<th>1309 errors in window</th><th>Last 1309</th><th>Last recycled</th><th>Recycles</th><th>Last checked</th></tr>");
        foreach (var poolName in s.EffectivePools().Select(pool => pool.Name))
        {
            var p = state.GetPool(poolName);
            if (p is null)
            {
                sb.Append($"<tr><td>{H(poolName)}</td><td><span class=\"badge info\">Pending</span></td><td colspan=\"10\">Not checked yet</td></tr>");
                continue;
            }
            // Green for Started, red for Stopped, amber for anything in between or unknown.
            var cls = p.State switch { "Started" => "ok", "Stopped" => "bad", _ => "warn" };
            var over = p.QueueDepth > p.Threshold;
            sb.Append("<tr>");
            sb.Append($"<td>{H(p.Name)}");
            if (p.LastError is not null) sb.Append($"<div class=\"err\">{H(p.LastError)}</div>");
            sb.Append("</td>");
            sb.Append($"<td><span class=\"badge {cls}\">{H(p.State)}</span></td>");
            sb.Append($"<td class=\"{(over ? "over" : "")}\">{(p.QueueDepth?.ToString() ?? "-")}{(over ? $" ({p.ConsecutiveOver}x over)" : "")}</td>");
            sb.Append($"<td>{p.Threshold}</td>");
            var longOver = p.LongRunningRequests > p.LongRunningThreshold;
            sb.Append($"<td class=\"{(longOver ? "over" : "")}\">{(p.LongRunningRequests?.ToString() ?? "-")}{(longOver ? $" ({p.LongRunningConsecutiveOver}x over)" : "")}</td>");
            sb.Append($"<td>{(p.OldestRequest is { } o ? $"{o.TotalSeconds:0} s" : "-")}</td>");
            sb.Append($"<td>{p.LongRunningThreshold}</td>");
            sb.Append($"<td class=\"{(p.RecentAspNetErrors > 0 ? "over" : "")}\">{p.RecentAspNetErrors}</td>");
            sb.Append($"<td>{T(p.LastAspNetError)}</td>");
            sb.Append($"<td>{T(p.LastRecycled)}</td><td>{p.RecycleCount}</td><td>{T(p.LastChecked)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");

        // Log
        sb.Append($"<h2>Last {ActionLog.RecentCount} log entries</h2><table><tr><th>Time</th><th>Level</th><th>App pool</th><th>Message</th></tr>");
        var entries = log.Recent();
        if (entries.Count == 0) sb.Append("<tr><td colspan=\"4\">No entries yet</td></tr>");
        foreach (var e in entries)
        {
            var cls = e.Level switch { "Error" => "bad", "Warning" => "warn", "Action" => "ok", _ => "info" };
            sb.Append($"<tr><td style=\"white-space:nowrap\">{T(e.Timestamp)}</td><td><span class=\"badge {cls}\">{H(e.Level)}</span></td>" +
                      $"<td>{H(e.AppPool ?? "-")}</td><td>{H(e.Message)}</td></tr>");
        }
        sb.Append("</table>");

        // Footer: version, author and license.
        var version = typeof(StatusPage).Assembly.GetName().Version?.ToString(3) ?? "";
        sb.Append($"<p class=\"sub\" style=\"margin-top:24px\">Exchange AppPool Monitor {H(version)} &middot; Copyright (c) 2026 Nathan Kelly &middot; Free software under the MIT License</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
