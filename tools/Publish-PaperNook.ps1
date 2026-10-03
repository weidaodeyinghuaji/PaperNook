[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$DotnetPath = "",
    [switch]$RequireLiveQuota,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$semver = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(alpha|beta|rc)(?:\.(0|[1-9][0-9]*))?)?$'
if ($Version -notmatch $semver) {
    throw "Unsupported PaperNook version '$Version'. Use SemVer such as 1.1.0-beta.1."
}
if ($ValidateOnly) {
    Write-Output "valid-version=$Version"
    exit 0
}

$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repo '输出'))

function Test-Dotnet10Sdk([string]$Candidate) {
    if (-not [System.IO.File]::Exists($Candidate)) { return $false }
    $sdks = & $Candidate --list-sdks 2>$null
    return $LASTEXITCODE -eq 0 -and @($sdks | Where-Object { $_ -match '^10\.' }).Count -gt 0
}

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $fallback = Join-Path (Split-Path (Split-Path $repo)) '.tools\dotnet10\dotnet.exe'
    if (Test-Dotnet10Sdk $fallback) {
        $DotnetPath = $fallback
    } else {
        $command = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($command -and (Test-Dotnet10Sdk $command.Source)) {
            $DotnetPath = $command.Source
        }
    }
}
if (-not (Test-Dotnet10Sdk $DotnetPath)) {
    throw 'A .NET 10 SDK was not found. Pass -DotnetPath explicitly.'
}

function Assert-ChildPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($outputRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish path escaped the output root: $full"
    }
    return $full
}

$runId = [Guid]::NewGuid().ToString('N')
$publish = Assert-ChildPath (Join-Path $outputRoot ".publish-$runId")
$stage = Assert-ChildPath (Join-Path $outputRoot ".package-$runId")
$smoke = Assert-ChildPath (Join-Path $outputRoot ".smoke-$runId")
$zipCheck = Assert-ChildPath (Join-Path $outputRoot ".zipcheck-$runId")
# The runnable portable directory is stable across releases. Versioned ZIPs remain
# immutable distribution artifacts; portable user data stays in this one directory.
$package = Assert-ChildPath (Join-Path $outputRoot 'PaperNook-便携免安装版')
$zip = Assert-ChildPath (Join-Path $outputRoot "PaperNook-v$Version-win-x64-portable.zip")
$hashFile = "$zip.sha256"

try {
    if ($RequireLiveQuota) {
        $checksProject = Join-Path $repo 'tests\PaperTodo.CodexMeterChecks\PaperTodo.CodexMeterChecks.csproj'
        foreach ($attempt in 1..3) {
            & $DotnetPath run --project $checksProject -c Release -- --live-required
            if ($LASTEXITCODE -ne 0) {
                throw "Authenticated Codex quota check $attempt of 3 failed with exit code $LASTEXITCODE."
            }
        }
    }

    & $DotnetPath publish (Join-Path $repo 'PaperTodo.csproj') `
        -c Release -r win-x64 --self-contained true -o $publish `
        "/p:Version=$Version" `
        "/p:AssemblyVersion=$($Version.Split('-')[0]).0" `
        "/p:FileVersion=$($Version.Split('-')[0]).0" `
        "/p:InformationalVersion=$Version" `
        /p:PublishSingleFile=true `
        /p:PublishReadyToRun=false `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:EnableCompressionInSingleFile=true `
        /p:PublishTrimmed=false `
        /p:EmbedAllSources=false `
        /p:EmbedUntrackedSources=false `
        /p:DebugType=none `
        /p:DebugSymbols=false `
        /p:PaperNookDistribution=self-contained
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    [System.IO.Directory]::CreateDirectory($stage) | Out-Null
    [System.IO.File]::Copy((Join-Path $publish 'PaperNook.exe'), (Join-Path $stage 'PaperNook.exe'), $true)
    [System.IO.File]::Copy((Join-Path $repo 'assets\PaperNook.ico'), (Join-Path $stage 'PaperNook.ico'), $true)
    [System.IO.File]::Copy((Join-Path $repo 'LICENSE.md'), (Join-Path $stage 'LICENSE.md'), $true)
    [System.IO.File]::Copy((Join-Path $repo 'README.zh.md'), (Join-Path $stage 'README.zh.md'), $true)
    [System.IO.File]::WriteAllBytes((Join-Path $stage 'papernook.portable'), [byte[]]@())

    $distributionFiles = @('PaperNook.exe', 'PaperNook.ico', 'LICENSE.md', 'README.zh.md', 'papernook.portable')
    foreach ($name in $distributionFiles) {
        if (-not [System.IO.File]::Exists((Join-Path $stage $name))) {
            throw "Required distribution file is missing: $name"
        }
    }

    $exe = Join-Path $stage 'PaperNook.exe'
    $actualVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    if ($actualVersion -ne $Version) { throw "EXE version '$actualVersion' does not match '$Version'." }
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
    if ($null -eq $icon) { throw 'PaperNook.exe has no extractable application icon.' }
    $icon.Dispose()

    Copy-Item -LiteralPath $stage -Destination $smoke -Recurse
    $smokeExe = Join-Path $smoke 'PaperNook.exe'
    $process = Start-Process -FilePath $smokeExe `
        -ArgumentList '--package-smoke-test' `
        -WorkingDirectory $smoke `
        -WindowStyle Hidden `
        -PassThru
    if (-not $process.WaitForExit(20000)) {
        Stop-Process -Id $process.Id -Force
        throw 'Portable package smoke test did not finish within 20 seconds.'
    }
    if ($process.ExitCode -ne 0) { throw "Portable smoke process exited with code $($process.ExitCode)." }
    if ([System.IO.File]::Exists((Join-Path $smoke 'PaperNook.crash.log'))) {
        throw 'Portable smoke run produced PaperNook.crash.log.'
    }

    $packageExe = Join-Path $package 'PaperNook.exe'
    $runningPackage = @(Get-CimInstance Win32_Process -Filter "name='PaperNook.exe'" |
        Where-Object { $_.ExecutablePath -and
            [string]::Equals($_.ExecutablePath, $packageExe,
                [System.StringComparison]::OrdinalIgnoreCase) })
    if ($runningPackage.Count -gt 0) {
        throw "Close the running PaperNook at '$package' before updating the portable directory."
    }

    # Replace only distribution-owned files; preserve portable user data and settings.
    [System.IO.Directory]::CreateDirectory($package) | Out-Null
    foreach ($name in $distributionFiles) {
        [System.IO.File]::Copy((Join-Path $stage $name), (Join-Path $package $name), $true)
    }

    if ([System.IO.File]::Exists($zip)) { [System.IO.File]::Delete($zip) }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    Expand-Archive -LiteralPath $zip -DestinationPath $zipCheck
    $expandedFiles = @(Get-ChildItem -LiteralPath $zipCheck -File -Recurse | ForEach-Object {
        [System.IO.Path]::GetRelativePath($zipCheck, $_.FullName)
    })
    $unexpected = @($expandedFiles | Where-Object { $_ -notin $distributionFiles })
    $missing = @($distributionFiles | Where-Object { $_ -notin $expandedFiles })
    if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
        throw "ZIP content mismatch. Missing=[$($missing -join ', ')]; Unexpected=[$($unexpected -join ', ')]"
    }
    foreach ($name in $distributionFiles) {
        $stageHash = (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash
        $zipHash = (Get-FileHash -LiteralPath (Join-Path $zipCheck $name) -Algorithm SHA256).Hash
        if ($stageHash -ne $zipHash) { throw "ZIP verification failed for $name." }
    }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText($hashFile, "$hash  $([System.IO.Path]::GetFileName($zip))`r`n")
    Write-Output "package=$package"
    Write-Output "zip=$zip"
    Write-Output "sha256=$hash"
}
finally {
    if ([System.IO.Directory]::Exists($publish)) { [System.IO.Directory]::Delete($publish, $true) }
    if ([System.IO.Directory]::Exists($stage)) { [System.IO.Directory]::Delete($stage, $true) }
    if ([System.IO.Directory]::Exists($smoke)) { [System.IO.Directory]::Delete($smoke, $true) }
    if ([System.IO.Directory]::Exists($zipCheck)) { [System.IO.Directory]::Delete($zipCheck, $true) }
}
