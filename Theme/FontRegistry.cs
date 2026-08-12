using Microsoft.UI.Xaml.Media;

namespace Skew.Theme;

/// <summary>
/// Resolves the UI font family. Port of FontRegistry.swift.
///
/// <para>
/// Skew's webapp uses Söhne, but it ships only as <c>.woff2</c> and is licensed,
/// so — exactly as on the Mac — the native app honours a system-wide Söhne if one
/// happens to be installed and otherwise falls back to the platform UI font. The
/// Mac's fallback is SF Pro; the Windows counterpart is Segoe UI Variable, which
/// like SF Pro carries optical sizes. "Text" is the optical size intended for UI
/// copy at these sizes; "Segoe UI" trails it for Windows 10.
/// </para>
/// </summary>
public static class FontRegistry
{
    private const string SoehneCandidates = "Söhne, Soehne, Söhne Buch, Soehne Buch";
    private const string SystemFallback = "Segoe UI Variable Text, Segoe UI";

    private static FontFamily? s_ui;

    /// <summary>The interface font, with Söhne first and the system font behind it.</summary>
    public static FontFamily Ui => s_ui ??= new FontFamily($"{SoehneCandidates}, {SystemFallback}");
}
