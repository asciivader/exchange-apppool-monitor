# Exchange AppPool Monitor: operations runbook

For whoever looks after the Exchange servers day to day. It explains what the service does, what its emails mean, what to do when something looks wrong, and how to turn it off quickly. No programming knowledge is needed. For how the code works, see [MAINTAINING.md](MAINTAINING.md).

Author: Nathan Kelly. Free software under the MIT License.

---

## 1. What it is, in one minute

- A Windows service called **ExchangeAppPoolMonitor** ("Exchange AppPool Monitor") runs on each Exchange server where it is installed.
- Every 10 seconds (by default) it checks two IIS app pools: **MSExchangeAutodiscoverAppPool** and **MSExchangeDefaultAppPool**.
- If a pool shows signs of hanging, it **recycles that pool** (the same as clicking Recycle in IIS Manager), **writes a log entry** and **emails the people in its settings**.
- It exists because a hung pool, left alone, can slide the whole server into a state that needs maintenance mode, a database failover and a reboot. Recycling the pool early avoids that.

The signs it watches for, any one of which triggers a recycle:

| Sign | Default trigger |
|---|---|
| Requests waiting in front of the pool (HTTP.sys request queue) | More than **100**, on 2 checks in a row |
| Requests stuck inside the pool for 30 seconds or more | More than **50**, on 2 checks in a row |
| ASP.NET error Event ID **1309** for the pool in the Application log | **1** within 10 minutes |

After a recycle, the same pool is **not recycled again for 10 minutes** (the cooldown), however bad things look. This prevents recycle loops.

---

## 2. Where everything is

| What | Where |
|---|---|
| Program and settings | `C:\Program Files\ExchangeAppPoolMonitor` |
| Settings file | `C:\Program Files\ExchangeAppPoolMonitor\monitorsettings.json` |
| Logs | `C:\Program Files\ExchangeAppPoolMonitor\logs` (one `actions-` and one `service-` file per day, kept 90 days) |
| Status page | `http://localhost:8085` on the server itself |
| Windows service | `services.msc` > **Exchange AppPool Monitor** |
| Event Viewer | Windows Logs > Application, source **ExchangeAppPoolMonitor** |
| Start menu | **Exchange AppPool Monitor** folder: Status page, Edit settings, Logs folder, README, Runbook, Maintainer's guide, License, Uninstall |
| This runbook and the other guides | `README.md` in the install folder; this file, MAINTAINING.md and DEPENDENCIES.md in its `docs` subfolder |

---

## 3. Daily or weekly check (2 minutes)

On each server with the service:

1. Open the **Status page** (Start menu, or `http://localhost:8085`).
2. Check:
   - **Monitor** shows **Running** (green). If it shows **DRY RUN**, the service is only reporting, not recycling (see [section 7](#7-dry-run-mode)).
   - **Last check** is within the last minute.
   - Both pools show **Started**, with numbers (not `-`) in **Queue depth** and **Requests 30s+**, and no red error text under the pool name.
3. Glance at the **last 25 log entries** for any red **Error** lines.

Anything unexpected: see [section 6](#6-troubleshooting).

---

## 4. What the emails mean and what to do

The service sends one email per action. Subjects start with `[AppPool Monitor]` (changeable in settings).

### "Recycled MSExchange...AppPool on SERVER"

The service saw a problem and recycled the pool. The email body says which sign triggered it, for example:

```
Reason: 2140 request(s) have been running 30 seconds or longer (oldest 184 s),
over the threshold of 50, for 2 consecutive check(s).
```

**What to do:**

1. Usually nothing. This is the service doing its job. Users may have seen a short delay.
2. Open the status page a minute later and check the pool is **Started** and the numbers are back near 0.
3. If the same server sends these **several times a day**, something underneath is wrong (a back-end dependency, Active Directory, a load balancer health check, a bad client). The recycles are treating the symptom. Investigate the cause, and tell whoever owns Exchange.
4. If it happens during normal busy periods when nothing was actually wrong, the thresholds may be too low for this server. See [section 8](#8-tuning-the-thresholds).

### "FAILED to recycle MSExchange...AppPool on SERVER"

The service tried to recycle but IIS refused or did not answer. The body includes the error.

**What to do:**

1. Open IIS Manager on the server and look at the pool. If it is **Stopped**, start it. A stopped pool cannot be recycled, and Exchange services that use it are down.
2. If it is started, recycle it by hand (IIS Manager > Application Pools > right-click > Recycle) and watch the status page.
3. If IIS itself is not responding, follow your normal Exchange procedure for a sick server (maintenance mode, move databases, reboot). This is the situation the service tries to prevent; it was not able to in this case.
4. The service will not try again for 10 minutes (the cooldown), and will send another email if it does.

### "[DRY RUN] Would recycle MSExchange...AppPool on SERVER"

The service is in dry run mode. It saw a trigger and is reporting what it **would** have done. Nothing was recycled. Use these during the first days to check the thresholds match real problems (see [section 7](#7-dry-run-mode)).

### No emails at all

That is normal when nothing is wrong. To confirm email still works, see [section 6](#6-troubleshooting), "No emails".

---

## 5. Turning it off quickly

| Need | Do this | Effect |
|---|---|---|
| **Stop it acting right now, keep watching** | Edit settings, set `"DryRun": true`, save | Takes effect at the next check (seconds). It keeps logging and emailing "would recycle", but never recycles. No restart needed. |
| **Stop it completely** | `Stop-Service ExchangeAppPoolMonitor` (or services.msc > Stop) | Nothing is checked or recycled until started again. It starts again at the next reboot unless you also set the startup type to Disabled: `Set-Service ExchangeAppPoolMonitor -StartupType Disabled`. |
| **Turn off one trigger** | In settings set `LongRunningRequests.Enabled` or `Event1309.Enabled` to `false`, or set `QueueThreshold` very high (for example `100000`) | The other triggers keep working. |
| **Remove it** | Settings > Apps > Exchange AppPool Monitor > Uninstall | Service and program removed. You are asked whether to keep the settings and logs. |

Stopping or uninstalling the service does **not** affect Exchange or IIS. The service only ever recycles pools; it changes nothing else.

---

## 6. Troubleshooting

| Symptom | Check | Fix |
|---|---|---|
| Status page does not open | Is the service running? `Get-Service ExchangeAppPoolMonitor` | Start it. If it stops again, read the end of today's `logs\service-*.log` for a `[Critical]` line. |
| Service will not start | End of today's `logs\service-*.log` | Usual causes: settings file has a typo (see below), or port 8085 is in use by something else (change `StatusPageUrl` in settings). |
| Settings typo | `Get-Content "C:\Program Files\ExchangeAppPoolMonitor\monitorsettings.json" -Raw \| ConvertFrom-Json` | An error points at the problem, usually a missing comma or quote. No output means the file is valid. |
| "Access denied" when saving settings | | Use the **Edit settings** Start menu shortcut (it opens Notepad as administrator), or another editor run as administrator. |
| A pool shows `Unknown` or red error text | The error text on the status page | `appcmd.exe not found`: IIS tools missing. `No request queue counter instance`: pool name in settings does not match IIS. `Could not list long running requests`: the IIS Request Monitor feature is missing (`Install-WindowsFeature Web-Request-Monitor`). |
| `Performance counter category ... does not exist` | | Counters are damaged. Elevated prompt: `lodctr /R`, then restart the service. |
| **Last check** is not moving | | The loop is stuck, most likely IIS is not answering. Restart the service. If IIS is unresponsive, treat the server as sick. |
| No emails | Log for `Email notification failed` | Check `SmtpServer`, `Port`, `From`, `To` in settings, and that the SMTP server accepts mail from this server (for `localhost`, the receive connector must allow anonymous relay from 127.0.0.1). |
| Recycling over and over | Log: reasons and times | The cooldown limits it to once per 10 minutes per pool. If it keeps happening, the cause is elsewhere; see section 4. Switch to dry run while investigating if needed. |
| 1309 errors in Event Viewer but not counted | | Only events logged after the service started are counted. The event's process must belong to a watched pool. See README "Trigger 3". |

Every error the service hits is also in the log (once per distinct problem, not every 10 seconds), on the status page, and in Event Viewer.

---

## 7. Dry run mode

In dry run mode the service does everything except the actual recycle: it checks, decides, logs `[DRY RUN] Would recycle` and emails. New installs start in dry run.

**Recommended:** leave dry run on for the first few days on each server. Compare the dry run emails with what actually happened (user complaints, monitoring alerts). When they line up, turn it off:

1. Start menu > Exchange AppPool Monitor > **Edit settings**.
2. Change `"DryRun": true` to `"DryRun": false`. Save.
3. The status page stops showing the DRY RUN badge within seconds.

---

## 8. Tuning the thresholds

All in `monitorsettings.json` (Edit settings shortcut). Changes apply at the next check; no restart.

| To | Change |
|---|---|
| React to smaller backlogs (earlier) | Lower `QueueThreshold` and/or `LongRunningRequests.Threshold` |
| Fewer unnecessary recycles | Raise them, or raise `ConsecutiveSamplesBeforeRecycle` from 2 to 3 |
| React faster | Lower `PollIntervalSeconds` (minimum 2) or set `ConsecutiveSamplesBeforeRecycle` to 1 |
| Ignore occasional 1309s | Raise `Event1309.ErrorCount` (for example 3 within `WindowMinutes` 10) |
| Different limits per pool | Add `"QueueThreshold": ...` or `"LongRunningThreshold": ...` to that pool's entry in `AppPools` |

To see what normal looks like on a server, run this during a busy period and set thresholds well above what you see:

```powershell
# Requests waiting in front of each pool
Get-Counter '\HTTP Service Request Queues(*)\CurrentQueueSize' -SampleInterval 5 -MaxSamples 12

# Requests stuck inside each pool for 30 seconds or more
foreach ($p in 'MSExchangeAutodiscoverAppPool','MSExchangeDefaultAppPool') {
    $n = (& "$env:windir\system32\inetsrv\appcmd.exe" list requests /apppool.name:$p /elapsed:30000 | Select-String '^REQUEST').Count
    "{0,-32} {1}" -f $p, $n
}
```

The full list of settings is in the README, "Configuration reference".

---

## 9. Upgrading, moving, removing

- **Upgrade:** run the newer setup on the server. It stops the service, replaces the program, keeps settings and logs, and restarts the service if it was running.
- **New server:** run the setup there. To copy settings from another server, copy its `monitorsettings.json` into the new install folder after setup, then restart the service.
- **Remove:** Settings > Apps > Exchange AppPool Monitor > Uninstall.
- **Air-gapped servers:** use the **standalone** setup. It contains everything, including .NET, and needs nothing else installed. See [DEPENDENCIES.md](DEPENDENCIES.md).

---

## 10. Record for this environment

Fill this in when deploying, and keep it with your other Exchange documentation, so the next person knows how it was set up.

| Item | Value |
|---|---|
| Servers it is installed on | |
| Edition (standalone or dotnet6) and version | |
| Where the setup files and source are kept | |
| Who receives the emails (`Email:To`) | |
| SMTP server used | |
| Thresholds, if changed from the defaults, and why | |
| Date dry run was turned off | |
| SonarQube project / review record | |
| Who to ask about it | |
