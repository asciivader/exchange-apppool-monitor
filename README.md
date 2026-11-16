# Exchange AppPool Monitor

Written by **Nathan Kelly**. Free software, distributed under the **[MIT License](LICENSE)**: free to use, copy, modify and share, including for commercial use. See [License](#license).

## Documentation

| Document | Read it if you |
|---|---|
| **This README** | want to know what it does, every setting, how to install, the status page and logs |
| **[docs/RUNBOOK.md](docs/RUNBOOK.md)** | operate the Exchange servers: what the emails mean, what to do, how to turn it off quickly |
| **[docs/MAINTAINING.md](docs/MAINTAINING.md)** | need to understand, change, build or release the code |
| **[docs/DEPENDENCIES.md](docs/DEPENDENCIES.md)** | need to know what it depends on, or are installing or building in an air-gapped environment |

All of these are installed with the app (in the install folder and its `docs` subfolder) and linked from the Start menu, so they are on every server it runs on.

## Pre-built files

Alongside the source, the published repository holds the ready-made standalone (air-gapped) build, so nothing has to be built to install or inspect it:

| Folder | Contents |
|---|---|
| `release/` | `ExchangeAppPoolMonitor-Setup-<version>-standalone.exe`, the single installer to use on the servers (everything included, no .NET needed), and `SHA256SUMS.txt` |
| `binaries/standalone/` | The same build with every file separate, for inspection: `ExchangeAppPoolMonitor.exe`, the app's `ExchangeAppPoolMonitor.dll` and `.pdb`, `monitorsettings.json`, and all of the .NET 6 and ASP.NET Core 6 runtime libraries it bundles |
| `packages/` | Microsoft's .NET runtime package files, used only to rebuild from source without internet access (see [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md)). The app itself uses no NuGet packages. |

To rebuild the installer from this source offline: `.\installer\build-installer.ps1 -Flavor Standalone -PackageSource packages`.

A Windows service (.NET 6) for an Exchange server that watches the `MSExchangeAutodiscoverAppPool` and `MSExchangeDefaultAppPool` IIS app pools and recycles a pool automatically when it gets into trouble.

## Why it exists

When these pools start queuing requests and nothing is done, the server can slide into an unstable state where the only fix is the long way round: put it into maintenance mode, move the databases off, and reboot it. Recycling the affected pool **early**, while the backlog is still building, avoids that.

This service does that early recycle automatically:

- It checks **near constantly** (every 10 seconds by default) for the signs that a pool is getting into trouble.
- It **acts within seconds** when it sees them, by recycling the pool the same way IIS Manager does.
- It **records** what it did in its own log files and **emails** you and anyone else on the list, so a human knows every time it stepped in and why.

It runs on each Exchange server as a normal Windows service and needs no one watching it.

## What it does

A pool is recycled when any of these happens:

1. its **HTTP.sys request queue** is deeper than a threshold for a number of checks in a row;
2. too many of its **requests have been running 30 seconds or longer** (the same thing you see with `appcmd list requests /elapsed:30000`), for a number of checks in a row;
3. it logs **ASP.NET Event ID 1309** errors in the Application event log (a set number within a set number of minutes).

Every action is written to the app's own log files and to the Windows Application event log (Event Viewer), emailed to a list of addresses, and shown on a small built in web page together with the current state of the service and both pools.

Everything is controlled by one file, `monitorsettings.json`.

---

## Contents

- [Why it exists](#why-it-exists)
- [How it works](#how-it-works)
- [Trigger 1: request queue depth](#trigger-1-request-queue-depth)
- [Trigger 2: long running requests](#trigger-2-long-running-requests)
- [Trigger 3: ASP.NET 1309 errors](#trigger-3-aspnet-1309-errors)
- [Safety features](#safety-features)
- [Requirements](#requirements)
- [Two editions](#two-editions)
- [Build](#build)
  - [Code review and SonarQube analysis](#code-review-and-sonarqube-analysis)
- [Install](#install)
  - [With the installer](#with-the-installer-recommended)
  - [Settings pages](#settings-pages)
  - [Silent install](#silent-install-several-servers)
  - [Manual install](#manual-install-without-the-installer)
- [Configuration reference](#configuration-reference)
- [Email notifications](#email-notifications)
- [Status page and API](#status-page-and-api)
- [Logs](#logs)
- [Action log](#action-log)
- [First run checklist](#first-run-checklist)
- [Troubleshooting](#troubleshooting)
- [Upgrade and uninstall](#upgrade-and-uninstall)
- [Security notes](#security-notes)
- [Code map](#code-map)
- [Known limitations](#known-limitations)
- [License](#license)

---

## How it works

One process, running as the Windows service `ExchangeAppPoolMonitor`, contains two parts:

- **The monitor loop**, which wakes up every `PollIntervalSeconds` (default 10) and checks the pools.
- **A small web server**, which serves the status page (default `http://localhost:8085`).

Each check, the monitor loop does this:

```
1. For each watched pool:
     a. read the request queue depth      (perf counter HTTP Service Request Queues\CurrentQueueSize)
     b. count long running requests       (appcmd list requests /apppool.name:<pool> /elapsed:30000)
        queue deeper than its threshold for N checks in a row?              -> trigger
        or more long running requests than allowed for N checks in a row?   -> trigger
        or at least ErrorCount 1309 errors in the last WindowMinutes?       -> trigger
     c. triggered and not in cooldown?
          -> recycle (appcmd recycle apppool), write the log, send the email
        triggered but in cooldown?
          -> log it once and wait
     d. read the pool state               (appcmd list apppool /text:state)
     e. publish the pool's status for the web page

2. If the 1309 trigger is on:
     a. run "appcmd list wp" to learn which w3wp.exe PID belongs to which pool
     b. read new Event ID 1309 entries from the Application log
     c. assign each event to a pool (by PID, or by EventMatches text as a fallback)
        (these are acted on in step 1 of the next check, at most one interval later)
```

**Why this order.** The queue read is a fast counter read with no external process, so it comes first and nothing can delay it. The long running request count needs appcmd, which has to talk to IIS, but it is a trigger, so it is read before deciding. The pool state is only for the status page, so it is read last, after any recycle. All appcmd calls time out after 20 seconds, so a hung IIS cannot stall the loop for long.

**How fast it reacts.** With the defaults (check every 10 seconds, 2 checks in a row over the threshold), a pool is recycled roughly 10 to 20 seconds after its queue or its long running request count goes over the threshold. A 1309 error is acted on within about one interval of being logged. Both can be tightened (see [Choosing a threshold](#choosing-a-threshold)).

Nothing that goes wrong for one pool (or with reading the Application log) stops the checks for the other pool, and nothing ends the loop. Problems are shown on the status page and written to the log once, not on every check.

The service talks to IIS only through `%windir%\system32\inetsrv\appcmd.exe` and the Windows performance counters. It reads the Application event log to find 1309 errors, and writes its own entries there under the source `ExchangeAppPoolMonitor`. It does **not** use PowerShell or the Exchange Management Shell.

---

## Trigger 1: request queue depth

### What the request queue is

HTTP.sys is the Windows kernel driver that receives every web request before IIS handles it. It keeps a separate **request queue for each IIS app pool**, named after the pool. A request waits in that queue until a thread in the pool's `w3wp.exe` picks it up.

- In a healthy pool the queue sits at or near **0**, because requests are picked up as fast as they arrive.
- When the pool hangs, deadlocks, or runs out of threads, requests **pile up** in the queue. Users see Outlook prompting, Autodiscover timing out, or OWA hanging.
- When the queue reaches the pool's **Queue Length** limit (1000 by default in IIS), HTTP.sys rejects further requests with **503 Service Unavailable**.

Because the queue is in front of the worker process, its depth is a direct measure of "this pool has stopped keeping up", which makes it a good reason to recycle.

### How the service measures it

It reads the performance counter:

```
\HTTP Service Request Queues(<app pool name>)\CurrentQueueSize
```

on every check, through the Windows Performance Data Helper (`pdh.dll`), the same Windows component Performance Monitor and `Get-Counter` use. This is the same number you get from PowerShell or Performance Monitor. (See `RequestQueueReader.cs`.)

### Checking it yourself

On the Exchange server (any PowerShell window, the Exchange shell is not needed):

```powershell
# Every queue on the server, refreshed every 5 seconds
Get-Counter '\HTTP Service Request Queues(*)\CurrentQueueSize' -SampleInterval 5 -Continuous

# Just the two watched pools
Get-Counter '\HTTP Service Request Queues(msexchangeautodiscoverapppool)\CurrentQueueSize',
            '\HTTP Service Request Queues(msexchangedefaultapppool)\CurrentQueueSize' -SampleInterval 5 -Continuous
```

Other ways:

- **Performance Monitor:** run `perfmon`, Add Counters, choose **HTTP Service Request Queues** > **CurrentQueueSize**, and pick the pools.
- **netsh** (elevated): `netsh http show servicestate view=requestq` lists every request queue and the requests currently waiting in it.
- **PID to pool:** `%windir%\system32\inetsrv\appcmd.exe list wp` lists each running `w3wp.exe` with its PID and pool. The service runs this too, for the 1309 trigger.
- **Requests stuck inside the pool:** `appcmd list requests /elapsed:30000`. That is a different number from the queue; see [Trigger 2](#trigger-2-long-running-requests).

Other counters in the same category that are useful when investigating (the service does not act on them):

| Counter | Meaning |
|---|---|
| `RejectedRequests` | Requests refused with a 503 because the queue was full. If this is climbing, users are already getting errors. |
| `ArrivalRate` | Requests arriving per second. Helps tell "busy" apart from "stuck". |
| `MaxQueueItemAge` | Age in milliseconds of the oldest request waiting in the queue. |

### Choosing a threshold

The default of **100** is a starting point, not a measured value. To choose your own:

1. Watch the `Get-Counter` output above during a normal busy period (Monday morning is good).
2. Set `QueueThreshold` well above the highest normal value you saw, so normal bursts never trigger it.
3. Run in `DryRun` mode for a few days and see whether the dry run alerts line up with real problems.

`ConsecutiveSamplesBeforeRecycle` (default 2) means the queue must stay over the threshold for that many checks in a row. With the default 10 second interval, a spike that is gone by the next check (under about 10 seconds) is ignored, and a backlog that lasts is acted on within about 20 seconds.

Tuning for speed versus false alarms:

| Goal | Change |
|---|---|
| React faster | Lower `PollIntervalSeconds` (minimum 2) or set `ConsecutiveSamplesBeforeRecycle` to 1. |
| Catch the backlog earlier | Lower `QueueThreshold`, so action is taken while the queue is still small. |
| Fewer unnecessary recycles | Raise `ConsecutiveSamplesBeforeRecycle` or `QueueThreshold`. |

Because the aim is to recycle before the server becomes unstable, err towards a threshold that is clearly above normal but well below the depths you saw before past incidents.

Each pool can have its own threshold (see [Configuration reference](#configuration-reference)).

---

## Trigger 2: long running requests

### What it measures

The number of requests that IIS has already handed to the pool's `w3wp.exe` and that have been **running for 30 seconds or longer** without finishing. It is the same check as running this by hand:

```powershell
& "$env:windir\system32\inetsrv\appcmd.exe" list requests /elapsed:30000
```

(In a Command Prompt, the same thing is `%windir%\system32\inetsrv\appcmd.exe list requests /elapsed:30000`.)

When an Exchange pool is going bad, these stack up into the **thousands**, because requests go in but do not come out. That makes it a very direct sign of a hung pool.

How it differs from the queue (Trigger 1):

| | Request queue (Trigger 1) | Long running requests (Trigger 2) |
|---|---|---|
| Where | HTTP.sys, in front of the pool | Inside the pool's w3wp.exe |
| Which requests | Waiting to be picked up | Picked up but not finished |
| Counts age | No (just how many) | Yes, only those older than `OlderThanSeconds` |
| Source | Performance counter | `appcmd list requests` |

A pool can show one without the other: a deadlocked pool often fills up with long running requests first, and the queue only grows once all its threads are tied up. Watching both catches either pattern.

### How the service measures it

Each check, for each pool:

```
appcmd list requests /apppool.name:<pool> /elapsed:<OlderThanSeconds x 1000>
```

The service counts the `REQUEST` lines in the output and reads the `time:... msec` value on each line to find the oldest one. The count and the age of the oldest request are shown on the status page and included in the log entry and email when it acts, for example:

```
Recycled. 2140 request(s) have been running 30 seconds or longer (oldest 184 s), over the threshold of 50, for 2 consecutive check(s).
```

This uses the IIS **Request Monitor** feature (`Web-Request-Monitor`), which Exchange setup installs as a prerequisite.

### Checking it yourself

```powershell
# Every long running request on the server, with its URL and age
& "$env:windir\system32\inetsrv\appcmd.exe" list requests /elapsed:30000

# Just the count, per pool
foreach ($p in 'MSExchangeAutodiscoverAppPool','MSExchangeDefaultAppPool') {
    $n = (& "$env:windir\system32\inetsrv\appcmd.exe" list requests /apppool.name:$p /elapsed:30000 | Select-String '^REQUEST').Count
    "{0,-32} {1}" -f $p, $n
}
```

### Choosing a threshold

The default is **more than 50 requests older than 30 seconds**, for `ConsecutiveSamplesBeforeRecycle` checks in a row. These climb into the thousands when a pool is going bad, so acting at 50 to 100 heads the problem off at the very start of the climb, long before the server becomes unstable. Use 100 if 50 turns out to trigger during normal busy periods. Check what a normal busy period looks like with the loop above and keep the threshold clearly above it. Some Exchange requests are long by design (for example EWS or ActiveSync long polling, mostly in other pools), so a handful of long running requests in a healthy pool is normal.

`OlderThanSeconds` (default 30) can be changed if you prefer a different age, and each pool can have its own threshold with `LongRunningThreshold`.

---

## Trigger 3: ASP.NET 1309 errors

### What a 1309 is

Event ID **1309** is a Warning in the **Application** log from a source named like `ASP.NET 4.0.30319.0`. ASP.NET writes it when a web application hits an unhandled exception. The event text says `Event code: 3005` and includes the exception, the application path, the request URL, and the worker process details. On Exchange, a run of these from the Autodiscover or default pool is a common sign that the pool is in a bad state.

### How the service handles them

- Each check, it asks the Application log for Event ID 1309 entries newer than the last ones it saw. Only events whose source starts with `ASP.NET` count, because other software can use the same ID.
- **Only events logged after the service started are counted.** Starting the service will not recycle a pool because of old events.
- Each event is assigned to a pool:
  1. **By process ID (preferred).** The event contains the `w3wp.exe` PID. The service runs `appcmd list wp` every check and remembers each PID's pool for an hour, so it can still match an event from a process that has since crashed or been recycled.
  2. **By text (fallback).** If the PID is not known, the event text is compared against each pool's `EventMatches` list.
  3. If neither matches, the event belongs to some other site or pool and is ignored.
- Each matched event writes a Warning line to the action log.
- When a pool has at least `ErrorCount` errors within the last `WindowMinutes`, it is recycled. The default (1 error within 10 minutes) recycles on the first error. If 1309s are routine noise on your servers, raise `ErrorCount`.
- After a recycle the error count for that pool is reset, so errors from the old process do not count against the new one.

### About EventMatches

`EventMatches` is only needed for the case where a 1309 is written by a process that started and died between two checks, so the service never saw its PID. Leave it empty unless you find events being missed.

If you do use it, **do not use a bare `/Autodiscover`**. Exchange serves that path from both of its web sites (the front end on Default Web Site and the back end on Exchange Back End), which can run in different pools, so it could match the wrong pool. Use the **Application domain** line from one of your own 1309 events instead, which includes the site ID, for example:

```json
{ "Name": "MSExchangeAutodiscoverAppPool", "EventMatches": [ "/LM/W3SVC/2/ROOT/Autodiscover" ] }
```

Matching is a case insensitive "contains" against all of the event's data fields.

---

## Safety features

| Feature | What it prevents |
|---|---|
| **Consecutive samples** | A short spike in the queue or in long running requests causing a recycle. |
| **Cooldown** (`RecycleCooldownMinutes`, default 10) | A recycle loop. After a recycle, or a failed attempt, that pool is not recycled again until the cooldown ends, however many triggers fire. A trigger during the cooldown is logged once per incident. This also stops a broken pool from sending an email on every check. |
| **Dry run** (`DryRun`) | Lets you see exactly what the service would do (log and email, marked `[DRY RUN]`) without touching the pools. |
| **Ignores old 1309 events** | A restart of the service recycling a pool because of events from before it started. |
| **Graceful recycle** | `appcmd recycle apppool` asks IIS for its normal overlapped recycle. IIS starts a new `w3wp.exe`, sends new requests to it, and lets the old process finish what it is doing before shutting it down. The HTTP.sys queue belongs to the pool, not the process, so waiting requests are not dropped. This is the same thing that happens when you click Recycle in IIS Manager. |
| **One pool at a time** | Pools are checked in sequence, so two recycles can never be started at the same instant. |

---

## Requirements

On the Exchange server:

- Windows Server with IIS (any Exchange 2013, 2016, 2019 or Subscription Edition server already has it).
- **.NET:** depends on the edition (see [Two editions](#two-editions)). The **standalone** edition needs nothing installed. The **dotnet6** edition needs the **ASP.NET Core Runtime 6.0 (x64)**.
- The service must run as **LocalSystem** (or another account in the local Administrators group). appcmd needs administrator rights to recycle pools.

To build:

- The **.NET 6 SDK** (or any newer SDK) on any machine (Windows, Mac or Linux), plus **NSIS 3** to build the installer. The project builds on all three; it only runs on Windows.

The project targets **.NET 6** (`net6.0`, marked Windows-only in `AssemblyInfo.cs`) and is pinned to **C# 10**, the language version of the .NET 6 SDK, so it builds with the .NET 6 SDK and a newer SDK cannot slip in newer language features. Note that Microsoft ended support for .NET 6 in November 2024, so it no longer gets security fixes; a newer SDK prints warning `NETSDK1138` about this, which is expected and does not stop the build. Moving to .NET 8 later only needs the `TargetFramework` and `LangVersion` in the `.csproj` changed (see [docs/MAINTAINING.md](docs/MAINTAINING.md)).

**The project uses no NuGet packages.** Everything it needs is part of .NET 6 and ASP.NET Core 6, or is called directly from Windows. See [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md) for the details and for building in an air-gapped environment. (An earlier version that used three Microsoft NuGet packages is kept on the `exchange-apppool-monitor-nuget` branch.)

---

## Two editions

The app comes in two editions, built from the same code. They behave the same; they differ only in whether .NET is bundled.

| | **Standalone** | **dotnet6** |
|---|---|---|
| For servers | **without** .NET | **with** the ASP.NET Core Runtime 6.0 (x64) already installed |
| Installer | `ExchangeAppPoolMonitor-Setup-<version>-standalone.exe`, about 29 MB | `ExchangeAppPoolMonitor-Setup-<version>-dotnet6.exe`, under 1 MB |
| Files installed | **6** plus the `docs` folder: `ExchangeAppPoolMonitor.exe`, `monitorsettings.json`, `monitorsettings.default.json`, `README.md`, `LICENSE`, `Uninstall.exe` | **10** plus the `docs` folder: the same 6, plus the app's `ExchangeAppPoolMonitor.dll`, its debug symbols (`.pdb`) and the two small `.json` files .NET needs to start it |
| How | The .NET 6 and ASP.NET Core runtimes are packed into the single exe (about 85 MB) | Uses the runtime installed on the server |
| Updates to .NET | Come with a new build of the app | Come from patching the runtime on the server (Windows Update or the runtime installer) |
| For air-gapped networks | **Yes**: nothing else to install | Only if the runtime installer is brought in separately |

Either edition creates a `logs` folder when it runs. The standalone edition also creates a `runtime` folder: the single exe unpacks a few of .NET's own native files there the first time it starts (normally they would go to the temp folder, where cleanup tools can delete them, so setup points them here). It is removed on uninstall and recreated if deleted.

**Which runtime for the dotnet6 edition:** "ASP.NET Core Runtime 6.0.x", Windows x64 installer, from https://dotnet.microsoft.com/download/dotnet/6.0. It includes the base .NET 6 runtime, so it is the only thing to install. Any 6.0.x patch release works. Notes:

- The "ASP.NET" already on Exchange servers is **ASP.NET 4.x** (part of the .NET Framework). It is a different product and does not count.
- The **Hosting Bundle** also works, but it adds a module to IIS that this app does not need, so the plain runtime is the better choice on an Exchange server.
- Check what a server has with `dotnet --list-runtimes`: it needs a `Microsoft.AspNetCore.App 6.0.x` line and a `Microsoft.NETCore.App 6.0.x` line. The dotnet6 setup checks this itself and stops with a message (exit code 3 when silent) if either is missing.

Switching a server from one edition to the other: run the other edition's setup. It upgrades in place, keeps your settings and logs, and removes the previous edition's files.

---

## Build

There are two things you can build:

- **The installers** (recommended): a normal Windows setup program per edition. See [Install](#install).
- **Just the program files**, if you would rather install by hand. See [Manual install](#manual-install-without-the-installer).

Building needs the .NET 6 SDK (or newer). That is only needed on the machine you build on, not on the servers.

### Building the installers

Also needs **NSIS 3** (free, from https://nsis.sourceforge.io; on Linux `apt install nsis`, on macOS `brew install makensis`).

```powershell
# Windows, from the infra\exchange-apppool-monitor folder
.\installer\build-installer.ps1                       # both editions
.\installer\build-installer.ps1 -Flavor Standalone    # or just one: Standalone or DotNet6
```

```bash
# Linux or macOS
./installer/build-installer.sh              # both editions
./installer/build-installer.sh DotNet6      # or just one
```

For each edition the script publishes the app, adds this README, and runs NSIS. The results go to `artifacts\`:

- `ExchangeAppPoolMonitor-Setup-1.0.0-standalone.exe`
- `ExchangeAppPoolMonitor-Setup-1.0.0-dotnet6.exe`

The version comes from `<Version>` in `ExchangeAppPoolMonitor.csproj`; change it there before building a new release so upgrades show the right version in Apps.

### Building just the program files

The edition is chosen with `-p:Flavor=`. Standalone is the default.

```powershell
dotnet publish -c Release -o .\publish                      # standalone: 2 files, about 85 MB
dotnet publish -c Release -p:Flavor=DotNet6 -o .\publish    # dotnet6: 6 files, under 1 MB
```

Copy the whole `publish` folder; for the dotnet6 edition the exe will not run without the DLLs next to it.

### Code review and SonarQube analysis

The project is plain C# with no generated code, so SonarQube analyses it with the standard **SonarScanner for .NET**. The scanner hooks into a normal build, which means the analysis machine needs the **.NET 6 SDK** (or newer) and the NuGet packages the project uses (see [Building without internet access](#building-without-internet-access)).

From the folder that contains `ExchangeAppPoolMonitor.csproj`:

```powershell
# One time: install the scanner (needs a Java runtime on the machine, as SonarScanner requires)
dotnet tool install --global dotnet-sonarscanner

# Analysis: begin, build, end. Replace the server URL and token with your SonarQube details.
dotnet sonarscanner begin /k:"ExchangeAppPoolMonitor" /n:"Exchange AppPool Monitor" /v:"1.0.0" `
    /d:sonar.host.url="https://sonarqube.example.com" /d:sonar.token="<token>"
dotnet build ExchangeAppPoolMonitor.csproj -c Release -p:Flavor=DotNet6
dotnet sonarscanner end /d:sonar.token="<token>"
```

Notes:

- Older SonarQube servers (before 10.0) use `/d:sonar.login="<token>"` instead of `sonar.token`.
- Analysing either edition gives the same results: the two editions compile the same source files and differ only in packaging. `-p:Flavor=DotNet6` is used above because it builds faster.
- The `installer` folder holds the NSIS installer script and build scripts. They are not C#, so the .NET scanner does not analyse them; review them by hand if your process covers installers.

**Expected findings.** The code has been checked against SonarQube's C# rules (the `SonarAnalyzer.CSharp` analyzer, version 10.36, which is the one SonarQube itself uses). One finding is expected and is intentional:

| Rule | Where | Why it is intentional |
|---|---|---|
| S5332 "EnableSsl should be set to true" (security hotspot) | `EmailNotifier.cs`, `EnableSsl = s.EnableSsl` | TLS for email is a setting (`Email:EnableSsl`), not hard-coded off. The default setup sends through the Exchange server's own anonymous relay on `localhost` port 25, which does not use TLS. Set `Email:EnableSsl` to `true` when sending through an authenticated server, for example on port 587. Mark the hotspot as reviewed/safe with this explanation. |

Other things a reviewer may ask about, and where they are handled:

- **Runs as LocalSystem:** required to recycle app pools (`appcmd recycle apppool` needs administrator rights). See [Security notes](#security-notes).
- **Starts external processes:** only `%windir%\system32\inetsrv\appcmd.exe`, by full path, with fixed arguments passed through `ProcessStartInfo.ArgumentList` (no shell, no string concatenation into a command line), and with a 20 second timeout. See `AppCmd.cs`.
- **Web server:** the status page is read only (two GET routes, no input taken from requests) and listens on `localhost` by default. See `Program.cs` and `StatusPage.cs`; every value written into the page is HTML-encoded.
- **Secrets:** the only secret is the optional SMTP password in `monitorsettings.json`, stored as plain text by design (documented in [Security notes](#security-notes), with the folder permissions to apply).

### Building without internet access

The project uses no NuGet packages, so with the **.NET 6 SDK on Windows** the **dotnet6 edition builds with no internet access and nothing extra**: everything it needs ships with the SDK.

The **standalone edition** bundles the .NET runtime into its exe, and Microsoft distributes that bundle-able runtime only as two package files (`Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.AspNetCore.App.Runtime.win-x64`). On an air-gapped build machine, bring those in and point the build at them:

```powershell
.\installer\build-installer.ps1 -PackageSource <folder with the runtime package files>
```

The evaluation package includes that folder (`packages`). A newer SDK than .NET 6 needs a few more of .NET's own package files, which the same folder also holds. Full details: [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md).

### Building the installers from reviewed source

The installers are built from the same source by `installer\build-installer.ps1` (see [Building the installers](#building-the-installers)). If your process requires the deployed program to be built from the exact source that was reviewed, run that script on your own build machine after the review, and deploy the installers it produces. A rebuild on a different machine or SDK version produces equivalent but not byte-identical files, so compare by building, not by file hashes.

---

## Install

### With the installer (recommended)

Copy the setup for the edition you want (see [Two editions](#two-editions)) to the Exchange server and run it (it asks for administrator rights). Supported on **Windows Server 2016 or later, 64-bit**. The dotnet6 setup first checks that the ASP.NET Core Runtime 6.0 is installed and stops if it is not.

The installer:

1. Installs the program to `C:\Program Files\ExchangeAppPoolMonitor` (you can change the folder). On an upgrade it first removes the previous program files, keeping your settings and logs.
2. Creates the Windows service **ExchangeAppPoolMonitor** ("Exchange AppPool Monitor"): runs as LocalSystem, starts automatically (delayed start, a couple of minutes after boot, once IIS and Exchange are up), and restarts itself a minute after a crash.
3. Registers the app in **Settings > Apps** and **Control Panel > Programs and Features**, so it can be uninstalled from there.
4. Adds a Start menu folder, **Exchange AppPool Monitor**, with shortcuts to the status page, the settings file, the logs folder, the README, the runbook, the maintainer's guide, the license, and the uninstaller. The **Edit settings** shortcut opens Notepad as administrator (expect a UAC prompt), because files under Program Files can only be saved by an elevated program. If you open `monitorsettings.json` any other way, use an elevated editor or the save will fail with "Access denied".

### Settings pages

After you choose the folder, setup asks for the main settings on two pages and writes your answers into `monitorsettings.json`:

**Email notifications**

| Field | Setting written | Notes |
|---|---|---|
| Send notifications to | `Email:To` | One or more addresses, separated by `;` or `,`. Leaving it empty turns email off (`Email:Enabled` false); setup asks you to confirm. |
| From address | `Email:From` | Required when there are recipients. |
| SMTP server, Port | `Email:SmtpServer`, `Email:Port` | Defaults `localhost` and `25` (the Exchange server's own transport). |
| Use SSL/TLS | `Email:EnableSsl` | Usually with port 587. |
| User name, Password | `Email:Username`, `Email:Password` | Leave empty for an anonymous relay. The password is stored as plain text in the settings file. |

**Monitoring**

| Field | Setting written | Default |
|---|---|---|
| Check queue depth and long running requests every N seconds | `Monitor:PollIntervalSeconds` | 10 |
| Check for ASP.NET 1309 errors every N seconds | `Monitor:Event1309:CheckIntervalSeconds` | 10 |
| Recycle when the request queue is deeper than | `Monitor:QueueThreshold` | 100 |
| Recycle when more requests than this are 30+ s old | `Monitor:LongRunningRequests:Threshold` | 50 |
| Dry run | `Monitor:DryRun` | **ticked** on a new install |

Each entry is checked before you can continue (addresses must contain `@`, intervals at least 2 seconds, thresholds at least 1).

- **New install:** the pages start with the defaults above. Dry run is ticked so nothing is recycled until you have seen what it would do; turn it off later in the settings file.
- **Upgrade:** the pages start with the values already in your `monitorsettings.json`, so clicking Next keeps everything as it is.
- **Everything else** in the file (pool list, cooldown, log retention, anything you added by hand) is never changed by setup.
- **Silent install** (`/S`) shows no pages and leaves `monitorsettings.json` exactly as it is.

How it works: setup keeps your answers in a small file in its temporary folder (deleted when setup closes) and runs `ExchangeAppPoolMonitor.exe --import-settings` to write them into the JSON file, keeping its formatting and every other setting. On an upgrade it first runs `--export-settings` to read the current values. If an existing settings file cannot be read, setup skips the pages and leaves the file alone.

On the last page, **Start the service now** is ticked, and **Open the settings file now** is not (tick it to review or change settings the pages do not cover). Then open the **Status page** shortcut.

### Silent install (several servers)

The examples use the standalone setup; the dotnet6 setup takes the same switches (and exits with code 3 if the runtime is missing).

```powershell
# Install or upgrade, do not start the service
.\ExchangeAppPoolMonitor-Setup-1.0.0-standalone.exe /S

# Install or upgrade, then start the service
.\ExchangeAppPoolMonitor-Setup-1.0.0-standalone.exe /S /START

# Install to another folder (/D must be last, no quotes, even with spaces)
.\ExchangeAppPoolMonitor-Setup-1.0.0-standalone.exe /S /D=E:\Apps\ExchangeAppPoolMonitor
```

A typical rollout: install silently, copy your finished `monitorsettings.json` into the install folder, then start the service. For example, from an admin machine:

```powershell
$servers = 'EXCH01','EXCH02'
foreach ($s in $servers) {
    Copy-Item .\ExchangeAppPoolMonitor-Setup-1.0.0-standalone.exe "\\$s\C$\Windows\Temp\"
    Invoke-Command -ComputerName $s { Start-Process C:\Windows\Temp\ExchangeAppPoolMonitor-Setup-1.0.0-standalone.exe -ArgumentList '/S' -Wait }
    Copy-Item .\monitorsettings.json "\\$s\C$\Program Files\ExchangeAppPoolMonitor\"
    Invoke-Command -ComputerName $s { Start-Service ExchangeAppPoolMonitor }
}
```

### Manual install (without the installer)

If you prefer not to use the installer, publish the program files (see [Build](#building-just-the-program-files)) and run these in an **elevated** PowerShell window. This does not create an entry in Apps; remove it with the manual steps in [Upgrade and uninstall](#upgrade-and-uninstall).

```powershell
# 1. Copy the published files
robocopy .\publish "C:\Program Files\ExchangeAppPoolMonitor" /E

# 2. Edit the settings
notepad "C:\Program Files\ExchangeAppPoolMonitor\monitorsettings.json"

# 3. Optional: try it from the console first (Ctrl+C to stop), then browse to http://localhost:8085
& "C:\Program Files\ExchangeAppPoolMonitor\ExchangeAppPoolMonitor.exe"

# 4. Create and start the service
New-Service -Name ExchangeAppPoolMonitor -DisplayName "Exchange AppPool Monitor" `
    -BinaryPathName '"C:\Program Files\ExchangeAppPoolMonitor\ExchangeAppPoolMonitor.exe"' `
    -StartupType Automatic -Description "Recycles Exchange app pools early when requests back up or ASP.NET 1309 errors appear."
sc.exe config ExchangeAppPoolMonitor start= delayed-auto
sc.exe failure ExchangeAppPoolMonitor reset= 86400 actions= restart/60000/restart/60000/restart/60000
# Standalone edition only: unpack .NET's native files into the install folder, not the temp folder
New-ItemProperty -Path HKLM:\SYSTEM\CurrentControlSet\Services\ExchangeAppPoolMonitor -Name Environment -PropertyType MultiString `
    -Value 'DOTNET_BUNDLE_EXTRACT_BASE_DIR=C:\Program Files\ExchangeAppPoolMonitor\runtime' -Force | Out-Null
Start-Service ExchangeAppPoolMonitor
```

Notes:

- The exe path is wrapped in double quotes inside single quotes because it contains a space.
- No account is given, so the service runs as LocalSystem.
- `start= delayed-auto` (the space after `=` is required by `sc.exe`) starts it a couple of minutes after boot, once IIS and Exchange are up.
- `sc.exe failure` makes Windows restart the service a minute after a crash.

---

## Configuration reference

`monitorsettings.json` lives next to `ExchangeAppPoolMonitor.exe`. The service notices when the file is saved and uses the new values from the next check, **except `StatusPageUrl`, which needs a service restart**. If the file is missing, the service refuses to start.

Check the JSON before saving over a live file. A missing comma is easy to make and the service cannot use a file it cannot read:

```powershell
Get-Content "C:\Program Files\ExchangeAppPoolMonitor\monitorsettings.json" -Raw | ConvertFrom-Json | Out-Null   # no output means it is valid
```

### Full example

```json
{
  "Monitor": {
    "PollIntervalSeconds": 10,
    "QueueThreshold": 100,
    "ConsecutiveSamplesBeforeRecycle": 2,
    "RecycleCooldownMinutes": 10,
    "AppPools": [
      { "Name": "MSExchangeAutodiscoverAppPool", "EventMatches": [] },
      { "Name": "MSExchangeDefaultAppPool", "QueueThreshold": 150, "LongRunningThreshold": 100, "EventMatches": [] }
    ],
    "LongRunningRequests": {
      "Enabled": true,
      "OlderThanSeconds": 30,
      "Threshold": 50
    },
    "Event1309": {
      "Enabled": true,
      "CheckIntervalSeconds": 10,
      "ErrorCount": 1,
      "WindowMinutes": 10
    },
    "LogDirectory": "logs",
    "LogRetentionDays": 90,
    "StatusPageUrl": "http://localhost:8085",
    "DryRun": false
  },
  "Email": {
    "Enabled": true,
    "SmtpServer": "localhost",
    "Port": 25,
    "EnableSsl": false,
    "Username": "",
    "Password": "",
    "From": "apppool-monitor@example.com",
    "To": [ "exchange-admins@example.com", "oncall@example.com" ],
    "SubjectPrefix": "[AppPool Monitor]"
  },
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" },
    "EventLog": { "LogLevel": { "Default": "Information", "Microsoft": "Warning" } }
  }
}
```

(In this example `MSExchangeDefaultAppPool` has its own thresholds of 150 for the queue and 100 for long running requests; the Autodiscover pool uses the global 100 and 50.)

### Monitor section

| Setting | Default | Description |
|---|---|---|
| `PollIntervalSeconds` | `10` | Seconds between checks. Minimum 2. |
| `QueueThreshold` | `100` | Recycle when the request queue is **deeper than** this (equal does not count). Used by every pool that does not set its own. |
| `ConsecutiveSamplesBeforeRecycle` | `2` | How many checks in a row the queue (or the long running request count) must be over its threshold. `1` acts on the first reading. |
| `RecycleCooldownMinutes` | `10` | Minimum minutes between recycles of the same pool. Applies to all triggers and to failed attempts. `0` disables the cooldown (not recommended). |
| `AppPools` | the two Exchange pools | List of pools to watch. If empty or missing, the two default pools are used. |
| `AppPools[].Name` | | IIS app pool name, as shown in IIS Manager. Case does not matter. |
| `AppPools[].QueueThreshold` | (global) | Optional queue threshold for this pool only. |
| `AppPools[].LongRunningThreshold` | (global) | Optional long running request threshold for this pool only. |
| `AppPools[].EventMatches` | `[]` | Optional fallback text for matching 1309 events to this pool. See [About EventMatches](#about-eventmatches). |
| `LongRunningRequests.Enabled` | `true` | Turns the long running request trigger on or off. The other triggers are not affected. |
| `LongRunningRequests.OlderThanSeconds` | `30` | A request counts once it has been running this long. `30` matches `appcmd list requests /elapsed:30000`. |
| `LongRunningRequests.Threshold` | `50` | Recycle when **more than** this many requests are that old. Used by every pool that does not set its own. |
| `Event1309.Enabled` | `true` | Turns the 1309 trigger on or off. The other triggers are not affected. |
| `Event1309.CheckIntervalSeconds` | `10` | Seconds between checks of the Application log for new 1309 errors. Minimum 2. Independent of `PollIntervalSeconds`. A 1309 found by this check is acted on at the next pool check. |
| `Event1309.ErrorCount` | `1` | Number of 1309 errors that triggers a recycle... |
| `Event1309.WindowMinutes` | `10` | ...within this many minutes. |
| `LogDirectory` | `logs` | Folder for the app's log files (action and service logs). Relative paths are relative to the exe folder. |
| `LogRetentionDays` | `90` | Daily log files older than this are deleted. `0` keeps them forever. |
| `StatusPageUrl` | `http://localhost:8085` | Address the status page listens on. Use `http://*:8085` to make it reachable from other machines. Restart needed. |
| `DryRun` | `false` | When `true`, logs and emails what it would do, marked `[DRY RUN]`, but does not recycle. |

### Email section

| Setting | Default | Description |
|---|---|---|
| `Enabled` | `true` | Turns notifications off without removing the settings. |
| `SmtpServer` | `localhost` | SMTP host name or IP. |
| `Port` | `25` | `25` for an internal relay, `587` for authenticated submission. |
| `EnableSsl` | `false` | Use STARTTLS. Usually `true` with port 587. |
| `Username` / `Password` | empty | Leave both empty for an anonymous relay. The password is stored in plain text. |
| `From` | | Sender address. Must be one the SMTP server accepts. |
| `To` | | List of recipients. Every notification goes to all of them. |
| `SubjectPrefix` | `[AppPool Monitor]` | Put at the start of every subject, handy for inbox rules. |

### Logging section

Standard .NET logging settings. The action log is not affected by them; every action is always recorded there.

- `LogLevel` controls what goes to the **service log** (`logs\service-*.log`) and to the console when run interactively. The `Microsoft.AspNetCore` line keeps the web server's routine per-request messages out.
- `EventLog.LogLevel` controls what goes to the **Windows Application event log** (Event Viewer, source `ExchangeAppPoolMonitor`). It is set to `Information` so every action shows up there; without this section .NET would only send Warnings and Errors. The `Microsoft` line keeps the web server's own routine messages out. To stop writing to Event Viewer, set its `Default` to `None`.

If `Enabled` is false, `To` is empty, or `From` is blank, no email is sent and nothing is logged about it.

---

## Email notifications

### When an email is sent

Exactly one email per action:

| Event | Subject |
|---|---|
| Pool recycled | `[AppPool Monitor] Recycled MSExchangeAutodiscoverAppPool on EXCH01` |
| Recycle attempted but failed | `[AppPool Monitor] FAILED to recycle MSExchangeAutodiscoverAppPool on EXCH01` |
| Dry run | `[AppPool Monitor] [DRY RUN] Would recycle MSExchangeAutodiscoverAppPool on EXCH01` |

No email is sent for a trigger skipped because of the cooldown, for individual 1309 events, or for errors reading counters; those go to the action log and the status page.

### Example body

```
MSExchangeAutodiscoverAppPool on EXCH01 was recycled at 2026-11-05 14:32:10 -05:00.

Reason: Request queue depth 412 is over the threshold of 100 for 2 consecutive check(s).

appcmd output: "MSExchangeAutodiscoverAppPool" successfully recycled
```

### SMTP options

- **Through the Exchange server itself (simplest):** `SmtpServer: localhost`, `Port: 25`, no username. This works when a receive connector on the server accepts anonymous mail from 127.0.0.1 for internal recipients. To send to external addresses the connector must allow anonymous relay from 127.0.0.1.
- **Through a relay or another Exchange server:** set `SmtpServer` to its name, `Port: 25`, and make sure its receive connector accepts this server's IP.
- **Authenticated (for example Exchange Online):** `SmtpServer: smtp.office365.com`, `Port: 587`, `EnableSsl: true`, plus a mailbox `Username` and `Password`. The account must have SMTP AUTH enabled.

A failed send is logged as an Error (`Email notification failed: ...`) and shown on the status page. It never stops the monitor or the recycle.

---

## Status page and API

### `http://localhost:8085/`

A single HTML page with no scripts or external files. It reloads itself every 15 seconds. Three sections:

**Application**

| Row | Meaning |
|---|---|
| Monitor | `Running` (green) or `Stopped` (red), plus a `DRY RUN` badge when dry run is on. |
| Started | When the service started, and uptime. |
| Last check | When the monitor last finished a check. If this stops moving, the loop is stuck. |
| Check interval, Queue trigger, Long running request trigger, ASP.NET 1309 trigger, Recycle cooldown | The settings currently in effect. |
| Email notifications | Whether email is on, the recipients, and the SMTP server. |
| Last error | Only shown if the last check hit an unexpected problem. |

**App pools** (one row per watched pool)

| Column | Meaning |
|---|---|
| App pool | Pool name. Any problem reading it is shown underneath in red. |
| State | IIS state: Started (green), Stopped (red), or other / Unknown (amber). `Pending` means it has not been checked yet. |
| Queue depth | Current request queue depth. Red, with "(Nx over)", when over the threshold. |
| Queue threshold | Queue threshold in effect for this pool. |
| Requests 30s+ | Requests running at least `OlderThanSeconds` (the column title shows the setting). Red, with "(Nx over)", when over the threshold. `-` if the trigger is off or the count could not be read. |
| Oldest | How long the oldest of those requests has been running. |
| Long running threshold | Long running request threshold in effect for this pool. |
| 1309 errors in window | 1309 errors counted within the last `WindowMinutes`. |
| Last 1309 | Time of the most recent 1309 for this pool. |
| Last recycled | When the monitor last recycled (or tried to recycle) this pool. |
| Recycles | Successful recycles since the service started. |
| Last checked | When this pool was last checked. |

**Last 25 log entries**, newest first, with level badges: Action (green), Info (blue), Warning (amber), Error (red).

### `http://localhost:8085/api/status`

The same information as JSON, for scripts or another monitoring tool:

```powershell
Invoke-RestMethod http://localhost:8085/api/status | Select-Object -ExpandProperty pools | Format-Table
```

```json
{
  "server": "EXCH01",
  "monitorStatus": "Running",
  "startedAt": "2026-11-05T09:00:01-05:00",
  "lastPoll": "2026-11-05T14:32:10-05:00",
  "lastError": null,
  "dryRun": false,
  "pools": [
    {
      "name": "MSExchangeAutodiscoverAppPool",
      "state": "Started",
      "queueDepth": 0,
      "threshold": 100,
      "consecutiveOver": 0,
      "longRunningRequests": 3,
      "oldestRequest": "00:00:41",
      "longRunningThreshold": 50,
      "longRunningConsecutiveOver": 0,
      "lastChecked": "2026-11-05T14:32:10-05:00",
      "lastRecycled": "2026-11-05T14:31:40-05:00",
      "recycleCount": 1,
      "recentAspNetErrors": 0,
      "lastAspNetError": null,
      "lastError": null
    }
  ],
  "log": [ { "timestamp": "...", "level": "Action", "appPool": "MSExchangeAutodiscoverAppPool", "message": "Recycled. ..." } ]
}
```

---

## Logs

The app logs to two places:

- **Its own files**, in `C:\Program Files\ExchangeAppPoolMonitor\logs` (or wherever `LogDirectory` points). This is the complete record, kept for `LogRetentionDays` regardless of what happens to the Windows event log.
- **The Windows Application event log**, source `ExchangeAppPoolMonitor`, so entries show in Event Viewer and in any tool that already collects event logs from your servers. Controlled by `Logging:EventLog:LogLevel` (see [Logging section](#logging-section)). Errors appear as Error events, warnings (such as a 1309 match or a trigger during cooldown) as Warning events, and recycles and other entries as Information events.

In Event Viewer: Windows Logs > Application > Filter Current Log > Event sources: `ExchangeAppPoolMonitor`. In PowerShell:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ExchangeAppPoolMonitor' } -MaxEvents 50 | Format-Table TimeCreated, LevelDisplayName, Message -Wrap
```

The event source is registered automatically the first time the service writes, which works because it runs as LocalSystem.

There are two kinds of log file, one of each per day, and files older than `LogRetentionDays` are deleted automatically:

| File | Contents | Format |
|---|---|---|
| `actions-yyyy-MM-dd.log` | The **action log**: every recycle, dry run, trigger, 1309 match, email, and problem with a pool. This is what the status page shows and what you normally read. | One JSON object per line |
| `service-yyyy-MM-dd.log` | The **service log**: the service starting and stopping, startup failures (for example the port already in use or a bad settings file), web server errors, and a plain text copy of each action log entry. Use it to troubleshoot the service itself. | Plain text |

Example service log lines:

```
2026-11-05 09:00:01.412 -05:00 [Information] ActionLog: Monitor started. Watching MSExchangeAutodiscoverAppPool, MSExchangeDefaultAppPool.
2026-11-05 09:00:01.530 -05:00 [Information] Microsoft.Hosting.Lifetime: Now listening on: http://localhost:8085
2026-11-05 14:31:40.123 -05:00 [Information] ActionLog: MSExchangeAutodiscoverAppPool: Recycled. Request queue depth 412 is over the threshold of 100 for 2 consecutive check(s). ...
```

If the service fails to start, the reason is written to the service log as a `[Critical]` entry. A failure so early that the app never starts (for example files missing from the install folder) cannot be captured by the app at all; Windows records it under "Application Error" or ".NET Runtime" in the Application event log, and the Service Control Manager notes in the System log that the service failed to start.

## Action log

### Where

`logs\actions-yyyy-MM-dd.log`, one file per day.

### Format

One JSON object per line:

```json
{"Timestamp":"2026-11-05T14:31:40.1234-05:00","Level":"Action","AppPool":"MSExchangeAutodiscoverAppPool","Message":"Recycled. Request queue depth 412 is over the threshold of 100 for 2 consecutive check(s). appcmd: \"MSExchangeAutodiscoverAppPool\" successfully recycled"}
```

Reading it in PowerShell:

```powershell
# Today's log as a table
Get-Content "C:\Program Files\ExchangeAppPoolMonitor\logs\actions-$(Get-Date -f yyyy-MM-dd).log" | ConvertFrom-Json | Format-Table Timestamp, Level, AppPool, Message -Wrap

# Every recycle in the last 90 days
Get-Content "C:\Program Files\ExchangeAppPoolMonitor\logs\actions-*.log" | ConvertFrom-Json | Where-Object Level -eq 'Action'
```

### Levels and what is logged

| Level | Logged when |
|---|---|
| **Action** | A pool was recycled, or would have been in dry run. |
| **Info** | Service started or stopped; an email was sent. |
| **Warning** | A 1309 error was matched to a pool; a trigger fired during the cooldown (once per incident); appcmd could not list worker processes. |
| **Error** | A recycle failed; an email failed; a pool's state or queue could not be read; the Application log could not be read for 1309 errors. Each distinct error is logged once, not on every check. |

The status page shows the last 25 entries. They are reloaded from the files when the service starts, so a restart does not clear the page.

---

## First run checklist

1. Set `"DryRun": true` and fill in the Email section.
2. Start the exe from an elevated console and open `http://localhost:8085`.
3. Check both pools show **Started** with a **Queue depth** number (not `-`) and no red error text.
4. To test email and the whole recycle path without touching anything, temporarily set `QueueThreshold` to `-1` (or `LongRunningRequests.Threshold` to `-1`) and `ConsecutiveSamplesBeforeRecycle` to `1` and save the file. Within a check or two you should see a `[DRY RUN] Would recycle` entry for each pool and receive the dry run emails. Put the values back afterwards.
5. Install it as a service. Leave dry run on for a few days and compare the dry run alerts with what users report.
6. When you trust it, set `"DryRun": false`.

---

## Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| Service will not start | Look at the end of today's `logs\service-*.log` for a `[Critical]` entry. Usual causes: `monitorsettings.json` is missing or invalid, or port 8085 is already in use (change `StatusPageUrl`). If there is no service log at all, the process could not start; check that the whole publish folder was copied (not just the exe), and look for an "Application Error" event in Event Viewer. |
| `appcmd.exe not found` | IIS management tools are missing, or the service is not running on the Exchange server. |
| `appcmd ... failed` / access denied | The service is not running as LocalSystem or an administrator. Check the Log On tab in services.msc. |
| `No request queue counter instance found for this pool` | The pool name in the config does not match IIS. Compare with `appcmd list apppool`. |
| `Could not list long running requests` | Usually the IIS Request Monitor feature is missing. Check with `Get-WindowsFeature Web-Request-Monitor` and install it with `Install-WindowsFeature Web-Request-Monitor`, or set `LongRunningRequests.Enabled` to `false`. The other triggers keep working either way. |
| Long running count always 0 but you see stuck requests by hand | Compare your manual command with what the service runs (shown in [Trigger 2](#trigger-2-long-running-requests)). Make sure `OlderThanSeconds` matches the `/elapsed` value you use (30 = 30000). |
| `Performance counter category 'HTTP Service Request Queues' does not exist` | The counters are damaged. From an elevated prompt run `lodctr /R`, then restart the service. |
| Queue depth always 0 | Normal for a healthy pool. Compare with `Get-Counter` to be sure. |
| 1309 errors appear in Event Viewer but not on the page | Check the event's source starts with `ASP.NET` and its ID is 1309. Check its PID belongs to a watched pool (`appcmd list wp`). If the process died between checks, add an `EventMatches` entry. Events from before the service started are ignored by design. |
| `Could not check for ASP.NET 1309 errors` | The account cannot read the Application log (LocalSystem can). |
| No emails | Look for `Email notification failed` in the log. Check the SMTP server, port, and that the receive connector accepts mail from this server. Check `Enabled`, `From` and `To`. |
| Page not reachable from another PC | By default it only listens on localhost. Set `StatusPageUrl` to `http://*:8085`, restart the service, and allow the port in Windows Firewall. |
| Recycling over and over | Each recycle is in the log with its reason. If the queue fills again within minutes of every recycle, the cause is elsewhere (a back end, AD, a load balancer health check). Raise the threshold or cooldown while you investigate. |

---

## Upgrade and uninstall

### Upgrade

Run the newer setup (interactively or with `/S`). It:

- stops the service if it is running;
- removes the previous program files and installs the new ones (this is also how you switch editions);
- **keeps your `monitorsettings.json` and the logs**;
- saves the new version's default settings as `monitorsettings.default.json` next to yours, so you can compare them and copy over any new settings you want (settings missing from your file use their defaults);
- starts the service again if it was running before.

### Uninstall

Any of these:

- **Settings > Apps > Exchange AppPool Monitor > Uninstall** (Server 2016: also **Control Panel > Programs and Features**).
- Start menu: **Exchange AppPool Monitor > Uninstall Exchange AppPool Monitor**.
- Silently: `& "C:\Program Files\ExchangeAppPoolMonitor\Uninstall.exe" /S`

The uninstaller stops and removes the service, then removes the program files, the Start menu folder and the Apps entry. It asks whether to also delete the settings file and the logs. The default (and the silent uninstall) is to **keep** them, so a reinstall picks up the same settings; delete the folder by hand afterwards if you do not want them.

### Manual install

If you installed by hand, upgrade with:

```powershell
Stop-Service ExchangeAppPoolMonitor
robocopy .\publish "C:\Program Files\ExchangeAppPoolMonitor" /E /XF monitorsettings.json   # keep your settings
Start-Service ExchangeAppPoolMonitor
```

and remove with:

```powershell
Stop-Service ExchangeAppPoolMonitor
sc.exe delete ExchangeAppPoolMonitor
Remove-Item "C:\Program Files\ExchangeAppPoolMonitor" -Recurse   # also deletes the settings and logs
```

---

## Security notes

- **The status page has no login.** It is read only (nothing on it can recycle a pool or change settings), but it shows server and pool names and error details. It listens on localhost only by default. If you open it to the network, restrict the firewall rule to admin machines.
- **The SMTP password** (if used) is stored in plain text in `monitorsettings.json`. Restrict the install folder to Administrators and SYSTEM:
  ```powershell
  icacls "C:\Program Files\ExchangeAppPoolMonitor" /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F"
  ```
- **The service runs as LocalSystem** because recycling app pools requires administrator rights. Anyone who can edit the install folder can change what it does, which is another reason to lock the folder down.

---

## Code map

| File | Purpose |
|---|---|
| `ExchangeAppPoolMonitor.csproj` | Project file: .NET 6 target, version, author details, and the two editions (`Flavor` = `Standalone` or `DotNet6`). No NuGet packages. |
| `AssemblyInfo.cs` | Marks the app as Windows-only. |
| `WindowsServiceLifetime.cs` | Runs the app as a Windows service: reports start and stop to the Service Control Manager through `advapi32.dll`. |
| `Program.cs` | Entry point. Loads `monitorsettings.json`, registers the services, sets up the Windows service host and the log destinations (console, Event Viewer, service log file), and maps the `/` and `/api/status` web routes. |
| `Settings.cs` | Classes the JSON settings are loaded into, with the defaults. |
| `MonitorWorker.cs` | The monitor loop: collects 1309 errors, checks each pool, decides when to recycle, applies the cooldown, logs and emails. Start here to understand the behaviour. |
| `RequestQueueReader.cs` | Reads `HTTP Service Request Queues\CurrentQueueSize` for each pool through Windows' `pdh.dll`. |
| `AspNetErrorReader.cs` | Reads new Event ID 1309 entries from the Application log and pulls out the w3wp.exe PID. |
| `AppCmd.cs` | Runs `appcmd.exe` to get pool state, count long running requests, list worker processes, and recycle pools. |
| `ActionLog.cs` | Writes the daily JSON line action log, keeps the last 25 entries for the page, and triggers the daily cleanup of old files. |
| `ServiceFileLoggerProvider.cs` | Sends standard .NET logging to the daily plain text service log, alongside the Windows event log. |
| `LogFiles.cs` | Shared log file helpers: folder and file names, retention cleanup, and the last resort startup failure writer. |
| `EmailNotifier.cs` | Sends the notification emails over SMTP. |
| `MonitorState.cs` | Latest status of the service and each pool, shared between the loop and the web page. |
| `StatusPage.cs` | Builds the HTML status page. |
| `monitorsettings.json` | The settings file. Copied next to the exe on build and publish. |
| `SettingsCli.cs` | The `--export-settings` / `--import-settings` command line mode the installer uses to read and write the settings from its pages. |
| `LICENSE` | The MIT License text. Installed next to the app and shown by the installer. |
| `installer/ExchangeAppPoolMonitor.nsi` | NSIS script for the setup exe: install, service creation, Apps entry, Start menu, upgrade and uninstall. |
| `installer/build-installer.ps1` / `.sh` | Publish the app and build the setup exe for each edition, on Windows or Linux/macOS. |

---

## Known limitations

- **One server.** Each Exchange server runs its own copy and only looks after its own pools.
- **Counters reset on restart.** Recycle counts and the 1309 window are kept in memory and start from zero when the service restarts. The log files keep the full history.
- **1309 PID matching depends on the event layout.** The PID is taken as the value just before `w3wp.exe` in the event data. This matches the standard ASP.NET 4.x layout but has not been checked against every .NET Framework version. If PIDs are not being picked up, the log line for each matched event says whether a PID was found, and `EventMatches` is the fallback.
- **The pool is not started if it is stopped.** A stopped pool shows red on the page, but the service does not start it (a recycle of a stopped pool fails, is logged, and is emailed once per cooldown).

---

## License

Exchange AppPool Monitor is written by **Nathan Kelly** and is **free software**, distributed under the **MIT License**. The full text is in the [`LICENSE`](LICENSE) file, which is also installed next to the app and shown by the installer.

```
Copyright (c) 2026 Nathan Kelly
```

In short, the MIT License lets anyone:

- **use** it for free, on any number of servers, at work or at home, including commercially;
- **copy and share** it, with or without changes;
- **change** it, since the source code is included.

The only condition is that the copyright notice and license text stay with any copy of the software or substantial parts of it. The software is provided **as is, with no warranty**: the author is not liable for any problems arising from its use. Test it (dry run mode exists for this) before relying on it in production.

This makes it more than freeware: it is free of charge *and* the source is open, so anyone can read exactly what it does to their Exchange servers.

### Third-party components

The app is built on components from other projects, each under its own license. All of them allow free use and redistribution:

| Component | Used for | License |
|---|---|---|
| .NET 6 runtime and ASP.NET Core 6 (Microsoft) | Running the app, the status page, configuration, logging and event log access. Bundled inside the exe in the standalone edition; installed separately for the dotnet6 edition. | MIT |
| Windows components: `appcmd.exe` (IIS), `pdh.dll` (performance counters), `advapi32.dll` (services) | Called by the app on the server; part of Windows and IIS, not shipped with the app | Part of Windows |
| NSIS (Nullsoft Scriptable Install System) | Building the installers | zlib/libpng license. Installers made with NSIS can be distributed freely. |

The copyright and license above cover Exchange AppPool Monitor's own code. They do not change the licenses of these components.
