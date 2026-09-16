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

Motion and map intel work as soon as you install, with nothing to set up. Nothing is saved to your profile, and removing the mod puts everything back the way it was.

## Requirements

- SPT 4.1.5

## Install

1. Download `DeployScreen_V1.1.0.zip` from the [`releases`](releases) folder.
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
| Banners | Enabled | On | Use your own images from the `banners` folder. Does nothing until you add some. |
| Banners | Captions | Map intel | **Map intel**: a briefing for the map. See [Map intel](#map-intel).<br>**From file name**: captions taken from your image file names.<br>**Keep vanilla**: the game's own captions. |
| Motion | Enabled | On | Slowly zoom and drift each banner. |
| Motion | Zoom | 1.06 | How far the zoom goes, from 1.0 to 1.3. |
| Motion | Seconds per cycle | 18 | How long one zoom in and back out takes, from 4 to 60. |
| Intel | Show your tasks | On | Include a card listing the tasks you've started on this map. |
| Backdrop | Match the map | Off | Change the menu scene behind the screen to suit the map. See [Backdrop](#backdrop). |

## Map intel

With **Captions** set to **Map intel**, each banner shows one of these cards:

| Card | Shows |
| --- | --- |
| Map name | Raid length, how long a typical raid lasts, and the average player level |
| **BOSSES** | Up to four bosses and their spawn chance, like `Reshala 39%  ·  Knight 20%` |
| **EXTRACTS** | How many extracts the map has, and up to three that are always open |
| **YOUR TASKS** | Tasks you've started that take place on this map |

A card only appears when there's something to put on it, so Labs, for example, has no **BOSSES** card. Boss and task names are in your game language. If a map has more banners than cards, the cards repeat.

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
- The stock banners are **765×460**. Images with a different shape get stretched.
- Images are used in file name order. A leading number like `01 - ` or `3.` only sets the order and isn't shown in the caption.
- Each map shows the same number of banners it does without the mod: ten on Customs and Factory, five on Labs, four on most other maps. Extra images aren't used, and if you add fewer, they repeat.

### Captions from file names

Set **Captions** to **From file name** to caption each banner with its file name. Anything after a `|` becomes the line underneath:

```
Dorms|Three storeys, two keys.png
```

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

Turn on **Match the map** and the menu scene behind the loading screen changes to suit where you're going: the factory scene for Factory, the forest for Woods, the lab for Labs.

**It's off by default** because switching scenes means loading one, which can cause a brief stutter while you're setting up the raid. Turn it on and see how it runs for you.

The game has six scenes to choose from: `Factory`, `Wood`, `Laboratory`, `TheUnheardEdition`, `Cyber` and `Random`. The mod only uses the first three by default. `TheUnheardEdition` and `Cyber` are themed for game editions and aren't in every install; if a map asks for one you don't have, the mod leaves the backdrop alone.

To pick a different scene for a map, edit `environments.txt`:

```
TarkovStreets = Cyber
Woods         = Wood
Shoreline     = Random     # leave the menu alone for this map
```

## Performance

- **Motion** and **map intel** only do anything while the loading screen is showing.
- **Custom banners** are loaded the first time they're needed and kept in memory until you quit.
- **Backdrop** loads a scene whenever the next map needs a different one, which can cause a short stutter. That's why it's off by default.

## Compatibility

- Client-only. No server mod.
- Doesn't change your profile or any game files.
- **Other background mods.** Environment Replace and Raid Movie Background Replacer change the main menu and startup screens, not this one, so they work alongside it. If you use one that replaces the main menu scene, leave **Match the map** off so the two don't fight over it.
- **Fika** hasn't been tested. The mod only changes your own screen.
- If a game update renames something the mod relies on, only that feature turns off, and the BepInEx log says which.

## Known limitations

- **Your tasks card only lists tasks tied to one map.** Tasks you can do anywhere don't appear, even if you plan to do them here.
- **The extract count covers the whole map.** Some extracts only open from certain spawns, and the card doesn't know where you'll spawn.
- **"Always open" means a 100% chance to be available.** The extract may still need payment or an item.
- **Card headings are in English.** Boss and task names follow your game language, but labels like `BOSSES` and `min raid` don't.
- **The first banner might show its normal caption** until the banners switch for the first time.
- **High Zoom values can show a banner's edges.** Lower **Zoom** if you see them.

## How it works

The loading screen builds each banner by loading its image from the server, then fills in the caption when that banner is selected.

- **Custom banners** use your image instead of the one from the server.
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

Use PowerShell rather than Git Bash: Bash can mangle Windows paths passed to `-SPTPath`.

## License

Not chosen yet.
