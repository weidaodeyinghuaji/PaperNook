param()

$ErrorActionPreference = 'Stop'
$scriptsDirectory = Split-Path -Parent $PSScriptRoot
$writer = Join-Path $scriptsDirectory 'write_release_manifest.ps1'
$verifier = Join-Path $scriptsDirectory 'verify_signed_release.ps1'

if (-not (Test-Path -LiteralPath $writer)) {
    throw "Missing production manifest writer: $writer"
}
if (-not (Test-Path -LiteralPath $verifier)) {
    throw "Missing production release verifier: $verifier"
}

. $verifier

$fixture = Join-Path ([IO.Path]::GetTempPath()) ("papertodo-release-checks-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $selfContained = Join-Path $fixture 'PaperNook-v1.0.0-win-x64-self-contained.exe'
    $noRuntime = Join-Path $fixture 'PaperNook-v1.0.0-win-x64-no-runtime.exe'
    $portable = Join-Path $fixture 'PaperNook-v1.0.0-win-x64-portable.zip'
    [IO.File]::WriteAllBytes($selfContained, [byte[]](1, 2, 3, 4))
    [IO.File]::WriteAllBytes($noRuntime, [byte[]](5, 6, 7))
    [IO.File]::WriteAllBytes($portable, [byte[]](8, 9))

    & $writer `
        -ReleaseDirectory $fixture `
        -Repository 'weidaodeyinghuaji/PaperNook' `
        -Tag 'v1.0.0' `
        -Version '1.0.0' `
        -PublishedUtc '2026-09-20T00:00:00Z'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $first = Get-Content -LiteralPath (Join-Path $fixture 'release-manifest.json') -Raw
    & $writer `
        -ReleaseDirectory $fixture `
        -Repository 'weidaodeyinghuaji/PaperNook' `
        -Tag 'v1.0.0' `
        -Version '1.0.0' `
        -PublishedUtc '2026-09-20T00:00:00Z'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $second = Get-Content -LiteralPath (Join-Path $fixture 'release-manifest.json') -Raw
    if ($first -cne $second) { throw 'Manifest output is not deterministic.' }

    $manifest = Test-ReleaseManifest -ManifestPath (Join-Path $fixture 'release-manifest.json') -ReleaseDirectory $fixture
    if (-not $manifest.IsValid) { throw "Valid manifest rejected: $($manifest.Errors -join '; ')" }

    $parsed = $first | ConvertFrom-Json
    $names = @($parsed.assets | ForEach-Object fileName)
    $sorted = @($names | Sort-Object)
    if (($names -join '|') -cne ($sorted -join '|')) { throw 'Manifest assets are not sorted by fileName.' }
    if ((@($parsed.assets | ForEach-Object distribution) -join '|') -cne 'no-runtime|portable|self-contained') {
        throw 'Manifest distributions were not inferred as expected.'
    }

    $parsed.assets[0].PSObject.Properties.Remove('sha256')
    $parsed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fixture 'release-manifest-invalid.json') -Encoding utf8NoBOM
    $invalid = Test-ReleaseManifest -ManifestPath (Join-Path $fixture 'release-manifest-invalid.json') -ReleaseDirectory $fixture
    if ($invalid.IsValid) { throw 'Manifest without sha256 was accepted.' }

    & $verifier -ReleaseDirectory $fixture -AllowUnsignedTestFixture
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host 'PASS release-manifest-deterministic-and-strict'
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
