using Microsoft.UI.Xaml;

namespace Mori;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window.
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND).
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public App()
    {
        InitializeComponent();
        this.UnhandledException += App_UnhandledException;
        // CEF init happens in OnLaunched (browser process) and in the static
        // Main() subprocess fast-path. See ExecuteSubprocessAndExitIfNeeded.
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try { System.IO.File.WriteAllText("crash.log", $"[CRASH] {e.Exception.Message}\n{e.Exception.StackTrace}"); } catch {}
        System.Console.WriteLine($"[CRASH] {e.Exception.Message}\n{e.Exception.StackTrace}");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Initialize the global CEF context on the WinUI thread before any
        // browser view is created (mac main.mm: CefInitialize before the window).
        Mori.Cef.CefRuntimeHost.Initialize(
            System.Environment.GetCommandLineArgs(),
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        // Pre-initialize ExtensionStore on the UI thread so its ObservableCollection
        // captures the correct dispatcher, preventing crashes when CEF threads read it.
        _ = Mori.Models.ExtensionStore.Shared;

        Window = new MainWindow();
        Window.Closed += (_, _) => Mori.Cef.CefRuntimeHost.Shutdown();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }
}
