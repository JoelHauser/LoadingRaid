<#
.SYNOPSIS
    Summarizes confirmed loading captures, grouped by map, mode, resolution and test label.
.DESCRIPTION
    Uses the first Update after GameWorld.OnGameStarted as an endpoint proxy, not an exact
    measurement of when controls unlock. Canceled, incomplete and unfocused captures are
    excluded.

    Cold and warm loads separate themselves: since schema 2 every report carries the game
    session it came from and how many times that map had already loaded in it, so the first
    load of a map in a session and the second are grouped apart without anyone having to set
    a Test label. The label is still honoured on top of that, for comparing two settings.

    Runs from different game sessions are never pooled. That was the flaw in every comparison
    made before schema 2: bot load and whatever else the machine was doing differ between
    sessions by more than the thing being measured.
.PARAMETER Path
    The plugin's diagnostics folder.
.PARAMETER AllSessions
    Pool runs across game sessions anyway. Off by default, and the results are not evidence
    of much -- see above.
#>
[CmdletBinding()]
param(
    [string]$Path = 'H:\SPT4.1.X\BepInEx\plugins\DeployScreen\diagnostics',
    [switch]$AllSessions)
$ErrorActionPreference = 'Stop'
function Median($values) {
    $sorted = @($values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}
$reports = @(foreach ($file in Get-ChildItem -LiteralPath $Path -Filter '*.json' -File) {
    try {
        $report = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($report.schemaVersion -lt 1 -or $report.schemaVersion -gt 2 -or $null -eq $report.durationSeconds -or $null -eq $report.unfocusedSeconds) {
            Write-Warning "Skipping unsupported or incomplete report: $($file.Name)"
            continue
        }
        if ($report.outcome -ne 'first-update-after-game-started' -or $report.unfocusedSeconds -gt 0) {
            Write-Verbose "Excluded $($file.Name): outcome=$($report.outcome), unfocused=$($report.unfocusedSeconds)s"
            continue
        }
        $report
    }
    catch { Write-Warning "Skipping unreadable report: $($file.Name)" }
})
if ($reports.Count -eq 0) { Write-Warning 'No confirmed, continuously focused loading reports found.'; return }
# Staging captures are grouped on what staging actually did, for the same reason the minimal
# fields are: a raid where the map had no art, or where the character lighting could not be
# applied, is not comparable with one where both worked, and pooling them would hide exactly
# the difference the capture exists to measure.
# A schema-1 report has neither field; it groups as a session of its own and a load index of 0,
# which keeps the old captures readable without letting them pool with the new ones.
foreach ($report in $reports) {
    $session = if ($report.sessionId) { $report.sessionId } else { 'before-schema-2' }
    if ($AllSessions) { $session = 'all' }
    Add-Member -InputObject $report -NotePropertyName 'groupSession' -NotePropertyValue $session -Force

    $index = if ($null -ne $report.mapLoadIndex) { [int]$report.mapLoadIndex } else { 0 }
    # Everything past the second load of a map is warm in the same way, so they pool.
    $warmth = if ($index -le 0) { 'unknown' } elseif ($index -eq 1) { 'first' } else { 'repeat' }
    Add-Member -InputObject $report -NotePropertyName 'warmth' -NotePropertyValue $warmth -Force
}

$reports | Group-Object groupSession, map, mode, resolution, warmth, label, version, configuredCustomArt, configuredMotion, configuredCaptions, configuredBackdrop, minimalPreviewSkipped, minimalBannersSkipped, minimalBackgroundCreated, minimalEnvironmentSuspended, stagingArtShown, stagingCharacterLit, stagingIntelShown | ForEach-Object {
    $group = $_.Group
    [pscustomobject][ordered]@{
        Map = $group[0].map
        Mode = $group[0].mode
        Resolution = $group[0].resolution
        Warmth = $group[0].warmth
        Label = $group[0].label
        Version = $group[0].version
        MinimalPreviewSkipped = $group[0].minimalPreviewSkipped
        MinimalBannersSkipped = $group[0].minimalBannersSkipped
        MinimalEnvironmentSuspended = $group[0].minimalEnvironmentSuspended
        StagingArtShown = $group[0].stagingArtShown
        StagingCharacterLit = $group[0].stagingCharacterLit
        RaidConditions = $group[0].raidConditions
        Runs = $group.Count
        MedianLoadSeconds = [Math]::Round((Median $group.durationSeconds), 3)
        MedianLongestGapSeconds = [Math]::Round((Median $group.longestFocusedFrameGapSeconds), 3)
        MedianGapSeconds = [Math]::Round((Median $group.focusedGapSeconds), 3)
        MaxSampledNativeMiB = [Math]::Round(($group.nativeSampledPeakBytes | Measure-Object -Maximum).Maximum / 1MB, 1)
        MaxSampledManagedMiB = [Math]::Round(($group.managedSampledPeakBytes | Measure-Object -Maximum).Maximum / 1MB, 1)
        # The mod's own share of the main thread. Set against MedianGapSeconds it says whether
        # the art is worth arguing about at all.
        MedianArtDecodeMs = [Math]::Round((Median @($group | ForEach-Object { if ($null -ne $_.artDecodeMs) { [double]$_.artDecodeMs } else { 0 } })), 1)
        MedianArtDecodes = [Math]::Round((Median @($group | ForEach-Object { if ($null -ne $_.artDecodeCount) { [int]$_.artDecodeCount } else { 0 } })), 1)
    }
}
