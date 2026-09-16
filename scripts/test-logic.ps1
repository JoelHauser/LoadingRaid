<#
.SYNOPSIS
    Checks the parts of the mod that do not need the game, against real files.

.DESCRIPTION
    Loads the built DeployScreen.Client.dll by reflection and exercises:

        ImageHeader   pixel sizes read from PNG and JPEG headers, against System.Drawing,
                      on every stock banner in the install and on generated images --
                      including a JPEG with 130 KB of metadata ahead of its frame header
        BannerArt     size tags ("01 - Dorms@4k"), file-name captions, crop rectangles
        BannerImage   choosing between sizes of one picture for a measured banner

    What it cannot check is anything that needs a live screen: measuring a banner, the
    log line, the driver. Those are only testable in game.

    Build first. The DLL is copied to a temp folder before it is loaded, so this never
    holds a lock on the build output. Unity types (Rect, Mathf) are resolved from the SPT
    install's Managed folder; only their plain managed parts are used.

    Exits 1 if anything fails.

.PARAMETER SPTPath
    The SPT install whose Managed assemblies and stock banners are used.

.EXAMPLE
    scripts\test-logic.ps1
    scripts\test-logic.ps1 -SPTPath "C:\path\to\SPT"
#>
[CmdletBinding()]
param(
    [string]$SPTPath = "C:\HUH"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$dll = Join-Path $root 'src\DeployScreen.Client\bin\Release\DeployScreen.Client.dll'
if (-not (Test-Path $dll)) { throw "no build at $dll -- run scripts\pack.ps1 first" }

$work = Join-Path ([IO.Path]::GetTempPath()) 'DeployScreen-test'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Path $work | Out-Null
Copy-Item $dll $work

$script:managed = Join-Path $SPTPath 'EscapeFromTarkov_Data\Managed'
$script:bepinex = Join-Path $SPTPath 'BepInEx\core'

# Kept in a variable so it can be taken off again at the end. Left attached, it is still live
# while PowerShell tears the runspace down, and a resolve that arrives then re-enters it until
# the stack runs out -- which is where the StackOverflowException after "N passed" came from.
$script:resolver = [ResolveEventHandler] {
    param($sender, $e)
    $name = (New-Object Reflection.AssemblyName($e.Name)).Name
    foreach ($dir in @($script:managed, $script:bepinex)) {
        $path = Join-Path $dir "$name.dll"
        if (Test-Path $path) { return [Reflection.Assembly]::LoadFrom($path) }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($script:resolver)

Add-Type -AssemblyName System.Drawing

$asm = [Reflection.Assembly]::LoadFrom((Join-Path $work 'DeployScreen.Client.dll'))
$static = [Reflection.BindingFlags]'Static,NonPublic,Public'
$instance = [Reflection.BindingFlags]'Instance,NonPublic,Public'
function TypeOf($name) { return $asm.GetType("DeployScreen.Client.$name", $true) }

$script:pass = 0
$script:fail = 0
function Check($label, $ok, $detail) {
    if ($ok) { $script:pass++; Write-Host "  PASS  $label" }
    else { $script:fail++; Write-Host "  FAIL  $label  -- $detail" -ForegroundColor Red }
}

# ------------------------------------------------------------ image headers

$tryRead = (TypeOf 'ImageHeader').GetMethod('TryReadSize', $static)
function HeaderSize($path) {
    $a = New-Object object[] 3
    # [string]: Join-Path hands back a PSObject, which reflection will not unwrap.
    $a[0] = [string]$path; $a[1] = 0; $a[2] = 0
    $ok = $tryRead.Invoke($null, $a)
    return @($ok, $a[1], $a[2])
}

Write-Host "=== sizes read from headers ===" -ForegroundColor Cyan

$stockFolder = Join-Path $SPTPath 'SPT_Runtime\SPT_Data\images\banners'
if (Test-Path $stockFolder) {
    $files = Get-ChildItem $stockFolder -File
    $mismatches = 0
    foreach ($f in $files) {
        $img = [System.Drawing.Image]::FromFile($f.FullName)
        $w = $img.Width; $h = $img.Height
        $img.Dispose()
        $r = HeaderSize $f.FullName
        if (-not ($r[0] -and $r[1] -eq $w -and $r[2] -eq $h)) {
            $mismatches++
            Write-Host "    $($f.Name): header $($r[1])x$($r[2]), actual ${w}x${h}"
        }
    }
    Check "every stock banner ($($files.Count)) matches System.Drawing" ($mismatches -eq 0) "$mismatches wrong"
}
else {
    Write-Host "  SKIP  no stock banners under $stockFolder"
}

function MakeImage($path, $w, $h, $format) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::DarkSlateGray)
    $g.Dispose()
    $bmp.Save($path, $format)
    $bmp.Dispose()
}

foreach ($c in @(
        @('ultrawide.png', 3440, 1440, [System.Drawing.Imaging.ImageFormat]::Png),
        @('tall.jpg', 1200, 1600, [System.Drawing.Imaging.ImageFormat]::Jpeg),
        @('4k.jpg', 3060, 1840, [System.Drawing.Imaging.ImageFormat]::Jpeg))) {
    $path = Join-Path $work $c[0]
    MakeImage $path $c[1] $c[2] $c[3]
    $r = HeaderSize $path
    Check "$($c[0]) reads as $($c[1])x$($c[2])" ($r[0] -and $r[1] -eq $c[1] -and $r[2] -eq $c[2]) "got $($r[1])x$($r[2])"
}

# The frame header pushed behind 130 KB of metadata, as screenshot tools and cameras write it.
$source = [IO.File]::ReadAllBytes((Join-Path $work '4k.jpg'))
$stream = New-Object IO.MemoryStream
$stream.Write($source, 0, 2)
foreach ($i in 1..2) {
    $payload = 65000
    $length = $payload + 2
    $stream.WriteByte(0xFF); $stream.WriteByte(0xE1)
    $stream.WriteByte([byte](($length -shr 8) -band 0xFF)); $stream.WriteByte([byte]($length -band 0xFF))
    $stream.Write((New-Object byte[] $payload), 0, $payload)
}
$stream.Write($source, 2, $source.Length - 2)
$metadata = Join-Path $work 'metadata.jpg'
[IO.File]::WriteAllBytes($metadata, $stream.ToArray())

$valid = $false
try { $img = [System.Drawing.Image]::FromFile($metadata); $valid = ($img.Width -eq 3060); $img.Dispose() } catch { }
Check "a JPEG with 130 KB of metadata is still a valid image" $valid 'System.Drawing could not open it'
$r = HeaderSize $metadata
Check "and its size is still read as 3060x1840" ($r[0] -and $r[1] -eq 3060 -and $r[2] -eq 1840) "got $($r[1])x$($r[2])"

$fake = Join-Path $work 'notes.png'
[IO.File]::WriteAllText($fake, 'not really a png')
$r = HeaderSize $fake
Check "a text file named .png is rejected" (-not $r[0]) "read as $($r[1])x$($r[2])"

# ---------------------------------------------------- names and captions

$art = TypeOf 'BannerArt'

Write-Host "=== size tags ===" -ForegroundColor Cyan
$pictureName = $art.GetMethod('PictureName', $static)
foreach ($c in @(
        @('01 - Dorms@4k', '01 - Dorms'),
        @('01 - Dorms @ 1440p', '01 - Dorms'),
        @('Dorms', 'Dorms'),
        @('@4k', '@4k'),
        @('a@b@c', 'a@b'))) {
    $got = $pictureName.Invoke($null, [object[]]@($c[0]))
    Check "'$($c[0])' is the picture '$($c[1])'" ($got -eq $c[1]) "got '$got'"
}

Write-Host "=== captions from file names ===" -ForegroundColor Cyan
$captions = $art.GetMethod('CaptionsFrom', $static)
foreach ($c in @(
        @('01 - Dorms; Three storeys, two keys', 'Dorms', 'Three storeys, two keys'),
        @('3. Gas station', 'Gas station', ''),
        @('24 Hour Shift', '24 Hour Shift', ''))) {
    $a = New-Object object[] 3
    $a[0] = $c[0]
    [void]$captions.Invoke($null, $a)
    Check "'$($c[0])'" ($a[1] -eq $c[1] -and $a[2] -eq $c[2]) "heading '$($a[1])', line '$($a[2])'"
}

# ------------------------------------------------------------------ cropping

Write-Host "=== crop to the banner shape (765:460) ===" -ForegroundColor Cyan
try {
    $cover = $art.GetMethod('CoverRect', $static)
    $stock = [single](765 / 460)
    foreach ($c in @(
            @(765, 460, 0, 0, 765, 460, 'stock size is left whole'),
            @(3440, 1440, 522, 0, 2395, 1440, '21:9 screenshot loses its sides'),
            @(1920, 1080, 62, 0, 1796, 1080, '16:9 screenshot loses a little of its sides'),
            @(2560, 1600, 0, 30, 2560, 1539, '16:10 screenshot loses a sliver top and bottom'),
            @(1200, 1600, 0, 439, 1200, 722, 'tall image loses top and bottom'),
            @(0, 0, 0, 0, 0, 0, 'unreadable size gives an empty rect'))) {
        $rect = $cover.Invoke($null, [object[]]@([int]$c[0], [int]$c[1], $stock))
        $got = "$($rect.x),$($rect.y),$($rect.width),$($rect.height)"
        $want = "$($c[2]),$($c[3]),$($c[4]),$($c[5])"
        $inside = ($rect.x + $rect.width -le $c[0]) -and ($rect.y + $rect.height -le $c[1])
        Check "$($c[6]) ($($c[0])x$($c[1]) -> $want)" ($got -eq $want -and $inside) "got $got, inside=$inside"
    }
}
catch { Check 'cropping checks ran' $false $_.Exception.GetBaseException().Message }

# ------------------------------------------------------- choosing a size

Write-Host "=== choosing between sizes of one picture ===" -ForegroundColor Cyan
try {
    $imageType = TypeOf 'BannerImage'
    $variantType = TypeOf 'BannerVariant'
    $fitType = TypeOf 'BannerFit'

    $image = [Activator]::CreateInstance($imageType, $true)
    $variants = $imageType.GetField('Variants', $instance).GetValue($image)
    foreach ($s in @(@(765, 460, 'stock'), @(1530, 920, '1440p'), @(3060, 1840, '4k'), @(3440, 1440, 'ultrawide screenshot'))) {
        $v = [Activator]::CreateInstance($variantType, $true)
        $variantType.GetField('Width', $instance).SetValue($v, [int]$s[0])
        $variantType.GetField('Height', $instance).SetValue($v, [int]$s[1])
        $variantType.GetField('Path', $instance).SetValue($v, $s[2])
        [void]$variants.Add($v)
    }

    $choose = $imageType.GetMethod('Choose', $instance)
    function Pick($w, $h, $measured) {
        $fit = [Activator]::CreateInstance($fitType)
        $fitType.GetField('Width', $instance).SetValue($fit, [int]$w)
        $fitType.GetField('Height', $instance).SetValue($fit, [int]$h)
        $chosen = $choose.Invoke($image, [object[]]@($fit, [bool]$measured))
        return $variantType.GetField('Path', $instance).GetValue($chosen)
    }

    foreach ($c in @(
            @(0, 0, $false, '4k', 'before any measurement: the largest'),
            @(765, 460, $true, 'stock', '765x460 banner: the smallest that is sharp'),
            @(1530, 920, $true, '1440p', '1530x920 banner: the 1440p file, not the bigger ones'),
            @(1600, 962, $true, 'ultrawide screenshot', '1600x962 banner: 1440p is too small, the cropped screenshot is the smallest sharp'),
            @(3200, 1924, $true, '4k', 'nothing is sharp enough: the largest'),
            @(1600, 1000, $true, 'ultrawide screenshot', '16:10-shaped banner: sizes compared after cropping to that shape'))) {
        $got = Pick $c[0] $c[1] $c[2]
        Check $c[4] ($got -eq $c[3]) "chose '$got', expected '$($c[3])'"
    }
}
catch { Check 'size-choice checks ran' $false $_.Exception.GetBaseException().Message }

# ------------------------------------------- measurements kept between sessions

Write-Host "=== remembered banner sizes ===" -ForegroundColor Cyan
try {
    $fitType = TypeOf 'BannerFit'
    $screenFit = TypeOf 'ScreenFit'
    $remember = $screenFit.GetMethod('Remember', $static)
    $byScreen = $screenFit.GetField('ByScreen', $static)
    $fitWidth = $fitType.GetField('Width', $instance)
    $fitHeight = $fitType.GetField('Height', $instance)

    function Remembered($saved) {
        # [void]: Remember returns void, but Invoke still puts a $null on the pipeline, and the
        # function would then hand back two objects rather than the table.
        [void]$remember.Invoke($null, [object[]]@([string]$saved))
        $map = $byScreen.GetValue($null)
        $out = @{}
        foreach ($k in $map.Keys) {
            $fit = $map[$k]
            $out[$k] = "$($fitWidth.GetValue($fit))x$($fitHeight.GetValue($fit))"
        }
        return $out
    }

    $r = Remembered '1920x1080=765x460;3840x2160=1530x920'
    Check 'two screen sizes read back' ($r.Count -eq 2 -and $r['1920x1080'] -eq '765x460' -and $r['3840x2160'] -eq '1530x920') "got $($r.Count): $($r.Keys -join ',')"

    $r = Remembered ''
    Check 'an empty setting leaves nothing remembered' ($r.Count -eq 0) "got $($r.Count)"

    # Anything the mod did not write itself, or that a player has edited by hand.
    $r = Remembered 'rubbish;=765x460;1920x1080=;1920x1080=0x0;1920x1080=nonsense;2560x1440=1020x613'
    Check 'malformed entries are dropped, good ones kept' ($r.Count -eq 1 -and $r['2560x1440'] -eq '1020x613') "got $($r.Count): $($r.Keys -join ',')"

    # A round trip has to survive, or a session would lose what the last one measured.
    $r = Remembered '3440x1440=1371x824'
    Check 'an ultrawide measurement survives the round trip' ($r['3440x1440'] -eq '1371x824') "got '$($r['3440x1440'])'"

    [void]$remember.Invoke($null, [object[]]@([string]''))
}
catch { Check 'remembered-size checks ran' $false $_.Exception.GetBaseException().Message }

[AppDomain]::CurrentDomain.remove_AssemblyResolve($script:resolver)

Write-Host ""
if ($script:fail -gt 0) {
    Write-Host "$script:pass passed, $script:fail failed" -ForegroundColor Red
    exit 1
}
Write-Host "$script:pass passed" -ForegroundColor Green
exit 0
