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

# ------------------------------------------------------- config keys BepInEx accepts
#
# BepInEx's ConfigDefinition rejects  =  newline  tab  \  "  '  [  ]  in a section or key
# name, and it throws while the plugin's Awake is still running. Nothing catches that, and
# with WriteUnityLog off -- the default on an SPT install -- the stack trace does not even
# reach LogOutput.log: the plugin loads, installs no patches, logs nothing and the game just
# looks unmodded. 1.6.0 shipped "Follow the raid's weather" and was inert on every launch.
#
# Source text rather than reflection on purpose: the keys are literals inside Awake, and
# reading them back off a built assembly would mean running the very code that throws.

Write-Host ""
Write-Host "=== config keys are ones BepInEx will accept ===" -ForegroundColor Cyan

$badChars = @('=', "`n", "`t", '\', '"', "'", '[', ']')

function ConfigNameIsLegal($name) {
    foreach ($c in $badChars) { if ($name.Contains($c)) { return $false } }
    return $true
}

# The check has to fail on the string that got us here, or it is not checking anything.
Check 'the guard rejects an apostrophe' (-not (ConfigNameIsLegal "Follow the raid's weather")) 'it did not'
Check 'the guard accepts a plain key' (ConfigNameIsLegal 'Follow the raid weather') 'it did not'

$binds = 0
$illegal = @()
foreach ($file in Get-ChildItem -Path (Join-Path $root 'src\DeployScreen.Client') -Filter *.cs) {
    $text = Get-Content -Raw -Path $file.FullName
    $pattern = 'Config\.Bind[^(]*\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"'
    foreach ($m in [regex]::Matches($text, $pattern)) {
        $binds++
        $section = $m.Groups[1].Value
        $key = $m.Groups[2].Value
        if (-not (ConfigNameIsLegal $section)) { $illegal += "$($file.Name): section '$section'" }
        if (-not (ConfigNameIsLegal $key)) { $illegal += "$($file.Name): key '$key'" }
    }
}

Check 'the binds were found at all' ($binds -ge 20) "only $binds found -- has Config.Bind been reshaped?"
Check "all $binds config names are legal" ($illegal.Count -eq 0) ($illegal -join '; ')

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

# --------------------------------------------------------- aspect ratios

# Every shape of monitor a player might have, plus the awkward ones. 32:9 and portrait are
# not paranoia: super-ultrawides exist, and a rotated monitor is a real configuration.
$aspects = @(
    @{ n = '5:4  (1280x1024)';   a = 1280 / 1024 }
    @{ n = '4:3  (1600x1200)';   a = 1600 / 1200 }
    @{ n = '3:2  (2256x1504)';   a = 2256 / 1504 }
    @{ n = '16:10 (2560x1600)';  a = 2560 / 1600 }
    @{ n = '16:9 (1920x1080)';   a = 1920 / 1080 }
    @{ n = '21:9 (3440x1440)';   a = 3440 / 1440 }
    @{ n = '32:9 (5120x1440)';   a = 5120 / 1440 }
    @{ n = '48:9 (7680x1440)';   a = 7680 / 1440 }
    @{ n = '1:1  (1080x1080)';   a = 1.0 }
    @{ n = '9:16 portrait';      a = 1080 / 1920 }
)

Write-Host "=== cover-cropping at every aspect ratio ===" -ForegroundColor Cyan

try {
    $coverRect = (TypeOf 'BannerArt').GetMethod('CoverRect', $static)

    # Sources a player might actually drop in: their own screenshots, at every common shape.
    $sources = @(
        @(1920, 1080), @(2560, 1440), @(3840, 2160), @(3440, 1440),
        @(5120, 1440), @(2560, 1600), @(1600, 1200), @(1080, 1920), @(765, 460), @(1, 1)
    )

    $bad = @()
    $checked = 0
    foreach ($shape in $aspects) {
        foreach ($src in $sources) {
            $w = $src[0]; $h = $src[1]
            $r = $coverRect.Invoke($null, [object[]]@([int]$w, [int]$h, [float]$shape.a))
            $checked++

            # Inside the image: Sprite.Create refuses a rect that strays past the texture at all.
            if ($r.x -lt 0 -or $r.y -lt 0 -or ($r.x + $r.width) -gt $w -or ($r.y + $r.height) -gt $h) {
                $bad += "$($shape.n) from ${w}x${h}: rect escapes the image"
                continue
            }
            if ($r.width -lt 1 -or $r.height -lt 1) { $bad += "$($shape.n) from ${w}x${h}: empty rect"; continue }

            # Cover, not contain: one axis must still be the full source, or nothing was gained.
            if ($r.width -ne $w -and $r.height -ne $h) {
                $bad += "$($shape.n) from ${w}x${h}: cropped on both axes"
                continue
            }

            # And the result has the shape that was asked for, to within a pixel of rounding.
            $got = $r.width / $r.height
            $tolerance = [Math]::Max(0.02, 2.0 / [Math]::Min($r.width, $r.height))
            if ([Math]::Abs($got - $shape.a) / $shape.a -gt $tolerance) {
                $bad += "$($shape.n) from ${w}x${h}: got $([Math]::Round($got,3)):1"
            }
        }
    }
    Check "crops stay inside the image and keep their shape ($checked combinations)" `
        ($bad.Count -eq 0) ($bad | Select-Object -First 4) -join '; '
}
catch { Check 'aspect-ratio crop checks ran' $false $_.Exception.GetBaseException().Message }

Write-Host "=== the staging planes cover the frame at every aspect ratio ===" -ForegroundColor Cyan

# StagingArea.RequiredOverscan is a closed-form bound on how much bigger than the frustum each
# art plane has to be. This checks that bound against the motion it is meant to cover, by
# actually running SceneDepth's drift sines and asking, at each sampled moment, how much of the
# plane the camera can see. A plane that is too small shows its own edge, and on a 4:3 or a
# portrait screen a sideways drift is a far larger fraction of the frame than it is on 16:9 --
# which is exactly why a single fixed number cannot be right.
try {
    $required = (TypeOf 'StagingArea').GetMethod('RequiredOverscan', $static)

    $worst = @()
    $combos = 0
    foreach ($shape in $aspects) {
        foreach ($drift in @(0.0, 0.05, 0.2, 0.5)) {
            foreach ($dist in @(0.5, 3.0, 20.0)) {
                foreach ($fov in @(25.0, 50.0, 80.0)) {
                    foreach ($sway in @(0.0, 0.12, 1.5)) {
                        $combos++
                        $k = [float]$required.Invoke($null, [object[]]@(
                            [float]$dist, [float]$fov, [float]$shape.a, [float]$drift, [float]$sway))

                        $halfFov = $fov * 0.5 * [Math]::PI / 180
                        $H = 2.0 * $dist * [Math]::Tan($halfFov)
                        $W = $H * $shape.a

                        # Sample the real motion rather than trusting the amplitudes twice.
                        $needH = 0.0; $needW = 0.0
                        for ($t = 0.0; $t -lt 400.0; $t += 0.37) {
                            $dx = ([Math]::Sin($t*0.081)*0.7 + [Math]::Sin($t*0.143+1.7)*0.3) * $drift
                            $dy = ([Math]::Sin($t*0.063+2.3)*0.6 + [Math]::Sin($t*0.117)*0.4) * $drift * 0.55
                            $dz = [Math]::Sin($t*0.049+0.9) * $drift * 0.8
                            $rx = [Math]::Sin($t*0.055+1.1) * $sway * 0.6
                            $ry = [Math]::Sin($t*0.071) * $sway

                            # Distance from the moved camera to the plane, which sits still.
                            $d = $dist - $dz
                            if ($d -le 0.01) { continue }
                            $visH = 2.0 * $d * [Math]::Tan($halfFov)
                            $visW = $visH * $shape.a

                            $shiftY = [Math]::Abs($dy) + $d * [Math]::Tan($rx * [Math]::PI / 180)
                            $shiftX = [Math]::Abs($dx) + $d * [Math]::Tan($ry * [Math]::PI / 180)

                            $needH = [Math]::Max($needH, ($visH + 2*[Math]::Abs($shiftY)) / $H)
                            $needW = [Math]::Max($needW, ($visW + 2*[Math]::Abs($shiftX)) / $W)
                        }
                        $need = [Math]::Max($needH, $needW)

                        if ($k -lt $need - 0.0005) {
                            $worst += "$($shape.n) drift=$drift dist=$dist fov=$fov sway=$sway needs $([Math]::Round($need,3)) got $([Math]::Round($k,3))"
                        }
                    }
                }
            }
        }
    }
    Check "the computed overscan always covers the drift ($combos combinations)" `
        ($worst.Count -eq 0) (($worst | Select-Object -First 3) -join ' | ')

    # A bound that is always enormous would be "correct" and useless -- it would shrink the art.
    $k169 = [float]$required.Invoke($null, [object[]]@([float]3.0, [float]50.0, [float](16/9), [float]0.05, [float]0.12))
    Check 'the default 16:9 case stays modest' ($k169 -lt 1.12) "got $([Math]::Round($k169,3))"

    # The narrower the frame, the more a sideways drift costs. If this did not hold, the shape
    # is not being taken into account at all.
    $kWide = [float]$required.Invoke($null, [object[]]@([float]3.0, [float]50.0, [float]3.556, [float]0.5, [float]0.5))
    $kNarrow = [float]$required.Invoke($null, [object[]]@([float]3.0, [float]50.0, [float]0.5625, [float]0.5, [float]0.5))
    Check 'a portrait screen needs more overscan than a 32:9 one' ($kNarrow -gt $kWide) `
        "portrait $([Math]::Round($kNarrow,3)) vs ultrawide $([Math]::Round($kWide,3))"

    # Nothing moving means nothing to hide.
    $kStill = [float]$required.Invoke($null, [object[]]@([float]3.0, [float]50.0, [float](16/9), [float]0.0, [float]0.0))
    Check 'a still camera needs no overscan at all' ([Math]::Abs($kStill - 1.0) -lt 0.001) "got $kStill"

    # Nonsense in, 1.0 out, rather than a NaN that would size a plane to nothing.
    $kBad = [float]$required.Invoke($null, [object[]]@([float]0.0, [float]50.0, [float]1.778, [float]0.05, [float]0.1))
    Check 'a zero distance degrades to 1.0 rather than NaN' `
        (-not [double]::IsNaN($kBad) -and $kBad -eq 1.0) "got $kBad"
}
catch { Check 'staging overscan checks ran' $false $_.Exception.GetBaseException().Message }

# ------------------------------------------------ the raid's own light

# MapGrade.ForRaid bends a map's light to the raid being loaded. It is bounded arithmetic over
# four inputs, so every combination can be checked here -- and it has to be, because a grade
# that blacks the screen out or blows it white is only visible in game, at the worst moment.

Write-Host "=== the light follows the raid, within bounds ===" -ForegroundColor Cyan

try {
    $gradeType = TypeOf 'MapGrade'
    $forRaid = $gradeType.GetMethod('ForRaid', $static)
    $weatherType = $asm.GetType('DeployScreen.Client.MapGrade+Weather', $true)

    # The three weather figures are normalised to 0..1 inside ReadWeather, so the sweep below
    # still reads in the game's own units and is converted here -- rain and fog out of 4, cloud
    # out of 5, which are the enum ranges ERainType, EFogType and ECloudinessType span.
    function NewWeather($hour, $rain, $fog, $cloud) {
        $w = [Activator]::CreateInstance($weatherType)
        # Fields are internal on a struct, so they are set through boxed reflection and the
        # boxed copy is what gets passed back. (Setting them on the unboxed value silently
        # writes to a temporary -- the same shape of trap as the internal-field read earlier.)
        $values = @(
            @('Known',       $true),
            @('Hour',        [float]$hour),
            @('HourOfDay',   [int]$hour),
            @('Rain',        [float]($rain / 4.0)),
            @('Fog',         [float]($fog / 4.0)),
            @('Cloud',       [float]($cloud / 5.0))
        )
        foreach ($pair in $values) {
            $weatherType.GetField($pair[0], $instance).SetValue($w, $pair[1])
        }
        return $w
    }

    $fExp = $asm.GetType('DeployScreen.Client.Grade', $true).GetField('Exposure', $instance)
    $fKey = $asm.GetType('DeployScreen.Client.Grade', $true).GetField('Key', $instance)
    $fRim = $asm.GetType('DeployScreen.Client.Grade', $true).GetField('Rim', $instance)

    function GradeFor($map, $hour, $rain, $fog, $cloud) {
        $w = NewWeather $hour $rain $fog $cloud
        return $forRaid.Invoke($null, [object[]]@([string]$map, $w))
    }

    $maps = @('bigmap','woods','tarkovstreets','laboratory','lighthouse','shoreline')
    $out = @()
    $n = 0
    foreach ($map in $maps) {
        foreach ($hour in 0..23) {
            foreach ($rain in @(0,2,4)) {
                foreach ($fog in @(0,2,4)) {
                    foreach ($cloud in @(0,3,5)) {
                        $n++
                        $g = GradeFor $map $hour $rain $fog $cloud
                        $e = [float]$fExp.GetValue($g)
                        if ([double]::IsNaN($e) -or $e -lt 0.25 -or $e -gt 1.6) {
                            $out += "$map h=$hour r=$rain f=$fog c=$cloud exposure=$e"
                            continue
                        }
                        foreach ($f in @($fKey, $fRim)) {
                            $c = $f.GetValue($g)
                            foreach ($ch in @($c.r, $c.g, $c.b)) {
                                if ([double]::IsNaN($ch) -or $ch -lt 0 -or $ch -gt 1.0001) {
                                    $out += "$map h=$hour r=$rain f=$fog c=$cloud channel=$ch"
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    Check "every time and weather stays in range ($n combinations)" ($out.Count -eq 0) `
        (($out | Select-Object -First 3) -join ' | ')

    # Night must actually be darker than noon, or the whole feature is inert.
    $noon = [float]$fExp.GetValue((GradeFor 'woods' 13 0 0 0))
    $night = [float]$fExp.GetValue((GradeFor 'woods' 2 0 0 0))
    Check 'a night raid is darker than a midday one' ($night -lt $noon * 0.7) `
        "noon $([Math]::Round($noon,3)) vs night $([Math]::Round($night,3))"

    # ...and night should be cooler, since it drifts toward moonlight.
    $noonKey = $fKey.GetValue((GradeFor 'tarkovstreets' 13 0 0 0))
    $nightKey = $fKey.GetValue((GradeFor 'tarkovstreets' 2 0 0 0))
    Check 'a night raid is cooler than a midday one' `
        (($nightKey.b - $nightKey.r) -gt ($noonKey.b - $noonKey.r)) `
        "noon b-r $([Math]::Round($noonKey.b - $noonKey.r,3)), night $([Math]::Round($nightKey.b - $nightKey.r,3))"

    # Fog eats contrast: the key and the rim should end up closer together.
    $clear = GradeFor 'shoreline' 12 0 0 0
    $foggy = GradeFor 'shoreline' 12 0 4 0
    function Gap($g) {
        $k = $fKey.GetValue($g); $r = $fRim.GetValue($g)
        return [Math]::Abs($k.r-$r.r) + [Math]::Abs($k.g-$r.g) + [Math]::Abs($k.b-$r.b)
    }
    Check 'heavy fog flattens the gap between key and rim' ((Gap $foggy) -lt (Gap $clear)) `
        "clear $([Math]::Round((Gap $clear),3)) vs foggy $([Math]::Round((Gap $foggy),3))"

    # Rain darkens.
    $dry = [float]$fExp.GetValue((GradeFor 'bigmap' 12 0 0 0))
    $wet = [float]$fExp.GetValue((GradeFor 'bigmap' 12 4 0 0))
    Check 'a downpour is darker than a dry raid' ($wet -lt $dry) `
        "dry $([Math]::Round($dry,3)) vs wet $([Math]::Round($wet,3))"

    # Unknown weather must leave the map's own grade completely alone.
    $unknown = [Activator]::CreateInstance($weatherType)
    $untouched = $forRaid.Invoke($null, [object[]]@([string]'woods', $unknown))
    $plain = $gradeType.GetMethod('For', $static).Invoke($null, [object[]]@([string]'woods'))
    Check 'unknown weather leaves the map grade untouched' `
        ([float]$fExp.GetValue($untouched) -eq [float]$fExp.GetValue($plain)) 'exposure moved'

    # An hour outside 0..23 should wrap rather than fall off the daylight curve.
    $wrapped = [float]$fExp.GetValue((GradeFor 'woods' 25 0 0 0))
    $oneAm = [float]$fExp.GetValue((GradeFor 'woods' 1 0 0 0))
    Check 'an out-of-range hour wraps' ([Math]::Abs($wrapped - $oneAm) -lt 0.001) `
        "25:00 gave $([Math]::Round($wrapped,3)), 01:00 gave $([Math]::Round($oneAm,3))"

    # Dawn and dusk are the same sun height and must not grade the same, or Lighthouse's 18:09
    # and 06:09 -- the one map where the two are the choice on offer -- look identical.
    $dawnKey = $fKey.GetValue((GradeFor 'lighthouse' 6 0 0 0))
    $duskKey = $fKey.GetValue((GradeFor 'lighthouse' 18 0 0 0))
    Check 'dawn and dusk do not grade the same' `
        (([Math]::Abs($dawnKey.r - $duskKey.r) + [Math]::Abs($dawnKey.b - $duskKey.b)) -gt 0.01) `
        "dawn $([Math]::Round($dawnKey.r,3))/$([Math]::Round($dawnKey.b,3)), dusk $([Math]::Round($duskKey.r,3))/$([Math]::Round($duskKey.b,3))"

    # ...and specifically: the evening is the warm one. Blue-minus-red is the measure, because
    # warming works by taking blue away while the morning keeps its cold.
    Check 'dusk is the warmer of the two' `
        ((($duskKey.b - $duskKey.r)) -lt (($dawnKey.b - $dawnKey.r))) `
        "dusk b-r $([Math]::Round($duskKey.b - $duskKey.r,3)), dawn b-r $([Math]::Round($dawnKey.b - $dawnKey.r,3))"

    # Minutes have to reach the curve: 18:00 and 18:59 are a long way apart in a sunset, and the
    # whole reason Hour is a float is that Lighthouse deploys at :09 and not on the hour.
    $sixPm = [float]$fExp.GetValue((GradeFor 'lighthouse' 18 0 0 0))
    $sevenPm = [float]$fExp.GetValue((GradeFor 'lighthouse' 18.75 0 0 0))
    Check 'a fractional hour moves the grade' ([Math]::Abs($sixPm - $sevenPm) -gt 0.001) `
        "18:00 gave $([Math]::Round($sixPm,3)), 18:45 gave $([Math]::Round($sevenPm,3))"
}
catch { Check 'raid light checks ran' $false $_.Exception.GetBaseException().Message }

# ------------------------------------------------- backdrop table coverage

# "Some maps have no background" was one of the reported faults. The table itself turned out
# to cover every playable map, so this pins that down: if a future SPT adds a raid map and it
# is not listed here, the mod now restores the player's own backdrop rather than leaving the
# previous map's -- but the table should still be updated, and this is what says so.

Write-Host "=== backdrop table covers every playable map ===" -ForegroundColor Cyan

try {
    $envMatch = TypeOf 'EnvironmentMatch'
    $defaultsField = $envMatch.GetField('Defaults', $static)
    $defaults = $defaultsField.GetValue($null)

    Check 'the backdrop table is readable' ($defaults -ne $null -and $defaults.Count -gt 0) 'no table'

    $locations = Join-Path $SPTPath 'SPT_Runtime\SPT_Data\database\locations'
    if (Test-Path $locations) {
        # ConvertFrom-Json cannot be used on this database: some files carry keys differing only
        # by case, and PowerShell's parser is case-insensitive, so it throws. See CLAUDE.md.
        Add-Type -AssemblyName System.Web.Extensions
        $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $ser.MaxJsonLength = [int]::MaxValue

        $playable = New-Object System.Collections.Generic.List[string]
        foreach ($dir in Get-ChildItem $locations -Directory) {
            $base = Join-Path $dir.FullName 'base.json'
            if (-not (Test-Path $base)) { continue }
            $json = $ser.DeserializeObject([IO.File]::ReadAllText($base))
            # Enabled and not Locked is what the player can actually deploy to; hideout,
            # develop and Private Area are neither and have no business in the table.
            if ($json['Enabled'] -eq $true -and $json['Locked'] -ne $true) {
                $playable.Add([string]$json['Id'])
            }
        }

        $missing = @()
        foreach ($id in $playable) { if (-not $defaults.ContainsKey($id)) { $missing += $id } }

        Check "every playable map ($($playable.Count)) has a backdrop" ($missing.Count -eq 0) `
            "missing: $($missing -join ', ')"

        # Location ids are not all lowercase (RezervBase, TarkovStreets), and the folder names
        # are. The table is built with OrdinalIgnoreCase precisely so that cannot matter.
        $mixed = @($playable | Where-Object { $_ -cne $_.ToLowerInvariant() })
        Check 'mixed-case ids still resolve in the table' `
            (@($mixed | Where-Object { -not $defaults.ContainsKey($_) }).Count -eq 0) `
            "case-sensitive lookup: $($mixed -join ', ')"
    }
    else {
        Write-Host "  SKIP  no location database under $locations"
    }

    # Every playable map should also have a light grade, or the staging area falls back to a
    # neutral one and the destination stops reading as a place.
    $gradeType = TypeOf 'MapGrade'
    $gradesField = $gradeType.GetField('Grades', $static)
    $grades = $gradesField.GetValue($null)

    Check 'the map grade table is readable' ($grades -ne $null -and $grades.Count -gt 0) 'no table'

    if ($playable -ne $null -and $playable.Count -gt 0) {
        $ungraded = @()
        foreach ($id in $playable) { if (-not $grades.ContainsKey($id)) { $ungraded += $id } }
        Check "every playable map ($($playable.Count)) has a light grade" ($ungraded.Count -eq 0) `
            "ungraded: $($ungraded -join ', ')"
    }

    # A grade whose exposure is zero would black the scene out; one far above 1 would blow it.
    #
    # Grade's fields are internal, so PowerShell's ordinary property access finds nothing on the
    # boxed struct and quietly hands back $null -- which casts to 0 and fails every map. Read the
    # field through reflection instead. (Same family as the $props.Count trap in CLAUDE.md: the
    # wrong answer looked like real data.)
    $gradeStruct = $grades.GetType().GetGenericArguments()[1]
    $exposureField = $gradeStruct.GetField('Exposure', $instance)

    Check 'the grade struct exposes Exposure' ($exposureField -ne $null) 'no such field'

    if ($exposureField -ne $null) {
        $badExposure = @()
        foreach ($key in $grades.Keys) {
            $e = [float]$exposureField.GetValue($grades[$key])
            if ($e -le 0.2 -or $e -gt 1.6) { $badExposure += "$key=$e" }
        }
        Check 'every grade exposure is sane' ($badExposure.Count -eq 0) "$($badExposure -join ', ')"
    }

    # The two edition-themed backdrops look like a mistake behind a raid, which is why the
    # table only ever uses Factory, Wood and Laboratory.
    $themed = 0
    foreach ($key in $defaults.Keys) {
        $value = [int]$defaults[$key]
        if ($value -eq 4 -or $value -eq 5) { $themed++ }   # TheUnheardEdition, Cyber
    }
    Check 'no map is sent to an edition-themed backdrop' ($themed -eq 0) "$themed do"
}
catch { Check 'backdrop coverage checks ran' $false $_.Exception.GetBaseException().Message }

[AppDomain]::CurrentDomain.remove_AssemblyResolve($script:resolver)

Write-Host ""
if ($script:fail -gt 0) {
    Write-Host "$script:pass passed, $script:fail failed" -ForegroundColor Red
    exit 1
}
Write-Host "$script:pass passed" -ForegroundColor Green
exit 0
