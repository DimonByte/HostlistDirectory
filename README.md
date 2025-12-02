# HostListDirectory

A directory of host lists for DNS filtering, ad blocking, and network security.

## About

This directory contains metadata and information about various host lists used for DNS filtering, ad blocking, and network security.

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

## Usage

The root directory contains a `directory.json` file with metadata of all the detected host lists in the database folder. The structure is designed for easy programmatic access and integration with DNS filtering tools.

## Note

This directory only provides metadata and information about host lists. It does not contain the actual host files themselves, which should be downloaded from their respective sources.

This is also in development. The hope is that when a good number of host file configs get added to the database folder, the GitHub Actions can automatically run the HostDirectoryBuilder script which will provide an up-to-date Directory.json for use in any project by anyone.