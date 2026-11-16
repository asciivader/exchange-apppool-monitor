# Exchange AppPool Monitor: maintainer's guide

For whoever looks after the code after the original author. It explains how the program is put together, why it was built the way it was, how to make common changes, and how to build, test and release a new version. It assumes you can read C#, but not that you know this code.

Author: Nathan Kelly. Free software under the MIT License (see [LICENSE](../LICENSE)).

Related documents:

| Document | For |
|---|---|
| [README.md](../README.md) | What the app does, every setting, installing, status page, logs, troubleshooting |
| [RUNBOOK.md](RUNBOOK.md) | Day to day operation: what the emails mean, what to do, how to turn it off |
| [DEPENDENCIES.md](DEPENDENCIES.md) | What it depends on, and building in an air-gapped environment |
| This guide | How the code works and how to change it |

---

## Contents

1. [The problem it solves](#1-the-problem-it-solves)
2. [Architecture at a glance](#2-architecture-at-a-glance)
3. [Source files](#3-source-files)
4. [What happens on each check](#4-what-happens-on-each-check)
5. [The three triggers in detail](#5-the-three-triggers-in-detail)
6. [Settings](#6-settings)
7. [Logging](#7-logging)
8. [Running as a Windows service](#8-running-as-a-windows-service)
9. [The installer](#9-the-installer)
10. [Design decisions and why](#10-design-decisions-and-why)
11. [How to make common changes](#11-how-to-make-common-changes)
12. [Building and releasing a new version](#12-building-and-releasing-a-new-version)
13. [Testing](#13-testing)
14. [Moving to a newer .NET](#14-moving-to-a-newer-net)
15. [Known limitations and ideas](#15-known-limitations-and-ideas)
16. [Glossary](#16-glossary)
17. [Handover checklist](#17-handover-checklist)

---

## 1. The problem it solves

On the Exchange servers this was written for, the IIS app pools **MSExchangeAutodiscoverAppPool** and **MSExchangeDefaultAppPool** occasionally hang. Requests go in and do not come out. Left alone, the server degrades until the only fix is the long way: put the server into maintenance mode, move its databases to another server, reboot, and bring it back.

The author found that **recycling the affected pool early**, while the backlog is still building, avoids that. The signs that a pool is going bad, in the order they usually appear:

1. Requests that have been running inside the pool for 30 seconds or more start to pile up. In a bad case they reach the thousands. Before this tool, this was checked by hand with `appcmd list requests /elapsed:30000`.
2. Requests start queuing in front of the pool (the HTTP.sys request queue grows).
3. ASP.NET writes Event ID 1309 errors for the pool.

The program automates the check and the recycle, around the clock, and tells people when it acts. It deliberately does **one** thing (recycle a pool) and changes nothing else on the server.

---

## 2. Architecture at a glance

One process, `ExchangeAppPoolMonitor.exe`, running as a Windows service under LocalSystem. Inside it:

```
                         ExchangeAppPoolMonitor.exe  (one process)
 +-------------------------------------------------------------------------------------+
 |                                                                                     |
 |   Program.cs: startup. Loads monitorsettings.json, sets up logging, registers parts  |
 |                                                                                     |
 |   +------------------------------+        +------------------------------------+    |
 |   | MonitorWorker (background    |        | Web server (ASP.NET Core / Kestrel)|    |
 |   | loop, one thread)            |        |  GET /           StatusPage.cs     |    |
 |   |                              |        |  GET /api/status  (JSON)           |    |
 |   |  every PollIntervalSeconds:  |        +------------------+-----------------+    |
 |   |   per pool:                  |                           | reads                |
 |   |    RequestQueueReader -------+--> pdh.dll (perf counter) |                      |
 |   |    AppCmd (list requests) ---+--> appcmd.exe             v                      |
 |   |    decide -> AppCmd recycle -+--> appcmd.exe     +---------------+              |
 |   |    EmailNotifier ------------+--> SMTP server    | MonitorState  | (latest      |
 |   |    publish status -----------+-----------------> | per pool)     |  status)     |
 |   |                              |                   +---------------+              |
 |   |  every Event1309 interval:   |                                                  |
 |   |    AppCmd (list wp) ---------+--> appcmd.exe     +---------------+              |
 |   |    AspNetErrorReader --------+--> Application    | ActionLog     | (last 25     |
 |   |                              |    event log      | entries)      |  + files)    |
 |   |  every action/problem -------+-----------------> +---------------+              |
 |   +------------------------------+                                                  |
 |                                                                                     |
 |   WindowsServiceLifetime: talks to the Service Control Manager (advapi32.dll)        |
 |   ServiceFileLoggerProvider + Event Viewer + console: where log messages go          |
 +-------------------------------------------------------------------------------------+
```

Key points:

- **Single decision-maker.** Only `MonitorWorker` decides and acts, on one background thread. No locking is needed inside it.
- **Shared state is read-only for the web side.** The web page reads `MonitorState` (immutable records swapped in each check) and `ActionLog` (locked list). It cannot change anything.
- **External effects go through three small classes:** `AppCmd` (IIS), `EmailNotifier` (SMTP), `ActionLog` (files). Everything else is reading.

---

## 3. Source files

All in the project folder (`infra/exchange-apppool-monitor` in the repository). Every file starts with a comment explaining its purpose, and the code is commented throughout; this table is the map.

| File | Responsibility | Read this when |
|---|---|---|
| `Program.cs` | Startup: settings file, logging destinations, service registration, the two web routes, startup failure handling. Also hands `--export-settings` / `--import-settings` to `SettingsCli`. | Changing startup, routes, logging destinations |
| `MonitorWorker.cs` | **The core.** The loop, the three triggers, the cooldown, recycling, building emails. | Changing what it watches or how it decides |
| `Settings.cs` | `MonitorSettings`, `AppPoolSettings`, `LongRunningRequestSettings`, `Event1309Settings`, `EmailSettings`: every setting and its default. | Adding or changing a setting |
| `RequestQueueReader.cs` | Reads `HTTP Service Request Queues(*)\CurrentQueueSize` through `pdh.dll`. | Queue depth reads fail or look wrong |
| `AppCmd.cs` | Runs `appcmd.exe`: pool state, `list wp` (PID to pool), `list requests` (long running requests), `recycle apppool`. Parses the output. 20 second timeout. | appcmd output or behaviour changes |
| `AspNetErrorReader.cs` | Reads new Event ID 1309 entries from the Application log and extracts the w3wp.exe PID. | 1309 detection problems |
| `EmailNotifier.cs` | Sends one plain text email over SMTP. Never throws. | Email problems or format changes |
| `ActionLog.cs` | The action log: daily `actions-*.log` JSON-lines files, the last 25 entries in memory, a copy to the normal .NET log. | Changing log content or format |
| `ServiceFileLoggerProvider.cs` | Sends standard .NET log messages to the daily `service-*.log` text file. | Changing the service log |
| `LogFiles.cs` | Log folder and file naming, retention cleanup, the last-resort startup failure writer. | Changing log locations or retention |
| `MonitorState.cs` | `PoolStatus` record and the shared status store the web page reads. | Adding something to the status page |
| `StatusPage.cs` | Builds the status page HTML (no scripts, no external files, everything HTML-encoded). | Changing the page |
| `SettingsCli.cs` | `--export-settings` / `--import-settings`: converts between the installer's INI answers and `monitorsettings.json`. | Changing the installer's settings pages |
| `WindowsServiceLifetime.cs` | Runs as a Windows service using `advapi32.dll` (start, stop, shutdown, status reporting). | Service start/stop problems |
| `AssemblyInfo.cs` | Marks the assembly Windows-only. | Rarely |
| `monitorsettings.json` | Default settings shipped with the app. | Changing shipped defaults |
| `ExchangeAppPoolMonitor.csproj` | .NET 6 target, version, author details, the two editions, build options. No NuGet packages. | Version bump, .NET upgrade |
| `installer/ExchangeAppPoolMonitor.nsi` | The NSIS installer: pages, service creation, Apps entry, upgrade, uninstall. | Changing installation |
| `installer/build-installer.ps1` / `.sh` | Build both installers (Windows / Linux or macOS). | Building a release |
| `docs/*.md`, `README.md`, `LICENSE` | Documentation and license. Installed with the app. | |

---

## 4. What happens on each check

`MonitorWorker.ExecuteAsync` runs for the life of the service. Each pass of the loop calls `PollAsync`, then sleeps until the next check of either kind is due.

```
PollAsync
 |
 +-- If PollIntervalSeconds have passed since the last pool check:
 |     for each pool in settings (one after another, never in parallel):
 |       CheckPoolAsync(pool)
 |         1. Queue depth      RequestQueueReader.Read(pool)            (pdh.dll, fast)
 |         2. Long running     AppCmd.CountLongRunningRequestsAsync     (appcmd list requests)
 |         3. Decide           queue over threshold N checks in a row?           -> reason
 |                             else long running over threshold N in a row?      -> reason
 |                             else enough 1309s in the window?                  -> reason
 |         4. Act              reason and not in cooldown -> RecycleAsync
 |                             reason but in cooldown     -> log once, wait
 |         5. Pool state       AppCmd.GetStateAsync (for display only, after acting)
 |         6. Log any read problem (only when it changes)
 |         7. Publish          MonitorState.SetPool(new PoolStatus(...))
 |
 +-- If Event1309.CheckIntervalSeconds have passed since the last 1309 check:
       CollectAspNetErrorsAsync
         1. AppCmd.ListWorkerProcessesAsync -> remember PID -> pool (kept 1 hour)
         2. AspNetErrorReader.ReadNew       -> new 1309 events since last time
         3. For each event: match to a pool (PID first, EventMatches text second)
            and record its time in that pool's list. Acted on at the next pool check.

RecycleAsync(pool, reason)
  DryRun?  yes -> log "[DRY RUN] Would recycle", email
           no  -> appcmd recycle apppool -> log "Recycled" (or "Recycle FAILED"), email
  Then, in every case: start the cooldown, reset both streaks and the 1309 list.
```

**Ordering is deliberate.** The queue read uses no external process, so nothing can delay it. The long running count needs appcmd, but it is a trigger, so it is read before deciding. The pool state is display only, so it is read last; a slow IIS can never delay a recycle because of it.

**Errors never stop the loop.** Each read is wrapped; a failure is recorded on the status page and written to the log **once** (the message is remembered and only logged again if it changes). A failure in one pool does not affect the other. An unexpected exception in a whole pass is caught by the safety net in `ExecuteAsync`.

**Timing.** The loop computes the next due time of either kind of check and sleeps exactly until then, so the two intervals are independent (tested: queue checks every 4 s and 1309 checks every 10 s gave 8 and 4 checks in 31 s).

---

## 5. The three triggers in detail

### Trigger 1: request queue depth

- **Source:** performance counter `\HTTP Service Request Queues(<pool>)\CurrentQueueSize`. HTTP.sys keeps one queue per app pool, named after it. Requests wait there until w3wp.exe picks them up.
- **How it is read:** `RequestQueueReader` opens one PDH query with the wildcard path `...(*)\CurrentQueueSize`, collects a sample, and looks up the pool by name (case-insensitive). The English counter name is used (`PdhAddEnglishCounter`) so it works on any Windows language.
- **Fires when:** depth **greater than** `QueueThreshold` (or the pool's own `QueueThreshold`) on `ConsecutiveSamplesBeforeRecycle` checks in a row. Equal does not count. A failed read resets the streak.
- **Edge cases:** no queue for the pool name gives "No request queue counter instance found" (usually a typo in the pool name). A missing counter category gives a message suggesting `lodctr /R`.

### Trigger 2: long running requests

- **Source:** `appcmd list requests /apppool.name:<pool> /elapsed:<ms>`, which lists requests that have been executing inside the pool's w3wp.exe for at least that long. Requires the IIS Request Monitor feature (Exchange setup installs it).
- **How it is read:** `AppCmd.CountLongRunningRequestsAsync` counts lines starting with `REQUEST ` and parses `time:NNN msec` to find the oldest.
- **Fires when:** count **greater than** `LongRunningRequests.Threshold` (or the pool's `LongRunningThreshold`) on `ConsecutiveSamplesBeforeRecycle` checks in a row. Default 50 requests older than 30 seconds.
- **Why it matters most:** this is the measure the author used by hand, and it usually climbs first when a pool hangs.

### Trigger 3: ASP.NET Event ID 1309

- **Source:** the Application event log, events with ID 1309 from a source whose name starts with `ASP.NET` (an unhandled exception in an ASP.NET app, "Event code: 3005").
- **How it is read:** `AspNetErrorReader` queries the log with an XPath filter for ID 1309 newer than the last event it saw, and remembers the last record ID so nothing is counted twice. Events from before the service started are ignored on purpose.
- **Which pool:** the event data contains the w3wp.exe PID (the value just before `w3wp.exe`). The PID is looked up in the map built from `appcmd list wp`, which remembers PIDs for an hour so an event from a process that has since died still matches. If the PID is unknown, the pool's `EventMatches` text (if any) is compared with the event text. If neither matches, the event belongs to another pool and is ignored.
- **Fires when:** at least `Event1309.ErrorCount` matched events within the last `Event1309.WindowMinutes`.

### Cooldown and dry run

- **Cooldown** (`RecycleCooldownMinutes`, default 10): after a recycle, a failed recycle attempt, or a dry run "would recycle", the same pool is not acted on again until the cooldown ends. A trigger during the cooldown is logged once per incident. This prevents recycle loops and email floods.
- **Dry run** (`DryRun`): everything happens except the `appcmd recycle` call. New installs start in dry run. It is read every check, so flipping it in the settings file takes effect within seconds.

---

## 6. Settings

- **File:** `monitorsettings.json` next to the exe. Required: the service will not start without it.
- **Loading:** `Program.cs` adds it with `reloadOnChange: true`. The classes in `Settings.cs` are bound to the `Monitor` and `Email` sections. The code reads them through `IOptionsMonitor<T>.CurrentValue`, so **every check uses the latest saved file**. Only `StatusPageUrl` is read once at startup.
- **Defaults:** the values after `=` in `Settings.cs` apply when a key is missing. The shipped `monitorsettings.json` holds the same values. Keep the two in step.
- **Lists default to empty** on purpose: the .NET configuration binder adds JSON list items to whatever a list already holds, so a non-empty default list would get duplicates. `EffectivePools()` supplies the two default pools when the list is empty.
- **Installer pages:** setup writes some settings through `SettingsCli` (see [section 9](#9-the-installer)). The keys it understands are listed at the top of `SettingsCli.cs`.

---

## 7. Logging

Three destinations, all fed from the standard .NET logging (`ILogger`):

| Destination | What | Controlled by |
|---|---|---|
| `logs\actions-yyyy-MM-dd.log` | The **action log**: what was done to pools, triggers, emails, problems. JSON, one object per line. The status page shows the last 25. | Always written (not filtered by log level) |
| `logs\service-yyyy-MM-dd.log` | The **service log**: everything, including startup, shutdown and web server errors, plus a text copy of each action log entry | `Logging:LogLevel` |
| Windows Application event log, source `ExchangeAppPoolMonitor` | The same messages, for Event Viewer and any log collection tools | `Logging:EventLog:LogLevel` |

How it fits together: code calls `ActionLog.Info/Action/Warning/Error`. `ActionLog` writes the JSON line, keeps it in memory, and passes a text copy to `ILogger`, which sends it to the service log, Event Viewer and the console. Other code (and .NET itself) logs straight to `ILogger`.

If the service cannot even start (bad settings file, port in use), `Program.cs` catches the exception and `LogFiles.WriteFatal` writes it to the service log as `[Critical]` before the process exits.

Files older than `LogRetentionDays` (default 90) are deleted once a day by `LogFiles.Cleanup`, which only touches files named `actions-` or `service-` plus a date.

---

## 8. Running as a Windows service

`WindowsServiceLifetime.cs` replaces Microsoft's `Microsoft.Extensions.Hosting.WindowsServices` package so the project needs no NuGet packages. Its comments describe the full sequence; in short:

1. `Program.cs` registers it only when `Environment.UserInteractive` is false (that is, when started by the Service Control Manager). From a console the app uses .NET's normal lifetime and runs until Ctrl+C. This is how to run it by hand for testing.
2. `WaitForStartAsync` starts a thread that calls `StartServiceCtrlDispatcher` (blocks until the service stops).
3. Windows calls `ServiceMain`, which registers the control handler, reports **Running**, and lets the host start the monitor loop and web server.
4. On Stop or system shutdown, `HandleControl` reports **Stop Pending** (30 second hint) and asks the host to stop.
5. When the host has stopped, `StopAsync` reports **Stopped**. If the app stops without being asked, it reports Stopped **with an error code**, so the recovery options apply.

The installer sets recovery to restart the service a minute after a failure, up to three times a day (`sc.exe failure`), including when it stops with an error rather than crashing (`sc.exe failureflag 1`).

**If the service will not start** with "The service did not respond to the start or control request in a timely fashion" (error 1053): check the end of `logs\service-*.log`. A `[Critical]` entry there means startup failed (often settings); no entry at all means the process could not start (missing files). Run the exe from an elevated console to see startup errors directly.

---

## 9. The installer

Built with NSIS 3 from `installer/ExchangeAppPoolMonitor.nsi`. The script's header comment lists everything it does. Highlights for maintainers:

- **Two editions from one script:** `FLAVOR=Standalone` or `DotNet6` (passed by the build scripts). `!if` blocks handle the differences: the dotnet6 edition checks for the ASP.NET Core 6 runtime in `.onInit`; the standalone edition sets `DOTNET_BUNDLE_EXTRACT_BASE_DIR` in the service's environment so the single exe unpacks its native files into the install folder's `runtime` subfolder rather than the temp folder.
- **Settings pages:** two `nsDialogs` pages. Flow: when Next is pressed on the folder page, `LoadSettingsForPages` writes defaults to an INI file in the installer's temporary folder, and on an upgrade runs the installed exe with `--export-settings` to load the current values. The pages read and write that INI file. After the files are copied, the install section runs the new exe with `--import-settings` to write the answers into `monitorsettings.json`. Silent installs skip all of this and leave the file alone.
- **Upgrade:** stops the service if running, deletes the old program files except `monitorsettings.json` and `logs` (only if the folder contains the exe), copies the new files, keeps the settings file, writes `monitorsettings.default.json`, updates the service, restarts it if it was running.
- **Uninstall:** stops and deletes the service, asks whether to keep settings and logs (silent: keep), removes shortcuts and the Apps entry.
- **Apps entry:** `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\ExchangeAppPoolMonitor`, written in the 64-bit registry view.

To add a field to a settings page: add the control in the page's `...Create` function, read and validate it in `...Leave`, write it to the INI, add a default in `LoadSettingsForPages`, and teach `SettingsCli.cs` the new key in both `Export` and `Import`.

---

## 10. Design decisions and why

| Decision | Why | Alternatives considered |
|---|---|---|
| Recycle (not restart IIS, not reboot) | A recycle is graceful (IIS starts a new worker before the old one finishes), affects one pool, and is what an admin would do first by hand | iisreset (affects every site), service restarts (heavier) |
| Use `appcmd.exe` for IIS | Always present with IIS, same on every version, commands match what admins type, no extra libraries | Microsoft.Web.Administration (extra dependency, version issues) |
| Read the queue with PDH | Part of Windows, no package, same data as Performance Monitor | System.Diagnostics.PerformanceCounter package (used in the NuGet version) |
| Watch long running requests | It was the author's own early warning sign, and it usually rises first | Queue depth alone (rises later) |
| Two checks in a row before acting | Avoids recycling on a momentary spike | Acting on one reading (configurable: set to 1) |
| Cooldown after every attempt, including failures and dry runs | Prevents recycle loops and email floods when a recycle does not fix the cause | No cooldown |
| Dry run by default on new installs | Lets thresholds be checked against real traffic before anything is recycled | Live by default |
| One background thread for all decisions | No locking or race conditions; two pools are checked in under a second | Parallel checks |
| Ignore 1309s from before startup | A restart of the service must not cause a recycle from old events | Read the whole log |
| Match 1309s by PID, then text | The same virtual path (for example /Autodiscover) exists on both Exchange web sites, so text alone can point at the wrong pool | Text only |
| Own log files plus Event Viewer | Files survive event log rollover and can be kept 90 days; Event Viewer suits log collection tools | One or the other |
| Status page bound to localhost, read only | Nothing on it can change anything; no login needed while it is local | Authentication (complexity) |
| .NET 6, C# 10 | What is available at the site | .NET 8 (see section 14) |
| No NuGet packages | Air-gapped environment and supply chain review | The NuGet version (kept on a branch) |
| Two editions | Standalone needs nothing installed (air-gapped); dotnet6 is tiny where the runtime is managed centrally | One edition |
| NSIS for the installer | Builds on any OS, supports custom pages, service setup, recovery options | MSI via WiX (Windows-only to build; possible future option) |

---

## 11. How to make common changes

### Watch another app pool

No code change: add it to `Monitor:AppPools` in `monitorsettings.json`:

```json
{ "Name": "MSExchangeOWAAppPool", "EventMatches": [] }
```

Optionally give it its own `QueueThreshold` / `LongRunningThreshold`. The status page and all triggers pick it up at the next check.

### Change a default

Change it in **both** `Settings.cs` (the value after `=`) and `monitorsettings.json`. If the installer pages show it, also change the default in `LoadSettingsForPages` in the `.nsi`. Update the README configuration tables.

### Add a setting

1. Add a property with a default and a `///` comment to the right class in `Settings.cs`.
2. Add it to `monitorsettings.json`.
3. Use it in the code through the settings object passed in (`s.YourSetting`).
4. Document it in the README "Configuration reference".
5. If the installer should ask for it, see the end of [section 9](#9-the-installer).

### Add a trigger

In `MonitorWorker.CheckPoolAsync`:

1. Gather the value (wrap it in try/catch and add any error message to `errors`).
2. Add a streak counter to `Tracking` if it should require consecutive checks.
3. Add an `if (reason is null && ...)` block in the Decide section that sets `reason` to a sentence explaining what was seen (it goes into the log and the email as is).
4. Reset its state in `RecycleAsync` alongside the others.
5. Add fields to `PoolStatus` and columns to `StatusPage.cs` if it should be visible.
6. Add settings, docs, and a row in the README trigger sections.

### Change the email

Subject and body are built in `MonitorWorker.RecycleAsync` (three cases: dry run, recycled, failed). Sending is in `EmailNotifier.cs`.

### Change the status page

`StatusPage.Render` builds the HTML as a string. Always pass values through `H()` (HTML encoding). The JSON at `/api/status` is built in `Program.cs` from `MonitorState`.

### Change the status page port or listen address

Setting `Monitor:StatusPageUrl`, read once at startup (restart the service). To make it reachable from other machines use `http://*:8085` and open the port in the Windows firewall for admin machines only.

---

## 12. Building and releasing a new version

### Prerequisites (build machine)

- .NET 6 SDK (x64). A newer SDK also works (it shows warning NETSDK1138 about .NET 6 being out of support).
- NSIS 3, for the installers.
- For air-gapped builds: the runtime package files, see [DEPENDENCIES.md](DEPENDENCIES.md).

### Release checklist

1. Make and review the change. Keep the code comments and docs in step with it.
2. Bump `<Version>` in `ExchangeAppPoolMonitor.csproj` (for example 1.0.0 to 1.1.0). This sets the exe version, the installer file names and the version shown in Apps.
3. Run the SonarQube analysis (README, "Code review and SonarQube analysis"). Only the documented S5332 hotspot should remain.
4. Build both installers: `.\installer\build-installer.ps1` (add `-PackageSource <folder>` when offline). Output: `artifacts\ExchangeAppPoolMonitor-Setup-<version>-standalone.exe` and `-dotnet6.exe`.
5. Test on a test Exchange server ([section 13](#13-testing)).
6. Roll out: run the setup on each server (or silently with `/S`). Upgrades keep settings and logs.
7. Keep the setup files and the source for that version together, and update the environment record in RUNBOOK.md section 10.

---

## 13. Testing

There are no automated tests in the project; it is small and most of its behaviour depends on IIS, Exchange and Windows. Test on a **non-production Exchange server** (or a Windows Server with IIS, which covers everything except realistic load):

| # | Test | Expected |
|---|---|---|
| 1 | Install the standalone edition interactively; fill in the settings pages | Service **Exchange AppPool Monitor** exists, startup type Automatic (Delayed Start), recovery set to restart; Apps shows it with publisher and version |
| 2 | Start the service | Status Running in services.msc within a few seconds; `logs\service-*.log` shows "Running as the Windows service"; status page loads |
| 3 | Status page | Both pools Started, with numbers in Queue depth and Requests 30s+, no red errors |
| 4 | Force a trigger in dry run: set `LongRunningRequests.Threshold` to `-1` and `ConsecutiveSamplesBeforeRecycle` to `1` | Within one check: `[DRY RUN] Would recycle` in the log for each pool, emails received; then cooldown messages, no repeat emails |
| 5 | Set `DryRun` to `false` with the forced threshold | Each pool is recycled once (check IIS Manager or `appcmd list wp` for new PIDs), "Recycled" emails; then cooldown |
| 6 | Restore the thresholds | Normal quiet operation |
| 7 | Stop and start the service; reboot the server | Clean stop (Stopped, no error in Event Viewer System log); starts automatically after boot |
| 8 | End the process in Task Manager | Windows restarts the service after a minute (recovery) |
| 9 | Run the setup again (upgrade) | Settings pages show the current values; settings and logs kept; service restarted if it was running |
| 10 | Install the dotnet6 edition over it (switch editions), and on a server without the runtime | Upgrade works; without the runtime, setup stops with the runtime message |
| 11 | Uninstall from Apps, keeping settings | Service removed; only `monitorsettings.json` and `logs` left in the folder |

To test a 1309 match, a real 1309 is needed (they cannot easily be faked with the right source). Leave the service in dry run on a server that produces them and check the log lines "ASP.NET 1309 error logged".

**Running by hand:** from an elevated console, `ExchangeAppPoolMonitor.exe` runs in the foreground with log output on screen (Ctrl+C to stop). Stop the service first so the port is free.

---

## 14. Moving to a newer .NET

.NET 6 has been out of support since November 2024 (no more security fixes). To move to .NET 8 (also MIT, long-term support):

1. In `ExchangeAppPoolMonitor.csproj`: `TargetFramework` to `net8.0`, `LangVersion` to `12.0` (or remove it).
2. Build. The code uses nothing that changed between 6 and 8, so it should build as is; check for new warnings.
3. dotnet6 edition: rename it (for example `DotNet8`) in the `.csproj`, the `.nsi` (`FLAVOR`, the runtime check folder names `6.0.*` to `8.0.*`, the messages) and the build scripts, and require the ASP.NET Core Runtime 8.0 on servers.
4. Standalone edition: nothing else; it bundles whatever runtime it is built against.
5. Update the docs (search for "6.0" and ".NET 6").
6. Run the full test list.

---

## 15. Known limitations and ideas

- **Per server.** Each server runs its own copy and knows nothing about the others. It will not stop several servers recycling at once (they each recycle only their own pools, which is usually fine).
- **Counters reset on restart.** Recycle counts and the 1309 window are in memory. The log files keep the history.
- **A stopped pool is not started.** It shows red on the status page; the recycle attempt fails and is emailed once per cooldown. Starting stopped pools automatically was left out on purpose (a pool may have been stopped by an admin).
- **1309 PID extraction** relies on the standard ASP.NET 4.x event layout (PID just before `w3wp.exe`). `EventMatches` is the fallback.
- **Status page has no authentication**; it relies on listening on localhost.
- **SMTP password in plain text** in the settings file; protect the folder (README, Security notes).
- Ideas not done: MSI installer (WiX); watching `MaxQueueItemAge` (the age of the oldest queued request) as another early trigger; a daily summary email; central collection of status from several servers via `/api/status`.

---

## 16. Glossary

| Term | Meaning |
|---|---|
| App pool | An IIS application pool: a group of web applications that share worker processes |
| w3wp.exe | The IIS worker process that runs an app pool's code. One or more per pool |
| Recycle | IIS starts a new worker process for the pool and retires the old one gracefully |
| HTTP.sys | The Windows kernel driver that receives HTTP requests and queues them per app pool |
| Request queue | Requests HTTP.sys is holding for a pool until w3wp.exe picks them up |
| Long running request | A request w3wp.exe has been executing for at least `OlderThanSeconds` |
| Event ID 1309 | ASP.NET warning for an unhandled exception in a web application |
| appcmd | `%windir%\system32\inetsrv\appcmd.exe`, the IIS command line tool |
| PDH | Performance Data Helper, the Windows API for reading performance counters (`pdh.dll`) |
| SCM | Service Control Manager, the part of Windows that starts and stops services |
| Cooldown | Minutes after an action during which the same pool is not acted on again |
| Dry run | Mode where everything happens except the actual recycle |
| Standalone / dotnet6 | The two editions: .NET bundled in the exe, or using an installed .NET 6 runtime |
| NSIS | Nullsoft Scriptable Install System, used to build the installers |

---

## 17. Handover checklist

For the person leaving and the person taking over:

- [ ] Where the source repository lives, and who has access
- [ ] Where the released setup files are kept, per version
- [ ] Which servers run it, which edition, which version (RUNBOOK section 10)
- [ ] Current settings per server, and why any differ from the defaults
- [ ] Who receives the emails; whether that list still makes sense
- [ ] The SMTP server and any account it uses
- [ ] SonarQube project name and the last review result
- [ ] Any open issues or ideas in progress
- [ ] A walk through the RUNBOOK with whoever operates it
- [ ] The build machine set up (SDK, NSIS, runtime package files for air-gapped builds) and one test build made by the new maintainer
