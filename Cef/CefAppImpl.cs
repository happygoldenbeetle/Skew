using Xilium.CefGlue;

namespace Skew.Cef;

/// <summary>
/// CefApp for the browser process. Configures command-line switches and
/// performs work after the global CEF context is initialized.
///
/// Direct port of the mac CefAppImpl (App/CefAppImpl.h/.mm). CefAppImpl also
/// acts as the CefBrowserProcessHandler — in CefGlue that is returned from
/// <see cref="GetBrowserProcessHandler"/>.
/// </summary>
public sealed class CefAppImpl : CefApp
{
    private readonly BrowserProcessHandler _browserProcessHandler = new();

    protected override CefBrowserProcessHandler GetBrowserProcessHandler()
        => _browserProcessHandler;

    // ── CefApp ───────────────────────────────────────────────────────────

    protected override void OnBeforeCommandLineProcessing(
        string processType, CefCommandLine commandLine)
    {
        // Browser-process only tweaks (process_type is empty for the browser).
        if (!string.IsNullOrEmpty(processType))
            return;

        // Smooth scrolling and modern web features on by default.
        if (!commandLine.HasSwitch("disable-smooth-scrolling"))
            commandLine.AppendSwitch("enable-smooth-scrolling");

        // Allow autoplay so embedded media behaves like a normal browser.
        commandLine.AppendSwitch("autoplay-policy", "no-user-gesture-required");

        // Enable Chromium's native automatic Picture-in-Picture. With these,
        // setting `video.autoPictureInPicture = true` makes the engine pop the
        // video out when the tab is hidden — no user-gesture restriction. The
        // user-facing toggle gates whether our agent sets that attribute.
        commandLine.AppendSwitch(
            "enable-features",
            "WebUIDarkMode,AutoPictureInPictureForVideoPlayback,MediaSessionEnterPictureInPicture,OverlayScrollbar,FluentScrollbar,FluentOverlayScrollbar,OverscrollHistoryNavigation");
        commandLine.AppendSwitch("enable-blink-features", "AutoPictureInPicture");
        
        // Force trackpad swipe navigation
        commandLine.AppendSwitch("overscroll-history-navigation", "1");

        // Force native Chromium web controls (like scrollbars) to render in dark mode
        // to match our WinUI shell.
        commandLine.AppendSwitch("force-dark-mode");

        // Let the page render as fast as it can rather than at 60.
        //
        // Offscreen rendering is paced by the compositor, and the compositor
        // paces itself twice: it waits for a vsync it does not actually have a
        // window for, and it caps itself at a frame rate on top of that. Both
        // are hard limits regardless of what windowless_frame_rate asks for, so
        // the page sat at 60 on a display running well above it.
        //
        // The cost is real: uncapped means Chromium will happily burn a core
        // producing frames nothing asked for. It is the trade the setting is
        // for.
        commandLine.AppendSwitch("disable-gpu-vsync");
        commandLine.AppendSwitch("disable-frame-rate-limit");

        // NOTE: the mac build also appends use-mock-keychain / password-store=basic
        // to avoid macOS Keychain "Safe Storage" prompts on ad-hoc-signed clones.
        // Those switches are macOS-specific; on Windows Chromium uses DPAPI and
        // does not prompt, so the intent (no credential prompts) needs no switch.
    }

    protected override void OnRegisterCustomSchemes(CefSchemeRegistrar registrar)
    {
        // Standard + secure + fetch-enabled, matching the mac RegisterCustomSchemes.
        var options = CefSchemeOptions.Standard
            | CefSchemeOptions.Secure
            | CefSchemeOptions.CorsEnabled
            | CefSchemeOptions.FetchEnabled;

        registrar.AddCustomScheme(SkewSchemes.InternalScheme, options);
        registrar.AddCustomScheme(SkewSchemes.ExtensionScheme, options);
    }

    /// <summary>
    /// CefBrowserProcessHandler portion. Registers the scheme handler factories
    /// once the global context is initialized (mac OnContextInitialized).
    /// </summary>
    private sealed class BrowserProcessHandler : CefBrowserProcessHandler
    {
        protected override void OnContextInitialized()
        {
            CefRuntime.RegisterSchemeHandlerFactory(
                SkewSchemes.ExtensionScheme, null,
                new SkewExtensionSchemeHandlerFactory());

            CefRuntime.RegisterSchemeHandlerFactory(
                SkewSchemes.InternalScheme, null,
                new SkewInternalSchemeHandlerFactory());

            // The UI layer creates browsers on demand once the window is up.
        }
    }
}
