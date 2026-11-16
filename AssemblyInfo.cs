// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.Runtime.Versioning;

// The app only runs on Windows: it uses IIS (appcmd.exe), Windows performance counters (pdh.dll),
// the Windows event log and the Windows Service Control Manager (advapi32.dll). Marking the whole
// assembly as Windows-only tells the compiler so, which is what lets the project target plain net6.0
// (see the .csproj) without warnings about Windows-only APIs.
[assembly: SupportedOSPlatform("windows")]
