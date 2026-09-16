<#
.SYNOPSIS
    Runs deterministic diagnostic accounting and report-comparison checks without Unity.
.DESCRIPTION
    Compiles the actual LoadTrace.cs source with a small test harness using PowerShell's C#
    compiler. Runtime hooks, minimal-screen rendering and restoration still need in-game tests.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = [IO.File]::ReadAllText((Join-Path $root 'src\DeployScreen.Client\LoadTrace.cs'))
$checks = [IO.File]::ReadAllText((Join-Path $root 'tests\PerformanceChecks.cs'))
Add-Type -TypeDefinition ($source + [Environment]::NewLine + $checks) -Language CSharp
[DeployScreen.Client.PerformanceChecks]::Run() | ForEach-Object { Write-Host "  PASS  $_" }

# Exercise the comparison script with real JSON, multiple groups, canceled/unfocused reports
# and a malformed file. Scratch files are unique and confined to this test directory.
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('DeployScreen-performance-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $cases = @(
        @('a', 'Vanilla', 'cold', 10, 0, 'first-update-after-game-started'),
        @('b', 'Vanilla', 'cold', 14, 0, 'first-update-after-game-started'),
        @('c', 'Minimal', 'cold', 8, 0, 'first-update-after-game-started'),
        @('d', 'Vanilla', 'warm', 5, 0, 'first-update-after-game-started'),
        @('e', 'Vanilla', 'cold', 100, 1, 'first-update-after-game-started'),
        @('f', 'Vanilla', 'cold', 100, 0, 'cancel-requested'))
    foreach ($case in $cases) {
        $report = @{
            schemaVersion = 1; map = 'Woods'; mode = $case[1]; label = $case[2]
            resolution = '1920x1080'; durationSeconds = $case[3]; unfocusedSeconds = $case[4]
            outcome = $case[5]; longestFocusedFrameGapSeconds = 0.5; focusedGapSeconds = 1.5
            workingSetSampledPeakBytes = 104857600; managedSampledPeakBytes = 10485760
        }
        [IO.File]::WriteAllText((Join-Path $scratch ($case[0] + '.json')), ($report | ConvertTo-Json))
    }
    [IO.File]::WriteAllText((Join-Path $scratch 'broken.json'), '{bad')
    $warnings = @()
    $result = @(& (Join-Path $PSScriptRoot 'compare-loading.ps1') -Path $scratch -WarningVariable warnings -WarningAction SilentlyContinue)
    if ($result.Count -ne 3) { throw "Expected 3 independent comparison groups, got $($result.Count)" }
    $baseline = $result | Where-Object { $_.Mode -eq 'Vanilla' -and $_.Label -eq 'cold' }
    if ($baseline.Runs -ne 2 -or $baseline.MedianLoadSeconds -ne 12) { throw 'Canceled/unfocused filtering or median failed' }
    if ($baseline.MaxSampledWorkingSetMiB -ne 100 -or $baseline.MaxSampledManagedMiB -ne 10) { throw 'Memory units failed' }
    if ($warnings.Count -ne 1) { throw 'Malformed-report warning missing' }
    Write-Host '  PASS  comparison keeps modes and cold/warm labels separate'
    Write-Host '  PASS  canceled and unfocused runs excluded; median correct'
    Write-Host '  PASS  memory units and malformed-report handling'
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved)).StartsWith('DeployScreen-performance-')) {
        throw 'Refusing cleanup outside the performance-test directory'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Host 'Performance checks passed.'
