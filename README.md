# Deploy Screen

Replaces what you look at while a raid loads.

The screen with your PMC and **Deploying in:** has no background image of its own. What
you actually see is two separate things: a rotating banner panel in front, drawn from the
map's own banner list, and the menu environment scene still rendering behind it. Neither
has changed in years, both are the same on every map, and nothing on the screen moves.

This mod takes all of it.

- **Motion** — a slow zoom and drift on every banner.
- **Map intel** — the bosses that can spawn and their chances, the extracts, and the tasks
  you have active on the map you are entering.
- **Banners** — your own images, per map, from a folder next to the DLL.
- **Backdrop** — the menu environment switched to suit the map you are deploying to.

Client-only BepInEx plugin. No server mod, and nothing written to your profile or the
database.

## Install

Extract into your SPT folder. You get:

```
BepInEx\plugins\DeployScreen\DeployScreen.Client.dll
BepInEx\plugins\DeployScreen\environments.txt
BepInEx\plugins\DeployScreen\banners\
```

**Motion and map intel work straight away**, on the stock banners, with nothing to set up.
Custom images need files, and backdrop matching starts switched off.

## Motion

Each banner slowly zooms in and drifts, then eases back. Every banner picks its own
direction and starts at a different point in the cycle, so a page of them never moves in
lockstep.

In F12, under **Motion**:

- **Enabled**
- **Zoom** — how far in, from 1.0 to 1.3. Default 1.06.
- **Seconds per cycle** — how long one in-and-out takes, from 4 to 60. Default 18.

Zoom is kept small on purpose. If a banner is not clipped by its frame, a large value will
show its edges.

## Map intel

The captions on the banners become a briefing for the map you are entering:

| Card | What it shows |
| --- | --- |
| **Map name** | raid length, typical raid length, average player level |
| **BOSSES** | up to four bosses and their spawn chance — `Reshala 39%  ·  Cultist Priest 20%` |
| **EXTRACTS** | how many there are, and up to three that are always open |
| **YOUR TASKS** | the quests you have started that are pinned to this map |

Boss and quest names come from the game's own localization, so they read the way the rest
of the game does. PMC waves are left out: they are not bosses, and they sit at the same
chance on every map.

**YOUR TASKS only counts quests the game ties to a single map.** A quest that can be done
anywhere does not appear, even if you plan to do it here — the database marks 281 quests
that way. Turn the card off with **Intel → Show your tasks**.

Cards go one per banner, in order, and repeat if the map has more banners than cards.

## Banners

Drop images into a folder named after the map:

```
banners\bigmap\01 - Dorms.png
banners\bigmap\02 - Gas station.jpg
banners\Woods\Sawmill.png
banners\_default\anything.png
```

PNG, JPG and JPEG. `_default` is the fallback for any map without a folder of its own; a
map with neither keeps its stock images.

The stock banners are **765x460** — anything that shape works, anything far off it gets
stretched.

**How many show up is whatever the map shows in vanilla** — ten on Customs and Factory,
four on Woods, five on Labs. Extra files past that are not reached, and fewer files than
that are cycled so every slot is filled. This mod does not change the count.

Files are used in name order. A leading `01 - ` or `3.` is treated as ordering and dropped
from the caption.

### Captions

In the F12 settings, **Banners → Captions**:

- **Map intel** (default) — see above.
- **From file name** — `Dorms|Three storey, two keys.png` becomes that heading with that
  line underneath.
- **Keep vanilla** — the game's own headings.

### Location ids

Folder names are matched without regard to case, so `woods` and `Woods` both work. These
are the ids as the game spells them:

```
bigmap (Customs)      factory4_day        factory4_night      laboratory
Interchange           Labyrinth           Lighthouse          RezervBase (Reserve)
Sandbox (Ground Zero) Sandbox_high        Shoreline           Suburbs
TarkovStreets         Terminal            Town                Woods
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
TarkovStreets = Cyber
Woods         = Wood
Shoreline     = Random     # leave the menu alone for this map
```

A backdrop your install does not have is logged and skipped rather than loaded as nothing.

## Compatibility

Built against **SPT 4.1.5** / EFT `0.16.9.5.40743`.

It holds no `Assembly-CSharp` reference and resolves every game type by name at runtime, so
a game update that does not rename these classes will not break it — and one that does
leaves the affected feature off, with a line in the log, rather than throwing into the menu.

Map intel adds its captions to the game's locale table for the session. Every key is under
`deployscreen/`, so none of the game's own text can be overwritten.

No known conflicts. SPT itself does not patch any of this, and the existing background mods
(Environment Replace, Raid Movie Background Replacer) work on the main menu and the startup
splash, not the deploy screen.

## Building

```
scripts\pack.ps1 -SPTPath C:\HUH
scripts\pack.ps1 -SPTPath C:\HUH -Install
```

Run it through PowerShell, not Bash.
