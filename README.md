# Deploy Screen

Replaces what you look at while a raid loads.

The screen with your PMC and **Deploying in:** has no background image of its own. What
you actually see is two separate things: a rotating banner panel in front, drawn from the
map's own banner list, and the menu environment scene still rendering behind it. Neither
has changed in years, and both are the same on every map.

This mod takes both.

- **Banners** — your own images, per map, from a folder next to the DLL.
- **Backdrop** — the menu environment switched to suit the map you are deploying to.

Client-only BepInEx plugin. No server mod, no changes to your profile or the database.

## Install

Extract into your SPT folder. You get:

```
BepInEx\plugins\DeployScreen\DeployScreen.Client.dll
BepInEx\plugins\DeployScreen\environments.txt
BepInEx\plugins\DeployScreen\banners\
```

**Out of the box it does nothing.** The banner half needs images, and backdrop matching
starts switched off. Both are deliberate — an install you have not set up should look
exactly like vanilla.

## Banners

Drop images into a folder named after the map:

```
banners\bigmap\01 - Dorms.png
banners\bigmap\02 - Gas station.jpg
banners\woods\Sawmill.png
banners\_default\anything.png
```

PNG, JPG and JPEG. `_default` is the fallback for any map without a folder of its own; a
map with neither is left vanilla.

The stock banners are **765x460** — anything that shape works, anything far off it gets
stretched.

**How many show up is whatever the map shows in vanilla** — ten on Customs and Factory,
four on Woods, five on Labs. Extra files past that are not reached, and fewer files than
that are cycled so every slot is filled. This mod does not change the count.

Files are used in name order. A leading `01 - ` or `3.` is treated as ordering and dropped
from the caption.

### Captions

In the F12 settings, **Captions**:

- **Keep vanilla** (default) — the game's own headings.
- **From file name** — `Dorms|Three storey, two keys.png` becomes that heading with that
  line underneath.

### Location ids

```
bigmap (Customs)      factory4_day        factory4_night      interchange
laboratory            labyrinth           lighthouse          rezervbase (Reserve)
sandbox (Ground Zero) sandbox_high        shoreline           suburbs
tarkovstreets         terminal            town                woods
```

## Backdrop

Turn on **Match the map** in F12 and the menu environment behind the deploy screen
switches to suit the destination — the Factory backdrop for Factory, the woods one for
Woods, the lab one for Labs.

**It is off by default because it is a real Unity scene load**, started while you are still
setting up the raid. That is a few seconds of slack before you deploy, but it is a scene
load on a screen you are about to leave. Turn it on and watch for a hitch.

The game ships six backdrops: `Factory`, `Wood`, `Laboratory`, `TheUnheardEdition`, `Cyber`
and `Random`. Only the first three are used by default — the other two are edition-themed
and look like a mistake behind a raid. There is no way to add a seventh; they are baked
into the game build.

Per-map choices live in `environments.txt`:

```
tarkovstreets = Cyber
woods         = Wood
shoreline     = Random     # leave the menu alone for this map
```

A backdrop your install does not have is logged and skipped rather than loaded as nothing.

## Compatibility

Built and tested against **SPT 4.1.5** / EFT `0.16.9.5.40743`.

It holds no `Assembly-CSharp` reference and resolves every game type by name at runtime, so
a game update that does not rename these classes will not break it — and one that does
leaves the mod inert with a line in the log rather than throwing into the menu.

No known conflicts. SPT itself does not patch any of this, and the existing background mods
(Environment Replace, Raid Movie Background Replacer) work on the main menu and the startup
splash, not the deploy screen.

## Building

```
scripts\pack.ps1 -SPTPath C:\HUH
scripts\pack.ps1 -SPTPath C:\HUH -Install
```

Run it through PowerShell, not Bash.
