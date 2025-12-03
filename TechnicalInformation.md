## Structure

```
Directory.json                 # Contains all host lists and all format types
database/                      # Contains all the database files required for the HostDirectoryBuilder
├── domains-subdomains/        # List of domains and subdomains folder
├── hosts/                     # Host file folder
│   └── authorname/            # Author name of the host file
│       └── hostfilename.json  # JSON file that contains the information and metadata of the host file.
└── hosts-compressed/          # Compressed Host file
```

## Scripts

The HostListDirectory is built using two main PowerShell scripts:

1. **HostDirectoryBuilder.ps1**: This script scans the `database` directory and builds a `Directory.json` file containing metadata about each host list found.
2. **HostEntryBuilder.ps1**: This script generates a JSON configuration file for a new host list entry, extracting metadata from the host list content or prompting the user for input. This will then be moved to the database folder like this (database > authornameofhostlist > hostlistname.json)

### HostDirectoryBuilder.ps1

This script performs the following steps:

- Sets up an array of subdirectories representing different host list formats.
- Defines helper functions:
  - `Get-HostFileMetadata`: Extracts metadata from host list content using detection methods defined in the JSON configuration.
  - `Get-FormatType`: Determines the format type of a host list based on its location within the database directory.
  - `Get-HostFileContent`: Downloads the content of a host list from a specified URL.
  - `Get-FileSize`: Returns the size of a file in human-readable format.
  - `Get-EntriesCount`: Counts the number of entries in a host list, excluding comments and empty lines.

- Iterates through each subdirectory and author directory within the database.
- For each JSON configuration file found, it:
  - Reads the configuration.
  - Downloads the host list content.
  - Extracts metadata using `Get-HostFileMetadata`.
  - Creates an entry object with all relevant information.
  - Adds the entry to the directory.

- Finally, it saves the complete directory to `Directory.json`.

### HostEntryBuilder.ps1

This script is used to generate a new JSON configuration file for a host list entry. It:

- Prompts the user for the URL of the list.
- Downloads the content of the list.
- Defines patterns for extracting metadata such as title, description, homepage, license, expiry, last modified, and version.
- Extracts metadata from the content using `Get-Metadata`.
- If metadata is not found, it prompts the user for input.
- Outputs the JSON configuration to `entry.json`.

## Usage

The root directory contains a `Directory.json` file with metadata of all the detected host lists in the database folder. The structure is designed for easy programmatic access and integration with DNS filtering tools.

This directory only provides metadata and information about host lists. It does not contain the actual host files themselves, which should be downloaded from their respective sources.

This is also in development. The hope is that when a good number of host file configs get added to the database folder, the GitHub Actions can automatically run the HostDirectoryBuilder script which will provide an up-to-date Directory.json for use in any project by anyone.