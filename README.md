# Deploy Screen Overhaul

Turns the raid loading screen into the place you are about to land in: your own picture of the map, lit for the hour and the weather you are deploying into, with your PMC lit to match it.

> **First release.** Built against SPT 4.1.6 and played in game.

## What it does

The loading screen with your PMC and **Deploying in:** has no background of its own. What you're looking at is a slideshow of small banners in front of the main menu room. The banners haven't changed in years, they're the same on every map, and nothing on the screen moves.

This mod replaces that with one picture of your destination, hung as the world your character is standing in:

- **The map as the world.** Your art fills the screen, the menu room's furniture and the banner panel step aside, and two planes at different depths shear against each other as the camera drifts -- so it has depth rather than being a pan across a flat image.
- **Lit for the raid, not just the map.** The hour comes from the same clock the location screen showed you and the weather from the same forecast, so a foggy 03:00 Woods and a clear midday one do not look alike.
- **A PMC who belongs there.** The picture is measured every raid, and your character is lit to what it actually reads -- which is what stops him looking like a cut-out standing in front of a photo.
- **Your own images, per map.** Drop them in the map's folder.
- **Readable over anything.** The writing carries its own shadow, and how much the corners are dimmed is measured from your picture rather than fixed -- barely anything over a dark treeline, a good deal over a white sky.
- **The countdown too.** GET READY and the final count stay on the map art in the same corners, instead of cutting back to the menu room for the last few seconds.
- **Backing out, too.** Press Back and the art stays up through the whole return to the menu -- the client's work, the rebuild, all of it -- and then dissolves into the menu instead of cutting to a dark room with a loading wheel in the corner.
- **A different picture each time.** Each load of a map takes the next picture in its folder, so ten pictures means ten raids before you see one twice.
- **Measurement.** Record loading phases, frame gaps, art decode cost and memory to a JSON report, and compare cold against warm loads.

Motion and map intel work as soon as you install, with nothing to set up. Nothing is saved to your profile, and removing the mod puts everything back the way it was.

## Requirements

- SPT 4.1.x -- built, played and tested against **4.1.6**

## Install

1. Download `DeployScreen_V1.0.0.zip` from the [`releases`](releases) folder.
2. Extract it into your SPT folder. You should end up with:

   ```
   BepInEx/plugins/DeployScreen/DeployScreen.Client.dll
   BepInEx/plugins/DeployScreen/banners/
   ```

To uninstall, delete the `BepInEx/plugins/DeployScreen` folder. Your settings are kept separately in `BepInEx/config/com.mybutthasarash.deployscreen.cfg`, which you can delete too.

## Settings

Press **F12** and open **Deploy Screen Overhaul**. Changes take effect the next time you load into a raid.

| Section | Setting | Default | What it does |
| --- | --- | --- | --- |
| Performance | Record loading | On | Save a JSON loading report under the plugin's `diagnostics` folder. |
| Banners | Enabled | On | Use your own images from the `banners` folder. Does nothing until you add some. |
| Motion | Enabled | On | Keep the art moving — the camera drifts across it while the raid loads, and the held picture drifts on its own after you press Back. |
| Intel | Show your tasks | On | Include the tasks you've started on this map. Only visible if **Intel under the map name** is on. |
| Scene | Depth and atmosphere | On | Drift the backdrop's camera and breathe its lights, for real parallax. See [Depth](#depth). |
| Staging area | Grade strength | 0.65 | How far the picture and the lighting are pulled toward the destination. 0 leaves everything as the game had it, and is the way to turn the whole look off without uninstalling. |
| Staging area | Follow the raid weather | On | Let the forecast change the light as well as the hour. Off means each map always looks the same. |
| Staging area | Rearrange the screen | On | Map name top-left, progress bottom-left, the way out bottom-right, and the logo out of the way. Off keeps the stock arrangement. |
| Staging area | Intel under the map name | Off | A briefing — bosses, extracts, your tasks — cycling under the location name. |
| Staging area | Keep the art through the countdown | On | GET READY and the final count stay on the art instead of cutting back to the menu room. |
| Staging area | Seconds to fade in | 0.6 | How long the art takes to arrive when the screen opens. 0 for an instant cut. |
| Staging area | Seconds to fade back | 0.8 | How long the art takes to dissolve into the menu after you press Back. |
| Staging area | Seconds to linger after cancelling | 0.4 | How long the art holds at full strength before that dissolve starts. |

### Everything else

There are about forty more, and they are deliberately out of the way. Tick **Advanced settings** in
the configuration manager to see them: plane distances, overscan, key and rim intensities, camera
drift and sway, the cancel-transition timings, and the individual switches for each thing the mod
turns off on the menu camera. They exist because they were needed while building this, and they are
kept because someone tuning their own screen will want them — but none of them is a decision you
should have to make to use the mod.

Two are hidden entirely, because the mod writes them and reads them back: **Measured sizes**, which
remembers how big banners really are on your screen, and **Pictures already shown**, which is how far
through each map's folder the rotation has got. Editing either by hand can only make things worse;
deleting them costs one raid of re-measuring and restarts the rotation.

Every setting, advanced or hidden, is in
`BepInEx/config/com.mybutthasarash.deployscreen.cfg` and can be edited there without the
configuration manager installed at all.


## The staging area

The deploy screen stops being a menu with a picture in it. Your art for the destination becomes
the place your PMC is standing in while the raid loads.

**It needs art.** Put images in the map's folder under `banners/` (see [Custom banners](#custom-banners)).
A map with no art is left alone: you get the normal screen, with only the lighting following the
destination.

### What it actually does

Your PMC is a real 3D model, but it's drawn by a *different camera* than the backdrop scene and
composited on top — that's how the game is built, and it can't be changed cheaply. So rather than
fight it, the staging area uses the three things that make a composite convincing:

- **Parallax.** Because the character and the world are on different cameras, drifting the world's
  camera moves the world and *not* the character. Two art planes at different depths shear against
  each other as well, so the scene has genuine depth rather than a pan across a flat image.
- **Light that agrees.** A key and a rim light are added that fall on your PMC *and nothing else*,
  coloured for where you're going — sodium for Streets, cold green for Woods, clinical blue for
  Labs. This is the setting that matters most: a cut-out looks like a cut-out because its light
  disagrees with its surroundings, not because of its shape.
- **The map as the world.** Your art is hung deep in the scene and the menu backdrop's own furniture
  is switched off, so you're looking at the destination rather than at a mall shutter with the
  destination behind it.

**It's lit for the raid, not just the map.** The screen takes the time from the same clock the
location screen showed you -- whichever of the two times you picked -- and the weather from the same
forecast its weather icon came from. So a 03:00 foggy Woods and a clear midday one don't look the
same: night pulls everything toward moonlight and leans on the rim light to keep your PMC readable,
fog greys things down and eats the contrast, rain cools and darkens, and heavy cloud flattens the
light until it has no direction at all.

Dawn and dusk are told apart rather than treated as one low sun. Evening is amber; morning keeps its
cold and comes out pink and a little dimmer. On Lighthouse, where the choice on offer is 18:09
against 06:09, that's the whole difference between the two raids.

Turn off **Follow the raid's weather** if you'd rather each map always looked the same.

The banner panel steps aside — its pictures are the world now — and the map briefing, bosses,
extracts and your tasks cycle slowly in the line beneath the location name.

### Backing out

Pressing Back used to cut straight to a dark, blurred menu room with a loading wheel in the corner,
and sit there for twenty seconds before the menu appeared. That room is the game's own preloader,
and it is genuinely working -- but it is not what you asked to look at.

Now the art stays up for the whole of it. It holds while the client finishes, and only once the
client goes idle does it linger a moment and dissolve into the menu. Three dots pulse in the bottom
right while it holds, because the art sitting still over a working client reads as a freeze
otherwise -- they are standing in for the game's own wheel, which the art is covering.

If the client never looks busy at all, the art lets go after eight seconds rather than waiting
around. If it is *still* busy after two minutes, the art gives up anyway: an unbounded hold is a
worse failure than a visible cut.

Both ends of that are tunable -- **Seconds to linger after cancelling** is the pause before the
dissolve starts, **Seconds to fade back** is the dissolve itself.

### Tuning it

Two settings are in **world units**, and the mod can't measure how big the game's scenes are, so
these are the ones likely to want adjusting on your machine:

- **Scene → Camera drift** — how far the world camera moves. This creates the parallax.
- **Staging area → Near plane distance** — the gap between the two art planes. Wider gap, stronger shear.

**Overscan** is worked out for you. The art planes are built oversized so the drift can't reveal an
edge, and how much they need depends on your screen's shape as much as on the drift — a sideways
drift is a much bigger fraction of a 4:3 frame than a 32:9 one. The setting is a **floor**; the mod
uses whichever is larger and logs what it picked.

That margin isn't free: at rest you see the middle `1/overscan` of your picture. At the defaults
that's about 96% of it and you'd never notice. Crank the drift up on a short plane distance and it
can reach half — the log warns past 1.5× and names the two settings that cause it.

### Ultrawide and unusual screens

Everything takes its shape from the game's own camera, so 21:9, 32:9, 16:10, 4:3, 5:4 and a rotated
portrait monitor are all handled the same way, and nothing is ever stretched — pictures are
cover-cropped, never squashed.

The one thing worth knowing on a very wide screen: a 16:9 screenshot cover-cropped to 32:9 keeps its
width and throws away most of its height, so it arrives with far fewer pixels than the frame wants
and will look soft. The log says so, once per picture. Save a taller or larger source and the mod
will pick it — see [Several sizes of one picture](#several-sizes-of-one-picture).

If the scene looks empty with the menu furniture gone, turn off **Hide the menu scene** and the
original set stays in front of your art.

Nothing is loaded that wasn't already, so this costs no extra loading time, and everything is put
back when you leave the screen.

## Depth

The screen behind your PMC isn't a picture — it's a real 3D scene, with geometry at different
distances and real lights. The stock deploy screen simply never moves any of it, which is most of
why it reads as a flat wallpaper with someone standing in front of it.

**Depth and atmosphere** drifts that scene's own camera by a few centimetres. Because the scene is
genuinely three-dimensional, near geometry sweeps across the frame faster than far geometry does —
the parallax is computed by the engine, not faked with layers. A slow rotation on top keeps the
drift from reading like a slider, and the scene's lights wander by a fraction of a stop so the air
feels like it's moving.

Nothing is added to the scene and nothing new is loaded, so this costs no loading time. Everything
is put back exactly as it was found when you leave the screen.

**Camera drift is the setting to tune.** It's measured in world units, and the mod has no way to
measure how big the scenes are. The default assumes one unit is about a metre:

- Motion invisible? Raise it, a little at a time.
- Scene swimming or seasick? Lower it.
- 0 turns the drift off and leaves only the sway.

**The character contact shadow** is one the game already has -- the dark patch under the PMC. Shown,
it's what stops him looking pasted on top of the backdrop rather than standing in it. Hidden, it's
gone: a blob authored to sit under a character in a dim menu room doesn't necessarily sit under him
over a photograph, and when it reads as a smear behind him it does more harm than the grounding is
worth. Leave it alone touches nothing.

**Idle movement** is off by default for an honest reason: the game property that enables it can be
set but not read, so the mod can't tell what it was before and simply turns it off again on the way
out. If your PMC already shifts its weight, leave this alone.

## Map intel

With **Intel under the map name** on, the line beneath the location name cycles slowly through these:

| Card | Shows |
| --- | --- |
| Map name | Raid length, how long a typical raid lasts, and the average player level |
| **BOSSES** | The four most likely bosses and their spawn chance, like `Reshala 39%  ·  Knight 20%` |
| **EXTRACTS** | How many extracts the map has, and up to three that are always open |
| **YOUR TASKS** | Tasks you've started that take place on this map |

- **Always open** means a 100% chance and nothing asked of you, like payment, power, a co-op deal or a flare. Every extract on Labs and Reserve asks for something, so their cards say none are always open.
- **Bosses** are listed most likely first. Escorts and event-only spawns aren't counted.
- A card only appears when there's something to put on it, so Labs, for example, has no **BOSSES** card.
- Map, boss, extract and task names come from the game's own text, in your game language.
- If a map has more banners than cards, the cards repeat.

## Getting art without taking screenshots

The mod ships no pictures. If you don't have your own yet, there's a script that fills the folders
from the Escape from Tarkov wiki:

```
scripts\fetch-wiki-art.ps1 -SPTPath "C:\path\to\SPT"
```

It downloads five pictures for each map -- the official showcase captures, at 1920x1080 -- into the
right folder per map, and never overwrites anything already there. Use `-PerMap 8` for more, or
`-Maps shoreline,woods` for just some.

Pictures are saved as JPEG. The wiki's PNGs are around 3.8 MB each, and the game decodes them on the
deploy screen while the raid is loading, which stutters the first load of each map; the same picture
as JPEG is about 500 KB and looks the same behind a character. Pass `-KeepPng` to keep the originals.

These are the best photographs the wiki has. On a screen wider than 1920 they get upscaled and the
mod will say so in the log; your own screenshots, taken at your own resolution, will always look
sharper. Think of the wiki art as somewhere to start rather than somewhere to stop.

## Custom banners

Put images in a folder named after the map, inside `BepInEx/plugins/DeployScreen/banners`:

```
banners/bigmap/01 - Dorms.png
banners/bigmap/02 - Gas station.jpg
banners/Woods/Sawmill.png
banners/_default/anything.png
```

- PNG, JPG and JPEG all work.
- `_default` is used for any map that doesn't have its own folder. A map with neither keeps its stock images.
- Pictures are used in file name order. A leading number like `01 - ` or `3.` only sets the order and isn't shown in the caption.
- **The staging area's backdrop takes the next picture in the folder each time you load that map**, and wraps round at the end, so every picture is seen once before any is seen twice. How far through each map you are is kept in the config under `Banners -> Pictures already shown`, so it survives a restart; deleting that line just starts every map from its first picture again.
- Each map shows the same number of banners it does without the mod: ten on Customs and Factory, five on Labs, four on most other maps. Extra pictures aren't used, and if you add fewer, they repeat.

### Size and shape

The mod measures how big banners really are on your screen. The first time you load into a raid at a resolution, it writes that to the BepInEx log (`BepInEx/LogOutput.log`), for example:

```
[DeployScreen] banners show at 1530x920 px on this 3840x2160 screen (1.78:1). Custom images at least that size will look sharp; the stock art is 765x460.
```

Make your images at least that size and they'll look sharp. Any picture too small for your screen is named in the log.

**Any shape works.** Images are cropped from the center to the banner's shape, never stretched, so a screenshot from a 21:9, 32:9 or 16:10 monitor can go straight in.

### Several sizes of one picture

Give each size the same name, with `@` and anything after it:

```
banners/bigmap/01 - Dorms.png
banners/bigmap/01 - Dorms@1440p.png
banners/bigmap/01 - Dorms@4k.png
```

They count as one picture. The mod reads each file's real size and uses the smallest one that's sharp on your screen, so the same folder works on any monitor. What you write after the `@` is only a label.

### Map folder names

Capitalization doesn't matter, so `woods` and `Woods` both work.

| Map | Folder |
| --- | --- |
| Customs | `bigmap` |
| Factory (day) | `factory4_day` |
| Factory (night) | `factory4_night` |
| Ground Zero (up to level 20) | `Sandbox` |
| Ground Zero (higher levels) | `Sandbox_high` |
| Interchange | `Interchange` |
| Labs | `laboratory` |
| Lighthouse | `Lighthouse` |
| Reserve | `RezervBase` |
| Shoreline | `Shoreline` |
| Streets of Tarkov | `TarkovStreets` |
| Woods | `Woods` |

## Hitching while you wait

If the deploy screen stutters while your raid loads, it's worth knowing what's actually happening:
**the raid world isn't being drawn — it's being built.** Unity loads the map asynchronously, but the
part that turns loaded data into a live scene (creating objects, uploading textures, compiling
shaders) runs on the **main thread, a slice at a time, every frame**. That's the hitch. No mod can
move that work elsewhere, and anything claiming to "fix" it is worth a raised eyebrow.

EFT already does the sensible things — it doubles Unity's texture-upload budget during the load and
restores it afterwards. This mod doesn't override any of that.

What it can do is smaller and more honest:

- **Decode art early** (on by default) — this one is a real fix, because the stall was this mod's
  own. Reading a 4K picture costs tens of milliseconds of main-thread time, and it used to happen
  *while the map was loading*. Now it happens on the previous screen, while you're picking a time of
  day. Same work, better moment.
- **Frame rate cap while loading** — fewer menu frames leave more of the machine for the loader.
  Your own cap is restored when the screen closes.
- **Loading priority** — Unity's documented trade. Raising it finishes the load sooner but makes
  each frame do more work, so frames get *longer* even as there are fewer of them. That can read as
  worse stuttering, not better. It's genuinely a coin-flip and it's off by default.
- **Pause character IK** — your PMC runs limb solvers and hand posers every frame. This stops those
  while loading; the character stays on screen and keeps animating.

**Change one at a time and measure.** Turn on **Record loading**, do three raids on the same map
with the same settings, change one setting, do three more, then run `scripts/compare-loading.ps1`.
Every one of these settings is written into the report so the comparison is fair. Without that
you're guessing, and so is anyone else.

## Performance

**No improvement over stock loading has been measured yet**, and this mod does not rewrite EFT's asset loading or guarantee that long freezes disappear. What it does is measure: the report says where the time went, which is the part that was missing.

### Comparing runs

There is one presentation now -- the staging area -- so there are no modes to compare. What the
reports still separate is **cold against warm**: the first load of a map in a session decodes its
art and pays for it, and later ones do not. `compare-loading.ps1` groups on that by itself, without
needing a label.

1. In F12, set **Performance > Record loading** on. Set **Test label** to `first` for the first raid after restarting the game.
2. Load the same map with the same raid settings, graphics settings, other mods and custom-art files. Stay focused on the game; alt-tabbed captures are excluded from the comparison.
3. Use `repeat` for subsequent loads in that game session. Collect at least three comparable captures for each mode. A first load after restart is not necessarily a cold disk-cache load.
4. Load the same map again in the same session, so a warm run pairs with the cold one.
5. Also try a run with the plugin removed as a manual sanity check. That run cannot produce this mod's report, so it is a comparison by feel rather than by number.

Reports appear after loading under:

```
BepInEx/plugins/DeployScreen/diagnostics/<UTC timestamp>-<id>.json
```

From this repository, summarize them in PowerShell:

```powershell
scripts\compare-loading.ps1 -Path 'C:\HUH\BepInEx\plugins\DeployScreen\diagnostics' |
    Format-Table Map, Mode, Label, Runs, MedianLoadSeconds, MedianLongestGapSeconds, MedianGapSeconds -AutoSize
```

The comparison keeps maps, modes, labels, resolutions, plugin versions, relevant settings and applied minimal features separate. It excludes canceled, incomplete and unfocused captures. Use `-Verbose` to see exclusions; the complete JSON retains the raw details.

### What the report measures

- **Capture duration:** from the deploy screen's `Show` prefix to the plugin's first `Update` after `GameWorld.OnGameStarted`. This is a raid-start proxy, **not an exact measurement of when player controls unlock**. Work before this screen appears is outside the capture.
- **Frame gaps:** monotonic wall-clock time between plugin updates, plus capture boundaries. Focused intervals of at least 100 ms contribute their full duration to `focusedGapSeconds`. This is time inside long frame intervals, not a measured amount of CPU blocking. Phase changes inside a gap have their own timestamps; the gap is labeled with the phase at its previous frame.
- **Loading phases:** the actual strings passed to EFT's `ChangeStatus` callback, with repeated phases deduplicated. These locate stalls but do not identify the responsible function.
- **Memory and GC:** managed heap and process working set sampled at start, every five seconds and at completion, plus GC collection-count changes. Sampled peaks can miss short spikes, and working set is not VRAM. The recorder never forces a collection.

Each detailed list is capped at 128 entries, with dropped counts and complete aggregate gap totals. Reports are serialized and written on a background thread after capture; there are no per-frame disk writes. Measurement still has some overhead. Closing without a confirmed raid-start marker yields an incomplete report after 30 seconds; a capture also has a 30-minute limit. A force-quit or crash may leave no report. Reports remain until you delete them; switch **Record loading** off when finished testing.

### What the presentation costs

- **Motion** and **map intel** only do anything while the loading screen is showing, and motion skips the banners that aren't currently on screen.
- **Your pictures** are loaded the first time they're needed and kept while they're useful. Big pictures take longer to load and use more video memory: one sized for a 4K screen is about 22 MB. Sizes your screen has no use for are freed when the loading screen closes, so keeping several sizes of a picture costs you nothing but disk space.
- **Measuring** the art happens once per raid and isn't something you'd notice. What it measures is saved, so later sessions at the same resolution get the right size from the first raid.

## Compatibility

- Client-only. No server mod.
- Doesn't change your profile or any game files.
- **Other background mods.** Environment Replace and Raid Movie Background Replacer change the main menu and startup screens, not this one, so they work alongside it. If you use one that replaces the main menu scene, leave **Match the map** off so the two don't fight over it.
- **Fika** hasn't been tested. The mod only changes your own screen.
- If a game update renames something the mod relies on, only that feature turns off, and the BepInEx log says which.

## Known limitations

- **Your tasks card only lists tasks tied to one map.** Tasks you can do anywhere don't appear, even if you plan to do them here.
- **Extracts are counted for the whole map.** Some only open from certain spawns, and the card doesn't know where you'll spawn, so an extract listed as always open may not be open to you.
- **Card headings are in English.** Names follow your game language, but labels like `BOSSES` and `min raid` don't. Nor do a few boss names the game has no text for, such as Kaban and Kollontay.
- **The first banner might show its normal caption** until the banners switch for the first time.
- **High Zoom values can show a banner's edges.** Lower **Zoom** if you see them.
- **The first raid at a resolution you've never played at uses the largest size of each picture.** Banners can only be measured once they're on screen, so the best size is picked from the next raid on. The measurement is remembered between sessions, so this only happens once per resolution rather than once per session.
- **The mod can't change the game's own layout.** On ultrawide and other screens it measures, and crops to, wherever the game puts the banner.

## How it works

The loading screen builds each banner by loading its image from the server, then fills in the caption when that banner is selected.

- **Custom banners** use your image instead of the one from the server, cropped to the banner's shape.
- **Resolution**: once banners are on screen, the mod measures them in pixels, writes that to the log, and uses it to choose between sizes of the same picture from then on.
- **Motion** drifts the backdrop camera across the art during the load, and slowly zooms the held picture after you press Back.
- **Map intel** reads what the game already knows about the map you picked (boss chances, extracts and your tasks). Off by default -- turn on **Intel under the map name** to see it.

The mod looks up the game code it needs by name when the game starts, rather than being built against one version of the game. That's why an update can turn off a single feature without breaking the rest.

[`CLAUDE.md`](CLAUDE.md) has the full technical notes: which game code is hooked, what was verified, and what is still untested.

## Building from source

You need:

- The .NET SDK (any version that can build `net472`)
- An SPT 4.1.6 install

The game doesn't need to have been started first. The mod isn't compiled against the game's own code, so a fresh install is enough.

Then, from PowerShell:

```powershell
scripts\pack.ps1 -SPTPath "C:\path\to\SPT"            # build and pack releases\DeployScreen_V<version>.zip
scripts\pack.ps1 -SPTPath "C:\path\to\SPT" -Install   # also copy it into that install
```

`-Install` never overwrites anything in `banners`.

To check the parts that don't need the game (reading image sizes, cropping, choosing a size, the light grade and the picture rotation) against real files:

```powershell
scripts\test-logic.ps1 -SPTPath "C:\path\to\SPT"
scripts\test-performance.ps1   # diagnostic accounting and comparison, no game required
```

Use PowerShell rather than Git Bash: Bash can mangle Windows paths passed to `-SPTPath`.

## License

Not chosen yet.
