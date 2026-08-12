using System.Text.Json;

namespace Skew.Cef;

/// <summary>
/// Bridge between the extension scheme handler (<see cref="SkewSchemeHandler"/>)
/// and the chrome.* runtime builder. Port of the mac ExtensionRuntimeBridge.h /
/// SkewExtensionPageRuntimeJS, which lived in BrowserClient.mm.
///
/// <para>
/// The Alloy/child-view embedding model has no built-in extension runtime, so
/// Skew implements the chrome.* surface itself. This shim is injected at serve
/// time (and post-load) so an extension page can talk to the native host through
/// the <c>window.__skewExt*</c> hooks that <see cref="Controls.SkewBrowserView"/>
/// drives.
/// </para>
/// </summary>
public static class ExtensionRuntimeBridge
{
    /// <summary>
    /// Full, wrapped chrome.* runtime shim JS for an enabled extension page, or
    /// null if the id doesn't resolve to an enabled extension. Mirrors
    /// SkewExtensionPageRuntimeJS(NSString*).
    /// </summary>
    public static string? ExtensionPageRuntimeJs(string extensionId)
    {
        if (string.IsNullOrEmpty(extensionId))
            return null;
        Models.BrowserExtension? extension = Models.ExtensionStore.Shared.GetSnapshot()
            .FirstOrDefault(item => item.Enabled && string.Equals(
                item.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        if (extension?.Manifest is null)
            return null;
        return ExtensionRuntimeShim.Generate(extension.Id, extension.Manifest, extension.Path);
    }
}
