using System.Text.RegularExpressions;

namespace HostlistDirectory;

public sealed class AnalysisResult
{
    public Dictionary<string, string> Metadata { get; init; } = [];
    public long Entries { get; init; }
    public long AllowRules { get; init; }
    public string DetectedFormat { get; init; } = "unknown";
    public long Lines { get; init; }
}

public sealed class InvalidListContentException(string message) : Exception(message);

/// <summary>
/// Single pass over a list, line by line (never holds the whole file in memory):
/// parses the header, sniffs the format and counts entries properly for that format.
/// </summary>
public sealed partial class ListAnalyzer
{
    private const int MaxHeaderLines = 400;
    private const int MaxPatternLines = 1000;

    private readonly HeaderParser _header = new();
    private readonly Dictionary<string, List<Regex>> _customPatterns;
    private readonly Dictionary<string, string> _customHits = [];
    private readonly Dictionary<string, long> _votes = [];

    private bool _inHeader = true;
    private bool _sawContent;
    private long _lineNo;
    private long _entries;
    private long _allow;
    private long _compressedLines;

    public ListAnalyzer(Dictionary<string, List<string>>? customPatterns)
    {
        _customPatterns = [];
        if (customPatterns is null) return;
        foreach (var (field, patterns) in customPatterns)
        {
            var list = new List<Regex>();
            foreach (var p in patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                try { list.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))); }
                catch (ArgumentException) { /* bad regex in database file; reported by validate */ }
            }
            if (list.Count > 0) _customPatterns[field] = list;
        }
    }

    public void AcceptLine(string rawLine)
    {
        _lineNo++;
        var line = rawLine.TrimEnd('\r', '\n');

        if (_lineNo <= MaxPatternLines && _customPatterns.Count > 0) ApplyCustomPatterns(line);

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            _header.BreakContinuation();
            return;
        }

        if (!_sawContent)
        {
            _sawContent = true;
            if (trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidListContentException("Server returned an HTML/XML page instead of a list");
        }

        // Comments: "#" is a comment everywhere except adblock element-hiding rules ("##", "#@#").
        if (IsComment(trimmed))
        {
            if (_inHeader && _lineNo <= MaxHeaderLines) _header.AcceptCommentLine(trimmed);
            return;
        }

        ClassifyAndCount(trimmed);
    }

    private static bool IsComment(string t)
    {
        if (t[0] == '!' || t[0] == ';') return true;
        if (t.StartsWith("//", StringComparison.Ordinal)) return true;
        if (t[0] != '#') return false;
        // Adblock generic cosmetic rules ("##.ad-banner", "##[id^=ad]", "#@#...", "#?#...", "#$#...") are rules, not comments.
        if (t.StartsWith("#@#", StringComparison.Ordinal) || t.StartsWith("#?#", StringComparison.Ordinal) || t.StartsWith("#$#", StringComparison.Ordinal))
            return false;
        if (t.Length > 2 && t[1] == '#' && (t[2] == '.' || t[2] == '['))
            return false;
        return true;
    }

    private void ClassifyAndCount(string t)
    {
        // Adblock "[Adblock Plus 2.0]" banner and RPZ zone preamble are part of the header.
        if (t[0] == '[' && t.EndsWith(']'))
        {
            Vote("adblock");
            return;
        }
        if (t.StartsWith("$TTL", StringComparison.Ordinal) || t.StartsWith("@ ", StringComparison.Ordinal) ||
            t.StartsWith("@\t", StringComparison.Ordinal) || RpzPreambleRegex().IsMatch(t))
        {
            Vote("rpz");
            return;
        }

        // Strip trailing inline comments ("0.0.0.0 foo.com # tracker")
        var hash = t.IndexOf(" #", StringComparison.Ordinal);
        if (hash < 0) hash = t.IndexOf("\t#", StringComparison.Ordinal);
        var rule = hash > 0 ? t[..hash].TrimEnd() : t;

        string format;
        long count = 1;
        var boilerplate = false;

        if (HostsRegex().Match(rule) is { Success: true } hm)
        {
            var hosts = hm.Groups["hosts"].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var real = hosts.Count(h => !IsHostsBoilerplate(h));
            boilerplate = real == 0;
            count = real;
            if (hosts.Length > 1) _compressedLines++;
            format = "hosts";
        }
        else if (rule.StartsWith("@@", StringComparison.Ordinal))
        {
            _allow++;
            Vote("adblock");
            _inHeader = false;
            return;
        }
        else if (rule.StartsWith("||", StringComparison.Ordinal) || rule.StartsWith('|') || rule.Contains("##") ||
                 rule.Contains("#@#") || rule.EndsWith('^') || rule.Contains("^$") || rule.Contains("$important") ||
                 (rule.StartsWith('/') && rule.EndsWith('/') && rule.Length > 2))
            format = "adblock";
        else if (DnsmasqRegex().IsMatch(rule))
            format = "dnsmasq";
        else if (rule.StartsWith("local-zone:", StringComparison.OrdinalIgnoreCase) || rule.StartsWith("local-data:", StringComparison.OrdinalIgnoreCase))
            format = "unbound";
        else if (RpzRuleRegex().IsMatch(rule))
            format = "rpz";
        else if (rule.StartsWith("*.", StringComparison.Ordinal) && DomainRegex().IsMatch(rule[2..]))
            format = "wildcard-asterisk";
        else if (DomainRegex().IsMatch(rule))
            format = "domains";
        else if (rule.Contains("FindProxyForURL", StringComparison.Ordinal))
        {
            Vote("pac");
            return;
        }
        else
            format = "unknown";

        if (boilerplate) return; // "127.0.0.1 localhost" etc. don't end the header and aren't entries

        _inHeader = false;
        Vote(format);
        _entries += count;
    }

    private void Vote(string format) => _votes[format] = _votes.GetValueOrDefault(format) + 1;

    private static readonly HashSet<string> HostsBoilerplate = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "localhost.localdomain", "local", "broadcasthost", "ip6-localhost", "ip6-loopback",
        "ip6-localnet", "ip6-mcastprefix", "ip6-allnodes", "ip6-allrouters", "ip6-allhosts", "0.0.0.0", "127.0.0.1",
    };

    private static bool IsHostsBoilerplate(string host) => HostsBoilerplate.Contains(host);

    private void ApplyCustomPatterns(string line)
    {
        foreach (var (field, regexes) in _customPatterns)
        {
            if (_customHits.ContainsKey(field)) continue;
            foreach (var rx in regexes)
            {
                try
                {
                    var m = rx.Match(line);
                    if (!m.Success) continue;
                    var value = (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
                    if (value.Length == 0 || HeaderParser.IsPlaceholder(value)) continue;
                    _customHits[field] = value;
                    break;
                }
                catch (RegexMatchTimeoutException) { }
            }
        }
    }

    public AnalysisResult Complete()
    {
        var meta = _header.Result();
        foreach (var (k, v) in _customHits) meta[k] = v; // custom patterns beat the generic parser

        var detected = _votes.Where(v => v.Key != "unknown").OrderByDescending(v => v.Value).Select(v => v.Key).FirstOrDefault()
                       ?? (_votes.ContainsKey("unknown") ? "unknown" : "empty");
        if (detected == "hosts" && _compressedLines > 0) detected = "hosts-compressed";

        return new AnalysisResult
        {
            Metadata = meta,
            Entries = _entries,
            AllowRules = _allow,
            DetectedFormat = detected,
            Lines = _lineNo,
        };
    }

    /// <summary>Maps a database folder name to the format we expect to sniff from the content.</summary>
    public static string ExpectedFormatForFolder(string folder) => folder.ToLowerInvariant() switch
    {
        "domains-subdomains" or "domains" or "wildcard-domains" => "domains",
        "wildcard-asterisk" => "wildcard-asterisk",
        "hosts" => "hosts",
        "hosts-compressed" => "hosts-compressed",
        "adblock" => "adblock",
        "dnsmasq" => "dnsmasq",
        "rpz" => "rpz",
        "unbound" => "unbound",
        "pac" => "pac",
        _ => folder.ToLowerInvariant(),
    };

    [GeneratedRegex(@"^(?:0\.0\.0\.0|127\.0\.0\.1|0|::1?|::|fe80::1%lo0|ff0[0-2]::\d|255\.255\.255\.255|(?:\d{1,3}\.){3}\d{1,3})\s+(?<hosts>\S.*)$")]
    private static partial Regex HostsRegex();

    [GeneratedRegex(@"^(?:address|server|local)=/", RegexOptions.IgnoreCase)]
    private static partial Regex DnsmasqRegex();

    [GeneratedRegex(@"^\S+\s+(?:\d+\s+)?(?:IN\s+)?CNAME\s+\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex RpzRuleRegex();

    [GeneratedRegex(@"^(?:\d+\s+)?(?:IN\s+)?(?:NS|SOA)\b|^\S+\s+(?:\d+\s+)?(?:IN\s+)?(?:NS|SOA)\s", RegexOptions.IgnoreCase)]
    private static partial Regex RpzPreambleRegex();

    [GeneratedRegex(@"^(?=.{1,253}\.?$)(?:[\p{L}\p{N}_](?:[\p{L}\p{N}_\-]{0,62})?\.)+[\p{L}\p{N}\-]{2,63}\.?$")]
    private static partial Regex DomainRegex();
}
