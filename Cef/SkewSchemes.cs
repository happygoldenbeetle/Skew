namespace Skew.Cef;

/// <summary>
/// Custom URL schemes Skew owns. Port of mac Shared/SkewSchemes.h.
///
/// These are registered as standard, secure, fetch-enabled schemes during
/// <see cref="CefAppImpl.OnRegisterCustomSchemes"/> and served by
/// <see cref="SkewSchemeHandler"/> factories registered in
/// <see cref="CefAppImpl.OnContextInitialized"/>.
/// </summary>
public static class SkewSchemes
{
    /// <summary>Serves enabled-extension page assets (skew-extension://&lt;id&gt;/path).</summary>
    public const string ExtensionScheme = "skew-extension";

    /// <summary>Serves Skew's own internal pages (skew://newtab/, etc.).</summary>
    public const string InternalScheme = "skew";

    /// <summary>
    /// Background-page path served inside the extension scheme. Matches the mac
    /// kSkewExtensionBackgroundPath constant.
    /// </summary>
    public const string ExtensionBackgroundPath = "/__skew_background__.html";
}
