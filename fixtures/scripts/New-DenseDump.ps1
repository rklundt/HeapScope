# HeapScope
# Copyright (C) 2026 Ray Klundt
# https://github.com/rklundt/HeapScope
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU Affero General Public License as published
# by the Free Software Foundation, version 3 only, with additional terms
# under Section 7 of that license.
#
# Additional terms, including attribution requirements, apply.
# See the ADDITIONAL-TERMS.md file distributed with this program.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
# GNU Affero General Public License for more details.
#
# You should have received a copy of the GNU Affero General Public License
# along with this program (see the LICENSE file). If not, see
# <https://www.gnu.org/licenses/>.
#
# Commercial licenses are available. See COMMERCIAL.md.

<#
.SYNOPSIS
    Produces a dense, realistic sample dump: the DenseHeap fixture grows a managed heap out of real Roslyn compiler
    objects (syntax trees, symbols, compilations), then the heap is captured as a full dump (sprint 1.2).

.DESCRIPTION
    Builds DenseHeap and LoadGen (Release), starts DenseHeap with this script's process as its parent, waits for its
    READY line, captures a full dump into artifacts/dumps (git-ignored) and stops DenseHeap. DenseHeap is always
    killed in a finally block, and it also exits by itself when this script's process exits, so an interrupted run
    leaves no process behind. A failed run deletes its partial dump.

.EXAMPLE
    ./fixtures/scripts/New-DenseDump.ps1 -TargetMB 3000
#>
param(
    [int] $TargetMB = 3000,

    [int] $TimeoutMinutes = 20,

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'artifacts\dumps'
}

function Invoke-Build([string] $project) {
    $output = dotnet build (Join-Path $root $project) -c Release -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | Write-Host
        throw "Building $project failed."
    }
}

Write-Host "Building fixtures (Release)..."
Invoke-Build 'fixtures\DenseHeap\DenseHeap.csproj'
Invoke-Build 'fixtures\LoadGen\LoadGen.csproj'

$denseExe = Join-Path $root 'fixtures\DenseHeap\bin\Release\net10.0\DenseHeap.exe'
$loadGenExe = Join-Path $root 'fixtures\LoadGen\bin\Release\net10.0\LoadGen.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$dumpPath = Join-Path $OutputDirectory "DenseHeap-$TargetMB-$stamp.dmp"
$logPath = [System.IO.Path]::ChangeExtension($dumpPath, '.log')

$watch = [System.Diagnostics.Stopwatch]::StartNew()
$info = New-Object System.Diagnostics.ProcessStartInfo $denseExe
$info.Arguments = "--target-mb $TargetMB --parent-pid $PID"
$info.UseShellExecute = $false
$info.RedirectStandardOutput = $true
$info.CreateNoWindow = $true
$dense = $null
$succeeded = $false
try {
    $dense = [System.Diagnostics.Process]::Start($info)
    Write-Host "DenseHeap started (pid $($dense.Id)), growing the heap to $TargetMB MB..."
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $ready = $null
    while (-not $ready) {
        $read = $dense.StandardOutput.ReadLineAsync()
        while (-not $read.Wait(1000)) {
            if ($dense.HasExited) { throw "DenseHeap exited early with code $($dense.ExitCode)." }
            if ((Get-Date) -gt $deadline) { throw "DenseHeap was not ready within $TimeoutMinutes minutes." }
        }

        $line = $read.Result
        if ($null -eq $line) { throw "DenseHeap closed its output before it was ready." }
        Add-Content -Path $logPath -Value $line
        Write-Host "  $line"
        if ($line.StartsWith('READY ')) { $ready = $line }
    }

    & $loadGenExe --requests 0 --dump $dumpPath --pid $dense.Id
    if ($LASTEXITCODE -ne 0) { throw "Dump capture failed (LoadGen exit code $LASTEXITCODE)." }

    $sizeMb = (Get-Item $dumpPath).Length / 1MB
    Write-Host ("Dense dump: {0} ({1:N0} MB), {2:N0} s in total" -f $dumpPath, $sizeMb, $watch.Elapsed.TotalSeconds)
    $succeeded = $true
}
finally {
    # Always force quit the fixture, whatever happened above.
    if ($dense -and -not $dense.HasExited) {
        $dense.Kill()
        $dense.WaitForExit(10000) | Out-Null
        Write-Host "DenseHeap stopped."
    }

    if (-not $succeeded -and (Test-Path $dumpPath)) {
        Remove-Item $dumpPath -ErrorAction SilentlyContinue
    }
}
