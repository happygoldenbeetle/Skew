namespace Mori.Helpers;

/// <summary>
/// The glyph and shortcut text for a page context-menu row.
///
/// <para>
/// The menu arrives from Chromium as labels and command ids — there is nowhere
/// in <c>CefMenuModel</c> to hang an icon — so the label is what the mapping has
/// to go on. Chromium's own labels carry mnemonics and ellipses that vary by
/// build, hence the normalising pass before the lookup.
/// </para>
///
/// <para>
/// The shortcut column is a label, not a binding: it names the key Chromium and
/// the rest of the browser already answer to, so nothing here has to be pressed
/// for the menu to work.
/// </para>
/// </summary>
internal static class ContextMenuChrome
{
    /// <summary>Segoe Fluent glyph per row, keyed by normalised label.</summary>
    private static readonly Dictionary<string, string> s_glyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        // Page
        ["back"] = "",
        ["forward"] = "",
        ["reload"] = "",
        ["save as"] = "",
        ["print"] = "",
        ["translate to english"] = "",
        ["view page source"] = "",
        ["inspect"] = "",

        // Image
        ["open image in new tab"] = "",
        ["save image as"] = "",
        ["copy image"] = "",
        ["copy image address"] = "",
        ["search google for image"] = "",

        // Link
        ["open link in new tab"] = "",
        ["open link in new window"] = "",
        ["open link in incognito window"] = "",   // RedEye, the eye Arc shows
        ["save link as"] = "",
        ["copy link address"] = "",

        // Whatever Chromium still supplies for text and fields
        ["undo"] = "",
        ["redo"] = "",
        ["cut"] = "",
        ["copy"] = "",
        ["paste"] = "",
        ["delete"] = "",
        ["select all"] = "",
    };

    /// <summary>What each row answers to, as Chrome and Arc show it.</summary>
    private static readonly Dictionary<string, string> s_shortcuts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["back"] = "B",
        ["forward"] = "F",
        ["reload"] = "Ctrl+R",
        ["save as"] = "Ctrl+S",
        ["print"] = "Ctrl+P",
        ["translate to english"] = "T",
        ["view page source"] = "Ctrl+U",
        ["inspect"] = "N",

        ["open image in new tab"] = "I",
        ["save image as"] = "V",
        ["copy image"] = "Y",
        ["copy image address"] = "O",
        ["search google for image"] = "S",
    };

    public static string? GlyphFor(string? label)
        => s_glyphs.TryGetValue(Normalize(label), out var glyph) ? glyph : null;

    public static string? ShortcutFor(string? label)
        => s_shortcuts.TryGetValue(Normalize(label), out var text) ? text : null;

    /// <summary>
    /// Strip the mnemonic ampersands Chromium puts in ("View page so&amp;urce")
    /// and the trailing ellipsis, in either the three-dot or the single-glyph
    /// spelling, so one key covers every way a row can be written.
    /// </summary>
    private static string Normalize(string? label)
    {
        if (string.IsNullOrEmpty(label)) return string.Empty;
        return label
            .Replace("&", "")
            .Replace("...", "")
            .Replace("…", "")
            .Trim();
    }
}
