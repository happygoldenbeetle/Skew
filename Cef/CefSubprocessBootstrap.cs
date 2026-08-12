using System.Runtime.CompilerServices;

namespace Skew.Cef;

/// <summary>
/// Runs the CEF subprocess fast-path before the WinUI-generated entry point.
///
/// <para>
/// WinUI generates the program <c>Main</c>, so we cannot easily own it. A module
/// initializer runs as soon as the assembly is loaded — before <c>Main</c> — which
/// is early enough to dispatch CEF render/GPU/utility subprocesses. On those
/// subprocesses <see cref="CefRuntimeHost.ExecuteSubprocessAndExitIfNeeded"/>
/// terminates the process; on the browser process it returns and WinUI starts
/// normally. This is the Windows analog of the mac main.mm framework-load + the
/// helper-process entry points the CEF framework bundles provide.
/// </para>
/// </summary>
internal static class CefSubprocessBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Command-line args identify subprocess type via --type=...; CEF reads
        // them directly, so passing the raw process args is sufficient.
        string[] args = Environment.GetCommandLineArgs();
        CefRuntimeHost.ExecuteSubprocessAndExitIfNeeded(args);
    }
}
