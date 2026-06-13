namespace Mori.Cef;

/// <summary>
/// Custom URL schemes Mori owns. Port of mac Shared/MoriSchemes.h.
///
/// These are registered as standard, secure, fetch-enabled schemes during
/// <see cref="CefAppImpl.OnRegisterCustomSchemes"/> and served by
/// <see cref="MoriSchemeHandler"/> factories registered in
/// <see cref="CefAppImpl.OnContextInitialized"/>.
/// </summary>
public static class MoriSchemes
{
    /// <summary>Serves enabled-extension page assets (mori-extension://&lt;id&gt;/path).</summary>
    public const string ExtensionScheme = "mori-extension";

    /// <summary>Serves Mori's own internal pages (mori://newtab/, etc.).</summary>
    public const string InternalScheme = "mori";

    /// <summary>
    /// Background-page path served inside the extension scheme. Matches the mac
    /// kMoriExtensionBackgroundPath constant.
    /// </summary>
    public const string ExtensionBackgroundPath = "/__mori_background__.html";
}
