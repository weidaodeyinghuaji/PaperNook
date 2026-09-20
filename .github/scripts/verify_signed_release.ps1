[CmdletBinding()]
param(
    [string] $ReleaseDirectory,
    [string] $PublisherSubject,
    [switch] $AllowUnsignedTestFixture
)

$ErrorActionPreference = 'Stop'

function New-VerificationResult([bool] $IsValid, [string[]] $Errors) {
    [pscustomobject]@{
        IsValid = $IsValid
        Errors = @($Errors)
    }
}

function Test-ReleaseManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ManifestPath,
        [Parameter(Mandatory)] [string] $ReleaseDirectory
    )

    $errors = [Collections.Generic.List[string]]::new()
    try {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
    }
    catch {
        return New-VerificationResult $false @("Manifest is not valid JSON: $($_.Exception.Message)")
    }

    $requiredRoot = @('schema', 'repository', 'tag', 'version', 'publishedUtc', 'assets')
    foreach ($name in $requiredRoot) {
        if (-not $manifest.ContainsKey($name)) { $errors.Add("Missing root property: $name") }
    }
    foreach ($name in $manifest.Keys) {
        if ($requiredRoot -notcontains $name) { $errors.Add("Unknown root property: $name") }
    }
    if ($manifest.schema -ne 1) { $errors.Add('Manifest schema must be 1.') }
    if ($manifest.repository -ne 'weidaodeyinghuaji/PaperNook') { $errors.Add('Manifest repository is not trusted.') }
    if ($manifest.assets -isnot [Collections.IEnumerable] -or $manifest.assets -is [string]) {
        $errors.Add('Manifest assets must be an array.')
    }
    else {
        $requiredAsset = @('distribution', 'fileName', 'version', 'length', 'sha256', 'downloadUrl')
        foreach ($asset in @($manifest.assets)) {
            if ($asset -isnot [Collections.IDictionary]) {
                $errors.Add('Manifest asset must be an object.')
                continue
            }
            foreach ($name in $requiredAsset) {
                if (-not $asset.Contains($name)) { $errors.Add("Asset is missing property: $name") }
            }
            foreach ($name in $asset.Keys) {
                if ($requiredAsset -notcontains $name) { $errors.Add("Unknown asset property: $name") }
            }
            if (-not $asset.Contains('fileName') -or [string]::IsNullOrWhiteSpace([string]$asset.fileName)) { continue }
            $fileName = [IO.Path]::GetFileName([string]$asset.fileName)
            if ($fileName -cne [string]$asset.fileName) {
                $errors.Add("Asset fileName is not a leaf name: $($asset.fileName)")
                continue
            }
            if (-not $asset.Contains('sha256') -or [string]$asset.sha256 -notmatch '^[0-9a-f]{64}$') {
                $errors.Add("Asset sha256 is invalid: $fileName")
            }
            $assetPath = Join-Path $ReleaseDirectory $fileName
            if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
                $errors.Add("Asset file is missing: $fileName")
                continue
            }
            $file = Get-Item -LiteralPath $assetPath
            if ($asset.Contains('length') -and [long]$asset.length -ne $file.Length) {
                $errors.Add("Asset length does not match: $fileName")
            }
            if ($asset.Contains('sha256') -and [string]$asset.sha256 -match '^[0-9a-f]{64}$') {
                $actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actualHash -cne [string]$asset.sha256) { $errors.Add("Asset hash does not match: $fileName") }
            }
        }
    }

    New-VerificationResult ($errors.Count -eq 0) $errors.ToArray()
}

function Test-AuthenticodeAsset {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $PublisherSubject,
        [switch] $AllowUnsignedTestFixture
    )

    if ($AllowUnsignedTestFixture) {
        return New-VerificationResult $true @()
    }

    $errors = [Collections.Generic.List[string]]::new()
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        $errors.Add("Authenticode status is $($signature.Status): $Path")
    }
    if ($null -eq $signature.SignerCertificate) {
        $errors.Add("Signer certificate is missing: $Path")
    }
    elseif (-not [string]::IsNullOrWhiteSpace($PublisherSubject) -and $signature.SignerCertificate.Subject -cne $PublisherSubject) {
        $errors.Add("Publisher subject does not match: $Path")
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        $errors.Add("RFC3161 timestamp is missing: $Path")
    }
    New-VerificationResult ($errors.Count -eq 0) $errors.ToArray()
}

function Invoke-ReleaseVerification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ReleaseDirectory,
        [string] $PublisherSubject,
        [switch] $AllowUnsignedTestFixture
    )

    $manifestPath = Join-Path $ReleaseDirectory 'release-manifest.json'
    $manifestResult = Test-ReleaseManifest -ManifestPath $manifestPath -ReleaseDirectory $ReleaseDirectory
    if (-not $manifestResult.IsValid) {
        throw "Release manifest verification failed: $($manifestResult.Errors -join '; ')"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($asset in @($manifest.assets | Where-Object { $_.fileName -match '(?i)\.exe$' })) {
        $result = Test-AuthenticodeAsset `
            -Path (Join-Path $ReleaseDirectory $asset.fileName) `
            -PublisherSubject $PublisherSubject `
            -AllowUnsignedTestFixture:$AllowUnsignedTestFixture
        if (-not $result.IsValid) {
            throw "Release signature verification failed: $($result.Errors -join '; ')"
        }
    }
    Write-Host 'Release manifest and assets verified.'
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
        throw 'ReleaseDirectory is required.'
    }
    Invoke-ReleaseVerification @PSBoundParameters
}
