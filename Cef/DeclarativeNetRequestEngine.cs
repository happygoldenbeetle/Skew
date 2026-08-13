using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Skew.Models;
using Xilium.CefGlue;

namespace Skew.Cef;

/// <summary>
/// The blocking half of <c>chrome.declarativeNetRequest</c>: rules declared by
/// extensions, matched against every request Chromium is about to make.
///
/// <para>
/// This is what an ad blocker actually runs on. MV3 blockers ship their filter
/// lists as static rulesets in the manifest and expect the browser — not the
/// extension — to do the matching, because the whole point of the API is that
/// no extension code runs per request. Nothing in CEF implements it, so the
/// engine lives here and hooks <c>OnBeforeResourceLoad</c>.
/// </para>
///
/// <para>
/// Rules are compiled once, on load, into regexes; matching a request is then a
/// walk over the candidate list. It runs on Chromium's IO thread for every
/// subresource on the page, so it must never throw and never block: every entry
/// point swallows its own errors and a malformed rule is dropped at compile
/// time rather than examined at match time.
/// </para>
/// </summary>
internal static class DeclarativeNetRequestEngine
{
    /// <summary>One compiled rule, ready to match.</summary>
    private sealed class CompiledRule
    {
        public required string ExtensionId { get; init; }
        public int Id { get; init; }
        public int Priority { get; init; }
        public string ActionType { get; init; } = "block";
        public string? RedirectUrl { get; init; }

        /// <summary>The urlFilter or regexFilter, as a regex over the full URL.</summary>
        public Regex? UrlPattern { get; init; }

        /// <summary>
        /// The longest literal run in the filter, lowercased. A URL that does
        /// not contain it cannot match the pattern, and a substring scan is
        /// enormously cheaper than a regex — with 58,000 rules loaded that
        /// difference is the whole cost of a page load.
        /// </summary>
        public string? LiteralHint { get; init; }

        public HashSet<string>? ResourceTypes { get; init; }
        public HashSet<string>? ExcludedResourceTypes { get; init; }

        /// <summary>Domains of the page making the request (initiatorDomains).</summary>
        public List<string>? InitiatorDomains { get; init; }
        public List<string>? ExcludedInitiatorDomains { get; init; }

        /// <summary>Domains of the request target (requestDomains).</summary>
        public List<string>? RequestDomains { get; init; }
        public List<string>? ExcludedRequestDomains { get; init; }

        public HashSet<string>? RequestMethods { get; init; }

        /// <summary>"thirdParty", "firstParty", or null for either.</summary>
        public string? DomainType { get; init; }
    }

    /// <summary>Static rules, keyed by extension. Rebuilt when the set of extensions changes.</summary>
    private static readonly Dictionary<string, List<CompiledRule>> s_static = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rules added at runtime through updateDynamicRules / updateSessionRules.</summary>
    private static readonly Dictionary<string, List<CompiledRule>> s_dynamic = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The raw JSON of dynamic rules, so getDynamicRules can hand back what was given.</summary>
    private static readonly Dictionary<string, List<JsonElement>> s_dynamicRaw = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object s_lock = new();

    /// <summary>Flat match list, rebuilt on change so the request path takes no locks it can avoid.</summary>
    private static CompiledRule[] s_matchSet = [];

    /// <summary>Requests blocked since launch, per extension — what a badge count is made of.</summary>
    private static readonly Dictionary<string, int> s_blockedCounts = new(StringComparer.OrdinalIgnoreCase);

    public static int BlockedCount(string extensionId)
    {
        lock (s_lock)
            return s_blockedCounts.TryGetValue(extensionId, out int count) ? count : 0;
    }

    public static int TotalBlocked
    {
        get { lock (s_lock) return s_blockedCounts.Values.Sum(); }
    }

    // ── Loading ──────────────────────────────────────────────────────────

    /// <summary>
    /// Compile every enabled extension's static rulesets. Called at startup and
    /// whenever an extension is installed, removed, enabled or disabled.
    /// </summary>
    /// <summary>
    /// What the last compile was built from. The store raises several change
    /// notifications while it loads, and recompiling 58,000 rules on each one
    /// costs seconds of startup for an identical result.
    /// </summary>
    private static string s_loadedSignature = "";

    /// <summary>
    /// Compiled static rules, keyed by extension id, version and path — the
    /// things that would change what the rules are. Survives a reload triggered
    /// by some other extension.
    /// </summary>
    private static readonly Dictionary<string, List<CompiledRule>> s_compileCache = new(StringComparer.Ordinal);

    public static void Reload()
    {
        try
        {
            string signature = string.Join("|", ExtensionStore.Shared.GetSnapshot()
                .Where(extension => extension.Enabled)
                .OrderBy(extension => extension.Id, StringComparer.Ordinal)
                .Select(extension => extension.Id + ":" + extension.Version));
            lock (s_lock)
            {
                if (signature == s_loadedSignature && s_matchSet.Length > 0) return;
                s_loadedSignature = signature;
            }

            var compiled = new Dictionary<string, List<CompiledRule>>(StringComparer.OrdinalIgnoreCase);

            foreach (BrowserExtension extension in ExtensionStore.Shared.GetSnapshot())
            {
                if (!extension.Enabled) continue;
                if (!DeclaresNetRequest(extension)) continue;

                // Keyed by what would change the rules. The store raises a
                // change per extension as it loads, so without this cache a
                // blocker's 58,000 rules are recompiled again every time some
                // unrelated extension appears after it.
                string cacheKey = extension.Id + "@" + extension.Version + "@" + extension.Path;
                List<CompiledRule>? rules;
                lock (s_lock) s_compileCache.TryGetValue(cacheKey, out rules);

                if (rules is null)
                {
                    rules = [];
                    foreach (string rulePath in StaticRulesetPaths(extension))
                    {
                        string full = Path.Combine(extension.Path, rulePath.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(full)) continue;
                        rules.AddRange(CompileRuleFile(extension.Id, full));
                    }
                    lock (s_lock) s_compileCache[cacheKey] = rules;

                    if (rules.Count > 0)
                        ExtensionDiagnostics.Write("dnr", extension.Id,
                            $"Compiled {rules.Count} static rules.");
                }

                if (rules.Count > 0) compiled[extension.Id] = rules;
            }

            lock (s_lock)
            {
                s_static.Clear();
                foreach (var pair in compiled) s_static[pair.Key] = pair.Value;
                RebuildMatchSetLocked();
            }
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("dnr-error", "", $"Reload failed: {ex.Message}");
        }
    }

    private static bool DeclaresNetRequest(BrowserExtension extension)
        => extension.Manifest?.Permissions.Any(permission =>
            permission.StartsWith("declarativeNetRequest", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// The rule files named by <c>declarative_net_request.rule_resources</c>.
    /// Disabled rulesets are skipped — a blocker ships many and enables a few.
    /// </summary>
    private static IEnumerable<string> StaticRulesetPaths(BrowserExtension extension)
    {
        string manifestPath = Path.Combine(extension.Path, "manifest.json");
        if (!File.Exists(manifestPath)) yield break;

        JsonDocument? document = null;
        try { document = JsonDocument.Parse(File.ReadAllText(manifestPath)); }
        catch (Exception) { yield break; }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("declarative_net_request", out JsonElement dnr) ||
                !dnr.TryGetProperty("rule_resources", out JsonElement resources) ||
                resources.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (JsonElement entry in resources.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (entry.TryGetProperty("enabled", out JsonElement enabled) &&
                    enabled.ValueKind == JsonValueKind.False)
                    continue;
                if (entry.TryGetProperty("path", out JsonElement path) &&
                    path.ValueKind == JsonValueKind.String &&
                    path.GetString() is { Length: > 0 } value)
                    yield return value;
            }
        }
    }

    private static List<CompiledRule> CompileRuleFile(string extensionId, string path)
    {
        var result = new List<CompiledRule>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                CompiledRule? rule = CompileRule(extensionId, entry);
                if (rule is not null) result.Add(rule);
            }
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("dnr-error", extensionId,
                $"Could not read {Path.GetFileName(path)}: {ex.Message}");
        }
        return result;
    }

    /// <summary>Turn one rule object into something matchable, or null if it is unusable.</summary>
    private static CompiledRule? CompileRule(string extensionId, JsonElement entry)
    {
        try
        {
            if (entry.ValueKind != JsonValueKind.Object) return null;
            if (!entry.TryGetProperty("action", out JsonElement action) ||
                !action.TryGetProperty("type", out JsonElement actionType))
                return null;

            string type = actionType.GetString() ?? "block";
            // modifyHeaders and upgradeScheme need response-side work that the
            // resource path here does not do yet; dropping them is better than
            // pretending a header rule applied.
            if (type is not ("block" or "allow" or "allowAllRequests" or "redirect"))
                return null;

            string? redirectUrl = null;
            if (type == "redirect")
            {
                if (!action.TryGetProperty("redirect", out JsonElement redirect)) return null;
                if (redirect.TryGetProperty("url", out JsonElement urlValue))
                    redirectUrl = urlValue.GetString();
                // extensionPath and transform forms are not handled yet.
                if (string.IsNullOrEmpty(redirectUrl)) return null;
            }

            JsonElement condition = entry.TryGetProperty("condition", out JsonElement conditionValue)
                ? conditionValue : default;

            bool caseSensitive = condition.ValueKind == JsonValueKind.Object &&
                condition.TryGetProperty("isUrlFilterCaseSensitive", out JsonElement sensitivity) &&
                sensitivity.ValueKind == JsonValueKind.True;

            Regex? pattern = null;
            string? literalHint = null;
            if (condition.ValueKind == JsonValueKind.Object)
            {
                // Interpreted, not RegexOptions.Compiled: a blocker ships tens of
                // thousands of rules, and JIT-compiling each one costs seconds of
                // startup and a great deal of memory for patterns most pages
                // never reach. The literal pre-check below is what keeps matching
                // fast instead.
                RegexOptions options =
                    (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) |
                    RegexOptions.CultureInvariant;

                if (condition.TryGetProperty("regexFilter", out JsonElement regexFilter) &&
                    regexFilter.GetString() is { Length: > 0 } regexText)
                {
                    pattern = new Regex(regexText, options, TimeSpan.FromMilliseconds(50));
                }
                else if (condition.TryGetProperty("urlFilter", out JsonElement urlFilter) &&
                    urlFilter.GetString() is { Length: > 0 } filterText)
                {
                    pattern = new Regex(UrlFilterToRegex(filterText), options,
                        TimeSpan.FromMilliseconds(50));
                    literalHint = LongestLiteral(filterText);
                }
            }

            return new CompiledRule
            {
                ExtensionId = extensionId,
                Id = entry.TryGetProperty("id", out JsonElement id) && id.TryGetInt32(out int idValue) ? idValue : 0,
                Priority = entry.TryGetProperty("priority", out JsonElement priority) &&
                    priority.TryGetInt32(out int priorityValue) ? priorityValue : 1,
                ActionType = type,
                RedirectUrl = redirectUrl,
                UrlPattern = pattern,
                LiteralHint = literalHint,
                ResourceTypes = StringSet(condition, "resourceTypes"),
                ExcludedResourceTypes = StringSet(condition, "excludedResourceTypes"),
                InitiatorDomains = StringList(condition, "initiatorDomains") ?? StringList(condition, "domains"),
                ExcludedInitiatorDomains = StringList(condition, "excludedInitiatorDomains") ??
                    StringList(condition, "excludedDomains"),
                RequestDomains = StringList(condition, "requestDomains"),
                ExcludedRequestDomains = StringList(condition, "excludedRequestDomains"),
                RequestMethods = StringSet(condition, "requestMethods"),
                DomainType = condition.ValueKind == JsonValueKind.Object &&
                    condition.TryGetProperty("domainType", out JsonElement domainType)
                        ? domainType.GetString() : null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Translate the urlFilter mini-language into a regex.
    ///
    /// <para>
    /// <c>||</c> anchors to a domain and its subdomains, <c>|</c> to the start or
    /// end of the URL, <c>^</c> matches a separator (anything not a letter,
    /// digit, _, -, . or %) or the end of the URL, and <c>*</c> is a wildcard.
    /// Everything else is literal.
    /// </para>
    /// </summary>
    internal static string UrlFilterToRegex(string filter)
    {
        var builder = new StringBuilder();
        int start = 0;
        int end = filter.Length;

        if (filter.StartsWith("||", StringComparison.Ordinal))
        {
            // Scheme, then optionally any subdomain prefix, then the literal.
            builder.Append("^[a-z]+://([^/?#]*\\.)?");
            start = 2;
        }
        else if (filter.StartsWith('|'))
        {
            builder.Append('^');
            start = 1;
        }

        bool anchorEnd = false;
        if (end > start && filter[end - 1] == '|')
        {
            anchorEnd = true;
            end--;
        }

        for (int i = start; i < end; i++)
        {
            char c = filter[i];
            switch (c)
            {
                case '*':
                    builder.Append(".*");
                    break;
                case '^':
                    // A separator, or the end of the URL.
                    builder.Append("(?:[^a-zA-Z0-9_\\-.%]|$)");
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        if (anchorEnd) builder.Append('$');
        return builder.ToString();
    }

    /// <summary>
    /// The longest run of ordinary characters in a urlFilter — no wildcards, no
    /// anchors. Any URL the pattern matches must contain it verbatim, so it is a
    /// sound filter to test first. Short runs are not worth the scan.
    /// </summary>
    private static string? LongestLiteral(string filter)
    {
        string? best = null;
        int start = -1;
        for (int i = 0; i <= filter.Length; i++)
        {
            bool literal = i < filter.Length && filter[i] is not ('*' or '^' or '|');
            if (literal)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                int length = i - start;
                if (best is null || length > best.Length)
                    best = filter.Substring(start, length);
                start = -1;
            }
        }
        return best is { Length: >= 4 } ? best.ToLowerInvariant() : null;
    }

    private static HashSet<string>? StringSet(JsonElement condition, string name)
    {
        if (condition.ValueKind != JsonValueKind.Object ||
            !condition.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
            return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                set.Add(text);
        return set.Count > 0 ? set : null;
    }

    private static List<string>? StringList(JsonElement condition, string name)
    {
        if (condition.ValueKind != JsonValueKind.Object ||
            !condition.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                list.Add(text.TrimStart('*', '.').ToLowerInvariant());
        return list.Count > 0 ? list : null;
    }

    // ── Dynamic and session rules ────────────────────────────────────────

    /// <summary>
    /// Apply an updateDynamicRules / updateSessionRules call. Both are treated
    /// the same way: session rules should die with the browser, and they do,
    /// because nothing here is written to disk.
    /// </summary>
    public static void UpdateDynamicRules(string extensionId, JsonElement args)
    {
        try
        {
            lock (s_lock)
            {
                if (!s_dynamicRaw.TryGetValue(extensionId, out List<JsonElement>? raw))
                    raw = s_dynamicRaw[extensionId] = [];

                if (args.TryGetProperty("removeRuleIds", out JsonElement removals) &&
                    removals.ValueKind == JsonValueKind.Array)
                {
                    var doomed = removals.EnumerateArray()
                        .Where(item => item.TryGetInt32(out _))
                        .Select(item => item.GetInt32())
                        .ToHashSet();
                    raw.RemoveAll(rule => rule.TryGetProperty("id", out JsonElement id) &&
                        id.TryGetInt32(out int value) && doomed.Contains(value));
                }

                if (args.TryGetProperty("addRules", out JsonElement additions) &&
                    additions.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement rule in additions.EnumerateArray())
                        raw.Add(rule.Clone());
                }

                var compiled = new List<CompiledRule>();
                foreach (JsonElement rule in raw)
                {
                    CompiledRule? entry = CompileRule(extensionId, rule);
                    if (entry is not null) compiled.Add(entry);
                }
                s_dynamic[extensionId] = compiled;
                RebuildMatchSetLocked();
            }
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("dnr-error", extensionId, $"updateDynamicRules: {ex.Message}");
        }
    }

    /// <summary>The dynamic rules as the extension gave them, for getDynamicRules.</summary>
    public static List<JsonElement> GetDynamicRules(string extensionId)
    {
        lock (s_lock)
            return s_dynamicRaw.TryGetValue(extensionId, out List<JsonElement>? raw)
                ? new List<JsonElement>(raw) : [];
    }

    private static void RebuildMatchSetLocked()
    {
        var all = new List<CompiledRule>();
        foreach (var rules in s_static.Values) all.AddRange(rules);
        foreach (var rules in s_dynamic.Values) all.AddRange(rules);
        s_matchSet = [.. all];
    }

    // ── Matching ─────────────────────────────────────────────────────────

    internal enum Decision { Allow, Block, Redirect }

    internal readonly record struct MatchResult(Decision Decision, string? RedirectUrl, string? ExtensionId);

    /// <summary>
    /// Decide what to do with one request. Called on Chromium's IO thread for
    /// every subresource, so it takes no locks beyond reading the match set
    /// reference and never allocates on the common no-match path.
    /// </summary>
    public static MatchResult Match(string url, string? initiatorUrl, string resourceType, string method)
    {
        CompiledRule[] rules = s_matchSet;
        if (rules.Length == 0) return new MatchResult(Decision.Allow, null, null);

        try
        {
            string lowerUrl = url.ToLowerInvariant();
            string? initiatorHost = HostOf(initiatorUrl);
            string? requestHost = HostOf(url);
            bool thirdParty = initiatorHost is not null && requestHost is not null &&
                !IsSameSite(initiatorHost, requestHost);

            CompiledRule? bestBlock = null;
            CompiledRule? bestAllow = null;
            CompiledRule? bestRedirect = null;

            foreach (CompiledRule rule in rules)
            {
                // The cheap test first: a URL without the rule's literal cannot
                // match its pattern, and this skips the great majority of them
                // without touching the regex engine.
                if (rule.LiteralHint is not null &&
                    !lowerUrl.Contains(rule.LiteralHint, StringComparison.Ordinal))
                    continue;

                if (!Matches(rule, url, initiatorHost, requestHost, resourceType, method, thirdParty))
                    continue;

                switch (rule.ActionType)
                {
                    case "allow" or "allowAllRequests":
                        if (bestAllow is null || rule.Priority > bestAllow.Priority) bestAllow = rule;
                        break;
                    case "block":
                        if (bestBlock is null || rule.Priority > bestBlock.Priority) bestBlock = rule;
                        break;
                    case "redirect":
                        if (bestRedirect is null || rule.Priority > bestRedirect.Priority) bestRedirect = rule;
                        break;
                }
            }

            // An allow of equal or higher priority beats a block, which is what
            // lets a list ship broad blocks with narrow exceptions.
            if (bestBlock is not null &&
                (bestAllow is null || bestAllow.Priority < bestBlock.Priority))
            {
                int blocked;
                lock (s_lock)
                {
                    s_blockedCounts.TryGetValue(bestBlock.ExtensionId, out int count);
                    blocked = count + 1;
                    s_blockedCounts[bestBlock.ExtensionId] = blocked;
                }
                // A trace at the first block and then at widening intervals: it
                // is the only way to see from outside that rules are biting,
                // without writing a line per request on the IO thread.
                if (blocked is 1 or 10 or 100 or 1000 or 10000)
                {
                    ExtensionDiagnostics.Write("dnr-block", bestBlock.ExtensionId,
                        $"Blocked {blocked} request(s); latest rule {bestBlock.Id} on {Truncate(url)}");
                }
                return new MatchResult(Decision.Block, null, bestBlock.ExtensionId);
            }

            if (bestRedirect is not null &&
                (bestAllow is null || bestAllow.Priority < bestRedirect.Priority))
                return new MatchResult(Decision.Redirect, bestRedirect.RedirectUrl, bestRedirect.ExtensionId);

            return new MatchResult(Decision.Allow, null, null);
        }
        catch (Exception)
        {
            // Never let a bad rule take the page down with it.
            return new MatchResult(Decision.Allow, null, null);
        }
    }

    private static bool Matches(
        CompiledRule rule, string url, string? initiatorHost, string? requestHost,
        string resourceType, string method, bool thirdParty)
    {
        if (rule.UrlPattern is not null && !rule.UrlPattern.IsMatch(url)) return false;

        if (rule.ResourceTypes is not null && !rule.ResourceTypes.Contains(resourceType)) return false;
        if (rule.ExcludedResourceTypes is not null && rule.ExcludedResourceTypes.Contains(resourceType)) return false;

        if (rule.RequestMethods is not null && !rule.RequestMethods.Contains(method)) return false;

        if (rule.DomainType is not null)
        {
            bool wantsThirdParty = rule.DomainType.Equals("thirdParty", StringComparison.OrdinalIgnoreCase);
            if (wantsThirdParty != thirdParty) return false;
        }

        if (rule.InitiatorDomains is not null &&
            !(initiatorHost is not null && rule.InitiatorDomains.Any(domain => HostMatches(initiatorHost, domain))))
            return false;
        if (rule.ExcludedInitiatorDomains is not null && initiatorHost is not null &&
            rule.ExcludedInitiatorDomains.Any(domain => HostMatches(initiatorHost, domain)))
            return false;

        if (rule.RequestDomains is not null &&
            !(requestHost is not null && rule.RequestDomains.Any(domain => HostMatches(requestHost, domain))))
            return false;
        if (rule.ExcludedRequestDomains is not null && requestHost is not null &&
            rule.ExcludedRequestDomains.Any(domain => HostMatches(requestHost, domain)))
            return false;

        return true;
    }

    /// <summary>A domain condition covers the host itself and everything under it.</summary>
    private static bool HostMatches(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string url)
        => url.Length <= 120 ? url : url[..120] + "…";

    private static string? HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Same-site by registrable-ish domain: the last two labels. Not the public
    /// suffix list, so co.uk pairs are judged more loosely than Chromium would.
    /// </summary>
    private static bool IsSameSite(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        return LastTwoLabels(a).Equals(LastTwoLabels(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string LastTwoLabels(string host)
    {
        string[] parts = host.Split('.');
        return parts.Length <= 2 ? host : parts[^2] + "." + parts[^1];
    }

    /// <summary>Map CEF's resource type onto the strings DNR rules are written against.</summary>
    public static string ResourceTypeName(CefResourceType type) => type switch
    {
        CefResourceType.MainFrame => "main_frame",
        CefResourceType.SubFrame => "sub_frame",
        CefResourceType.Stylesheet => "stylesheet",
        CefResourceType.Script => "script",
        CefResourceType.Image => "image",
        CefResourceType.FontResource => "font",
        CefResourceType.Object => "object",
        CefResourceType.Media => "media",
        CefResourceType.Xhr => "xmlhttprequest",
        CefResourceType.Ping => "ping",
        CefResourceType.CspReport => "csp_report",
        CefResourceType.Favicon => "image",
        CefResourceType.SubResource => "other",
        CefResourceType.Prefetch => "other",
        _ => "other",
    };
}
