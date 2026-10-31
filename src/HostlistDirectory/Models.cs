using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostlistDirectory;

/// <summary>
/// One file in the database folder (database/&lt;format&gt;/&lt;author&gt;/&lt;list&gt;.json).
/// Everything except name + mainMirror is optional. Values found in the list's own header
/// win over the plain fields here; values in <see cref="Overrides"/> win over everything.
/// </summary>
public sealed class DatabaseEntry
{
    public string? Name { get; set; }
    /// <summary>Display author. Defaults to the author folder name.</summary>
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public string? License { get; set; }
    public List<string>? Tags { get; set; }
    public string? MainMirror { get; set; }
    public List<string>? AlternativeMirrors { get; set; }
    /// <summary>Set to false to keep the file but leave it out of Directory.json.</summary>
    public bool? Enabled { get; set; }
    /// <summary>Optional note shown in Directory.json, e.g. "Use at your own risk, very large".</summary>
    public string? Notes { get; set; }

    /// <summary>Forced values that ignore whatever the list header says (title, description, homepage, license, expiry, version, lastModified).</summary>
    public Dictionary<string, string>? Overrides { get; set; }

    /// <summary>Custom regexes per field, tried before the generic header parser. Capture group 1 is the value.</summary>
    public Dictionary<string, List<string>>? Patterns { get; set; }

    /// <summary>Legacy v1 schema, still understood so old files / PRs keep working.</summary>
    public Dictionary<string, LegacyDetectionMethod>? DetectionMethods { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LegacyDetectionMethod
{
    public string? Type { get; set; }
    public List<string>? Patterns { get; set; }
    public JsonElement? Value { get; set; }
    public int? Priority { get; set; }
}

public sealed class DirectoryFile
{
    [JsonPropertyName("hostlistdirectoryinfo")]
    public DirectoryInfoBlock Info { get; set; } = new();
    public List<DirectoryEntry> Entries { get; set; } = [];
}

public sealed class DirectoryInfoBlock
{
    public string Name { get; set; } = "HostDirectory";
    public string Homepage { get; set; } = "https://github.com/DimonByte/HostlistDirectory";
    public string License { get; set; } = "Unlicense license";
    public int SchemaVersion { get; set; } = 2;
    /// <summary>ISO 8601 UTC.</summary>
    public string? LastUpdated { get; set; }
    public int TotalLists { get; set; }
    public int ReachableLists { get; set; }
    public SortedDictionary<string, int> Formats { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DirectoryEntry
{
    public int Id { get; set; }
    /// <summary>Stable identifier derived from the database path, e.g. "hosts/stevenblack/unified-hosts". Use this instead of id.</summary>
    public string Key { get; set; } = "";
    public string? Author { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? MainMirror { get; set; }
    public List<string> AlternativeMirrors { get; set; } = [];
    /// <summary>Number of blocking rules / domains counted in the file (compressed hosts lines are expanded).</summary>
    public long? Entries { get; set; }
    /// <summary>Entry count the list claims in its own header, if any.</summary>
    public long? DeclaredEntries { get; set; }
    /// <summary>Folder the database file lives in (hosts, domains-subdomains, adblock, ...).</summary>
    public string? FormatType { get; set; }
    /// <summary>Format sniffed from the content.</summary>
    public string? DetectedFormat { get; set; }
    public string? License { get; set; }
    /// <summary>Raw expiry string from the header, e.g. "8 hours".</summary>
    public string? Expiry { get; set; }
    public double? ExpiresHours { get; set; }
    /// <summary>ISO 8601 UTC when parseable, otherwise the raw header text.</summary>
    public string? LastModified { get; set; }
    public string? Version { get; set; }
    public string? Notes { get; set; }
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>ok | mirror | unreachable</summary>
    public string Status { get; set; } = "ok";
    /// <summary>URL the content was actually fetched from on the last successful check.</summary>
    public string? ServedFrom { get; set; }
    public string? LastChecked { get; set; }
    public string? LastSuccess { get; set; }
    public RepositoryInfo? Repository { get; set; }
}

public sealed class RepositoryInfo
{
    public string? Url { get; set; }
    public string? Description { get; set; }
    public string? License { get; set; }
    public int? Stars { get; set; }
    public bool? Archived { get; set; }
    public string? PushedAt { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IndentSize = 2,
    };

    public static readonly JsonSerializerOptions WriteDatabase = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IndentSize = 4,
    };
}
