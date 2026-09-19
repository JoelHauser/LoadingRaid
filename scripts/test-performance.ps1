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
    # mapLoadIndex 1 is the first load of that map in that session, 2 or more is a repeat, and
    # the whole point of the field is that the two never pool.
    $cases = @(
        @('a', 'Vanilla', 'cold', 10, 0, 'first-update-after-game-started', 's1', 1, 40),
        @('b', 'Vanilla', 'cold', 14, 0, 'first-update-after-game-started', 's1', 1, 60),
        @('c', 'Minimal', 'cold', 8, 0, 'first-update-after-game-started', 's1', 1, 0),
        @('d', 'Vanilla', 'warm', 5, 0, 'first-update-after-game-started', 's1', 2, 0),
        @('e', 'Vanilla', 'cold', 100, 1, 'first-update-after-game-started', 's1', 1, 0),
        @('f', 'Vanilla', 'cold', 100, 0, 'cancel-requested', 's1', 1, 0))
    foreach ($case in $cases) {
        $report = @{
            schemaVersion = 2; map = 'Woods'; mode = $case[1]; label = $case[2]
            resolution = '1920x1080'; durationSeconds = $case[3]; unfocusedSeconds = $case[4]
            outcome = $case[5]; longestFocusedFrameGapSeconds = 0.5; focusedGapSeconds = 1.5
            sessionId = $case[6]; mapLoadIndex = $case[7]; sessionLoadIndex = $case[7]
            artDecodeMs = $case[8]; artDecodeCount = 1
            nativeSampledPeakBytes = 104857600; managedSampledPeakBytes = 10485760
        }
        [IO.File]::WriteAllText((Join-Path $scratch ($case[0] + '.json')), ($report | ConvertTo-Json))
    }

    # Same map, same settings, a different game session: it must not pool with the others.
    $other = @{
        schemaVersion = 2; map = 'Woods'; mode = 'Vanilla'; label = 'cold'
        resolution = '1920x1080'; durationSeconds = 99; unfocusedSeconds = 0
        outcome = 'first-update-after-game-started'; longestFocusedFrameGapSeconds = 0.5
        focusedGapSeconds = 1.5; sessionId = 's2'; mapLoadIndex = 1; sessionLoadIndex = 1
        artDecodeMs = 0; artDecodeCount = 0
        nativeSampledPeakBytes = 104857600; managedSampledPeakBytes = 10485760
    }
    [IO.File]::WriteAllText((Join-Path $scratch 'g.json'), ($other | ConvertTo-Json))

    [IO.File]::WriteAllText((Join-Path $scratch 'broken.json'), '{bad')
    $warnings = @()
    $result = @(& (Join-Path $PSScriptRoot 'compare-loading.ps1') -Path $scratch -WarningVariable warnings -WarningAction SilentlyContinue)
    if ($result.Count -ne 4) { throw "Expected 4 independent comparison groups, got $($result.Count)" }
    $baseline = $result | Where-Object { $_.Mode -eq 'Vanilla' -and $_.Label -eq 'cold' -and $_.Runs -eq 2 }
    if ($baseline.MedianLoadSeconds -ne 12) { throw 'Canceled/unfocused filtering or median failed' }
    if ($baseline.MaxSampledNativeMiB -ne 100 -or $baseline.MaxSampledManagedMiB -ne 10) { throw 'Memory units failed' }
    if ($baseline.Warmth -ne 'first') { throw 'Cold loads not marked first' }
    if ($baseline.MedianArtDecodeMs -ne 50) { throw "Art decode median wrong: $($baseline.MedianArtDecodeMs)" }
    $repeat = @($result | Where-Object { $_.Warmth -eq 'repeat' })
    if ($repeat.Count -ne 1 -or $repeat[0].MedianLoadSeconds -ne 5) { throw 'Repeat load not separated' }
    $pooled = @(& (Join-Path $PSScriptRoot 'compare-loading.ps1') -Path $scratch -AllSessions -WarningAction SilentlyContinue)
    if ($pooled.Count -ne 3) { throw "AllSessions should pool the two sessions, got $($pooled.Count)" }
    if ($warnings.Count -ne 1) { throw 'Malformed-report warning missing' }
    Write-Host '  PASS  comparison keeps modes and cold/warm labels separate'
    Write-Host '  PASS  first and repeat loads separate on their own, without a label'
    Write-Host '  PASS  runs from different game sessions are not pooled'
    Write-Host '  PASS  -AllSessions pools them when asked'
    Write-Host '  PASS  canceled and unfocused runs excluded; median correct'
    Write-Host '  PASS  art decode cost is carried through'
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
