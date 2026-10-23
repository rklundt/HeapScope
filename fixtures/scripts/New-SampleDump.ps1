<#
.SYNOPSIS
    Produces a Small or Medium sample dump of LeakyApi with every leak active (sprint 0.4).

.DESCRIPTION
    Builds LeakyApi and LoadGen in Release, starts LeakyApi on a local port with all leaks enabled, drives it with
    LoadGen until the tier's item count is reached, captures a full dump into artifacts/dumps (git-ignored), and
    stops the API. Prints the dump size and timings, and warns when the size is outside the tier's accepted range.
    LeakyApi's output goes to a log file next to the dump; a failed run deletes its partial dump.

    Tiers (ARCHITECTURE.md 10):
      Small   about 500 MB dump (accepted 300 to 900 MB)
      Medium  about 4 GB dump (accepted 3 to 6 GB); needs about 4 GB of free RAM and 5 GB of disk

.EXAMPLE
    ./fixtures/scripts/New-SampleDump.ps1 -Tier Small
#>
[CmdletBinding()]
param(
    [ValidateSet('Small', 'Medium')]
    [string] $Tier = 'Small',

    [int] $Port = 5080,

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'artifacts\dumps'
}

# Each request grows every one of the five leaks by ItemsPerRequest items of about 200 bytes and 4 to 5 objects.
$settings = @{
    Small  = @{ Requests = 300;  ItemsPerRequest = 1000; PayloadBytes = 64; MinMB = 300;  MaxMB = 900 }
    Medium = @{ Requests = 3200; ItemsPerRequest = 1000; PayloadBytes = 64; MinMB = 3072; MaxMB = 6144 }
}[$Tier]

function Test-PortFree([int] $port) {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $port)
    try {
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        $listener.Stop()
    }
}

if (-not (Test-PortFree $Port)) {
    throw "Port $Port is in use (perhaps a LeakyApi left over from an earlier run). Stop it or pass -Port."
}

function Invoke-Build([string] $project) {
    $output = dotnet build (Join-Path $root $project) -c Release -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | Write-Host
        throw "Building $project failed."
    }
}

Write-Host "Building fixtures (Release)..."
Invoke-Build 'fixtures\LeakyApi\LeakyApi.csproj'
Invoke-Build 'fixtures\LoadGen\LoadGen.csproj'

$apiExe = Join-Path $root 'fixtures\LeakyApi\bin\Release\net10.0\LeakyApi.exe'
$loadGenExe = Join-Path $root 'fixtures\LoadGen\bin\Release\net10.0\LoadGen.exe'
$url = "http://127.0.0.1:$Port"
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$dumpPath = Join-Path $OutputDirectory "LeakyApi-$Tier-$stamp.dmp"
$apiLog = Join-Path $OutputDirectory "LeakyApi-$Tier-$stamp.log"

$api = Start-Process -FilePath $apiExe -PassThru -WindowStyle Hidden -RedirectStandardOutput $apiLog -ArgumentList @(
    '--urls', $url,
    '--Leaks:Enabled=all',
    "--Leaks:ItemsPerRequest=$($settings.ItemsPerRequest)",
    "--Leaks:PayloadBytes=$($settings.PayloadBytes)",
    "--ParentPid=$PID")
$succeeded = $false
try {
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        if ($api.HasExited) {
            throw "LeakyApi exited with code $($api.ExitCode) before answering. Its output: $(Get-Content $apiLog -Raw)"
        }

        try {
            Invoke-WebRequest -Uri "$url/health" -UseBasicParsing -TimeoutSec 2 | Out-Null
            break
        }
        catch {
            if ((Get-Date) -gt $deadline) { throw "LeakyApi did not answer on $url within 60 s." }
            Start-Sleep -Milliseconds 250
        }
    }

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & $loadGenExe --url $url --requests $settings.Requests --concurrency 8 --dump $dumpPath --pid $api.Id
    if ($LASTEXITCODE -ne 0) { throw "LoadGen failed with exit code $LASTEXITCODE." }
    $watch.Stop()

    $items = $settings.Requests * $settings.ItemsPerRequest * 5
    $sizeMb = (Get-Item $dumpPath).Length / 1MB
    Write-Host ("{0} dump: {1} ({2:N0} MB), about {3:N0} leaked items, {4:N0} s in total" -f `
        $Tier, $dumpPath, $sizeMb, $items, $watch.Elapsed.TotalSeconds)
    if ($sizeMb -lt $settings.MinMB -or $sizeMb -gt $settings.MaxMB) {
        Write-Warning ("The dump is outside the {0} range of {1:N0} to {2:N0} MB." -f $Tier, $settings.MinMB, $settings.MaxMB)
    }

    $succeeded = $true
}
finally {
    Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
    if (-not $succeeded -and (Test-Path $dumpPath)) {
        Remove-Item $dumpPath -ErrorAction SilentlyContinue
    }
}
