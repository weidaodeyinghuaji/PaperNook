[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReleaseDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^[^/]+/[^/]+$')] [string] $Repository,
    [Parameter(Mandatory)] [ValidatePattern('^v[^\s]+$')] [string] $Tag,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')] [string] $Version,
    [string] $PublishedUtc = ([DateTimeOffset]::UtcNow.ToString('O'))
)

$ErrorActionPreference = 'Stop'

function Resolve-Distribution([string] $FileName) {
    if ($FileName -match '(?i)-self-contained\.exe$') { return 'self-contained' }
    if ($FileName -match '(?i)-no-runtime\.exe$') { return 'no-runtime' }
    if ($FileName -match '(?i)-portable\.zip$') { return 'portable' }
    throw "Unsupported release asset name: $FileName"
}

$resolvedDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $resolvedDirectory -PathType Container)) {
    throw "Release directory does not exist: $resolvedDirectory"
}

$published = [DateTimeOffset]::Parse(
    $PublishedUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime().ToString('O')
$baseUrl = "https://github.com/$Repository/releases/download/$Tag"
$assets = @(
    Get-ChildItem -LiteralPath $resolvedDirectory -File |
        Where-Object { $_.Name -match '(?i)^PaperNook-v.+-win-x64-(?:self-contained|no-runtime)\.exe$' -or $_.Name -match '(?i)^PaperNook-v.+-win-x64-portable\.zip$' } |
        Sort-Object -Property Name |
        ForEach-Object {
            [ordered]@{
                distribution = Resolve-Distribution $_.Name
                fileName = $_.Name
                version = $Version
                length = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                downloadUrl = "$baseUrl/$([Uri]::EscapeDataString($_.Name))"
            }
        }
)

if ($assets.Count -eq 0) {
    throw 'No supported PaperNook release assets were found.'
}

$manifest = [ordered]@{
    schema = 1
    repository = $Repository
    tag = $Tag
    version = $Version
    publishedUtc = $published
    assets = $assets
}

$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText(
    (Join-Path $resolvedDirectory 'release-manifest.json'),
    $json + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
