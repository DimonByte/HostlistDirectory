using System.Text.RegularExpressions;

namespace HostlistDirectory;

/// <summary>
/// Reads "Key: value" metadata out of a list's comment header, whatever the comment
/// syntax (#, ##, !, ;, //) and whatever the author calls the key
/// ("Description", "Desc", "Contents", "Project home page", "Last updated", ...).
/// </summary>
public sealed partial class HeaderParser
{
    public const string Title = "title";
    public const string Description = "description";
    public const string Homepage = "homepage";
    public const string License = "license";
    public const string Expiry = "expiry";
    public const string LastModified = "lastModified";
    public const string Version = "version";
    public const string DeclaredEntries = "declaredEntries";
    public const string Syntax = "syntax";

    public static readonly string[] AllFields = [Title, Description, Homepage, License, Expiry, LastModified, Version, DeclaredEntries, Syntax];

    /// <summary>Normalised key → (field, rank). Lower rank wins when a header has several aliases for the same field.</summary>
    private static readonly Dictionary<string, (string Field, int Rank)> Aliases = BuildAliases();

    private static Dictionary<string, (string, int)> BuildAliases()
    {
        var map = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        void Add(string field, params string[] keys)
        {
            for (var i = 0; i < keys.Length; i++) map[keys[i]] = (field, i);
        }

        Add(Title, "title", "name", "list name", "list title", "filter name");
        Add(Description, "description", "desc", "contents", "about", "summary", "purpose", "info");
        Add(Homepage, "homepage", "home page", "project home page", "project homepage", "website", "web site",
            "project page", "project", "home", "repository", "repo", "source", "github", "url");
        Add(License, "license", "licence", "lic", "license type", "licensed under", "licensing", "copyright license");
        Add(Expiry, "expires", "expiry", "expiration", "expire", "update frequency", "update interval", "updated every");
        Add(LastModified, "last modified", "last updated", "last update", "updated", "modified", "date",
            "generated", "generated on", "updated on", "last change", "last changed", "timestamp", "build date", "time updated");
        Add(Version, "version", "ver", "revision", "build", "release");
        Add(DeclaredEntries, "number of entries", "entries", "number of unique domains", "number of domains",
            "total number of entries", "total entries", "entry count", "domain count", "total domains", "domains",
            "total number of network filters", "count", "rules count", "number of rules", "total");
        Add(Syntax, "syntax", "format", "type");
        return map;
    }

    private readonly Dictionary<string, (string Value, int Rank)> _found = [];
    private string? _pendingField;
    private int _pendingRank;

    public bool IsComplete => AllFields.All(_found.ContainsKey);

    /// <summary>Feed one comment line (prefix still attached). Returns false if it wasn't a comment.</summary>
    public bool AcceptCommentLine(string rawLine)
    {
        var text = StripCommentPrefix(rawLine);
        if (text is null) return false;

        text = text.Trim();
        // Decorative rulers like "=====" or "#####"
        if (text.Length == 0 || DecorationRegex().IsMatch(text))
        {
            _pendingField = null;
            return true;
        }

        var m = KeyValueRegex().Match(text);
        if (m.Success && !m.Groups["value"].Value.StartsWith("//", StringComparison.Ordinal))
        {
            var key = NormaliseKey(m.Groups["key"].Value);
            var value = CleanValue(m.Groups["value"].Value);
            if (Aliases.TryGetValue(key, out var alias))
            {
                if (value.Length == 0)
                {
                    // AdAway style: "# License:" then the value on the next comment line.
                    _pendingField = alias.Field;
                    _pendingRank = alias.Rank;
                }
                else
                {
                    Set(alias.Field, value, alias.Rank);
                    _pendingField = null;
                }
                return true;
            }
        }

        if (_pendingField is not null)
        {
            Set(_pendingField, CleanValue(text), _pendingRank);
            _pendingField = null;
        }
        return true;
    }

    public void BreakContinuation() => _pendingField = null;

    private void Set(string field, string value, int rank)
    {
        if (value.Length == 0 || IsPlaceholder(value)) return;
        if (field == Homepage && !LooksLikeUrl(value)) return;
        if (field is Version or Title or Expiry or LastModified && LooksLikeUrl(value)) return;
        if (field == License && value.Length > 120) return; // a paragraph of legalese, not a licence name
        if (field == DeclaredEntries && ParseCount(value) is null) return;
        if (_found.TryGetValue(field, out var existing) && existing.Rank <= rank) return;
        _found[field] = (value, rank);
    }

    public Dictionary<string, string> Result() => _found.ToDictionary(kv => kv.Key, kv => kv.Value.Value);

    /// <summary>Returns the comment body without its prefix, or null if the line is not a comment.</summary>
    public static string? StripCommentPrefix(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0) return null;
        if (t.StartsWith("//", StringComparison.Ordinal)) return t.TrimStart('/');
        return t[0] switch
        {
            '#' or '!' or ';' => t.TrimStart(t[0]),
            _ => null,
        };
    }

    public static bool IsPlaceholder(string value) =>
        value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
        value == "-" ||
        (value.StartsWith('%') && value.EndsWith('%')) || value.Contains("{{", StringComparison.Ordinal);

    public static bool LooksLikeUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static long? ParseCount(string value)
    {
        var m = CountRegex().Match(value);
        if (!m.Success) return null;
        var digits = new string([.. m.Value.Where(char.IsDigit)]);
        return long.TryParse(digits, out var n) ? n : null;
    }

    private static string NormaliseKey(string key) =>
        WhitespaceRegex().Replace(key.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), " ");

    private static string CleanValue(string value)
    {
        var v = value.Trim().Trim('"', '\'', '<', '>').Trim();
        return WhitespaceRegex().Replace(v, " ");
    }

    // Key is 1-5 words, starts with a letter, no URL scheme.
    [GeneratedRegex(@"^(?<key>[A-Za-z][A-Za-z0-9 _\-\.()/]{0,38}?)\s*[:=]\s*(?<value>.*)$")]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"^[\s=\-_*#~+]+$")]
    private static partial Regex DecorationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\d[\d,\. ']*")]
    private static partial Regex CountRegex();
}
