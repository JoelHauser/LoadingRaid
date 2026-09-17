<#
.SYNOPSIS
    Summarizes confirmed loading captures, grouped by map, mode, resolution and test label.
.DESCRIPTION
    Uses the first Update after GameWorld.OnGameStarted as an endpoint proxy, not an exact
    measurement of when controls unlock. Canceled, incomplete and unfocused captures are
    excluded. First/cold and repeat/warm loads must have different Test label values.
#>
[CmdletBinding()]
param([string]$Path = 'C:\HUH\BepInEx\plugins\DeployScreen\diagnostics')
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
        if ($report.schemaVersion -ne 1 -or $null -eq $report.durationSeconds -or $null -eq $report.unfocusedSeconds) {
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
$reports | Group-Object map, mode, resolution, label, version, configuredCustomArt, configuredMotion, configuredCaptions, configuredBackdrop, minimalPreviewSkipped, minimalBannersSkipped, minimalBackgroundCreated, minimalEnvironmentSuspended, stagingArtShown, stagingCharacterLit, stagingIntelShown | ForEach-Object {
    $group = $_.Group
    [pscustomobject][ordered]@{
        Map = $group[0].map
        Mode = $group[0].mode
        Resolution = $group[0].resolution
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
        MaxSampledWorkingSetMiB = [Math]::Round(($group.workingSetSampledPeakBytes | Measure-Object -Maximum).Maximum / 1MB, 1)
        MaxSampledManagedMiB = [Math]::Round(($group.managedSampledPeakBytes | Measure-Object -Maximum).Maximum / 1MB, 1)
    }
}
