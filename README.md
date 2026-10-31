# HostListDirectory

A machine-readable directory of well-known DNS filtering, ad blocking and security lists: hosts files, plain domain lists, AdBlock-syntax DNS lists, wildcard lists, DNSMasq and RPZ.

`Directory.json` is rebuilt automatically every 6 hours by GitHub Actions. Every list is downloaded so its metadata (title, description, licence, entry count, last-modified, version, size, SHA-256) comes from the list itself rather than going stale.

> This repository only holds metadata. Download the lists from their own sources (`mainMirror` / `alternativeMirrors`).

## Using Directory.json

```text
https://raw.githubusercontent.com/DimonByte/HostlistDirectory/main/Directory.json
```

```jsonc
{
  "hostlistdirectoryinfo": { "schemaVersion": 2, "lastUpdated": "2026-10-31T18:00:00Z", "totalLists": 197, "reachableLists": 197, "formats": { "hosts": 49, ... } },
  "entries": [
    {
      "id": 144,                                   // position in this build; use "key" to reference a list
      "key": "hosts-compressed/hagezi/multi/multi-pro",   // stable
      "author": "Hagezi",
      "title": "HaGeZi's Multi PRO - extended protection (recommended)",   // from the list header
      "name": "HaGeZi's Multi PRO (recommended)",                          // from the database file
      "description": "...", "homepage": "...", "tags": ["ads", "trackers", ...],
      "mainMirror": "https://cdn.jsdelivr.net/gh/...", "alternativeMirrors": ["https://raw.githubusercontent.com/..."],
      "entries": 479378,             // counted: domains/rules (compressed hosts lines are expanded)
      "declaredEntries": 53265,      // what the header claims, if anything
      "formatType": "hosts-compressed", "detectedFormat": "hosts-compressed",
      "license": "GPL-3.0", "expiry": "8 hours", "expiresHours": 8,
      "lastModified": "2026-10-31T15:47:00Z", "version": "2026.1031.1547.27",
      "notes": null, "sizeBytes": 12361890, "sha256": "…",
      "status": "ok",                // ok | mirror (main failed, an alternative worked) | unreachable (last known data kept)
      "servedFrom": "...", "lastChecked": "...", "lastSuccess": "...",
      "repository": { "url": "...", "license": "GPL-3.0", "stars": 12345, "archived": false, "pushedAt": "..." }
    }
  ]
}
```

Format types: `hosts`, `hosts-compressed`, `domains-subdomains`, `wildcard-asterisk` (`*.example.com`), `wildcard-domains` (`example.com` meaning it and all subdomains), `adblock`, `DNSMasq`, `rpz`.

## Adding a list

```bash
dotnet run --project src/HostlistDirectory -- add https://example.com/list.txt --tags ads,trackers
```

This downloads the list, sniffs its format, reads its header and writes `database/<format>/<author>/<name>.json`. Check the file, add `alternativeMirrors`, then run:

```bash
dotnet run --project src/HostlistDirectory -- validate --online
```

Pull requests that touch `database/` are validated automatically. See [TechnicalInformation.md](TechnicalInformation.md) for the database file format.

## Structure

```
Directory.json              # generated output, do not edit by hand
database.schema.json        # JSON schema for database files (VS Code picks it up automatically)
database/<format>/<author>/<list>.json
src/HostlistDirectory/      # the C# builder (.NET 10, no dependencies)
```

## Licence

The directory and builder are released under the Unlicense. Each list has its own licence, shown in its entry.
