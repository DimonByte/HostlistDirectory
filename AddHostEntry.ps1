$url = Read-Host "Enter the URL of the list"
try {
    $response = Invoke-WebRequest -Uri $url -TimeoutSec 30 -UseBasicParsing
    $content = $response.Content
} catch {
    Write-Error "Failed to download content from $url : $($_.Exception.Message)"
    exit
}

function Get-Metadata {
    param(
        [string]$Content,
        [string[]]$Patterns,
        [int]$GroupIndex = 1
    )
    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::Multiline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    foreach ($pattern in $Patterns) {
        $metedatamatches = [System.Text.RegularExpressions.Regex]::Matches($Content, $pattern, $regexOptions)
        if ($matmetedatamatchesches.Count -gt 0) {
            return $metedatamatches[0].Groups[$GroupIndex].Value.Trim()
        }
    }
    return $null
}

function Get-UserInput {
    param(
        [string]$Field,
        [string]$DefaultValue
    )
    $userinput = Read-Host "Enter $Field (or press Enter to use default: '$DefaultValue')"
    if ([string]::IsNullOrWhiteSpace($userinput)) {
        return $DefaultValue
    }
    return $userinput.Trim()
}

$titlePatterns = @(
    "^#?\s*(?:Title|Name):\s*(.*)$"
)
$descriptionPatterns = @(
    "^#?\s*(?:Description|Desc):\s*(.*)$"
)
$homepagePatterns = @(
    "^#?\s*(?:Homepage|URL):\s*(.*)$"
)
$licensePatterns = @(
    "^#?\s*(?:License|Lic):\s*(.*)$"
)
$expiryPatterns = @(
    "^#?\s*(?:Expires?|Expiry|Expiration):\s*(.*)$"
)
$lastModifiedPatterns = @(
    "^#?\s*(?:Last modified|Modified|Updated):\s*(.*)$"
)
$versionPatterns = @(
    "^#?\s*(?:Version|Ver):\s*(.*)$"
)
# Extract metadata from content
$title = Get-Metadata -Content $content -Patterns $titlePatterns
$description = Get-Metadata -Content $content -Patterns $descriptionPatterns
$homepage = Get-Metadata -Content $content -Patterns $homepagePatterns
$license = Get-Metadata -Content $content -Patterns $licensePatterns
$expiry = Get-Metadata -Content $content -Patterns $expiryPatterns
$lastModified = Get-Metadata -Content $content -Patterns $lastModifiedPatterns
$version = Get-Metadata -Content $content -Patterns $versionPatterns

if (-not $title) {
    $title = Get-UserInput -Field "title" -DefaultValue "Unknown"
}
if (-not $description) {
    $description = Get-UserInput -Field "description" -DefaultValue "No description provided"
}
if (-not $homepage) {
    $homepage = Get-UserInput -Field "homepage" -DefaultValue $url
}
if (-not $license) {
    $license = Get-UserInput -Field "license" -DefaultValue "Unknown"
}
if (-not $expiry) {
    $expiry = Get-UserInput -Field "expiry" -DefaultValue "Unknown"
}
if (-not $lastModified) {
    $lastModified = Get-UserInput -Field "last modified" -DefaultValue "Unknown"
}
if (-not $version) {
    $version = Get-UserInput -Field "version" -DefaultValue "Unknown"
}

$json = [ordered]@{
    name = $title
    description = $description
    homepage = $homepage
    tags = @("unknown")
    mainMirror = $url
    alternativeMirrors = @()
    detectionMethods = @{
        title = @{
            type = "text-locate"
            patterns = $titlePatterns
            priority = 1
        }
        description = @{
            type = "text-locate"
            patterns = $descriptionPatterns
            priority = 2
        }
        homepage = @{
            type = "text-locate"
            patterns = $homepagePatterns
            priority = 3
        }
        license = @{
            type = "text-locate"
            patterns = $licensePatterns
            priority = 4
        }
        expiry = @{
            type = "text-locate"
            patterns = $expiryPatterns
            priority = 5
        }
        lastModified = @{
            type = "text-locate"
            patterns = $lastModifiedPatterns
            priority = 6
        }
        version = @{
            type = "text-locate"
            patterns = $versionPatterns
            priority = 7
        }
    }
}

$json | ConvertTo-Json -Depth 10 | Out-File -FilePath "entry.json" -Encoding UTF8
Write-Host "Entry written to entry.json" -ForegroundColor Green