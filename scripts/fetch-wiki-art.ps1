<#
.SYNOPSIS
    Downloads real map screenshots from the Escape from Tarkov wiki into the mod's banner folders.

.DESCRIPTION
    Deploy Screen ships no art -- it shows the player's own pictures, and an install with none
    falls back to the game's stock banners. This fills the folders from the wiki so a fresh
    install has something real behind the character on the first raid.

    It takes screenshots, not maps: the wiki's largest images for a location are cartographic
    2D maps ten thousand pixels wide, which are the wrong thing entirely behind a PMC. The
    filter keeps landscape images at screen-like proportions and prefers the official
    "Showcase" set, which is BSG's own promotional capture of each location.

    Files are named so the mod's caption reader makes something sensible of them, and nothing
    already in a folder is overwritten.

.PARAMETER SPTPath
    The SPT install whose BepInEx\plugins\DeployScreen\banners folders get the art.

.PARAMETER PerMap
    How many pictures per map. The mod cycles as many as the map's banner count.

.PARAMETER Maps
    Limit to these location ids, e.g. -Maps shoreline,woods. Default is all of them.

.EXAMPLE
    scripts\fetch-wiki-art.ps1 -SPTPath "H:\SPT4.1.X"
    scripts\fetch-wiki-art.ps1 -SPTPath "H:\SPT4.1.X" -Maps shoreline -PerMap 8
#>
[CmdletBinding()]
param(
    [string]$SPTPath = "C:\HUH",
    [int]$PerMap = 5,
    [string[]]$Maps,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Folder name is the location id the game reports, which is what BannerArt.For looks up.
$locations = [ordered]@{
    'bigmap'       = 'Customs'
    'factory4_day' = 'Factory'
    'interchange'  = 'Interchange'
    'laboratory'   = 'The Lab'
    'lighthouse'   = 'Lighthouse'
    'rezervbase'   = 'Reserve'
    'sandbox'      = 'Ground Zero'
    'shoreline'    = 'Shoreline'
    'tarkovstreets'= 'Streets of Tarkov'
    'woods'        = 'Woods'
}

$banners = Join-Path $SPTPath 'BepInEx\plugins\DeployScreen\banners'
if (-not (Test-Path $banners)) { throw "no banners folder at $banners -- is the mod installed?" }

$api = 'https://escapefromtarkov.fandom.com/api.php'
$agent = 'DeployScreen-art-fetch/1.0 (SPT mod; one-off, a few files per map)'

# Anything that is not a photograph of the place: icons, cartography, quest overlays, people.
$reject = 'icon|2dmap|2d map|quest|portrait|\bkey\b|keycard|loot|spawn|extract map|marker|logo|banner '

function Wanted($title, $w, $h) {
    if ($w -lt 1600) { return $false }
    if ($h -lt 1) { return $false }
    $aspect = $w / $h
    if ($aspect -lt 1.55 -or $aspect -gt 2.6) { return $false }
    if ($title -match $reject) { return $false }
    return $true
}

function CleanName($title) {
    $name = $title -replace '^File:', ''
    $name = [IO.Path]::GetFileNameWithoutExtension($name)
    # The caption the mod shows comes from the file name, so make it read like one.
    # -creplace, not -replace: PowerShell's -replace is case-insensitive by default, which
    # makes [a-z][A-Z] match every pair of letters and space the whole name out.
    $name = $name -creplace '([a-z])([A-Z])', '$1 $2'
    $name = $name -replace '[_-]+', ' '
    $name = ($name -replace '\s+', ' ').Trim()
    return $name
}

$wanted = if ($Maps) { $Maps } else { $locations.Keys }
$grabbed = 0
$skipped = 0

foreach ($id in $wanted) {
    if (-not $locations.Contains($id)) { Write-Host "  ??    no wiki page mapped for '$id'" -ForegroundColor Yellow; continue }

    $page = $locations[$id]
    Write-Host ""
    Write-Host "=== $page  ->  banners\$id ===" -ForegroundColor Cyan

    $query = "$api`?action=query&generator=images&titles=$([Uri]::EscapeDataString($page))" +
             "&gimlimit=500&prop=imageinfo&iiprop=url|size&format=json"

    try { $response = Invoke-RestMethod -Uri $query -Headers @{ 'User-Agent' = $agent } -TimeoutSec 40 }
    catch { Write-Host "  FAIL  could not ask the wiki: $($_.Exception.Message)" -ForegroundColor Red; continue }

    if (-not $response.query) { Write-Host "  none  the wiki returned no images" -ForegroundColor Yellow; continue }

    $pages = $response.query.pages
    $candidates = foreach ($key in $pages.PSObject.Properties.Name) {
        $entry = $pages.$key
        $info = $entry.imageinfo | Select-Object -First 1
        if (-not $info) { continue }
        if (-not (Wanted $entry.title $info.width $info.height)) { continue }

        [pscustomobject]@{
            Title    = $entry.title
            Url      = $info.url
            Width    = [int]$info.width
            Height   = [int]$info.height
            Showcase = if ($entry.title -match 'showcase') { 1 } else { 0 }
        }
    }

    if (-not $candidates) { Write-Host "  none  nothing on that page looks like a screenshot" -ForegroundColor Yellow; continue }

    # BSG's own showcase captures first, then simply the biggest.
    $picked = $candidates | Sort-Object -Property @{ Expression = 'Showcase'; Descending = $true },
                                                  @{ Expression = 'Width'; Descending = $true } |
              Select-Object -First $PerMap

    $folder = Join-Path $banners $id
    if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }

    $n = 0
    foreach ($one in $picked) {
        $n++
        $extension = [IO.Path]::GetExtension(($one.Url -split '\?')[0])
        if ($extension -notmatch '^\.(png|jpg|jpeg)$') { $extension = '.png' }

        $file = Join-Path $folder ('{0:00} - {1}{2}' -f $n, (CleanName $one.Title), $extension)

        if ((Test-Path $file) -and -not $Force) {
            Write-Host ("  keep  {0}  (already there)" -f (Split-Path $file -Leaf))
            $skipped++
            continue
        }

        # Fandom's CDN serves WebP from a .png URL -- content negotiation, and it ignores an
        # Accept header asking for PNG. Unity's ImageConversion.LoadImage reads PNG and JPEG
        # only, so those files arrive looking right, sitting in the right folder, and load as
        # nothing. 'format=original' is what makes it hand over the file the page actually has.
        $url = $one.Url + $(if ($one.Url -match '\?') { '&format=original' } else { '?format=original' })

        try {
            Invoke-WebRequest -Uri $url -Headers @{ 'User-Agent' = $agent } -OutFile $file -TimeoutSec 120
            Write-Host ("  got   {0}  {1}x{2}" -f (Split-Path $file -Leaf), $one.Width, $one.Height) -ForegroundColor Green
            $grabbed++
        }
        catch {
            Write-Host "  FAIL  $($one.Title): $($_.Exception.Message)" -ForegroundColor Red
        }

        Start-Sleep -Milliseconds 300
    }
}

Write-Host ""
Write-Host "$grabbed downloaded, $skipped already present, into $banners" -ForegroundColor Green
