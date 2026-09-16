# SPT-DeployScreen -- working notes for Claude

Replaces what you look at while a raid loads: the banners on the deploy screen, and
optionally the menu backdrop behind it. Client-only BepInEx plugin, **no `spt-*`
references and no `Assembly-CSharp` reference at all**.

**Nothing in this repo has ever run in the game.** Everything below was read out of the
game assemblies and SPT's database by static analysis, which is not the same as it
working.

## The box this is built on

| | |
| --- | --- |
| SPT install | `C:\HUH` |
| SPT version | 4.1.5 |
| EFT client | `0.16.9.5.40743` |
| BepInEx | 5.4.23.5, HarmonyLib **2.9.0** |
| Built | 2026-09-16, clean, 0 warnings |

```
scripts\pack.ps1 -SPTPath C:\HUH
scripts\pack.ps1 -SPTPath C:\HUH -Install
```

**Run those through PowerShell, not Bash** -- same `C:HUH` mangling trap as CamoPatch,
BarrelHealing and SPT-Casino.

## Why there is no Assembly-CSharp reference

CamoPatch's notes record this as the single biggest time sink in that repo: the
`Assembly-CSharp.dll` in `Managed` is **not** the one the game runs. The SPT Launcher
applies `SPT_Data\Launcher\Patches\SPT-core\...\Assembly-CSharp.dll.delta` (HDiffPatch,
`HDIFF13&zstd`) at startup, and that delta **renames obfuscated types**. An install that
has never been launched still holds the unpatched original -- `C:\HUH` is one.

This repo sidesteps the whole problem: it references no game assembly and resolves every
type, method and field by its **patched** name at runtime through `AccessTools`
(`GameTypes.cs`). Consequences worth keeping:

- It builds against any install, launched or not. `pack.ps1` needs no `-GameAssembly`.
- A rename in a future EFT build makes the mod **inert with a log line**, not a crash.
- There is no compile-time checking of any game member. `GameTypes.Resolve()` is the only
  place that can catch a bad name, so **new game members must be added there**, not looked
  up ad hoc at the patch site.

A patched copy, if one is ever needed for analysis, is at
`scratchpad\managed415\Assembly-CSharp.dll` (16,233,472 bytes; the unpatched original is
15,994,432) and `hpatchz.exe` is in `scratchpad\hdiff`. Both came from the CamoPatch
session and are **in a temp folder that will eventually be cleaned up**.

## What the deploy screen actually is

The thing everyone stares at is `EFT.UI.Matchmaker.MatchmakerTimeHasCome`, and **it has no
background image field**. Its fields are `_playerModelView`, `_locationName`, `_subCaption`,
`_deployingText`, `_cancelButton`, `_bannersPanel`, `_partyInfoPanel`. What you see is two
unrelated layers:

1. **The menu environment still rendering behind it** -- `EFT.UI.EnvironmentUI`, which
   loads one of six baked Unity scenes by name through `SceneManager.LoadSceneAsync`.
2. **A rotating banner panel in front** -- `MatchmakerBannersPanel`, one banner per entry
   in the map's own `Banners` array.

The banners are the stale art. They are also the easy half.

### The banner path

```
MatchmakerTimeHasCome.Show(IEftSession, RaidSettings, MatchmakerPlayersController)
  -> MatchmakerBannersPanel.Show(Location, ESideType, ProfileStats, IImageLoader)
       -> TryCreateBanner(LocationBanner, IImageLoader)     one per banner, awaited
            -> IImageLoader.LoadTextureMain(string) -> Task<Texture2D>
            -> CreateBanner(string, string, Sprite) -> MatchmakerBanner
```

- `EFT.IImageLoader` is a real named public interface with one method,
  `Task<Texture2D> LoadTextureMain(string url)`. Implemented by `EFT.ClientBackendSession`.
- `MatchmakerBanner.Init(string bannerName, string description, Sprite sprite)` is public,
  and `_bannerImage` on it is a `UnityEngine.UI.Image`.
- Key-binding banners reach `CreateBanner` from `KeyBannerGenerator.GetKeyBindingBanner` by
  a **different path**, which is exactly why the hook is on `TryCreateBanner` and not on
  `CreateBanner` or `MatchmakerBanner.Init`. Hooking either of those would repaint the
  tutorial banners too.

### Where the stock art lives

Server-side, not in a bundle: `SPT_Runtime\SPT_Data\images\banners\` -- 44 files, 21 at
765x460, 20 at 764x460, 2 at 764x430, 1 at 1064x616. Referenced per map from
`SPT_Data\database\locations\<map>\base.json` under `Banners`, each entry
`{ id, pic: { file, path, rcid, type } }`. Customs and Factory declare 10, Woods 4, Labs 5.

**This is why the mod does not change the banner count.** The count is driven by the
caller's loop over `location.Banners`; changing it means rewriting that array on a live
shared game object. Extra files are not reached, fewer are cycled.

### The environment path

`EnvironmentUI : MonoBehaviourSingleton<EnvironmentUI>`, so `Instance` / `Instantiated` are
public statics reachable with `BindingFlags.FlattenHierarchy`.

```
Task SetEnvironmentAsync(EEnvironmentUIType)     public
Task ScheduleEnvironmentLoading(string)          public
EEnvironmentUIType GetRandomEnvironment()        public
EnvironmentData[] _environments                  public field
```

`EnvironmentData { EEnvironmentUIType Type; string[] EligibleVersions; string SceneName }`.
`EligibleVersions` is why `IsAvailable` checks before asking -- the edition-themed scenes
are not in every build.

`EFT.EEnvironmentUIType`: `Random=0 Factory=1 Wood=2 Laboratory=3 TheUnheardEdition=4
Cyber=5`. The six scenes are baked into the build
(`Assets/Scenes/UI/EnvironmentUIScene{,Factory,Wood,Lab,Tue,Cyber}.unity`, read out of
`globalgamemanagers`), so **a seventh cannot be added** -- only chosen between.

## How this is put together

```
DeployScreenPlugin.cs   BepInPlugin, the three F12 settings, wiring
GameTypes.cs            every game type/method/field, resolved by name -- the only place they appear
BannerArt.cs            art discovery on disk, caption parsing, the sprite cache
BannerPatches.cs        Show / TryCreateBanner / Close
EnvironmentMatch.cs     the map -> backdrop table, environments.txt, the scene request
```

### The patches

| Method | Kind | Why |
| --- | --- | --- |
| `MatchmakerBannersPanel.Show` | prefix | Learn the map from `__0`, load its art, reset the index |
| `MatchmakerBannersPanel.TryCreateBanner` | prefix | Build the banner from disk, `__result = Task.CompletedTask`, skip the original |
| `MatchmakerBannersPanel.Close` | postfix | Drop the panel from `InFlight` -- it is a strong ref to a Unity object |
| `MatchmakerOfflineRaidScreen.Show` | postfix | Request the backdrop early, while the raid is still being set up |
| `MatchmakerTimeHasCome.Show` | postfix | Same, for paths that skip the offline screen |

Both environment hooks take `object[] __args` and **find the `RaidSettings` by type** rather
than by position, because the two `Show` overloads order their arguments differently.
HarmonyLib 2.9.0 supports `__args` -- verified by dumping its injection-name constants
(`__args __instance __result __state __exception __originalMethod __runOriginal`), because
an ASCII scan of the DLL misses them; those literals live in the UTF-16 string heap.

### Deliberate choices

- **Inert by default.** No images on disk means `BannerArt.For` returns null and every
  patch falls straight through. Backdrop matching ships **off** -- it is a real Unity scene
  load on a screen the player is about to leave.
- **Negative caching.** `BannerArt.Cache` stores nulls, so a map with no folder is not
  re-stat'ed on every deploy.
- **One warning per session** per feature. A cosmetic patch that throws must not bury the
  log.
- The environment task is **observed and swallowed** (`Observe`) so a faulted fire-and-forget
  Task never surfaces as an unhandled task exception, and `_lastRequested` is reset on
  failure so a retry is possible.

## Untested, and what to look for

Nothing here has been in the game. In rough order of risk:

- **Does `TryCreateBanner` interleave?** The prefix assumes the panel awaits each call
  before the next, so `Progress.Next` is a plain counter. If they are started in parallel
  the order of images would shuffle -- still correct art, wrong order.
- **`object __0` on an async method.** Harmony patches the outer stub, whose parameters are
  the declared ones, so `__0` should be the `Location`. Confirm the log names the right map.
- **Sprite lifetime.** Sprites and their textures are cached and never destroyed. Bounded by
  the number of files on disk, but it is real VRAM.
- **The backdrop scene load.** The whole reason it defaults to off. Watch for a hitch on the
  offline raid screen, and for the raid starting before the swap finishes.
- **`Close()` on the panel** may not be called where expected; the leak is small if not.
- **BepInEx log** lines are all prefixed `[DeployScreen]`.

## Future work

- **`Location.Preview` is an unused hook.** Every map's `base.json` has
  `Preview: {"path":"","rcid":""}` -- empty on every map -- while `Scene` is populated
  (`maps/customs_preset.bundle`). `EFT.ResourceKey { path, rcid }`. BSG wired a per-location
  preview asset slot and never shipped it; `LocationInfoPanel` has the matching
  `Image _banner` and `Sprite _defaultImage`. Needs bundle authoring.
- **Arbitrary banner counts**, by cloning `location.Banners` in the `Show` prefix and
  restoring it after. Rejected for 1.0.0 as mutation of shared game state.
- **`VideoEnvironmentUIRoot`** exists (subclasses `EnvironmentUIRoot`, holds
  `GameObject _videoHolderGameObject` and a `VideoPlayer[]`) and
  `UnityEngine.VideoModule.dll` ships -- but `StreamingAssets\Video` and `\Intro` are both
  **empty**. The video backdrop path is plumbed and unused.
- **Text.** `locales\global\en.json` holds `"DEPLOYING ON LOCATION"`, `"The Time has Come"`,
  `"Deploying in:"` -- all server-side and trivially editable.

## Publishing

- GUID `com.mybutthasarash.deployscreen`, following SPT-Casino's registered prefix.
- Version lives in the csproj `<Version>` and `PluginVersion`; `pack.ps1` refuses to pack if
  they disagree.
- The zip holds only the four files under `BepInEx/plugins/DeployScreen/`.
- `-Install` never clobbers an existing `environments.txt` or anything under `banners\`.
- **The Forge forbids mods substantially written by AI agents.** The user has acknowledged
  this for their other repos and said "we're all good" -- do not re-raise it unprompted.
- Existing mods in this space: Environment Replace (kmyuhkyuk, SPT 4.0.8, v1.6.0) does the
  **main menu background and startup splash**; Raid Movie Background Replacer does the menu.
  Neither touches the deploy screen. SPT's only nearby patch is
  `DisableMatchmakerPlayerPreviewButtonsPatch` in `spt-singleplayer.dll`.

## Where this was left off

2026-09-16: **1.0.0** written and built clean against SPT 4.1.5 / BepInEx 5.4.23.5. Packed
to `releases\DeployScreen_V1.0.0.zip`, which is tracked -- the sibling repos track their
release zips too.

Git remote is **https://github.com/JoelHauser/LoadingRaid.git**, on `main`. The local folder
is `SPT-DeployScreen` and the GitHub repo is `LoadingRaid`: the same name split as
`SPT-NoMagazineCamo` -> `CamoPatch`, so it is the house pattern rather than a mistake.

The global git identity is unset on this machine; the sibling repos commit as
`Joel Hauser <jhauser@bostonlightsource.com>` and this one is configured the same way,
**locally to the repo**.

**Still not installed, and still never run in the game.**
