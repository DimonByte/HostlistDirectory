# Technical information

## The builder

`src/HostlistDirectory` is a .NET 10 console app with no NuGet dependencies. It replaces the old `HostDirectoryBuilder.ps1` / `AddHostEntry.ps1` scripts.

```text
dotnet run --project src/HostlistDirectory -c Release -- <command>

  build    [--output Directory.json] [--parallel 6] [--only <key-filter>] [--no-github] [--verbose]
  validate [--online]
  add <url> [--format <folder>] [--author <name>] [--file <name>] [--tags a,b] [--dry-run]
  inspect <url>          show what the parser reads from a list (handy when a header looks odd)
```

### What `build` does for each database file

1. **Download, with fallback.** Tries `mainMirror`, then each `alternativeMirrors` entry, retrying transient errors with backoff. A 404 or an HTML error page counts as a failure. Lists are streamed line by line, so 50 MB lists don't sit in memory.
2. **Read the header, whatever its syntax.** Comment prefixes `#`, `##`, `!`, `;` and `//` are all understood. Keys are matched against aliases, so `Description` / `Desc` / `Contents` / `Info`, `Homepage` / `Project home page` / `Source`, and `Last modified` / `Updated` / `Date` / `Generated` all land in the same field. Values on the line after an empty key (AdAway's `# License:` followed by the licence on the next line) are picked up too. Only the header is parsed (comments before the first rule), so section titles further down a merged list don't overwrite it.
3. **Sniff the format and count properly.** The content is classified as hosts, compressed hosts (several domains per line), plain domains, `*.` wildcards, adblock, DNSMasq, RPZ or Unbound. Counting follows the format: localhost/broadcast boilerplate is skipped, compressed lines are expanded, and adblock `@@` allow rules aren't counted as blocks. If the folder says `hosts` but the content is plain domains, a warning is raised.
4. **Normalise.** Dates in about 30 styles (`31 Oct 2026 15:45 UTC`, `Thu, 15 Oct 2026 at 17:57:51 GMT`, `24 Oct 2026 22:48 UTC+7`, `10/31/2026 13:14`, ISO, Unix…) become ISO 8601 UTC. `Expires: 4 days (update frequency)` becomes `expiresHours: 96`. Tags are lower-cased, de-duplicated and split if someone wrote `"ads, trackers"` as one tag.
5. **GitHub lookup.** For lists hosted on GitHub (directly, raw, jsDelivr `/gh/` or `*.github.io`), the repository's licence, description, stars, archived flag and last push are added. Archived repositories raise a warning. In Actions this uses `GITHUB_TOKEN`.
6. **Never lose a list.** If every mirror fails, the entry stays in `Directory.json` with `status: "unreachable"` and the last good data from the previous build.

Values are picked in this order: `overrides` in the database file, then the list's own header, then the plain database fields, then GitHub. Licence is the exception: the database `license` beats the header, because headers often just link to a LICENSE file.

The run ends with a summary in the Actions job page listing unreachable lists, mirror fallbacks, format mismatches and custom patterns that stopped matching (a sign the list's header changed). `Directory.json` is only rewritten when something besides timestamps changed.

## Database file format (schema v2)

`database/<format>/<author>/<list>.json`. Subfolders under the author folder are fine (`hosts/Hagezi/Multi/multi-pro.json`).

```jsonc
{
    "name": "HaGeZi's Multi PRO (recommended)",    // required
    "author": "HaGeZi",                             // optional, defaults to the author folder name
    "description": "Fallback if the header has none",
    "homepage": "https://github.com/hagezi/dns-blocklists",
    "license": "GPL-3.0",
    "tags": ["ads", "trackers", "malware"],
    "mainMirror": "https://cdn.jsdelivr.net/gh/hagezi/dns-blocklists@latest/adblock/pro.txt",   // required
    "alternativeMirrors": ["https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/pro.txt"],
    "notes": "Aggressive: may block some legitimate sites.",
    "enabled": true,

    // Rarely needed:
    "overrides": { "title": "Force this title regardless of the header" },
    "patterns":  { "version": ["^###\\s*Version:\\s*(.*)$"] }   // custom regex, group 1 = value
}
```

Fields usable in `overrides` / `patterns`: `title`, `description`, `homepage`, `license`, `expiry`, `lastModified`, `version`, `declaredEntries`, `syntax`.

The v1 `detectionMethods` block is still read (user-specified becomes an override, text-locate/regex becomes a pattern), so older files and PRs keep working. New files don't need it, because the generic header parser handles almost everything.

`database.schema.json` describes the format. VS Code applies it automatically through `.vscode/settings.json`.

## Directory.json fields

See the example in [README.md](README.md). Notes:

- `key` is stable (derived from the database path). `id` is just the position in the current build and changes when lists are added.
- `entries` is what the builder counted. `declaredEntries` is what the header claims. They differ for compressed hosts files, where Hagezi counts lines rather than domains.
- `status`: `ok`, `mirror` (an alternative mirror served it) or `unreachable` (data is from the last successful build, and `lastSuccess` says when that was).
- `lastModified` is ISO 8601 when it could be parsed. Otherwise it's the raw header text. If the header has no date, the HTTP `Last-Modified` header is used.
