// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExchangeAppPoolMonitor;

/// <summary>
/// Lets the app run as a Windows service, using the Windows service functions in advapi32.dll
/// directly, so no extra library (NuGet package) is needed.
/// </summary>
/// <remarks>
/// <para>
/// How a Windows service starts and stops, and where this class fits in:
/// </para>
/// <list type="number">
///   <item>The Service Control Manager (SCM) starts ExchangeAppPoolMonitor.exe. Program.cs sees it is
///         not running interactively and registers this class as the host's lifetime.</item>
///   <item>When the host starts, <see cref="WaitForStartAsync"/> starts a dedicated thread that calls
///         <c>StartServiceCtrlDispatcher</c>. That call connects the process to the SCM and does not return
///         until the service has stopped.</item>
///   <item>The SCM then calls <see cref="ServiceMain"/> on another thread. It registers
///         <see cref="HandleControl"/> to receive stop and shutdown requests, reports the service as
///         Running, and lets the host carry on starting (the monitor loop and the status page).</item>
///   <item>On Stop (services.msc, <c>Stop-Service</c>, uninstall) or Windows shutdown, the SCM calls
///         <see cref="HandleControl"/>. It reports Stop Pending and asks the host to shut down.</item>
///   <item>Once the monitor loop and web server have stopped, the host calls <see cref="StopAsync"/>,
///         which reports Stopped. The dispatcher call then returns and the process exits.</item>
/// </list>
/// <para>
/// If the app stops for any reason other than a stop request (for example a fatal error), it reports
/// Stopped with an error code. The installer sets the service's recovery options to restart it in
/// that case (<c>sc.exe failure</c> plus <c>sc.exe failureflag 1</c>).
/// </para>
/// <para>
/// This does the same job as Microsoft's Microsoft.Extensions.Hosting.WindowsServices package, which
/// this app used before it was made buildable without NuGet.
/// </para>
/// </remarks>
public sealed class WindowsServiceLifetime : IHostLifetime, IDisposable
{
    /// <summary>The service name the installer registers. For a single-service process Windows does not check it.</summary>
    public const string ServiceName = "ExchangeAppPoolMonitor";

    /// <summary>
    /// True when the process was started by the Service Control Manager rather than from a console.
    /// Services run on a non-interactive window station, which is what UserInteractive reports.
    /// </summary>
    public static bool IsRunningAsService => OperatingSystem.IsWindows() && !Environment.UserInteractive;

    private readonly IHostApplicationLifetime _application;
    private readonly ILogger<WindowsServiceLifetime> _logger;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The callbacks handed to Windows. They are kept in fields so the garbage collector cannot free
    // them while Windows still holds pointers to them.
    private readonly ServiceMainCallback _serviceMain;
    private readonly HandlerExCallback _handler;
    private IntPtr _serviceNamePtr;
    private IntPtr _statusHandle;
    private volatile bool _stopRequestedBySystem;

    public WindowsServiceLifetime(IHostApplicationLifetime application, ILogger<WindowsServiceLifetime> logger)
    {
        _application = application;
        _logger = logger;
        _serviceMain = ServiceMain;
        _handler = HandleControl;
    }

    /// <summary>
    /// Called by the host before anything else starts. Connects to the SCM and completes once the
    /// service has been reported as Running.
    /// </summary>
    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.Register(() => _started.TrySetCanceled(cancellationToken));
        // A background thread, so it never keeps the process alive on its own.
        var dispatcherThread = new Thread(RunDispatcher) { IsBackground = true, Name = "Service control dispatcher" };
        dispatcherThread.Start();
        return _started.Task;
    }

    /// <summary>
    /// Called by the host after the monitor loop and web server have stopped. Reports the final
    /// Stopped state to the SCM: success if the stop was requested, an error otherwise.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopRequestedBySystem)
        {
            ReportStatus(ServiceState.Stopped, win32ExitCode: 0, serviceExitCode: 0);
        }
        else
        {
            // Stopped without being asked to: report a service-specific error so the SCM's recovery
            // options (restart) apply.
            _logger.LogError("The service is stopping without a stop request; reporting a failure so Windows can restart it.");
            ReportStatus(ServiceState.Stopped, win32ExitCode: ErrorServiceSpecificError, serviceExitCode: 1);
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_serviceNamePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_serviceNamePtr);
            _serviceNamePtr = IntPtr.Zero;
        }
    }

    /// <summary>Runs on the dedicated thread. Blocks inside StartServiceCtrlDispatcher until the service stops.</summary>
    private void RunDispatcher()
    {
        _serviceNamePtr = Marshal.StringToHGlobalUni(ServiceName);
        var table = new[]
        {
            new ServiceTableEntry { NamePointer = _serviceNamePtr, MainFunctionPointer = Marshal.GetFunctionPointerForDelegate(_serviceMain) },
            new ServiceTableEntry(), // the table ends with an empty entry
        };

        if (!StartServiceCtrlDispatcher(table))
        {
            var error = Marshal.GetLastWin32Error();
            _started.TrySetException(new Win32Exception(error,
                $"Could not connect to the Service Control Manager (error {error}). " +
                "This usually means the exe was started without a console but not as a service."));
        }
    }

    /// <summary>Called by Windows on its own thread once the dispatcher is connected.</summary>
    private void ServiceMain(uint argCount, IntPtr args)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _started.TrySetException(new Win32Exception(error, $"Could not register the service control handler (error {error})."));
            return;
        }

        ReportStatus(ServiceState.Running, win32ExitCode: 0, serviceExitCode: 0);
        _logger.LogInformation("Running as the Windows service {ServiceName}.", ServiceName);
        _started.TrySetResult();
        // Returning here is normal: the service keeps running and controls arrive on the dispatcher thread.
    }

    /// <summary>Called by Windows when the service receives a control request.</summary>
    private uint HandleControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
                _stopRequestedBySystem = true;
                // Tell Windows the stop is under way and how long to wait before assuming it has hung.
                ReportStatus(ServiceState.StopPending, win32ExitCode: 0, serviceExitCode: 0, waitHintMs: 30_000);
                _application.StopApplication();
                return NoError;
            case ControlInterrogate:
                return NoError;
            default:
                return ErrorCallNotImplemented;
        }
    }

    private void ReportStatus(ServiceState state, uint win32ExitCode, uint serviceExitCode, uint waitHintMs = 0)
    {
        if (_statusHandle == IntPtr.Zero) return; // not connected to the SCM (should not happen once started)

        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = (uint)state,
            // Accept stop and shutdown only while running; nothing is accepted while stopping.
            ControlsAccepted = state == ServiceState.Running ? AcceptStop | AcceptShutdown : 0,
            Win32ExitCode = win32ExitCode,
            ServiceSpecificExitCode = serviceExitCode,
            CheckPoint = 0,
            WaitHint = waitHintMs,
        };
        if (!SetServiceStatus(_statusHandle, ref status))
            _logger.LogWarning("Could not report service state {State} to Windows (error {Error}).", state, Marshal.GetLastWin32Error());
    }

    // ------------------------------------------------------------------ Windows API (advapi32.dll)

    private enum ServiceState : uint
    {
        Stopped = 1,
        StopPending = 3,
        Running = 4,
    }

    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint AcceptStop = 0x1;
    private const uint AcceptShutdown = 0x4;
    private const uint ControlStop = 1;
    private const uint ControlInterrogate = 4;
    private const uint ControlShutdown = 5;
    private const uint NoError = 0;
    private const uint ErrorCallNotImplemented = 120;
    private const uint ErrorServiceSpecificError = 1066;

    private delegate void ServiceMainCallback(uint argCount, IntPtr args);
    private delegate uint HandlerExCallback(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr NamePointer;
        public IntPtr MainFunctionPointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, HandlerExCallback handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);
}
