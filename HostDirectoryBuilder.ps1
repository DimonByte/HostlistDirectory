$timer = [Diagnostics.Stopwatch]::StartNew()
$rootDirectory = $PSScriptRoot 
$databaseDirectory = "$rootDirectory\database"
$outputFile = "$rootDirectory\Directory.json"
$directory = @{
    entries = @()
}
$subdirectories = @( #Contains type of blocking types, hosts is most common.
    "adblock",
    "DNSMasq", 
    "domains-subdomains",
    "hosts",
    "hosts-compressed",
    "pac",
    "rpz",
    "wildcard-asterisk",
    "wildcard-domains"
)

function Get-HostFileMetadata {
    param(
        [string]$JsonPath,
        [string]$HostContent
    )

    $metadata = @{}

    if ([string]::IsNullOrWhiteSpace($HostContent)) {
        return $metadata
    }

    $config = Get-Content -Path $JsonPath -Raw | ConvertFrom-Json

    $methods = @()
    foreach ($prop in ($config.detectionMethods | Get-Member -MemberType NoteProperty)) {
        $name = $prop.Name
        $details = $config.detectionMethods.$name
        $priority = if ($details.PSObject.Properties.Name -contains 'priority') { [int]$details.priority } else { [int]::MaxValue }
        $methods += [PSCustomObject]@{ Name = $name; Details = $details; Priority = $priority }
    }
    $methods = $methods | Sort-Object Priority, Name

    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::Multiline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase

    foreach ($m in $methods) {
        $field = $m.Name
        $cfg   = $m.Details

        switch ($cfg.type) {
            'text-locate' {
                if ($cfg.patterns) {
                    foreach ($pattern in $cfg.patterns) {
                        # Apply regex to entire content with capture group 1
                        $metedatamatches = [System.Text.RegularExpressions.Regex]::Matches($HostContent, $pattern, $regexOptions)
                        if ($metedatamatches.Count -gt 0) {
                            # Use first match, first capturing group if available
                            $value = $null
                            $group = $metedatamatches[0].Groups[1]
                            if ($group -and $group.Value) {
                                $value = $group.Value.Trim()
                            } else {
                                $value = $metedatamatches[0].Value.Trim()
                            }
                            if ($value) {
                                $metadata[$field] = $value
                                break
                            }
                        }
                    }
                }
            }
            'regex' {
                if ($cfg.patterns) {
                    foreach ($pattern in $cfg.patterns) {
                        $metedatamatches = [System.Text.RegularExpressions.Regex]::Matches($HostContent, $pattern, $regexOptions)
                        if ($metedatamatches.Count -gt 0) {
                            $value = if ($metedatamatches[0].Groups.Count -gt 1) { $metedatamatches[0].Groups[1].Value.Trim() } else { $metedatamatches[0].Value.Trim() }
                            if ($value) {
                                $metadata[$field] = $value
                                break
                            }
                        }
                    }
                }
            }
            'user-specified' {
                if ($cfg.PSObject.Properties.Name -contains 'value' -and -not [string]::IsNullOrWhiteSpace($cfg.value)) {
                    $metadata[$field] = ($cfg.value).Trim()
                }
            }
            default {
            }
        }
    }

    return $metadata
}

function Get-FormatType {
    param([string]$Path)

    $absPath = (Resolve-Path -Path $Path).Path
    $absDb   = (Resolve-Path -Path $databaseDirectory).Path

    if ($absPath.StartsWith($absDb)) {
        $relative = $absPath.Substring($absDb.Length).TrimStart('\')
        # The first segment is the format type (e.g., "domains-subdomains")
        $formatType = $relative.Split('\')[0]
        return $formatType
    }
    return "unknown"
}

function Get-HostFileContent {
    param(
        [string]$Url
    )

    try {
        $response = Invoke-WebRequest -Uri $Url -TimeoutSec 30 -UseBasicParsing
        return $response.Content
    }
    catch {
        Write-Warning "Failed to download content from $Url $($_.Exception.Message)"
        return $null
    }
}

function Get-FileSize {
    param([string]$FilePath)
    
    if (Test-Path $FilePath) {
        $fileInfo = Get-Item $FilePath
        $size = $fileInfo.Length
        
        if ($size -lt 1KB) { return "$size bytes" }
        elseif ($size -lt 1MB) { return "{0:N2} KB" -f ($size / 1KB) }
        elseif ($size -lt 1GB) { return "{0:N2} MB" -f ($size / 1MB) }
        else { return "{0:N2} GB" -f ($size / 1GB) }
    }
    return "Unknown"
}

function Get-EntriesCount {
    param([string]$Content)
    
    if ($null -eq $Content) { return 0 }
    
    $lines = $Content -split "`n"
    $count = 0
    
    foreach ($line in $lines) {
        $line = $line.Trim()
        if ($line -and $line -notmatch "^[\#\!]" -and $line -notmatch "^$")
        {
            $count++
        }
    }
    
    return $count
}

Write-Host "Starting Host Directory Build Process..." -ForegroundColor Green

foreach ($subdir in $subdirectories) {
    $fullSubdir = "$databaseDirectory\$subdir"
    if (Test-Path $fullSubdir) {
        Write-Host "Scanning: $subdir" -ForegroundColor Yellow
        $authorDirs = Get-ChildItem -Path $fullSubdir -Directory -ErrorAction SilentlyContinue
        foreach ($authorDir in $authorDirs) {
            Write-Host "  Author: $($authorDir.Name)" -ForegroundColor Cyan
            $jsonFiles = Get-ChildItem -Path $authorDir.FullName -Filter "*.json" -ErrorAction SilentlyContinue
            foreach ($jsonFile in $jsonFiles) {
                Write-Host "    Processing: $($jsonFile.Name)" -ForegroundColor Gray
                try {
                    $config = Get-Content -Path $jsonFile.FullName | ConvertFrom-Json
                    $formatType = Get-FormatType -Path $jsonFile.FullName
                    
                    $hostContent = Get-HostFileContent -Url $config.mainMirror
                    $hostMetadata = Get-HostFileMetadata -JsonPath $jsonFile.FullName -HostContent $hostContent
                    
                    $homepage = if ($hostMetadata.homepage) { $hostMetadata.homepage } else { $config.homepage }
                    $entry = [PSCustomObject]@{
                        id                 = $directory.entries.Count + 1
                        author             = $authorDir.Name
                        name               = $config.name
                        description        = $config.description
                        homepage           = $homepage
                        tags               = $config.tags
                        mainMirror         = $config.mainMirror
                        alternativeMirrors = $config.alternativeMirrors
                        lastUpdated        = Get-Date -Format "dd MMMM yyyy HH:mm:ss (UTC)"
                        entries            = Get-EntriesCount -Content $hostContent
                        formatType         = $formatType
                        license            = $hostMetadata.license
                        expiry             = $hostMetadata.expiry
                        lastModified       = $hostMetadata.lastModified
                        version            = $hostMetadata.version
                        title              = $hostMetadata.title
                    }
                    $directory.entries += $entry
                    Write-Host "      Added: $($config.name)" -ForegroundColor Green
                }
                catch {
                    Write-Warning "Failed to process $($jsonFile.Name): $($_.Exception.Message)"
                }
            }
        }
    }
}

try {
    $hostlistdirectoryinfo = @{
    lastUpdated = Get-Date -Format "dd MMMM yyyy HH:mm:ss (UTC)"
    homepage = "https://github.com/DimonByte/HostlistDirectory"
    license = "Unlicense license"
    }
    $directoryJson = @{
    entries = $directory.entries
    hostlistdirectoryinfo = $hostlistdirectoryinfo
    } | ConvertTo-Json -Depth 10
    $directoryJson | Out-File -FilePath $outputFile -Encoding UTF8
    
    Write-Host "`nDirectory built successfully!" -ForegroundColor Green
    Write-Host "Total entries: $($directory.entries.Count)" -ForegroundColor Green
    Write-Host "Output file: $outputFile" -ForegroundColor Green
}
catch {
    Write-Error "Failed to save directory to JSON: $($_.Exception.Message)"
}
$timer.stop() 
$ts = $timer.Elapsed
$elapsedTime = "{0:00}:{1:00}:{2:00}.{3:00}" -f $ts.Hours, $ts.Minutes, $ts.Seconds, ($ts.Milliseconds / 10)
Write-Host "`nBuild process completed in $($elapsedTime)." -ForegroundColor Yellow