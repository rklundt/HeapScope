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
    The local full gate (sprint 1.9): what CI would run, until the repository has a remote.

.DESCRIPTION
    Builds the solution in Debug and Release (zero warnings), runs the full test suite, and appends one line per run to
    artifacts/gate/history.csv (git-ignored): time, commit, outcome, build and test durations, test counts and the names
    of failed tests, so tests that fail now and then show up over time. Exit code 0 when everything passed.

.EXAMPLE
    ./build/gate.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$historyDir = Join-Path $root 'artifacts\gate'
$history = Join-Path $historyDir 'history.csv'
New-Item -ItemType Directory -Force -Path $historyDir | Out-Null
Set-Location $root

function Invoke-Step([string] $Name, [scriptblock] $Body) {
    Write-Host "== $Name"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = & $Body 2>&1 | Out-String
    $watch.Stop()
    [pscustomobject]@{ Output = $output; Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1); ExitCode = $LASTEXITCODE }
}

$commit = (git rev-parse --short HEAD).Trim()
$dirty = if (git status --porcelain) { '+changes' } else { '' }

$debug = Invoke-Step 'Build Debug' { dotnet build HeapScope.sln -c Debug }
$release = Invoke-Step 'Build Release' { dotnet build HeapScope.sln -c Release }
$warnings = @($debug, $release | ForEach-Object { if ($_.Output -match '(\d+) Warning\(s\)') { [int]$Matches[1] } else { 0 } } | Measure-Object -Sum).Sum
$buildOk = $debug.ExitCode -eq 0 -and $release.ExitCode -eq 0 -and $warnings -eq 0

$total = 0; $failed = 0; $failures = @(); $testSeconds = 0; $testsOk = $false
if ($buildOk) {
    $tests = Invoke-Step 'Tests' { dotnet test --solution HeapScope.sln --no-build }
    $testSeconds = $tests.Seconds
    if ($tests.Output -match 'total:\s*(\d+)') { $total = [int]$Matches[1] }
    if ($tests.Output -match 'failed:\s*(\d+)') { $failed = [int]$Matches[1] }
    $failures = @([regex]::Matches($tests.Output, '(?m)^failed (\S+)') | ForEach-Object { $_.Groups[1].Value })
    $testsOk = $tests.ExitCode -eq 0 -and $failed -eq 0 -and $total -gt 0
}

$outcome = if ($buildOk -and $testsOk) { 'pass' } elseif (-not $buildOk) { 'build-failed' } else { 'tests-failed' }
$line = [pscustomobject]@{
    Time = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Commit = "$commit$dirty"
    Outcome = $outcome
    BuildSeconds = $debug.Seconds + $release.Seconds
    Warnings = $warnings
    TestSeconds = $testSeconds
    Tests = $total
    Failed = $failed
    FailedTests = ($failures -join ' ')
}
$line | Export-Csv -Path $history -Append -NoTypeInformation -Encoding utf8
$line | Format-List | Out-String | Write-Host

# Tests that failed in earlier runs too: the ones to look at first.
if ($failures.Count -gt 0 -and (Test-Path $history)) {
    $earlier = Import-Csv $history | Select-Object -SkipLast 1 | ForEach-Object { $_.FailedTests -split ' ' } | Where-Object { $_ }
    $repeat = $failures | Where-Object { $earlier -contains $_ }
    if ($repeat) { Write-Host "Failed before as well: $($repeat -join ', ')" }
}

exit $(if ($outcome -eq 'pass') { 0 } else { 1 })
