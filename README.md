# Deploy Screen

Gives you something worth looking at while a raid loads: moving banners, a briefing for the map you're entering, your own images, and a backdrop that fits the map.

> **Pre-release.** Built and checked against SPT 4.1.5, but not yet tested in game.

## What it does

The loading screen with your PMC and **Deploying in:** has no background of its own. What you're looking at is a slideshow of banners in front of the main menu scene. The banners haven't changed in years, they're the same on every map, and nothing on the screen moves.

This mod changes that:

- **Motion.** Each banner slowly zooms and drifts.
- **Map intel.** The banner captions tell you which bosses can spawn and how likely they are, how many extracts the map has, and which of your tasks are on this map.
- **Custom banners.** Use your own images, per map.
- **Backdrop.** The menu scene behind the screen changes to suit the map. Off by default.
- **Performance comparison.** Record loading phases and frame gaps, compare the stock presentation with an experimental minimal screen, and find out whether removing presentation work helps on your machine.

Motion and map intel work as soon as you install, with nothing to set up. Nothing is saved to your profile, and removing the mod puts everything back the way it was.

## Requirements

- SPT 4.1.5

## Install

1. Download `DeployScreen_V1.6.0.zip` from the [`releases`](releases) folder.
2. Extract it into your SPT folder. You should end up with:

   ```
   BepInEx/plugins/DeployScreen/DeployScreen.Client.dll
   BepInEx/plugins/DeployScreen/environments.txt
   BepInEx/plugins/DeployScreen/banners/
   ```

To uninstall, delete the `BepInEx/plugins/DeployScreen` folder. Your settings are kept separately in `BepInEx/config/com.mybutthasarash.deployscreen.cfg`, which you can delete too.

## Settings

Press **F12** and open **Deploy Screen**. Changes take effect the next time you load into a raid.

| Section | Setting | Default | What it does |
| --- | --- | --- | --- |
| Performance | Loading screen | Enhanced | **Staging area**: your map art becomes the world your PMC stands in — see [The staging area](#the-staging-area).<br>**Enhanced**: banners, motion and intel on the normal screen.<br>**Vanilla**: stock presentation with optional diagnostics.<br>**Minimal**: experimental reduced screen. |
| Performance | Record loading | On | Save a JSON loading report under the plugin's `diagnostics` folder. |
| Performance | Test label | Empty | Group comparable runs, for example `first` or `repeat`. |
| Banners | Enabled | On | Use your own images from the `banners` folder. Does nothing until you add some. |
| Banners | Captions | Map intel | **Map intel**: a briefing for the map. See [Map intel](#map-intel).<br>**From file name**: captions taken from your image file names.<br>**Keep vanilla**: the game's own captions, now working alongside your own art. |
| Motion | Enabled | On | Slowly zoom and drift each banner. |
| Motion | Zoom | 1.06 | How far the zoom goes, from 1.0 to 1.3. |
| Motion | Seconds per cycle | 18 | How long one zoom in and back out takes, from 4 to 60. |
| Intel | Show your tasks | On | Include a card listing the tasks you've started on this map. |
| Backdrop | Match the map | Off | Override **your** chosen backdrop with one picked to suit the map. See [Backdrop](#backdrop). |
| Scene | Depth and atmosphere | On | Drift the backdrop's camera and breathe its lights, for real parallax. See [Depth](#depth). |
| Scene | Camera drift | 0.05 | How far the camera drifts, in world units. **The one setting likely to need tuning.** |
| Scene | Camera sway | 0.12 | A slow rotation on top of the drift, in degrees. |
| Scene | Light wander | 0.06 | How much the scene's lights breathe, as a fraction of their set intensity. |
| Scene | Ground the character | On | Switch on the PMC's own contact shadow if it's off. |
| Scene | Idle movement | Off | Ask the PMC's animator for its idle patrol motion. |
| Scene | Dim behind the text | Off | Use the game's own scrim behind the loading text, for readability. |
| Ease the load | Decode art early | On | Read your pictures before the load starts, not during it. |
| Ease the load | Frame rate cap while loading | 0 (off) | Cap FPS on the deploy screen so the menu competes less with loading. |
| Ease the load | Loading priority | Leave alone | Unity's frame-rate-vs-load-speed trade. See [Hitching](#hitching-while-you-wait). |
| Ease the load | Pause character IK while loading | Off | Stop the PMC's limb solvers while the raid loads. |
| Staging area | Follow the raid's weather | On | Light the screen for the raid's real time of day, fog, rain and cloud. |
| Staging area | Near plane distance | 3 | Gap between the two art planes, in world units. Wider gap, stronger parallax. |
| Staging area | Overscan | 1.12 | A **floor**. The mod works out what your screen shape and drift actually need. |
| Staging area | Near haze | 0.55 | How dark the near frame is. 0 removes that plane. |
| Staging area | Hide the menu scene | On | Switch off the menu backdrop's furniture so your art is the world. |
| Staging area | Light the scene for the map | On | Tint the backdrop's own lights toward the destination. |
| Staging area | Grade strength | 0.65 | How far lights and art are pulled toward the destination's colour. |
| Staging area | Light the character to match | On | Key and rim lights that fall on your PMC and nothing else. |
| Staging area | Key light / Rim light | 0.85 / 1.25 | The two character lights. Rim usually wants to be brighter. |
| Staging area | Intel under the map name | On | Briefing, bosses, extracts and tasks in the line under the location. |
| Staging area | Seconds per intel line | 7 | How long each line stays. |

## The staging area

Set **Performance → Loading screen** to **Staging area** and the deploy screen stops being a menu
with a picture in it. Your art for the destination becomes the place your PMC is standing in while
the raid loads.

**It is not the default.** Switch to it deliberately, keep some raids on **Enhanced**, and compare —
that's what the modes are for.

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

**It's lit for the raid, not just the map.** Time of day, fog, rain and cloud are all decided before
you deploy, so a 03:00 foggy Woods and a clear midday one don't look the same: night pulls everything
toward moonlight and leans on the rim light to keep your PMC readable, fog greys things down and eats
the contrast, rain cools and darkens. Turn off **Follow the raid's weather** if you'd rather each map
always looked the same.

The banner panel steps aside — its pictures are the world now — and the map briefing, bosses,
extracts and your tasks cycle slowly in the line beneath the location name.

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

**Ground the character** switches on a contact shadow the game already has but doesn't always show.
It's what stops the PMC looking pasted on top of the backdrop rather than standing in it.

**Idle movement** is off by default for an honest reason: the game property that enables it can be
set but not read, so the mod can't tell what it was before and simply turns it off again on the way
out. If your PMC already shifts its weight, leave this alone.

## Map intel

With **Captions** set to **Map intel**, each banner shows one of these cards:

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

### Captions from file names

Set **Captions** to **From file name** to caption each banner with its file name. Anything after a `;` becomes the line underneath:

```
Dorms; Three storeys, two keys.png
```

### "Keep vanilla" captions with your own art

With **Captions** set to **Keep vanilla**, your own images now keep the game's own lore text
underneath them — the map's own writing, your pictures. This didn't work before 1.6.0: custom art
came up with no caption at all, because the mod couldn't reach the id the game files those captions
under. It can now.

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

## Backdrop

Your backdrop is a real game setting — the one in the game's own options. **This mod treats it as
the foundation to build on, not as something to replace**, so **Match the map** is off by default
and off is the recommended setting.

Turn it on and the menu scene behind the loading screen changes to suit where you're going: the
factory scene for Factory, the forest for Woods, the lab for Labs. With it on:

- **Your backdrop comes back.** It's restored the moment you leave the deploy screen — whether you
  cancel or come back from the raid — so an override can't follow you to the main menu. (Restoring
  is itself a scene load, so when a raid actually starts it's deferred until the menu returns,
  rather than stealing frames from the map load.)
- **A map with no backdrop of its own restores yours**, instead of keeping whatever the previous map
  asked for.
- **If a backdrop isn't available to you, yours stays.** The six scenes are customization unlocks,
  not just files — the mod checks what you actually have before asking for one.
- **If you change the setting yourself while it's on, the mod backs off** rather than overwriting
  your new choice.

Switching scenes means loading one, which can cause a brief stutter while you're setting up the
raid. That's the other reason it's off by default.

The game has six scenes: `Factory`, `Wood`, `Laboratory`, `TheUnheardEdition`, `Cyber` and `Random`.
The mod only uses the first three by default — the other two are themed for game editions and look
out of place behind a raid.

To pick a different scene for a map, edit `environments.txt`:

```
TarkovStreets = Cyber
Woods         = Wood
Shoreline     = Random     # leave the menu alone for this map
```

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

**No improvement over stock loading has been measured yet.** The experimental Minimal mode removes presentation work; it does not rewrite EFT's asset loading or guarantee that long freezes disappear.

- **Enhanced** keeps the existing features below.
- **Vanilla** bypasses this mod's banner, motion, intel, measurement and backdrop changes. Diagnostics can stay on for a baseline. Other installed mods still apply.
- **Minimal** skips the deploy screen's character-preview task and banner-panel creation when the hooks are available. It adds a plain dark background and suspends the current menu environment root when the UI does not depend on its camera. The map heading, loading status, countdown, party controls and cancel behavior remain the game's. No custom banner images are loaded in this mode.

Minimal is opt-in. Missing hooks leave the affected part stock; the log and report show which reductions were applied. Closing or canceling restores presentation objects, while respecting the game's own request to hide the environment during raid transition. The new rendering and restoration paths still need in-game validation, including canceling and loading a second raid.

### Comparing loading modes

1. In F12, set **Performance > Record loading** on and **Loading screen** to **Vanilla**. Set **Test label** to `first` for the first raid after restarting the game.
2. Load the same map with the same raid settings, graphics settings, other mods and custom-art files. Stay focused on the game; alt-tabbed captures are excluded from the comparison.
3. Use `repeat` for subsequent loads in that game session. Collect at least three comparable captures for each mode. A first load after restart is not necessarily a cold disk-cache load.
4. Repeat with **Enhanced** and **Minimal**, restarting between modes to avoid retained artwork or scene state affecting the comparison. Alternate mode order across sessions.
5. Also try a run with the plugin removed as a manual sanity check. That run cannot produce this mod's report; Vanilla mode is the instrumented baseline, not a zero-overhead measurement.

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

### Enhanced mode costs

- **Motion** and **map intel** only do anything while the loading screen is showing, and motion skips the banners that aren't currently on screen.
- **Custom banners** are loaded the first time they're needed and kept while they're useful. Big pictures take longer to load and use more video memory: one sized for a 4K screen is about 22 MB. Sizes your screen has no use for are freed when the loading screen closes, so keeping several sizes of a picture costs you nothing but disk space.
- **Measuring** the banners happens once per raid and isn't something you'd notice. What it measures is saved, so later sessions at the same resolution get the right size from the first raid.
- **Backdrop** loads a scene whenever the next map needs a different one, which can cause a short stutter. That's why it's off by default.

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
- **Motion** slowly scales and moves each banner image.
- **Map intel** reads what the game already knows about the map you picked (boss chances, extracts and your tasks) and adds it to the game's own text, so the banners show it as their captions.
- **Backdrop** asks the game to load a different menu scene.

The mod looks up the game code it needs by name when the game starts, rather than being built against one version of the game. That's why an update can turn off a single feature without breaking the rest.

[`CLAUDE.md`](CLAUDE.md) has the full technical notes: which game code is hooked, what was verified, and what is still untested.

## Building from source

You need:

- The .NET SDK (any version that can build `net472`)
- An SPT 4.1.5 install

The game doesn't need to have been started first. The mod isn't compiled against the game's own code, so a fresh install is enough.

Then, from PowerShell:

```powershell
scripts\pack.ps1 -SPTPath "C:\path\to\SPT"            # build and pack releases\DeployScreen_V<version>.zip
scripts\pack.ps1 -SPTPath "C:\path\to\SPT" -Install   # also copy it into that install
```

`-Install` never overwrites your `environments.txt` or anything in `banners`.

To check the parts that don't need the game (reading image sizes, cropping, choosing a size, and reading captions from file names) against real files:

```powershell
scripts\test-logic.ps1 -SPTPath "C:\path\to\SPT"
scripts\test-performance.ps1   # diagnostic accounting and comparison, no game required
```

Use PowerShell rather than Git Bash: Bash can mangle Windows paths passed to `-SPTPath`.

## License

Not chosen yet.
