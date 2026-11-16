# Dependencies and air-gapped use

This page lists everything Exchange AppPool Monitor depends on, at build time and at run time, and what is needed on a network with no internet access.

**Short version:** the app uses **no NuGet packages**. It needs only .NET 6 (which the standalone edition carries inside its exe) and parts of Windows and IIS that are already on every Exchange server. The standalone installer contains everything needed to run it.

## Summary

| Situation | Needs internet? | What is needed |
|---|---|---|
| **Installing and running the standalone edition** | **No** | Only the setup file. The .NET 6 runtime and ASP.NET Core are inside the exe. |
| Installing and running the dotnet6 edition | No, but... | The ASP.NET Core Runtime 6.0 (x64) must already be installed on the server. In an air-gapped network its offline installer has to be brought in separately. **Use the standalone edition in air-gapped environments.** |
| Building the dotnet6 edition from source | **No** | The .NET 6 SDK for Windows x64. Nothing else: the SDK contains every part the build uses. |
| Building the standalone edition from source | No, if the two runtime package files are provided | The .NET 6 SDK, plus `Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.AspNetCore.App.Runtime.win-x64` (the runtime that gets bundled into the exe) in a local folder. See [Building offline](#building-offline). |
| Building the installers | No | As above, plus NSIS 3 on the build machine (one installer file that can be carried in). |
| SonarQube analysis | No, if the scanner is installed internally | The SonarScanner for .NET and a Java runtime, provided by your SonarQube team, plus what building the dotnet6 edition needs. |

## What the running service uses

| What | Where it comes from | Used for | Code |
|---|---|---|---|
| .NET 6 runtime | Bundled in the standalone exe, or installed on the server (dotnet6 edition) | Running the app | |
| ASP.NET Core 6 | Same | The status page web server, settings loading, logging, background service host | `Program.cs`, `StatusPage.cs` |
| System.Diagnostics.EventLog | Part of ASP.NET Core 6 | Reading ASP.NET 1309 events; writing the app's entries to Event Viewer | `AspNetErrorReader.cs`, `Program.cs` |
| `appcmd.exe` | IIS, `%windir%\system32\inetsrv` | Pool state, worker process list, long running requests, recycling | `AppCmd.cs` |
| `pdh.dll` | Windows | Reading the request queue performance counter | `RequestQueueReader.cs` |
| `advapi32.dll` | Windows | Reporting start and stop to the Service Control Manager | `WindowsServiceLifetime.cs` |
| IIS Request Monitor feature (`Web-Request-Monitor`) | Windows feature, installed by Exchange setup | Lets `appcmd list requests` report running requests | |
| An SMTP server | Your mail system (default: the Exchange server itself, `localhost:25`) | Sending notifications | `EmailNotifier.cs` |

**What the service connects to:** nothing on the internet. It runs `appcmd.exe` on the same server, reads local performance counters and the local Application event log, listens for the status page on `localhost:8085` (unless changed), and sends email to the SMTP server in its settings. It does not check for updates, send telemetry, or download anything.

## Build-time pieces

| Piece | Needed for | Where it comes from |
|---|---|---|
| .NET 6 SDK (x64) | Compiling | Microsoft. Carry in the offline installer for an air-gapped build machine. Includes the reference files and the exe launcher the build uses. |
| `Microsoft.NETCore.App.Runtime.win-x64` 6.0.x | Standalone edition only: the .NET runtime bundled into the exe | Microsoft, distributed only as a package file (`.nupkg`). |
| `Microsoft.AspNetCore.App.Runtime.win-x64` 6.0.x | Standalone edition only: ASP.NET Core bundled into the exe | Same. |
| NSIS 3 | Building the installers | nsis.sourceforge.io |

The two runtime files are not third-party libraries: they *are* the .NET runtime, in the form Microsoft provides it for bundling. They are the only package files the build reads with the .NET 6 SDK, and only for the standalone edition.

**With a newer SDK** (for example the .NET 8 SDK) building this .NET 6 project, the SDK also reads a few more of .NET's own package files that the .NET 6 SDK already contains: `Microsoft.NETCore.App.Ref`, `Microsoft.AspNetCore.App.Ref` and `Microsoft.NETCore.App.Host.win-x64` (all 6.0.x), plus `Microsoft.NET.ILLink.Tasks` and `Microsoft.NET.ILLink.Analyzers` for the standalone single-file build. The `packages` folder (in the published repository and in the evaluation package) holds all of these, so either SDK works offline.

## Building offline

1. Install the .NET 6 SDK and NSIS on the build machine.
2. Have the `packages` folder (in the published repository and in the evaluation package) on the machine, or just the two runtime package files if using the .NET 6 SDK.
3. Build:

```powershell
# From the folder with ExchangeAppPoolMonitor.csproj; point -PackageSource at the packages folder
# (".\packages" in the published repository, "..\packages" in the evaluation package).

# Both installers, restoring only from the local folder:
.\installer\build-installer.ps1 -PackageSource .\packages

# Or just the program files:
dotnet restore ExchangeAppPoolMonitor.csproj -p:Flavor=Standalone --source .\packages
dotnet publish ExchangeAppPoolMonitor.csproj -c Release -p:Flavor=Standalone --no-restore -o .\publish
```

The dotnet6 edition needs no `--source` with the .NET 6 SDK; `-PackageSource` does no harm if given anyway.

This was tested with an empty package cache and only the local folder available: both editions built successfully.

### Getting the runtime package files

On any machine with internet access and the .NET SDK, building the standalone edition once fetches them:

```powershell
$env:NUGET_PACKAGES = "$PWD\pkgcache"     # start from an empty cache
dotnet publish ExchangeAppPoolMonitor.csproj -c Release -p:Flavor=Standalone -o out
Get-ChildItem pkgcache -Recurse -Filter *.nupkg | Copy-Item -Destination .\packages
```

Use the same 6.0.x version as the .NET 6 runtime you want to ship (the SDK picks the latest 6.0 patch it knows about). After a .NET 6 security patch, refresh these files and rebuild the standalone installer to ship the patched runtime.

## History: the NuGet version

The first version of the project used three Microsoft NuGet packages (`Microsoft.Extensions.Hosting.WindowsServices`, `System.Diagnostics.EventLog`, `System.Diagnostics.PerformanceCounter`), which brought in 37 more packages behind them. They were replaced so the project builds without NuGet:

| Package | Replaced by |
|---|---|
| System.Diagnostics.EventLog | The same library, which is already part of ASP.NET Core 6 |
| System.Diagnostics.PerformanceCounter | Calling Windows' `pdh.dll` directly (`RequestQueueReader.cs`) |
| Microsoft.Extensions.Hosting.WindowsServices | Calling Windows' `advapi32.dll` service functions directly (`WindowsServiceLifetime.cs`) |

The NuGet-based version is kept on the `exchange-apppool-monitor-nuget` branch of the repository, with its own copy of this page listing all 49 package files it needs.

## Licenses

.NET, ASP.NET Core and the runtime package files are © Microsoft and published under the MIT License. NSIS is under the zlib/libpng license. `appcmd.exe`, `pdh.dll` and `advapi32.dll` are part of Windows and IIS. See [License](../README.md#license) in the README.
