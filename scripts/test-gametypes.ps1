<#
.SYNOPSIS
    Checks that every game type and member GameTypes.cs resolves by name actually exists.

.DESCRIPTION
    This plugin references no game assembly: every type, method and field is looked up at
    runtime by its *patched* name through AccessTools. That buys version tolerance, but it
    also means there is no compile-time check on any of those names -- a typo, or a rename in
    a new EFT build, turns a feature off silently and is only visible as a log warning while
    the game runs.

    This script is that missing check, run against the real patched assembly.

    The Assembly-CSharp.dll sitting in Managed is NOT the one the game runs: the SPT Launcher
    applies an HDiffPatch delta at startup that renames obfuscated types. So a check against
    the on-disk copy would be meaningless. Point -Assembly at a patched copy. To make one:

        hpatchz.exe `
            "<SPT>\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll" `
            "<SPT>\SPT_Runtime\SPT_Data\Launcher\Patches\SPT-core\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll.delta" `
            "<out>\Assembly-CSharp.dll"

    hpatchz.exe is the HDiffPatch CLI; the delta declares "HDiff" with zstd compression. The
    patched file for 4.1.5 is 16,233,472 bytes against the original's 15,994,432.

    Everything here is metadata inspection through Cecil. Nothing is executed, and no Unity
    runtime is needed.

    Exits 1 if anything is missing.

.PARAMETER Assembly
    A PATCHED Assembly-CSharp.dll.

.PARAMETER SPTPath
    The SPT install, used to find Cecil and the other Managed assemblies.

.EXAMPLE
    scripts\test-gametypes.ps1 -Assembly C:\tmp\managed415\Assembly-CSharp.dll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Assembly,
    [string]$SPTPath = "C:\HUH"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Assembly)) { throw "no assembly at $Assembly" }

$managed = Join-Path $SPTPath 'EscapeFromTarkov_Data\Managed'
$cecil = Join-Path $SPTPath 'SPT_Runtime\Mono.Cecil.dll'
if (-not (Test-Path $cecil)) { throw "no Mono.Cecil.dll at $cecil" }

Add-Type -Path $cecil

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managed)
$parameters = New-Object Mono.Cecil.ReaderParameters
$parameters.AssemblyResolver = $resolver

# The patched assembly first, then the rest of Managed: a few of the names GameTypes asks for
# live in Comfort.dll rather than Assembly-CSharp, and AccessTools does not care which.
$modules = @([Mono.Cecil.AssemblyDefinition]::ReadAssembly($Assembly, $parameters).MainModule)
foreach ($name in @('Comfort.dll', 'Comfort.Unity.dll')) {
    $path = Join-Path $managed $name
    if (Test-Path $path) {
        $modules += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path, $parameters).MainModule
    }
}

$script:pass = 0
$script:fail = 0

function Find-GameType([string]$name) {
    # AccessTools spells a nested type Outer+Inner; Cecil spells it Outer/Inner.
    $cecilName = $name.Replace('+', '/')
    foreach ($module in $modules) {
        foreach ($type in $module.GetTypes()) {
            if ($type.FullName -eq $cecilName) { return $type }
        }
    }
    return $null
}

function Test-Member($type, [string]$kind, [string]$name) {
    $found = $false
    switch ($kind) {
        'field'  { $found = @($type.Fields     | Where-Object { $_.Name -eq $name }).Count -gt 0 }
        'method' { $found = @($type.Methods    | Where-Object { $_.Name -eq $name }).Count -gt 0 }
        'prop'   { $found = @($type.Properties | Where-Object { $_.Name -eq $name }).Count -gt 0 }
    }

    # A static member declared on a base class is still reachable with FlattenHierarchy, which
    # is how GameTypes reads Instance and Instantiated off MonoBehaviourSingleton.
    if (-not $found -and $type.BaseType -ne $null) {
        $base = Find-GameType $type.BaseType.FullName.Split('<')[0]
        if ($base -ne $null -and $base.FullName -ne $type.FullName) {
            return Test-Member $base $kind $name
        }
    }

    return $found
}

function Check([string]$typeName, [string]$kind, [string]$member, [string]$note = '') {
    $type = Find-GameType $typeName
    $label = if ($member) { "$typeName :: $member" } else { $typeName }
    if ($note) { $label = "$label   ($note)" }

    if ($type -eq $null) {
        Write-Host "  FAIL  $label  -- type not found" -ForegroundColor Red
        $script:fail++
        return
    }

    if (-not $member) {
        Write-Host "  PASS  $label"
        $script:pass++
        return
    }

    if (Test-Member $type $kind $member) {
        Write-Host "  PASS  $label"
        $script:pass++
    }
    else {
        Write-Host "  FAIL  $label  -- $kind not found" -ForegroundColor Red
        $script:fail++
    }
}

Write-Host "`n=== banners ==="
Check 'EFT.UI.Matchmaker.MatchmakerBannersPanel' 'method' 'Show'
Check 'EFT.UI.Matchmaker.MatchmakerBannersPanel' 'method' 'TryCreateBanner'
Check 'EFT.UI.Matchmaker.MatchmakerBannersPanel' 'method' 'CreateBanner'
Check 'EFT.UI.Matchmaker.MatchmakerBannersPanel' 'method' 'Close'
Check 'EFT.UI.Matchmaker.MatchmakerBannersPanel' 'field'  '_locationBanners'
Check 'EFT.UI.Matchmaker.BannerWithToggle'       'field'  'Banner'
Check 'EFT.UI.Matchmaker.MatchmakerBanner'       'field'  '_bannerImage'
Check 'EFT.UI.Matchmaker.MatchmakerBanner'       'field'  '_bannerCanvasGroup'
Check 'EFT.UI.Matchmaker.MatchmakerBanner'       'field'  'BannerName'
Check 'EFT.UI.Matchmaker.MatchmakerBanner'       'field'  'BannerDescription'

Write-Host "`n=== location and intel ==="
Check 'JsonType.LocationSettings+Location' 'field' 'Id'
Check 'JsonType.LocationSettings+Location' 'field' '_Id'
Check 'JsonType.LocationSettings+Location' 'field' 'Name'
Check 'JsonType.LocationSettings+Location' 'field' 'EscapeTimeLimit'
Check 'JsonType.LocationSettings+Location' 'field' 'AveragePlayTime'
Check 'JsonType.LocationSettings+Location' 'field' 'AveragePlayerLevel'
Check 'JsonType.LocationSettings+Location' 'field' 'BossLocationSpawn'
Check 'JsonType.LocationSettings+Location' 'field' 'exits'
Check 'BossLocationSpawn'                  'field' 'BossName'
Check 'BossLocationSpawn'                  'field' 'BossChance'
Check 'JsonType.BackendExitTriggerSettings' 'field' 'Name'
Check 'JsonType.BackendExitTriggerSettings' 'field' 'Chance'
Check 'JsonType.BackendExitTriggerSettings' 'field' 'PassageRequirement'
Check 'EFT.LocalizationManager' 'prop'   'Instance'
Check 'EFT.LocalizationManager' 'prop'   'Culture'
Check 'EFT.LocalizationManager' 'method' 'UpdateLocale'
Check 'EFT.LocalizationManager' 'method' 'LocalizedValue'
Check 'EFT.LocalizationManager' 'method' 'TryGetLocalization'
Check 'EFT.Locale' '' ''
Check 'EFT.Profile'                'field' 'QuestsData'
Check 'EFT.Quests.QuestDataClass'  'field' 'Status'
Check 'EFT.Quests.QuestDataClass'  'field' 'Template'
Check 'EFT.Quests.QuestTemplate'   'field' '_locationId'
Check 'EFT.Quests.QuestTemplate'   'field' '_templateId'

Write-Host "`n=== raid screens ==="
Check 'EFT.UI.Matchmaker.MatchmakerTimeHasCome' 'method' 'Show'
Check 'EFT.UI.Matchmaker.MatchmakerTimeHasCome' 'method' 'ChangeStatus'
Check 'EFT.UI.Matchmaker.MatchmakerTimeHasCome' 'method' 'AbortMatching'
Check 'EFT.UI.Matchmaker.MatchmakerTimeHasCome' 'method' 'ShowPlayerModel'
Check 'EFT.UI.Matchmaker.MatchmakerTimeHasCome' 'field'  '_playerModelView'
Check 'EFT.UI.Matchmaker.MatchmakerTimeHasCome' 'field'  '_bannersPanel'
Check 'EFT.UI.Matchmaker.MatchmakerOfflineRaidScreen' 'method' 'Show'
Check 'EFT.RaidSettings' 'prop'   'SelectedLocation'
Check 'EFT.GameWorld'    'method' 'OnGameStarted'

Write-Host "`n=== environment, and putting it back ==="
Check 'EFT.UI.EnvironmentUI' 'prop'   'Instance'      'via MonoBehaviourSingleton'
Check 'EFT.UI.EnvironmentUI' 'prop'   'Instantiated'  'via MonoBehaviourSingleton'
Check 'EFT.UI.EnvironmentUI' 'method' 'SetEnvironmentAsync'
Check 'EFT.UI.EnvironmentUI' 'method' 'ShowEnvironment'
Check 'EFT.UI.EnvironmentUI' 'method' 'EnableOverlay'
Check 'EFT.UI.MenuScreen'                'method' 'Awake'   'the main menu instance is caught here'
Check 'EFT.UI.MenuScreen'                'method' 'Show'    'the end of the wait after an abort'
# The wheel bottom-right after an abort. Both candidates, because the trace names which one turns.
Check 'EFT.UI.OperationQueueIndicator'    'field'  '_loader' 'the spinner beside the deploy screen'
Check 'EFT.UI.PreloaderUI'               'field'  '_loader' 'the other candidate spinner'
Check 'EFT.UI.EnvironmentUI' 'field'  '_environments'
Check 'EFT.UI.EnvironmentUI' 'field'  '_currentEnvironment'
Check 'EFT.UI.EnvironmentUI' 'field'  '_lastVisibleStateEnvironment'
Check 'EFT.UI.EnvironmentUI' 'field'  '_currentEnvironmentUiType' 'the restore hinges on this'
Check 'EFT.UI.EnvironmentUI+EnvironmentData' 'field' 'Type'
Check 'EFT.EEnvironmentUIType' '' ''
Check 'EFT.CustomizationSolver' 'method' 'GetAvailableEnvironmentUIs'
Check 'EFT.Customization.CustomizationEnvironmentUI' '' ''
Check 'Comfort.Common.Singleton`1' 'prop' 'Instance'

Write-Host "`n=== scene depth ==="
Check 'EFT.UI.EnvironmentUIRoot' 'field' 'CameraContainer'  'the parallax'
Check 'EFT.UI.EnvironmentUIRoot' 'field' 'MainScreenLights' 'the light wander'
Check 'EFT.UI.PlayerModelView'   'prop'  'ModelPlayerPoser'
Check 'MenuPlayerPoser'          'field' 'BottomShadow'     'the PMC ground contact'
Check 'MenuPlayerPoser'          'prop'  'Patrol'           'write-only, hence opt-in'

Write-Host "`n=== the staging area ==="
Check 'EFT.UI.EnvironmentUIRoot' 'field'  'MainScreenObjects' 'the menu furniture that steps aside'
Check 'LayersMaskController'     'field'  'WeaponPreview'     'the PMC layer, so it can be lit alone'
Check 'EFT.UI.Matchmaker.MatchmakerTimeHasCome' 'field' '_subCaption' 'borrowed for intel'
Check 'EFT.UI.Matchmaker.MatchmakerTimeHasCome' 'field' '_deployingText' 'the one the game writes'
# Load(..., Vector3 position, Transform parent, int layer, ...) is where the PMC's compositing is
# decided: PlayerModelView.Show passes its own transform and LayersMaskController.WeaponPreview,
# which is why the character is a UI preview and not an object in the backdrop scene.
Check 'EFT.UI.PlayerModelLoader' 'method' 'Load'              'carries parent + layer'
Check 'EFT.UI.PlayerModelLoader' 'method' 'CreatePlayerBody'  'sets BottomShadow active'

Write-Host "`n=== the game's own banner captions ==="
# TryCreateBanner's IL is CreateBanner(banner.id + " Name", banner.id + " Description", sprite),
# so this one field is the whole of what 1.3.1 recorded as unresolvable.
Check 'JsonType.LocationSettings+Location+LocationBanner' 'field' 'id' 'the caption key'
Check 'JsonType.LocationSettings+Location+LocationBannerLocalization' 'field' 'name'
Check 'JsonType.LocationSettings+Location+LocationBannerLocalization' 'field' 'description'

Write-Host "`n=== this raid's time and weather ==="
Check 'EFT.RaidSettings'           'field' 'TimeAndWeatherSettings' 'settled before deploy'
Check 'EFT.TimeAndWeatherSettings' 'field' 'HourOfDay'
Check 'EFT.TimeAndWeatherSettings' 'field' 'RainType'
Check 'EFT.TimeAndWeatherSettings' 'field' 'FogType'
Check 'EFT.TimeAndWeatherSettings' 'field' 'CloudinessType'
Check 'EFT.TimeAndWeatherSettings' 'field' 'WindType'
Check 'EFT.Weather.ERainType'       '' ''
Check 'EFT.Weather.EFogType'        '' ''
Check 'EFT.Weather.ECloudinessType' '' ''
Check 'EFT.RaidSettings'           'field' 'SelectedDateTime' 'CURR/PAST, the whole time choice'

Write-Host "`n=== the clock the player is shown, and the live weather ==="
# One clock for the whole game: GetCurrentLocationTime takes no location, and PAST is simply
# twelve hours back off it. Resolved at runtime from the session instance's own type, so both
# concrete sessions are checked here rather than the interface.
Check 'EFT.IMatchmakerSession`1'   'prop'    'GetCurrentLocationTime' 'the one clock'
Check 'EFT.IMatchmakerSession`1'   'prop'    'Weather'
Check 'EFT.EftClientBackendSession' 'prop'    'GetCurrentLocationTime'
Check 'EFT.EftClientBackendSession' 'prop'    'Weather'
Check 'EFT.ClientBackEndEmulator+ClientBackendSessionEmulator' 'prop'    'GetCurrentLocationTime'
Check 'EFT.ClientBackEndEmulator+ClientBackendSessionEmulator' 'prop'    'Weather'

# BSG's own misspellings. Cloudness and ScaterringFogDensity are the field names in the game;
# correcting either of them here turns the weather off.
Check 'EFT.Weather.WeatherNode'    'field' 'Cloudness'  'misspelt in the game, match it exactly'
Check 'EFT.Weather.WeatherNode'    'field' 'Rain'
Check 'EFT.Weather.WeatherNode'    'field' 'ScaterringFogDensity' 'misspelt in the game'
Check 'EFT.Weather.WeatherNode'    'field' 'Wind'

# Factory is the one map shown against a fixed pair rather than the clock, and MapGrade hard-codes
# the two times. If these ever stop being constants, the hard-coding is wrong.
Check 'EFT.UI.Matchmaker.LocationConditionsPanel' 'prop'    'FactoryDayTime'  '15:28, hard-coded in MapGrade'
Check 'EFT.UI.Matchmaker.LocationConditionsPanel' 'prop'    'FactoryNightTime' '03:28, hard-coded in MapGrade'

Write-Host ""
if ($script:fail -gt 0) {
    Write-Host "$($script:pass) passed, $($script:fail) FAILED" -ForegroundColor Red
    exit 1
}

Write-Host "$($script:pass) passed" -ForegroundColor Green
exit 0
