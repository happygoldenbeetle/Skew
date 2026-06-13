using Microsoft.UI.Dispatching;
using Xilium.CefGlue;

namespace Mori.Cef;

/// <summary>
/// Browser-process bootstrap for CEF. Port of the mac entry point (App/main.mm)
/// adapted to a managed WinUI host.
///
/// <para>Responsibilities:</para>
/// <list type="number">
///   <item>Load the vendored CEF library and run the subprocess fast-path.</item>
///   <item>Stand up the CEF context with our <see cref="CefAppImpl"/>.</item>
///   <item>Drive the CEF message loop cooperatively with the WinUI dispatcher.</item>
///   <item>Tear the context down on exit.</item>
/// </list>
///
/// <para>
/// macOS differences handled here: the mac build calls <c>CefRunMessageLoop()</c>
/// which owns the loop and drives AppKit. WinUI already owns the thread's loop,
/// so we initialize with an EXTERNAL message pump and call
/// <see cref="CefDoMessageLoopWork"/> on a <see cref="DispatcherQueueTimer"/>.
/// Windows also needs a dedicated subprocess executable; non-browser processes
/// short-circuit in <see cref="ExecuteSubprocessAndExitIfNeeded"/>.
/// </para>
/// </summary>
public static class CefRuntimeHost
{
    private static CefAppImpl? s_app;
    private static DispatcherQueueTimer? s_pumpTimer;
    private static bool s_initialized;

    /// <summary>The folder containing libcef.dll and the CEF resource payload.</summary>
    private static string CefRootDirectory =>
        Path.Combine(AppContext.BaseDirectory, "cef");

    /// <summary>
    /// Writable user-data folder so history/cookies/localStorage persist across
    /// launches, matching real-browser behavior (mac DefaultCachePath()).
    /// </summary>
    private static string DefaultCachePath()
    {
        string baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string path = Path.Combine(baseDir, "MoriBrowser", "Default");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Must be the first call in the process entry point. On render/GPU/utility
    /// subprocesses this runs the child logic and terminates the process; on the
    /// browser process it returns and normal startup continues.
    ///
    /// The mac build gets helper processes for free via framework helper bundles;
    /// on Windows the same executable is reused and dispatched by CefExecuteProcess.
    /// </summary>
    public static void ExecuteSubprocessAndExitIfNeeded(string[] args)
    {
        CefRuntime.Load(CefRootDirectory);

        var mainArgs = new CefMainArgs(args);
        // app must be available so subprocesses see the same command-line tweaks.
        s_app ??= new CefAppImpl();

        int exitCode = CefRuntime.ExecuteProcess(mainArgs, s_app, IntPtr.Zero);
        if (exitCode >= 0)
        {
            // This was a subprocess; CEF handled it. Exit immediately.
            Environment.Exit(exitCode);
        }
    }

    /// <summary>
    /// Initialize the global CEF context on the WinUI thread. Safe to call once.
    /// </summary>
    public static void Initialize(string[] args, DispatcherQueue dispatcher)
    {
        if (s_initialized)
            return;

        CefRuntime.Load(CefRootDirectory);

        var mainArgs = new CefMainArgs(args);
        s_app ??= new CefAppImpl();

        string cachePath = DefaultCachePath();

        var settings = new CefSettings
        {
            // No cef_sandbox in the minimal distribution (mirrors mac no_sandbox).
            NoSandbox = true,
            WindowlessRenderingEnabled = false,
            LogSeverity = CefLogSeverity.Warning,
            // Persist *session* cookies too, so logins survive a relaunch the way
            // every modern browser does (mac persist_session_cookies = true).
            PersistSessionCookies = true,
            CachePath = cachePath,
            RootCachePath = cachePath,
            // WinUI drives the thread loop; CEF must not own it.
            MultiThreadedMessageLoop = false,
            ExternalMessagePump = false,
            BrowserSubprocessPath = Path.Combine(AppContext.BaseDirectory, "Mori.exe"),
        };

        CefRuntime.Initialize(mainArgs, settings, s_app, IntPtr.Zero);

        StartMessagePump(dispatcher);
        s_initialized = true;
    }

    /// <summary>
    /// Cooperative pump: ask CEF to do a slice of work each tick. ~60Hz keeps the
    /// engine responsive without starving the WinUI dispatcher.
    /// </summary>
    private static void StartMessagePump(DispatcherQueue dispatcher)
    {
        s_pumpTimer = dispatcher.CreateTimer();
        s_pumpTimer.Interval = TimeSpan.FromMilliseconds(16);
        s_pumpTimer.IsRepeating = true;
        s_pumpTimer.Tick += (_, _) => CefRuntime.DoMessageLoopWork();
        s_pumpTimer.Start();
    }

    /// <summary>Tear down the global CEF context. Call once on app exit.</summary>
    public static void Shutdown()
    {
        if (!s_initialized)
            return;

        s_pumpTimer?.Stop();
        s_pumpTimer = null;
        CefRuntime.Shutdown();
        s_initialized = false;
    }
}
