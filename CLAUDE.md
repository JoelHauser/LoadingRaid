# SPT-DeployScreen -- working notes for Claude

Replaces what you look at while a raid loads: motion and map intel on the deploy screen's
banners, custom banner art sized and cropped for the player's screen, and optionally the menu backdrop behind it. Client-only
BepInEx plugin, **no `spt-*` references and no `Assembly-CSharp` reference at all**.

**Nothing in this repo has ever run in the game.** Everything below was read out of the
game assemblies, SPT's database and the locale files by static analysis, which is not the
same as it working. The 1.0.0 README said "built and tested" -- that was wrong and has been
corrected; nothing has been tested.

## The box this is built on

| | |
| --- | --- |
| SPT install | `C:\HUH` |
| SPT version | 4.1.5 |
| EFT client | `0.16.9.5.40743` |
| BepInEx | 5.4.23.5, HarmonyLib **2.9.0** |
| Built | 1.3.1 on 2026-09-16, clean, 0 warnings; `scripts\test-logic.ps1` 31 passed; `scripts\test-performance.ps1` 11 checks passed; both exit 0 |

```
scripts\pack.ps1 -SPTPath C:\HUH
scripts\pack.ps1 -SPTPath C:\HUH -Install
scripts\test-logic.ps1 -SPTPath C:\HUH     # the game-independent logic, against real files
scripts\test-performance.ps1             # diagnostic accounting and comparison, no engine needed
scripts\compare-loading.ps1              # summarizes installed plugin's diagnostic reports
```

**Run those through PowerShell, not Bash** -- same `C:HUH` mangling trap as CamoPatch,
BarrelHealing and SPT-Casino.

**This install has never been played.** The only profile,
`SPT_Runtime\user\profiles\6a98731622e6d01d7047c39f.json`, is a 0.38 KB stub
(`username: TEST`, no character created), and there is no `BepInEx\LogOutput.log`
anywhere. Note profiles live under `SPT_Runtime\user\`, not `C:\HUH\user\`.

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
- A rename in a future EFT build turns off **only the affected feature**, with a log line.
  `GameTypes.Resolve()` sets `BannersReady`, `DriverReady`, `IntelReady` and
  `EnvironmentReady` independently.
- There is no compile-time checking of any game member. `GameTypes` is the only place a
  bad name can be caught, so **new game members must be added there**, never looked up ad
  hoc at the patch site.
- The DLL references only `mscorlib`, `System`, `System.Core` (for `HashSet<T>`), `BepInEx`,
  `0Harmony`, `UnityEngine.CoreModule`, `UnityEngine.ImageConversionModule` and
  `UnityEngine.UIModule` (`Canvas`, `RectTransformUtility`, since 1.2.0) -- all shipped in
  `Managed` or `BepInEx\core`, and none of them the game's own code. Banner images are read as `Component` rather than
  `UnityEngine.UI.Image` specifically to keep `UnityEngine.UI` out.

A patched copy for analysis is at `scratchpad\managed415\Assembly-CSharp.dll` (16,233,472
bytes; the unpatched original is 15,994,432) and `hpatchz.exe` is in `scratchpad\hdiff`.
Both are **in a temp folder that will eventually be cleaned up**.

## What the deploy screen actually is

The thing everyone stares at is `EFT.UI.Matchmaker.MatchmakerTimeHasCome`, and **it has no
background image field**. Its fields are `_playerModelView`, `_locationName`, `_subCaption`,
`_deployingText`, `_cancelButton`, `_bannersPanel`, `_partyInfoPanel`. What you see is two
unrelated layers:

1. **The menu environment still rendering behind it** -- `EFT.UI.EnvironmentUI`, which
   loads one of six baked Unity scenes by name through `SceneManager.LoadSceneAsync`.
2. **A rotating banner panel in front** -- `MatchmakerBannersPanel`, one banner per entry
   in the map's own `Banners` array.

### The banner path

```
MatchmakerTimeHasCome.Show(IEftSession, RaidSettings, MatchmakerPlayersController)
  -> MatchmakerBannersPanel.Show(Location, ESideType, ProfileStats, IImageLoader)
       -> TryCreateBanner(LocationBanner, IImageLoader)     one per banner, awaited
            -> IImageLoader.LoadTextureMain(string) -> Task<Texture2D>
            -> CreateBanner(string, string, Sprite) -> MatchmakerBanner
       -> _locationBanners.Add(...)                          inside CG_Show.MoveNext
  -> SelectBanner(BannerWithToggle, bool)                    reads the captions
```

- **The fourth argument of `Show` is the session.** `EFT.IImageLoader` is implemented by
  `ClientBackendSession`, which has a public `Profile` property. That is how the quest list
  is reached without hunting for a singleton.
- `MatchmakerBanner` fields are **public**: `Image _bannerImage`, `CanvasGroup
  _imageCanvasGroup`, `CanvasGroup _bannerCanvasGroup`, `string BannerName`,
  `string BannerDescription`.
- `CreateBanner` instantiates `_bannerImageTemplate`, parents it under
  `_bannerImagesPlaceholder`, calls `Init`, and builds the toggle. It does **not** add to
  `_locationBanners` -- `CG_Show.MoveNext` does.
- Key-binding banners reach `CreateBanner` from `KeyBannerGenerator.GetKeyBindingBanner` by
  a **different path** and never land in `_locationBanners`. That is why art is replaced at
  `TryCreateBanner`, and motion and captions are applied by watching `_locationBanners` --
  hooking `CreateBanner` or `MatchmakerBanner.Init` would also pan and caption the tutorial
  diagrams.

### The caption trap -- read this before touching captions

`SelectBanner` does this with the description (from its IL):

```
if (BannerDescription.IsNullOrEmpty()) hide;
var localized = BannerDescription.Localized();
if (localized.IsNullOrEmpty()) hide;
if (localized.Equals(BannerDescription)) hide;      // <-- the trap
else show localized;
```

And `LocalizationManager.LocalizedValue(id, culture)` **returns `id` unchanged** when
`TryGetLocalization` fails. So a raw string put in `BannerDescription` localizes to itself,
compares equal, and **is hidden**. The heading has no such check and would show raw text,
which is exactly what makes this easy to miss -- half of it appears to work.

The fix is `Localization.cs`: register the text under `deployscreen/intel/<n>/h` and
`/d`, and put the **key** in the banner fields. Then the lookup resolves to something that
is not the key, and the description is shown.

- `LocalizationManager.Instance` is a public static; `Culture`, `UpdateLocale`,
  `LocalizedValue` are public.
- `EFT.Locale : Dictionary<string, string>`, public ctor `(IDictionary<string, string>)`.
- `UpdateLocale(culture, locale)`: if `_locales` has the culture, `existing.Merge(locale)`;
  otherwise `_locales[culture] = new Locale(locale)`. So publishing **merges**.
- `TryGetLocalization` looks up `_locales[culture][id]` -- keys are **per culture**, which
  is why `Publish` reads `Culture` first.
- If the table cannot be reached, `BannerDriver` falls back to the heading only and leaves
  the description empty, rather than setting raw text that would be hidden anyway.

### Intel sources

All off the `Location` handed to `Show` -- **nothing reads the server's files**, which would
not exist on a remote server. `JsonType.LocationSettings+Location` has 100 public fields,
including:

| Field | Type |
| --- | --- |
| `_Id` | the MongoID quests are keyed by |
| `Name`, `Id` | **internal** names (`ReserveBase`, `Sandbox`); `Id` is what banner folders use. The display name is `Localized(_Id + " Name")`, which is what `Location.LocalizedName` does |
| `BossLocationSpawn` | `BossLocationSpawn[]` -- `BossName`, `BossChance`, `BossZone`, ... |
| `exits` | `JsonType.BackendExitTriggerSettings[]` -- `Name`, `Chance`, `ExfiltrationTime`, `PassageRequirement` |
| `EscapeTimeLimit`, `AveragePlayTime`, `AveragePlayerLevel` | `int` |

**Almost every name in the raw data is internal**, and 1.1.0 showed them raw. Its text was
wrong or read badly on 10 of 12 maps -- found only by running the real database through the
intel rules for the preview. 1.1.1's rules, each checked against the data:

- **Map heading**: `Localized(_Id + " Name")`, falling back to `Name`. Raw `Name` gave
  `RESERVEBASE`, `SANDBOX`, `LABORATORY` for Reserve, Ground Zero and The Lab.
- **Boss names**, in order: `QuestCondition/Elimination/Kill/BotRole/<role>` (bossBully ->
  Reshala, bossKojaniy -> Shturman, bossGluhar -> Glukhar, sectantPriest -> Cultist Priest);
  then `ScavRole/<Role>` (**exUsec -> Rogue**, the only one found there); then
  `Intel.KnownBosses` for roles with no text at all -- bossBoar Kaban, bossKolontay Kollontay,
  bossKnight Knight, bossPartisan Partisan, bossZryachiy Zryachiy, peacemaker Peacemaker;
  then the old prefix-stripping tidy-up. 1.1.0 showed `ExUsec`, `Boar`, `Kolontay`.
- **Which roles are bosses**: not `pmc*` (the PMC waves and Raiders), not `follower*`, nothing
  containing `sniper` (**bossBoarSniper** is Kaban's guards, listed at 100%), nothing ending
  `Event` (arenaFighterEvent, crazyAssaultEvent at 5%). A boss appears once per zone with the
  same chance, so `AddBosses` dedupes by role.
- **Boss order**: most likely first, with a *stable* sort so equal chances keep map order,
  then capped at four. 1.1.0 capped in map order, so Shoreline showed Peacemaker 15% and
  dropped Cultist Priest 40%.
- **Extract names are locale keys, but not ones that translate to themselves.** 1.0.0/1.1.0's
  notes said "exfil names map to themselves, so raw is safe" -- concluded from four Customs
  exits, and **false**: `EXFIL_Train` is Armored Train, `E1` is Stylobate Building Elevator,
  `NW Exfil` is Railway Exfil, `Alpinist` is Cliff Descent. A handful have no text at all
  (`tunnel_shared`, `lab_Elevator_Med`, `Coastal_South_Road`). `ExtractName` uses the text when
  a key *exists* (`TryGetLocalization`), else the raw id only if it has no underscore.
- **"Always open"** is `Chance >= 100` **and** `PassageRequirement == None` **and** the id does
  not contain `sniper`. 1.1.0 checked only the chance, so Reserve listed its armored train,
  co-op exit and climbing route. `EFT.Interactive.ERequirementState`: `None=0 Empty=1
  TransferItem=2 WorldEvent=3 NotEmpty=4 HasItem=5 WearsItem=6 EmptyOrSize=7 SkillLevel=8
  Reference=9 ScavCooperation=10 Train=11 Timer=12 SecretTransferItem=13`. **Factory's exits
  omit the field entirely**, which deserializes as None -- correct, they have no requirement.
  **Flare exits say None** but only open on a flare; their ids all contain `sniper`
  (customs_sniper_exit, E9_sniper, wood_sniper_exit, Sniper_exit). Every Labs and Reserve
  exit has a requirement, so those cards read "none always open".
- The extract **count** is still every named exit on the map; `EntryPoints` limits some to
  certain spawn areas and the card does not know the player's.

### Quests on this map

```
session.Profile.QuestsData : List<EFT.Quests.QuestDataClass>
  .Status   : EQuestStatus          Started = 2
  .Template : EFT.Quests.QuestTemplate
      ._locationId  == Location._Id
      ._templateId  -> Localized("<templateId> name")
```

All of those are public fields. Verified against the database: `quests.json` has a
top-level `location` on 558 quests -- **281 are `"any"`**, 6 are `"marathon"`, and the rest
are map MongoIDs. All 12 map MongoIDs used resolve to a real `base.json`. Quest names are
`"<id> name"` in `locales\global\en.json` (`5936d90786f7742b1420ba5b name` -> Debut).

`"any"` quests are **not** shown, by design: they are not tied to the map. That is the
biggest visible limitation of the tasks card, and the README says so.

## Location ids are not all lowercase

`Location.Id` as the game spells it: `bigmap`, `factory4_day`, `factory4_night`,
`laboratory`, `hideout`, `develop` -- then `Interchange`, `Labyrinth`, `Lighthouse`,
`RezervBase`, `Sandbox`, `Sandbox_high`, `Shoreline`, `Suburbs`, `TarkovStreets`,
`Terminal`, `Town`, `Woods`. The **folder names** under `SPT_Data\database\locations` are
all lowercase, which is what 1.0.0's docs wrongly listed.

It never broke anything: `BannerArt` and `EnvironmentMatch` both use
`StringComparer.OrdinalIgnoreCase`, and Windows paths are case-insensitive. 1.1.0 corrects
the README, `banners-README.txt` and `environments.txt`.

## The environment path

`EnvironmentUI : MonoBehaviourSingleton<EnvironmentUI>`, so `Instance` / `Instantiated` are
public statics reachable with `BindingFlags.FlattenHierarchy`.

```
Task SetEnvironmentAsync(EEnvironmentUIType)     public
EEnvironmentUIType GetRandomEnvironment()        public
EnvironmentData[] _environments                  public field
```

`EnvironmentData { EEnvironmentUIType Type; string[] EligibleVersions; string SceneName }`.
`EFT.EEnvironmentUIType`: `Random=0 Factory=1 Wood=2 Laboratory=3 TheUnheardEdition=4
Cyber=5`. The six scenes are baked into the build, so **a seventh cannot be added**.

`EShadingType` is only `None=0 Raid=10 Hideout=11` -- a dim overlay, **not** a tint.
Anything mood- or weather-driven would have to go through `EnvironmentUIRoot.MainScreenLights`
(`Light[]`).

## How this is put together

```
DeployScreenPlugin.cs   BepInPlugin, all F12 settings, wiring
GameTypes.cs            every game type/method/field, resolved by name, in four Ready groups
BannerPatches.cs        Show / TryCreateBanner / Close; starts the driver
BannerDriver.cs         watches _locationBanners; attaches motion, sets intel captions
KenBurns.cs             the zoom-and-drift MonoBehaviour on one banner image
Intel.cs                builds the cards from the Location and the profile
Localization.cs         publishes cards to the locale table; looks up game names
BannerArt.cs            pictures and their sizes on disk, cropping, size choice, captions, sprites
ScreenFit.cs            measuring a banner in screen pixels, per screen size; the ideal-size log
ImageHeader.cs          pixel size from a PNG or JPEG header, without decoding
EnvironmentMatch.cs     the map -> backdrop table, environments.txt, the scene request
EnvironmentState.cs     the ONLY caller of SetEnvironmentAsync; captures and restores the player's
SceneDepth.cs           camera parallax, light wander, PMC grounding; rides LoadingPerformance
StagingArea.cs          the map art as the world: two world-space planes, furniture hidden, intel
MapGrade.cs             per-map light: scene tint, and the PMC key/rim masked to WeaponPreview
scripts/test-logic.ps1  ImageHeader, cropping, size choice, captions, backdrop table coverage
scripts/test-gametypes.ps1  every name GameTypes resolves, against a PATCHED Assembly-CSharp
```

### The patches

| Method | Kind | Why |
| --- | --- | --- |
| `MatchmakerBannersPanel.Show` | prefix | Map from `__0`, session from `__3`; queue art, build + publish intel, `Begin` the driver |
| `MatchmakerBannersPanel.TryCreateBanner` | prefix | Build the banner from disk, `__result = Task.CompletedTask`, skip the original |
| `MatchmakerBannersPanel.Close` | postfix | Drop the panel from `InFlight`; disable the driver, which restores every transform |
| `MatchmakerOfflineRaidScreen.Show` | postfix | Request the backdrop early, while the raid is still being set up |
| `MatchmakerTimeHasCome.Show` | postfix | Same, for paths that skip the offline screen |

### Why a driver instead of a postfix

`Show` is `async`: a Harmony postfix runs when it hands back its `Task`, **before any banner
exists**. Banners are added to `_locationBanners` over several frames as each one's image
arrives. So `BannerDriver` is a MonoBehaviour added to the panel's GameObject that checks the
list every `Update` and dresses each banner the first time it sees it. `Begin` resets it on
every `Show`, because the panel is reused between raids.

The driver runs for the whole deploy screen -- twenty to sixty seconds -- so what an **idle
frame** costs matters. 1.2.0's cost one `FieldInfo.GetValue`, a `foreach` over the list as
`IEnumerable` (which boxes `List<T>`'s struct enumerator, so one heap allocation per frame), a
`GetValue` per banner and a `HashSet<object>` probe -- every frame, long after there was
anything new to find. 1.2.1 reads the field **once in `Begin`** and scans by index from
`_scanned`, so an idle frame is one `IList.Count` and nothing else. Two facts from the IL make
that safe, and both were checked with Cecil:

- `_locationBanners` is `stfld` **only in the panel's constructor**. `Close()` calls `Clear()`
  on the same instance. So the reference never goes stale within a panel's life.
- `CreateBanner` does `Object.Instantiate` per banner per `Show`, so banners are new objects
  every raid. `_seen` was therefore never doing anything an index could not.

`Update` guards `count < _scanned` for the install where `Close` cannot be resolved and the
postfix never disables the driver.

### Motion

`KenBurns` animates only `localScale` and `anchoredPosition` of the banner image's
`RectTransform`. The game animates CanvasGroup **alpha** on banner switches, never the
transform, so they do not fight. It ping-pongs with a smoothstep rather than running away,
picks a random direction and starting phase per banner, uses `Time.unscaledDeltaTime`, and
captures the original transform on enable so `Restore()` puts it back exactly.

**It skips banners that are not on screen** (1.2.1). Every transform write dirties the
RectTransform and costs the canvas a rebuild, and a page of banners has one visible -- so
1.2.0 paid that five or six times over for something nobody could see. `MatchmakerBanner`'s
own `_bannerCanvasGroup` is the test, and the IL is why it is exact:

- `SetSelected(true)` sets `_bannerCanvasGroup.alpha = 1` **before** the fade begins, so a
  visible banner always reads as visible -- the gate cannot freeze one by mistake.
- `CG_SetSelected`, the fade's completion callback, sets it to `0` when `_isVisible` is false.
- The fade itself is on `_imageCanvasGroup`, through `VisualExtensions.SoftChange(..., 2f)`.

The phase keeps advancing while a banner is hidden, so one that comes back is where it would
have been rather than where it was left. The field is **optional** in `GameTypes`: if it
cannot be resolved, `Group` is null and every banner animates, exactly as 1.2.0 did.

### Deliberate choices

- **Motion and intel are on by default** -- they need nothing on disk. Custom art is still
  inert without files, and backdrop matching still ships **off**.
- **Intel is one of three caption modes**, not a separate switch, so there is no precedence
  question between intel and file-name captions.
- **Negative caching.** `BannerArt.Cache` stores nulls, so a map with no folder is not
  re-stat'ed on every deploy.
- **One warning per session** per feature. A cosmetic patch that throws must not bury the log.
- `Localization.Lookup` returns **null** for an unresolved key rather than the key, and it
  asks `TryGetLocalization(id, locale, out value)` whether the key **exists** -- not whether
  its text differs from the key, which misreads keys that translate to themselves
  ("Crossroads"). The differs-from-key test is kept only as a fallback if that method is gone.
- **File-name captions split on `;`.** 1.1.0 split on `|`, which Windows refuses in a file
  name ("Illegal characters in path", tested), so a description was impossible. `|` is still
  accepted.

## Traps hit while building this

Each of these produced a confident wrong answer first. Worth checking for again.

- **Case-sensitive regex against quests.json** reported `"woods"` 0 times. Case-insensitively
  it is 39 -- and that count was *also* misleading, matching quest text rather than the
  linkage, which turned out to be MongoIDs. Extract the actual distinct values.
- **ASCII-scanning a .NET DLL misses string literals.** They live in the UTF-16 `#US` heap.
  HarmonyLib's `__args` support was only confirmed by dumping `ldstr` operands with Cecil.
- **PowerShell eats a leading BOM when decoding native output.** `git log --format=%s`
  reported a clean subject while the commit object held `EF BB BF`. Only a byte-level read
  (`cmd /c "git cat-file commit HEAD > file"`) showed it.
- **Piping a here-string into `git commit -F -` adds a BOM** in Windows PowerShell 5.1, and
  passing it as an argument makes git treat it as a pathspec. **Write the message to a file
  with `UTF8Encoding($false)` and use `git commit -F <file>`.**
- `$props.Count` on `PSObject.Properties` enumerates rather than counting.
- **A void `MethodInfo.Invoke` still puts a `$null` on the PowerShell pipeline.** A helper that
  called one and then returned a hashtable handed back *two* objects, so `.Count` was 2 for a
  one-key table and every lookup came back empty -- four tests failed with numbers that looked
  almost right. Prefix it with `[void]`. Same family as the `$props.Count` trap.
- **An `AssemblyResolve` handler left attached overflows the stack at shutdown.** `test-logic.ps1`
  printed "27 passed" and then died with a StackOverflowException, exiting **253**, on 1.2.0 as
  well -- the handler is still live while PowerShell tears the runspace down and a resolve
  arriving then re-enters it. Keep the handler in a variable and `remove_AssemblyResolve` it
  before the end. A script that reports every test passing can still be failing its caller.
- A PowerShell `foreach { } | Select-Object` is an empty pipe element -- collect into a
  variable first.
- **Generalizing from one map.** "Exfil names map to themselves" came from checking four
  Customs exits. Run a rule over all twelve maps before writing it down.
- **Differs-from-key is not "missing".** A lookup helper that treats `value == key` as
  untranslated drops every name that translates to itself -- it emptied Customs' extract list
  in the preview's first pass.
- **`ConvertFrom-Json` cannot read `en.json`**: it has keys differing only by case
  (`Arena/Widgets/activate object`), and PowerShell's parser is case-insensitive, so it throws
  and every lookup silently comes back empty. Use
  `System.Web.Script.Serialization.JavaScriptSerializer` with `MaxJsonLength = [int]::MaxValue`.
- **`Sort-Object` in Windows PowerShell 5.1 is not stable.** The preview's first boss order put
  equal chances in arbitrary order; C#'s `OrderByDescending` is stable.
- **Windows file names cannot contain `< > : " / \ | ? *`.** Check any file-naming convention
  against that list before documenting it.
- **`Group-Object` with several properties does not collide on commas.** Its `Name` joins the
  values with `, `, which looks like it should merge `label='x, y'` with `version='z'` into the
  same group as `label='x'` with `version='y, z'`. It does not -- grouping compares each
  property separately and the `Name` is only for display. A fix and a regression test for this
  were written for 1.3.1 and both reverted, because the test passed against the *unfixed*
  script. Run the new test against the old code before believing a bug of this shape.

## Resolution

Requested by the user as three things: pick sharp images, log the ideal size, and be right on
ultrawide and 16:10. **Not** scaling the motion, which they declined -- `KenBurns` moves in the
banner's own RectTransform units, which the game's canvas already scales.

### Measured, not calculated

How big a banner is drawn depends on the game's CanvasScaler mode, its reference resolution,
and whatever `Utils.SetCanvasRestriction` does on wide screens (it sets `uiScaleMode` and
`scaleFactor`) -- serialized into prefabs, not readable from code. So `ScreenFit.TryMeasure`
takes the banner image's `GetWorldCorners` through `RectTransformUtility.WorldToScreenPoint`
(camera `null` for a `ScreenSpaceOverlay` root canvas, its `worldCamera` otherwise) and divides
out `KenBurns.CurrentZoom`. That holds at any resolution and any screen shape without knowing
any of the above.

- `BannerDriver` measures the first banner that has a size, retrying each frame for up to 120
  frames once banners exist. Once per `Show`.
- Kept per `Screen.width x Screen.height` for the session; a resolution change is measured again.
- **A measurement only affects the next raid**: the banners on screen were built before it
  existed. So the first raid at a resolution uses every picture's **largest** size.

### Sizes of one picture

`01 - Dorms.png`, `01 - Dorms@1440p.png` and `01 - Dorms@4k.jpg` are one picture -- the stem up
to the last `@` (`BannerArt.PictureName`). The tag is only a label. Each file's real size comes
from its header (`ImageHeader`: a PNG's IHDR at bytes 16 and 20; a JPEG by walking its segments
to the SOF marker, which can sit behind any amount of metadata).

`BannerImage.Choose` compares sizes **after cropping to the banner's shape**, so a wide
screenshot is not mistaken for a sharp one: the smallest within 2% of the measured size, else
the largest; the largest too when nothing has been measured.

### Shape

`BannerArt.CoverRect` takes the largest centred area with the banner's shape -- measured, or
765:460 before any measurement -- in whole pixels, **rounded** then clamped and integer-centred
so `Sprite.Create` never gets a rect outside the texture. Rounding matters: 460 * (765/460) is
764.9999, and flooring shaved a column off a stock-shaped image. Sprites are made per shape (to
0.01) from one texture, with `SpriteMeshType.FullRect` since the texture is non-readable.

### Log lines

- `ScreenFit.Record`, once per screen size: `banners show at WxH px on this WxH screen (r:1).
  Custom images at least that size will look sharp; the stock art is 765x460.`
- `BannerArt.WarnIfSoft`, once per file per screen size: names a picture with no size sharp
  enough, and suggests saving `<picture>@large.<ext>`.

### Memory

Textures load with `LoadImage(..., markNonReadable: true)`, dropping the CPU copy; a banner sized
for 4K is ~22 MB of GPU memory.

1.2.0 destroyed nothing, and the waste had a specific shape: a measurement only lasts the
session, so the **first raid of every session** ran unmeasured and loaded the *largest* size of
every picture; from the second raid on a smaller one was usually chosen and decoded **as well**,
and both stayed. Six banners at two sizes each is ~260 MB where ~60 MB was wanted. 1.2.1 attacks
it from both ends:

- **The measurement is kept between sessions.** `ScreenFit.Save`/`Remember` write `ByScreen` to
  the `Measured sizes` config entry as `1920x1080=765x460;...`. A session that has played at
  this resolution before starts measured, so the largest-size load stops happening at all after
  the first time. It is only a head start -- every `Show` measures again and overwrites it, so a
  resolution or interface-scale change still costs exactly one raid of the old numbers.
- **`BannerArt.ReleaseUnused`** frees, for every picture in the cache, each size other than the
  one this screen would choose now. It is called from the `Close` postfix: the banners are gone
  with the panel so nothing freed is still drawn, and it hands memory back at the moment the map
  is about to load. Anything freed is read from disk again if it is wanted later --
  `BannerVariant.Release` clears `_texture` and the sprites but leaves `_failed` alone, so a file
  that would not decode is still not retried.

What is still not freed: `BannerArt.Forget()` (the config-changed hook) drops the cache without
releasing, on the grounds that its sprites may be on screen at the time.

### Tested, and not

`scripts\test-logic.ps1` loads the built DLL by reflection -- Unity's `Rect` and `Mathf` resolve
from Managed, and their managed parts run fine outside the engine -- and checks, all 31 passing on
1.2.1: header sizes on all 44 stock banners against System.Drawing; generated PNG and JPEG; a JPEG
with 130 KB of APP1 metadata ahead of its frame header; a text file named .png; size tags;
file-name captions; crop rects for stock, 21:9, 16:9, 16:10 and tall images; six size choices; and
(1.2.1) `ScreenFit.Remember` against a good line, an empty one, a hand-mangled one and a round
trip. `Save` cannot be checked there -- it needs the config entry -- so it null-guards instead.

The script itself had two faults, both fixed in 1.2.1 and both worth knowing (see Traps): it
exited **253 with a StackOverflowException** after printing "N passed", and a void `Invoke` was
quietly adding a `$null` to a helper's output.

**Nothing that needs a screen has run**: `TryMeasure`, which render mode EFT's menu canvas uses,
whether world corners match what the player sees, the log lines, the driver's timing.

## The preview

https://claude.ai/artifact/5nfufGxXp3ZbeRz4k9QEEB -- private to the user. A browser
simulation of the deploy screen for all twelve playable maps, switchable between Vanilla,
1.1.0 and 1.1.1:

- **Real**: banner timing (`MatchmakerBannersPanel.BANNER_SWITCH_TIME = 10` s,
  `MatchmakerBanner.SWITCH_SPEED = 2` alpha/s, so a 0.5 s fade), `KenBurns`' exact math, each
  map's banner count, the vanilla captions (`<bannerId> Name` / `<bannerId> Description`),
  and every intel caption.
- **Guessed**: layout, font (Bender in game), the pictures, and whether the frame clips a
  zoomed image.

The vanilla captions are worth knowing: lore paragraphs about the map and factions, plus
promotions for BSG's Emissaries and Sherpas programs.

Its caption data was generated in PowerShell by applying the intel rules to `base.json`,
`en.json` and `quests.json`. **When an intel rule changes, that data has to be regenerated**,
or the preview silently shows the old rule.

## Untested, and what to look for

In rough order of risk:

- **Does the logged size match the screen?** Compare `banners show at` against a screenshot. If
  EFT's UI camera renders to a RenderTexture that is then scaled to the screen, every measurement
  is off by that scale, and so is every size choice.
- **Is `_bannerImage` clipped?** Nothing static says whether the banner frame masks its
  image (`Coffee.SoftMaskForUGUI` ships, which is suggestive). If not, zoom shows the edges.
  Default 1.06 is small for that reason.
- **First selection timing.** Captions are set as each banner appears. If `SelectBanner`
  runs on the first banner before the driver's next `Update`, that banner shows vanilla text
  until the next automatic switch. Self-correcting, but visible.
- **Is `QuestTemplate._locationId` populated client-side?** The database has it; whether the
  client template carries it has not been seen at runtime. If not, the tasks card always
  says "Nothing active here".
- **`Locale.Merge` semantics.** Assumed to overwrite existing keys. If it skips them, the
  second raid would show the first raid's intel.
- **`TryCreateBanner` interleaving.** Art order assumes each is awaited before the next.
- **`object __0` / `__3` on an async method.** Harmony patches the outer stub, whose
  parameters are the declared ones. Confirm the log names the right map.
- **`ReleaseUnused` on `Close` (1.2.1).** The one change in 1.2.1 that can be *seen* if it is
  wrong: it destroys textures and sprites from the `Close` postfix, on the assumption that
  `UIElement.Close()` -- the first instruction of the panel's `Close` -- has already disposed the
  banners through the `CompositeDisposable` that `CreateBanner` registers them with. If any
  `Image` still references one, expect blank or magenta banners **on the raid after** a
  resolution change. Suspect this first if banners ever come up empty.
- **Does the motion gate ever freeze a visible banner?** It should not -- `SetSelected(true)`
  sets the alpha before the fade -- but the failure would look like one banner in a page not
  moving. The page does select two banners per switch (`Update` calls `SelectBanner` on the
  current and the next), which is worth remembering if it looks wrong.
- **`Measured sizes` in the config.** Check `BepInEx\config\com.mybutthasarash.deployscreen.cfg`
  holds something like `1920x1080=765x460` after a raid, and that the second session at that
  resolution logs no `banners show at` line difference from the first.
- **The backdrop scene load.** Why it defaults to off.
- **BepInEx log** lines are all prefixed `[DeployScreen]`; the load line reports
  `motion=` and `intel=` so a resolve failure is visible immediately.

## 1.4.0: the backdrop is the player's, and the scene has depth

The user reported, from their own play: map selection changed their chosen background, some maps
appeared to have none, and the changes leaked into the main menu. All three were real, all three
come from the same feature, and the diagnosis below came off the **patched** assembly.

### The patched assembly is obtainable again -- this unblocks a lot

1.3.1 recorded this as blocked ("Regenerate the patched assembly before attempting this"). It is
not. `hpatchz.exe` survived in an older session scratchpad, and the delta is still in the install:

```
hpatchz.exe  <SPT>\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll
             <SPT>\SPT_Runtime\SPT_Data\Launcher\Patches\SPT-core\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll.delta
             <out>\Assembly-CSharp.dll
```

15,994,432 in, **16,233,472** out, matching what 1.0.0 recorded. Note the delta lives under
`SPT_Runtime\SPT_Data\`, not `SPT_Data\`. `Mono.Cecil.dll` is in `SPT_Runtime\`.

**Keep a copy outside the session scratchpad.** This is the second time it has been lost, and it is
the difference between reading the game and guessing at it.

`scripts\test-gametypes.ps1` is new and exists because of this: it checks every type and member
`GameTypes.cs` resolves by name against a patched assembly, with Cecil, executing nothing. 62
checks, all passing. That is the compile-time checking the no-Assembly-CSharp design gives up, and
until now there was no substitute for it.

### The player's backdrop is a setting, and the mod was bypassing it

`EnvironmentUI.Awake()`, from its IL:

```
SettingsManager.Instance.Game.Settings.EnvironmentUiType    Bsg.GameSettings.GameSetting<EEnvironmentUIType>
_compositeDisposable.BindState(setting, CG_Awake)           CG_Awake -> SetEnvironmentAsync
```

So the setting drives the scene, **one way**. `SetEnvironmentAsync` writes
`_currentEnvironmentUiType` and loads the scene; it never writes the setting back. 1.1.0's
`EnvironmentMatch` called it directly and had no restore at all, so:

- **The override outlived the screen.** The binding only fires when the *setting* changes, and the
  setting never changed -- so nothing put it back, for the rest of the session, main menu included.
  That is the leak, and it had exactly one cause.
- **Unmapped maps inherited the last map's backdrop.** `Request` returned early when a map was not
  in `Defaults`, leaving whatever the previous map had asked for. With a static `_lastRequested`
  that was never reset, the same map could show different scenes depending on where you went before.
- **Availability was checked against the wrong list.** `IsAvailable` only looked at
  `EnvironmentUI._environments`, which is which scenes the *build* ships. The game's own
  `GetRandomEnvironment` filters on `CustomizationSolver.GetAvailableEnvironmentUIs(side)` --
  backdrops are **customization unlocks**. Asking for one the player does not own is the most likely
  cause of "some maps have no background"; the `Defaults` table itself covers all 11 playable maps,
  which `test-logic.ps1` now asserts against the database.

`EnvironmentState.cs` owns all of it now, and nothing else may call `SetEnvironmentAsync`. It
captures `_currentEnvironmentUiType` first, refuses to change anything it cannot put back
(`EnvironmentRestoreReady`), and restores on every exit. Two subtleties worth keeping:

- **A restore is itself a scene load.** Running one the instant the raid starts drops it on the
  frames the map load needs, for a menu nobody is looking at. So when the raid started the restore
  is *owed*, and a postfix on `ShowEnvironment(true)` settles it when the menu comes back.
- **It lets go if the backdrop moves under it.** `_applied` records what we asked for; if the live
  value is neither that nor the captured original, the player changed the setting themselves, and
  forcing our value back would undo their choice.

`MinimalScreen.Restore` had a second, independent leak: it re-enabled an environment root **only
if** EFT still wanted it visible, so a root we disabled during a raid transition stayed disabled
forever, with EFT unaware it had ever been off. It now always undoes its own `SetActive(false)` and
then calls `ShowEnvironment` with what EFT wants, so the game's state and the scene agree again.

### What the deploy screen actually renders

Read off the patched assembly, not assumed:

- **The PMC is a live 3D model.** `_playerModelView` is `EFT.UI.PlayerModelView`; it exposes
  `PlayerBody` (`EFT.PlayerBody`) and `ModelPlayerPoser` (`MenuPlayerPoser`, with FinalIK `LimbIK[]`,
  hand posers, an `Animator`). The screen also holds `XCoordRotation _rotator` and
  `DragTrigger _dragTrigger` -- it is draggable. `ShowPlayerModel` calls
  `PlayerModelView.Show(..., update: 0f, position: null, animateWeapon: **true**)`, so it is already
  animating. The screen does not feel dead because the model is frozen.
- **`MenuPlayerPoser.BottomShadow` is a public GameObject.** The PMC's ground contact already
  exists. Grounding the character needs no new art, only for it to be on.
- **The backdrop is a real 3D scene.** `EFT.UI.EnvironmentUIRoot` has public
  `Transform CameraContainer`, `Light[] MainScreenLights`, `GameObject[] MainScreenObjects`,
  `ScreenPositionAnchor[] ScreenAnchors` (each with its own `Camera`), `CanvasGroup Shading`, and
  `RandomRotate/GetRandomOffset/RotateBack/ResetRotation`. DOTween ships (`DG.Tweening.Sequence` on
  `EnvironmentUI._currentSequence`).

**So parallax is free.** The depth is already in the scene; nothing ever moves. Translating
`CameraContainer` a few centimetres parallaxes near geometry against far geometry for real, because
the engine computes it. Rotation does **not** do this -- it shifts near and far by the same angle --
which is why `SceneDepth` leads with translation and treats sway as garnish.

`SceneDepth.cs` never assigns an absolute transform: it subtracts the offset it applied last frame,
reads whatever the game left, and adds the new one. The game moves `CameraContainer` itself
(`EnvironmentUI.Rotate()` -> `RandomRotate`), so composing rather than overwriting is what keeps the
two from fighting, and is what makes `Restore()` exact. It rides `LoadingPerformance`'s lifecycle
deliberately -- cancel, raid start, timeout, Show-threw and plugin-destroy all already funnel
through `Restore()`.

**Also verified, and matching what 1.3.0 claimed:** `ShowCameraContainer(false)` really does toggle
`CameraManager.Instance.Camera.gameObject` when `!InGameStatus.InRaid`. That note was right.

### The one number that needs a human

`Scene / Camera drift` is in **world units**, and the scenes' scale cannot be measured from code.
0.05 is a guess that assumes 1 unit = 1 metre. If the motion is invisible, raise it; if the scene
swims, lower it. Everything else in that section is relative -- light wander is a fraction of set
intensity, sway is degrees -- and so is scale-independent.

### Deliberately not done

- **Dust, haze, smoke.** A ParticleSystem needs a material, and a material needs a shader that is
  actually in the build. `Shader.Find` on a name that was not included returns null, and there is no
  way to confirm from here which were. This needs a shipped asset -- an AssetBundle with a particle
  material and a soft dust texture -- before it can be written honestly. **Identified, not invented.**
- **Moving the loading text.** The composition ask is real, but layout cannot be previewed from here
  and a wrong guess makes it worse. `EnvironmentUI.EnableOverlay(true)` -- the game's own scrim,
  `SetShadingVisibility(true, 0.4f)` -- is exposed as an opt-in instead, which is the readability
  half without moving anything.
- **`MenuPlayerPoser.Patrol` is write-only.** There is no readable backing field, so what it was
  before cannot be recovered; `Restore` sets it false. Hence opt-in, default off.

### Still not run in the game

Nothing here has executed. Build is clean at 0 warnings; `test-logic.ps1` is 35 passed, exit 0;
`test-performance.ps1` 11 passed; `test-gametypes.ps1` 62 passed against the patched assembly --
which means every name resolves, not that any of it behaves. **In-game verification is still owed**
for: the drift amplitude, whether the light wander reads as air or as a flicker, whether
`BottomShadow` was off in the first place, the cancel path, and the main menu after a raid.

## 1.5.0: the staging area

The user asked for the deploy screen to feel like the PMC is standing **in** the map, and for the
experience to be reinvented rather than decorated. They chose, when asked: the world comes from
their own per-map art, and the banner panel gives way to it.

### The screen is a composite, and that is the whole design

Read off the patched assembly. `PlayerModelView.Show` calls:

```
PlayerModelLoader.Load(playerVisual, inventoryController, update, filter, spinner, ct,
                       position:  _position,
                       parent:    playerModelView.transform,     <- a UI RectTransform
                       layer:     LayersMaskController.WeaponPreview,
                       animateWeapon: true,
                       alternativeBones: null,
                       enableBottomShadow: true)                 <- ldc.i4.1
```

So there are **three cameras**, not one scene:

| | what | drawn by |
| --- | --- | --- |
| backdrop | a real 3D scene, `EnvironmentUIRoot` | camera under `CameraContainer` |
| PMC | a real skinned model on layer `WeaponPreview`, parented into the canvas | a preview camera |
| UI | banners, map name, timer, cancel | the UI canvas |

`WeaponPreview` is shared with item icons, clothing icons and player icons -- it is the game's
"render a model for the interface" layer. **The character is not in the backdrop's 3D space**, and
putting it there means re-parenting out of the canvas and changing its layer, which breaks
`ScreenPositionAnchor` alignment, `XCoordRotation`/`DragTrigger` drag-to-rotate and UI ordering.

That is not a wall, it is the method. A composite is how film puts an actor somewhere they never
went, and three things sell one -- all reachable:

1. **Parallax.** Different cameras means drifting the backdrop camera moves the world and *not* the
   character. 1.4.0's `SceneDepth` drift was already doing this; it just had nothing behind it.
2. **Light agreement.** `Light.cullingMask = 1 << WeaponPreview` falls on the PMC and nothing else.
   This is the biggest lever by far: a cut-out reads as a cut-out because its light disagrees with
   its surroundings, not because of geometry.
3. **The map as the world.** The player's own art, hung deep in the backdrop scene, with
   `MainScreenObjects` switched off so the menu's furniture is not standing in front of the place
   they are going.

### Why a world-space Canvas and not a quad

A quad needs a material, a material needs a shader, and `Shader.Find` only finds shaders the build
included -- unknowable from outside the game. A **world-space `Canvas` with a `UnityEngine.UI.Image`**
draws through the UI path the mod already uses for the minimal background, so nothing is guessed.
Two planes at different depths (`StagingDistance`, and `DepthRatio` 2.6x beyond it) shear against
each other under the drift, which a single plane cannot do.

Both planes are **parented to the environment root, never to the camera** -- parented to the camera
they would travel with the drift and never parallax. Both are built oversized (`StagingOverscan`)
so the drift cannot reveal an edge. Their layer is chosen from the camera's own `cullingMask`, since
a plane on a culled layer is invisible with nothing in the log to explain it.

### Corrections to 1.4.0

- **"Ground the character" is a no-op.** `enableBottomShadow: true` is passed at load and
  `CreatePlayerBody` does `BottomShadow.SetActive(enableBottomShadow)` -- the contact shadow is
  **already on**. 1.4.0 claimed it was sometimes off. It was not. Grounding comes from the light
  match instead, which is the better answer anyway.
- **`EnvironmentShading` is not a colour grade**, just a dictionary of `CanvasGroup` overlays. The
  "dim behind the text" option is a flat UI scrim and nothing more.

### Intel borrows `_subCaption`

Rather than building a TextMeshPro object -- which would mean referencing TMPro and guessing a font,
a size and a position -- intel is written to `MatchmakerTimeHasCome._subCaption`, the line under the
map name. Nothing in that class writes it: `ChangeStatus` and `UpdateStatusText` both write
`_deployingText`. The `text` property is resolved off the live object, so the **assembly still has no
TMPro reference**.

**The caption trap does not apply here.** It is `SelectBanner`'s -- it hides a description that
localizes to itself -- and this sets the TMP text directly, so raw strings are correct and the
locale round-trip is unnecessary.

### Staging is a mode, and is not the default

`LoadingScreenMode` gains `Staging`. It is **not** the default, deliberately: this is a large feature
in a mod that has still never run, and Enhanced is the fallback that does not depend on any of it.
It also makes the thing A/B-able against the existing modes, which is what the mode infrastructure
was built for. Say so when handing it over -- the user has to switch to it.

Degrades in pieces: no art for the map means the menu scene is left alone and only the light follows
the destination; no `MainScreenObjects` means the furniture stays; no `WeaponPreview` means the PMC
is not relit; no `_subCaption` means intel has nowhere to go. Each is logged.

### What still needs a human

- **`Staging area / Near plane distance`** is in world units, like `Camera drift`, and the scenes'
  scale is not measurable from code. Both want tuning together: drift creates the parallax, distance
  sets how much of it you see.
- **Whether the character lights do anything at all.** A directional light masked to the preview
  layer should fall on the PMC, but if that preview is drawn with unlit shaders or a fixed light
  setup, it will do nothing. It fails invisible, not loud.
- **Whether hiding `MainScreenObjects` leaves a void.** If the scene's floor or walls live in that
  array rather than outside it, the map plane may be all that remains -- which may look right or may
  look like a poster. `Hide the menu scene` turns it off.

### Not done, and why

The **AssetBundle route** -- a modelled staging area the PMC genuinely stands in -- was offered and
not chosen for now. It remains the only way to get real geometry around the character, and it needs
a Unity project on 2022.3.43f1, a bundle build-and-load pipeline this repo has never had, and art.
`StagingArea` is deliberately shaped so a different backdrop provider could replace `MapSprite` and
`BuildPlanes` without touching the lighting, the lifecycle or the teardown.

## Aspect ratios

The banner path was already shape-agnostic and stays that way: `ScreenFit` **measures** a banner on
screen rather than working it out from the resolution, which is why 1.2.0 handles ultrawide and
16:10 without knowing anything about the game's CanvasScaler. Nothing there needed changing.

The **staging area** did. Two real faults, both found by the tests rather than by reading:

1. **The art was cropped to the screen and shown on a plane shaped by the camera.** `MapSprite`
   used `Screen.width/Screen.height`; `BuildPlane` sized the plane from `camera.aspect`. A UI Image
   stretches its sprite to its RectTransform, so any disagreement between those two is a stretch --
   which is exactly what happens when the camera has a viewport rect or renders to a RenderTexture.
   **Everything now takes its shape from the camera**, so the two cannot disagree and we never have
   to know which case we are in. `SafeAspect()` falls back to the measured screen, never a
   hard-coded 16:9.

2. **Overscan was a fixed 1.12 and that is not a number you can fix.** The planes are built
   oversized so the drift cannot reveal an edge, and how much they need depends on the distance,
   the field of view, the drift amplitudes **and the shape of the screen** -- the same sideways
   excursion is a small fraction of an ultrawide frame and a large fraction of a portrait one.
   `StagingArea.RequiredOverscan` computes it; the config value is now a **floor**.

### The bound, and how it is checked

```
halfTan    = tan(fov/2)
pulled     = distance + 0.8*drift          // camera pulled back: the frustum grows, the plane does not
growth     = pulled / distance
halfHeight = distance * halfTan
halfWidth  = halfHeight * aspect           // the aspect lives here, and only here
vertical   = growth + (0.55*drift + pulled*tan(0.6*sway)) / halfHeight
horizontal = growth + (drift     + pulled*tan(sway))     / halfWidth
```

`test-logic.ps1` checks that bound against the motion it is meant to cover: it runs SceneDepth's
actual drift sines over 1,080 combinations of aspect x drift x distance x fov x sway, and at each
sampled moment works out how much of the plane the camera can see. **The first version failed**, and
for a reason worth keeping: the rotation term was `2*sway/fov` on both axes, which ignores the
aspect entirely -- so a portrait screen was under-covered and would have shown the plane's edge. The
horizontal rotation shift has to be divided by the *horizontal* half-extent, which carries the
aspect. A closed form that looks symmetric usually is not.

Also fixed: `BannerArt.CoverRect` returned a **zero-height rect** for a very small source against a
very wide shape (a 1x1 image cropped to 48:9 wants 0.19 of a pixel, which rounds to 0), and
`Sprite.Create` throws on that. Clamped to at least one pixel on each side.

### Overscan is not free

The picture is stretched across the whole oversized plane, so at rest you see the middle
`1/overscan` of it. At the default (16:9, drift 0.05, distance 3) that is x1.04 and invisible. At
drift 0.2 over distance 0.5 on a 32:9 it is **x2.34**, meaning only the middle 43% of the art is on
screen. There is a log warning past x1.5 naming the two settings that cause it. This is inherent --
more drift needs more margin -- not a bug to fix.

### Checked at

5:4, 4:3, 3:2, 16:10, 16:9, 21:9 (3440x1440), 32:9 (5120x1440), 48:9 (7680x1440), 1:1 and 9:16
portrait, against ten source-image shapes: 100 crop combinations and 1,080 overscan combinations,
all passing. The browser preview carries the same `requiredOverscan` and is cross-checked against
the shipped C# to 1e-4, so the two cannot quietly drift apart.

**Still not run in the game.** None of this says the planes look right, only that the geometry
holds.

## 1.6.0: the raid's own light, the caption fix, and a cost pass

### The light follows the raid, not just the map

`RaidSettings.TimeAndWeatherSettings` is a public struct settled **before** deploy:

```
int HourOfDay   ERainType RainType   EFogType FogType
ECloudinessType CloudinessType       EWindSpeed WindType
bool IsRandomTime  bool IsRandomWeather
```

`MapGrade.ForRaid` bends the per-map grade with it. Hour is a smooth daylight curve (not a switch)
with its own golden-hour term; night pulls toward moonlight, drops exposure to ~0.42 and *lifts* the
rim, because at night a figure reads by its edge. Fog greys and collapses the key/rim gap -- fog is a
contrast eater before it is a colour. Rain cools, desaturates and darkens. Cloud flattens key toward
rim, because overcast has no direction. Exposure is clamped to 0.25-1.6 so no combination can black
the screen out or blow it white.

The weather enums are ordinal (`NoRain..Shower`, `NoFog..Continuous`, `Clear..Thundercloud`), so the
code converts to int and uses magnitude rather than hard-coding names.

`test-logic.ps1` runs **3,888 combinations** -- 6 maps x 24 hours x rain x fog x cloud -- and checks
exposure and every colour channel stays in range, plus that night is actually darker and cooler than
noon, fog actually flattens, rain actually darkens, and unknown weather leaves the map grade exactly
as it was.

### The caption fix, finally

1.3.1 recorded "keep vanilla captions are still emptied when custom art is on" as blocked, because
`LocationBanner` could not be resolved. With the patched assembly back, `TryCreateBanner`'s IL is
unambiguous:

```
CreateBanner(banner.id + " Name", banner.id + " Description", sprite)
```

`JsonType.LocationSettings+Location+LocationBanner.id` is public. `BeforeTryCreateBanner` now takes
`__0`, reads the id, and passes those two keys through when captions are set to Vanilla. **A player
can have this mod's art under BSG's own lore text**, which is what the setting always claimed.

### Staging is in the reports now

Staging state only reached the trace as `Mark()` events, so `compare-loading.ps1` -- which groups on
mode and the minimal-mode fields -- would have pooled a staging capture where the art was missing
with one where it worked. Exactly the mistake 1.3.1 had to fix for minimal mode. The report now
carries `stagingArtShown`, `stagingSceneObjectsHidden`, `stagingCharacterLit`, `stagingIntelShown`
and `raidConditions`, and the comparison groups on them.

### What the cost pass actually changed

The three items 1.2.1 found and deferred, all of which now matter more because `Intel.Build` runs on
the staging path too:

1. **`Localization.Reach` re-resolved the manager and culture on every lookup** -- a static property
   get plus an instance property get, times the dozens of lookups one `Intel.Build` does, on the
   loading path. Cached per batch; `Refresh()` at the top of `Build` and in `Publish`. Roughly 40-80
   reflective gets become 2.
2. **One `object[3]` allocated per `TryGetLocalization`.** Now a reused static buffer, cleared after
   each call so it pins nothing. Main thread only, which every caller is.
3. **`session.GetType().GetProperty("Profile")` on every intel build** -- cached against the session
   type. **`Tidy` allocated its prefix array per call** -- now `static readonly`.
4. **`ScreenFit.TryMeasure` did `GetComponentInParent<Canvas>()` on each of up to 120 attempts.**
   Cached, and `ForgetCanvas()` is called from `BannerDriver.Begin` because the panel is rebuilt
   between raids.
5. **`Measure` re-`GetComponent`d a `KenBurns` the driver already held.** The banner and its motion
   are now paired in one list.

That last one is worth a note: the first attempt indexed `_attached` by the `_measurable` index,
which **is wrong** -- `_attached` only gains an entry when motion is on and the component was not
already there, so the two diverge. Pairing them in a struct is the correct fix. A cache that is fast
and wrong is worse than the lookup it replaced.

### On "better than default"

Worth being precise, because it is easy to overclaim: **none of this makes the game's loading
faster.** The raid's asset load is on the main thread underneath all of it and this mod does not
touch it. What the pass does is make the mod's own cost small enough not to matter -- the deploy
screen is open for twenty to sixty seconds, and an idle frame should cost approximately nothing.
Whether *Minimal* mode's removal of presentation work helps the load itself is still an open
question with no runtime evidence either way; that is what the diagnostics exist to answer.

### Suites

52 logic (3,888 weather + 1,080 overscan + 100 crop combinations), 11 performance, **80** name
checks against the patched assembly, 0 warnings, references still clean. **Still never run in the
game.**

## 1.6.1: the first raid the staging area ever survived

1.6.0 never ran. Not "ran and looked wrong" -- never reached the end of `Awake` on any launch,
for anyone, from the moment it shipped.

```
ArgumentException: Cannot use any of the following characters in section and key names:
= \n \t \ " ' [ ]
  at BepInEx.Configuration.ConfigDefinition.CheckInvalidConfigChars
  at DeployScreen.Client.DeployScreenPlugin.Awake ()
```

The key was `"Follow the raid's weather"`. BepInEx rejects an apostrophe in a config key, and it
throws from inside `Config.Bind`, part-way through `Awake`, after the early binds and before a
single Harmony patch is installed. The mod loads, patches nothing, logs nothing, and the game
looks completely unmodded.

**Why this was invisible.** An SPT install has `[Logging.Disk] WriteUnityLog = false`, so the
stack trace never reaches `LogOutput.log`. What is there is a `Loading [Deploy Screen 1.6.0]` line
with none of the plugin's own lines after it, which reads like a mod that ran and did nothing. The
trace is in Unity's log instead:

    C:\Users\<user>\AppData\LocalLow\Battlestate Games\EscapeFromTarkov\Player.log

Read that before concluding a client mod "ran but looks wrong". Second tell, for pinpointing where
it threw: BepInEx rewrites `BepInEx\config\<guid>.cfg` on every `Bind`, in bind order, so the last
entry in the file is the last bind that succeeded and the crash is in the next one.

`scripts\test-logic.ps1` now rejects the whole character class in any `Config.Bind` section or key
across `src\DeployScreen.Client\*.cs`, and fails on `"Follow the raid's weather"` as a self-test so
the check cannot quietly stop checking. It reads source text rather than the built assembly on
purpose -- reflecting the keys off the DLL would mean running the code that throws -- and it runs
first, before anything that needs the assembly to load.

### What the live raid measured

Everything below is from the game, not from reasoning about it. The staging area works.

```
plane DeployScreen Map: 23.98x10.04u at 7.80u on layer 25 (Menu Environment),
  camera 'MainMenuCamera' mask 0x02000000, clip 0.05-1000.0, sprite 3440x1440,
  parent 'Environment UI/EnvironmentUISceneTue'
```

- The backdrop camera is `MainMenuCamera`, and its culling mask `0x02000000` is bit 25 -- layer 25,
  `Menu Environment`. `VisibleLayer` picks it correctly off the root.
- The plane geometry checks out: 23.98 x 10.04u at 7.8u works back to a 60 degree vertical FOV at
  2.39:1, which is the screen. Overscan sat at the 1.12 floor, and the art lands within a few per
  cent of 1:1 -- confirmed three ways against a test image (title cap height, horizon position,
  skyline block height).
- `EnvironmentUIRoot.MainScreenObjects` **hid nothing**, and the reason took another two builds to
  find. The array is not empty -- it has 2 entries -- but both are already inactive by the time the
  deploy screen runs, and `Hide` returns early on `!activeSelf`. Reporting "hid nothing" and
  "the array is empty" as the same thing cost an evening. See 1.6.2 below.
- The outer ~6% on each side is trim. Art with anything important near an edge loses it, which is
  worth saying in the banners README rather than letting people find out.

`Hide` now refuses to switch off anything holding the backdrop camera, the art planes or the
character, and logs the full transform path of everything it hides or spares. Cheap, and it is what
turned the array question from a guess into a fact.

### The hour that was never there

`conditions=-01:00` in the first live log. EFT writes `-1` into `HourOfDay` for "this raid has no
time set", which is every PvE raid the player has not given an explicit time. `MapGrade.Read` took
it literally, `Mathf.Repeat(-1, 24)` made it 23, and the whole screen was graded for midnight --
visibly, as a blue cast over everything. 1.5.0 had a clock fallback for exactly this and 1.6.0 lost
it. Restored: anything outside 0-23 falls back to `DateTime.Now.Hour`, and the log says which of
the two it used.

### Still open

Sixteen `NullReferenceException`s at raid teardown, zero the day before:

```
at UnityEngine.Animator.SetBool
at AnimationControllerParametersTable.SetBoltCatch
at EFT.Player+FirearmController.Destroy ()
at EFT.LocalPlayer.Dispose ()
```

The staging area's hiding was the obvious suspect and has been ruled out -- it hid one banner panel
and nothing else. Needs another raid's `Player.log` to say whether it is even reproducible.

`scripts\test-logic.ps1` cannot run past the image-header section under Windows PowerShell 5.1 on
this machine: the DLL binds `String.TrimEnd()` against Unity's mscorlib, which has the parameterless
overload, and .NET Framework 4.8 does not. Pre-existing, not from 1.6.1. It needs PowerShell 7.

## 1.6.2: the art was never hidden, it was behind the wall

1.6.1 rendered the art perfectly and nobody ever saw it. The symptom reported was a flash: the
picture for an instant during the scene transition, then the ordinary menu for the rest of the
load. Three wrong theories went past first -- a teardown, a scene reload, a UI panel in front --
and what settled it was a watchdog rather than another guess.

`StagingArea.Tick` now checks the map plane four times a second and logs the first time its state
changes: destroyed, orphaned, inactive (walking up to name the ancestor that was switched off), or
the camera being disabled. One line came back for a whole 27-second load:

```
[DeployScreen] art up at 0.17s (first look)
```

Alive, active, parented, camera enabled, never changing. That single line rules out every
disappearance theory at once and leaves exactly one: the art is **occluded**.

### What was in the way

```
menu scene: MainScreenObjects has 2 entries
menu scene: 'Environment UI/EnvironmentUISceneTue/CultistLayout' draws 12 renderer(s),
  nearest -2.89u -- in front of the art at 7.80u
```

`CultistLayout` -- the themed menu set. Twelve renderers, and a nearest bound of **minus** 2.89
units, meaning its bounds enclose the camera. The player stands inside that room; the art hangs
7.8u behind its far wall. The flash was the moment in the transition before the room drew.

`MainScreenObjects` was never the answer, and worse, it never said so. It has 2 entries, both
already inactive when the deploy screen runs, so `Hide`'s `!activeSelf` early return skipped both
and the count came out zero -- indistinguishable, from the log, from an empty array. **A silent
early return and an empty collection have to look different in the log, or a wrong conclusion
survives for as long as this one did.**

### The rule now

When the game's own list yields nothing, `HideWhatOccludes` measures the live scene instead: for
each child of the backdrop root, the nearest point of what it actually draws, along the camera's
axis, via `renderer.bounds.ClosestPoint(eye)` rather than the transform origin, which on
`CultistLayout` would have been meaningless. Anything nearer than the map plane is switched off.
Anything with no renderers is left alone, and so is anything behind the art -- the scene lights in
particular, since the grade is applied through them.

The depth test is what keeps this honest: it hides what provably occludes, and it says in the log
what it hid and why, with distances.

### Verified in game

Shoreline, 3440x1440, art consistent for the whole load rather than a flash. `scene-objects-hidden`
went from 1 to 2: `CultistLayout` and the banner panel.

## 1.7.0: the screen rearranged, and real art to put in it

Everything before this made a picture appear behind the character. This makes the screen look
like something, which turned out to be a different problem with a different cause.

### The layout was never the mod's to begin with

The preview this was designed from puts the map name large in the top-left, its intel under it,
the progress line bottom-left and the way out bottom-right. None of that was ever implemented --
the mod hung art and wrote one sub-caption line, and the preview's own provenance card tags the
layout as a stand-in. It is rendered at full confidence beside the parts that are real, so it
reads as the target. Worth knowing before defending the art again: the complaint "it does not
look like the mockup" was about the arrangement, not the picture.

### Ask the screen what it is before moving it

`StagingArea.DumpScreen` prints the deploy screen's hierarchy once per session, four levels deep:
name, on/off, anchored position, size, anchors, every component type, and for anything with a
`text` property its content, font size and colour. It runs from `LoadingPerformance.Begin`, not
from the staging area, because it describes the screen rather than any one mode -- put it inside
`StagingArea` and it silently produces nothing the moment someone is on Enhanced.

What it found, which no amount of reasoning would have:

```
CaptionsHolder @0,0 495x86 anc 0.5,1-0.5,1 | VerticalLayoutGroup | ContentSizeFitter
  MainCaption @247,-15 | TextMeshProUGUI 'DEPLOYING ON LOCATION' 42pt
  SubCation  @247,-66 | TextMeshProUGUI 'GAME MODE NOT SET' 18pt
Logo @0,54 713x317 anc 0.5,0-0.5,0 | Image
Location Name Panel @130,-170 109x27 anc 0.5,1-0.5,1
  Name @25,0 | CustomTextMeshProUGUI 'TARKOV STREETS' 24pt | ContentSizeFitter
PlayerModelView @-120,0 2710x1232 anc 0.5,0-0.5,1 | RawImage | CameraImage
  DragTrigger @0,0 anc 0.24,0-0.6,0.77
Back Button Panel @0,40 | BackButton 200x42 | 'BACK' 24pt
Deploying Caption @0,195 | 'deploying' 24pt
Loader @0,162 44x44 | Image | Animation
```

Three things in that dump are load-bearing:

- `Logo` is a 713x317 image anchored to the bottom centre -- the Escape from Tarkov wordmark,
  drawn across the art and through the character. Vanilla gets away with it because what is behind
  it is a dim scene. A photograph cannot.
- `PlayerModelView` sits at x=-120 because the stock screen keeps the right-hand half for the
  banner panel. Remove the panel and the character reads as crooked.
- `Loader` carries an `Animation`. Re-anchoring it leaves the animation playing against the frame
  it was authored in, and the spinner loops around the screen instead of spinning in place. It is
  hidden rather than moved. Anything with an Animation component is not yours to re-anchor.

### Centring the character, not its texture

Moving the view to x=0 centres the texture; the character is not in the middle of it. The game
says where it is: `DragTrigger` is anchored around the character so the mouse can turn it, 0.24 to
0.60 on this build, so its centre is the character's centre and the view shifts by the difference.
Derived at runtime and logged, rather than a number someone eyeballed once at one resolution:

```
character sits at 0.42 across its view, shifting 217px to centre it
```

### The screen is laid out more than once

Applying the arrangement at screen-show and walking away does not hold. The game sets those
positions again when it changes state -- the countdown before a raid is the visible one -- and
whatever writes last wins, so the screen snapped back to stock partway through every load. The
report is what ruled out the mod's own teardown: `loading-screen-disabled` came at 79.7s against a
countdown at 38.4s.

`ScreenLayout.Keep()` re-asserts from the existing `Update`, comparing before writing so it costs
nothing per frame, and says once per element when the game pushes back. Re-assert; do not set.

### Overscan was eating a tenth of every picture

`RequiredOverscan` works out what the drift actually needs and takes whichever is larger of that
and the configured floor. The floor shipped at 1.12, and the real requirement at the default drift
is 1.0134 -- so 10.7% of every picture was cropped for margin nothing used. Default is now 1.02,
and the setting's description no longer claims "there is no reason to lower it", which was simply
false. Arithmetic, for the next person who wonders:

```
frustum at 7.80u : 21.52 x 9.01 u      plane at x1.12 : 23.98 x 10.04 u  -> 89.3% visible
drift needs      : x1.0134             plane at x1.02 : 21.84 x 9.14  u  -> 98.2% visible
```

### Art: the wiki, and the trap in it

`scripts\fetch-wiki-art.ps1` fills the banner folders from the Escape from Tarkov wiki. Two things
in it are not obvious.

The biggest images on a map's page are the wrong ones. They are cartographic 2D maps up to
10694x6016, which behind a PMC look absurd. The filter keeps landscape images at screen-like
proportions, rejects icons, quest overlays, keys and portraits, and prefers the official
"Showcase" set -- BSG's own capture of each location, 1920x1080.

Fandom serves WebP from a .png URL. Content negotiation, and it ignores an `Accept: image/png`
header -- tested, not assumed. Unity's `ImageConversion.LoadImage` reads PNG and JPEG only, so
fifty files arrived with the right names in the right folders and loaded as nothing:

```
not a readable PNG/JPG: ...banners\Shoreline\01 - Shoreline Showcase 15.png
art-planes=0
```

`format=original` on the URL is what makes it hand over the real file -- 3.5MB PNG against a 758KB
WebP. If art ever silently fails to load again, check the magic number before anything else.

At 1920x1080 these upscale about 1.8x on a 3440-wide screen and the mod says so. The wiki has
nothing larger that is a photograph rather than a map; a player's own screenshots will be sharper.

### Finish work

- Two gradient scrims, top and bottom, built from the same image component the art planes use so
  no new UI assembly reference is needed, drawn as first siblings so they sit over the backdrop and
  under the character and the type. Squared falloff, so the darkness gathers at the edge rather
  than greying the band. White text on a bright sky is unreadable and a real screenshot has plenty
  of bright sky.
- The bottom scrim stops 64px short of the bottom edge. The menu task bar -- hideout, traders, and
  whatever tabs other mods have added -- is not part of this screen and is drawn underneath it. A
  scrim running to the edge dims someone else's UI, and it reads as that mod being broken.
- The map name as a title card: 44pt, uppercase via TMP's `FontStyles` (0x10, built from the live
  enum type since the assembly is not referenced), 6 units of tracking.
- The intel header in brass, `<color=#C8A45C>`, with rich text switched on for that field first --
  TMP prints the tags literally otherwise.

### Everything is put back

`ScreenLayout` records anchor, anchorMax, pivot, position and font size before touching anything,
and restores in reverse order, before the staging area's own restore, so the screen returns to its
own shape before the art it was arranged around goes. The scrim textures are destroyed, not leaked.
The deploy screen is reused between raids: a value left behind is permanent for the session.

Every element is found by the exact path the dump prints, and a miss is recorded and reported
(`not found: ...`) rather than worked around. Those names came from one game version.

## 1.7.1: two vignettes, and the cost of a photograph

### The black frame was post-processing, twice

Chased through four wrong answers before the log was made to say it: the near-haze plane, the
overscan floor, the camera changing under the planes, and the title's backing plate. The first
three were not it, and the fourth was a real dark box but a different one.

What it actually was:

```
PrismEffects: useVignette=True, vignetteStart=0.9, vignetteEnd=0.4, vignetteStrength=1
screen 3440x1440, desktop 3440x1440, fullscreen=True, camera target=screen
```

The menu camera darkens its own edges in post, after everything else is drawn. **No plane can be
sized out of that**, which is why every geometry change did nothing. Over the stock dim backdrop
nobody has ever seen it; over a photograph it is a black border on all four sides.

And then it survived being switched off, because **there are two of them**. The character preview
renders through its own `PrismEffects`, with its own vignette, into a RawImage that covers most of
the screen -- so its darkened edges are a frame over the art just as surely. Both are taken now,
both put back on restore, and the log names each camera:

```
vignette off on 'MainMenuCamera' (was True)
vignette off on 'Camera_timehascome0' (was True)
```

Lesson worth keeping: **when an effect is applied to the finished image, no amount of measuring
geometry will find it.** `ReportBorderSuspects` prints every field on a camera whose name mentions
vignette, border, letterbox, aspect, resolution, mask, crop or fit, with its live value. That one
diagnostic ended a search that four builds of reasoning had not.

The resolution theory died in the same line: `camera target=screen`, screen matching desktop,
`fullscreen=True`, and `MenuCameraResolutionFixer` holding no size fields at all.

### The cast shadow had nothing to fall on

`MaskAndShadow` on the preview camera draws the character's shadow onto the wall of the room he is
standing in. The staging area hides that room, so the shadow hangs beside him instead:

```
MaskAndShadow on 'Camera_timehascome0': ShadowShift=(-0.03, -0.01), ShadowBlurIterations=4,
  ShadowBlurStrength=4, ShadowStrength=0.5 -- cleared 2 shadow field(s)
```

Only the fields whose names say shadow are cleared. Switching the component off would take the
mask with it, and the mask is what stops the character arriving in a grey box.

### The art was stalling the load, and the reports proved it

Real screenshots are not free. The wiki's PNGs are about 3.8 MB each at 1920x1080, Unity decodes
them on the deploy screen, on the frames the raid is already loading, and five per map on first
sight of that map is measurable:

```
1.7.0  bigmap        gaps=48  stalled=48.3s  worst=16.5s   <- first Customs load
1.7.0  bigmap        gaps=15  stalled= 5.5s  worst= 1.6s   <- second, art cached
1.7.0  Interchange   gaps=37  stalled=31.0s  worst=15.2s   <- first Interchange load
```

The same picture as JPEG at quality 92 is about 500 KB -- 3,788 KB to 504 KB on Customs 20 -- for
no visible loss behind a character. The fetcher now converts on the way in, and the whole set went
from 156 MB to 22 MB. A PNG with an alpha channel throws a bare "generic error" out of GDI+ when
saved straight to JPEG, so it is drawn onto an opaque surface first.

**This is the honest trade of the wiki-art feature**: it is the mod's own diagnostics that caught
it, which is the argument for having kept them.

### sandbox_high and factory4_night

The game reports the location id, and high-level Ground Zero and night Factory have their own ids
while sharing a wiki page with their day/low counterparts. Without an entry each they fall through
to `_default`, and an empty `_default` means stock banners on those two maps while every other map
has art. Both are mapped now.

### The apostrophe, again

1.6.0 shipped inert because a config key contained an apostrophe. Tonight a new setting --
"Remove the character's cast shadow" -- did it a second time, in the same session, after the guard
written for exactly that bug had been added:

```
ArgumentException: Cannot use any of the following characters in section and key names: = \n \t \ " ' [ ]
  at DeployScreen.Client.DeployScreenPlugin.Awake ()
```

The guard catches it. It was not run before installing, because running it is optional. **A check
that has to be remembered is a check that will be skipped**; it belongs in pack.ps1, where nothing
can be packed or installed without it.

## 1.7.2: the border was ours all along

Six builds went looking for what was drawing a rectangle inset from the screen edges. The near
haze, the overscan floor, the camera moving under the planes, the title's backing plate, both
cameras' vignettes, the shadow catcher, ambient occlusion, MenuOverhaul's background plane. None
of them. It was the mod's own scrims:

```
'Matchmaker Time Has Come/DeployScreen Scrim Top'    x 440..3000 y 1008..1440 (22%) | Image a=0.62
'Matchmaker Time Has Come/DeployScreen Scrim Bottom' x 440..3000 y   85.. 288 (10%) | Image a=0.44
```

On a 3440 wide screen. Anchored 0..1 of the deploy screen's own rect, on the assumption that rect
was the screen. **It is not**: it stops 440px short on each side and 21px short at the bottom. So
two translucent panels with hard vertical edges sat over the art for six builds while the search
went through everything the *game* draws.

The scrims now overhang: 2000 units sideways, 140 up and down. The gradient runs vertically, so
stretching it sideways costs nothing, and the darkest end of each falls off-screen where it cannot
draw an edge against anything. The task-bar lift is gone too -- stopping short of the bar left a
hard line with bright art beneath it, which is worse than the dimming it was avoiding, and the bar
is drawn over this anyway.

### What actually found it

Not reasoning. A census. `ReportBigPanels` walked the whole canvas, projected every graphic's
corners into screen pixels, ranked them by area and printed the top twenty with colour and alpha.
The answer was the first line of output.

An earlier version of the same census asked for anything covering **a quarter of the screen or
more** and printed an empty list. That empty list was read as "nothing large is drawn over the
art", when what it meant was "the threshold was wrong": the scrims cover 22% and 10%. **A
diagnostic that can come back empty will eventually come back empty and be believed.** Rank
everything; let the reader pick.

The same census also answered, in one line each, two things that had each cost a build:
`PlayerModelView x 202..3816` -- the centring shift pushed the preview texture 202px off the left
edge -- and, from the renderer census, all 13 objects under `CultistLayout` reading `[off]`, which
closed the MenuOverhaul theory for good.

### Corrections to what 1.7.1 says

1.7.1 claims the black frame was the menu camera's post-processing vignette, found by
`ReportBorderSuspects`. The vignette **is** real and is still switched off -- `useVignette=True,
vignetteStrength=1` on `MainMenuCamera` -- but it was not what the border reports were about. The
border survived turning it off, and survived every other effect being disabled. Read that section
as "a real thing that was fixed along the way", not as the answer.

The preview camera's vignette was already off (`was False`), so it never mattered. Tracking the
two cameras in one field, though, meant the second call overwrote the first and the backdrop
camera's vignette was never given back -- it stayed off for the rest of the session. Fixed.

### Cleared out

The probes that found their answers are gone: `Corners`, `ReportBorderSuspects`, `NameHints`,
`ReportRenderers`, `ReportBigPanels`, and the camera effects roll-call. 358 lines. Their findings
are in this file, which is where a spent diagnostic belongs.

`SimplifyPreview` is narrowed back to `AmbientOcclusion` and `MaskAndShadow`. It briefly disabled
the preview's whole post stack -- PrismEffects, Bloom, ChromaticAberration, DesaturateEffect,
CameraMotionBlur -- while the border was suspected there. Disabling those changed how the character
looks for no proven benefit.

`DumpScreen`, `DumpPreview`, `DumpInto` and `WatchForCountdown` stay, behind
**Performance / Report the screen layout**, off by default. Sixty lines nobody needs on an ordinary
run, and exactly what is needed the day a game update renames something `ScreenLayout` moves --
which is how those names were found in the first place.

### The final countdown screen, for whoever picks it up

`Matchmaker Final Countdown`, a sibling of the deploy screen under `Menu UI/UI`, everything
centre-anchored:

```
Logo @0,14 713x317 | Image
Player Name Panel @0,-10 | Name 'PITTEST' 23pt | Description 'YOUR MAIN CHARACTER' 11pt
Get Ready Panel @0,-55 230x36 | Image rgba 0.80,0.00,0.00,0.60 | Text 'GET READY' 24pt | Glow
Deploying Caption @0,-215 | 'Deploying in:' 24pt
Time Icon @-100,-263 43x43 | Time @-64,-259 '00:28.005' 42pt
```

The catch is not the layout, it is the timing: `loading-screen-disabled` fires at ~82s and restores
the staging area, and the countdown appears after that, so it draws over the stock menu room. Making
it a continuation of the deploy screen means holding the art through the countdown, which means
changing when the restore runs -- and that restore is the thing that guarantees nothing outlives the
screen. Not a change to make casually.

### Still open

**Back does not return to the menu.** The abort fires -- the report ends `cancel-requested` -- but
there is no `loading-screen-disabled` event at all, so the screen never closes. `ScreenLayout` is
the prime suspect: it re-anchors `Back Button Panel`, which carries a HorizontalLayoutGroup, a
ContentSizeFitter and a LayoutElement, and hides four objects the game may expect. One run with
**Rearrange the screen = false** splits it.

**First-load stutter.** 3.8 MB PNGs became ~500 KB JPEGs, which ought to help, but it has not been
shown: Reserve ran 7 gaps / 3.9s on PNGs and 54 gaps / 40.2s on JPEGs, in different sessions with
different bot loads. A clean test is the same map twice in one session.

## The hitching on the deploy screen -- what it actually is

The user reports heavy hitching while waiting to get into a raid, and guessed it was the game
world being rendered. **It is not**, and the distinction matters for what can be done:

- **The raid world is not rendered during the deploy screen. It is being built.** The load runs
  through `EFT.AssetsManager.AssetsManager/LoadSceneOperation`,
  `EFT.LoadScenesFromPresetOperation` and `Streamer`, all of which call `SceneManager.LoadSceneAsync`.
- **Unity's async scene load is only asynchronous in its reading.** Integration -- instantiating
  objects, uploading textures, warming shaders -- happens on the **main thread, in slices, every
  frame**. That is the hitching, and no mod can move it off the main thread because Unity does not
  offer that.
- What *is* being rendered is the **menu**: the `EnvironmentUIRoot` scene, the PMC preview on the
  WeaponPreview layer, and the canvas.

### The game already tunes the obvious levers

Checked before touching anything, and worth knowing before anyone "optimises" these again:

- `LoadScenesFromPresetOperation/CG_LoadPresetAsync` reads `QualitySettings.asyncUploadTimeSlice`
  and `asyncUploadBufferSize`, **doubles both** (`get` then `ldc.i4.2` then `mul` then `set`) for
  the load, and restores them at the end.
- `Streamer/CG_LoadSceneCoroutine` sets `Application.backgroundLoadingPriority` to `Low (0)` while
  streaming chunks -- deliberately, so in-raid streaming does not cost frames.
- `ApplicationConfig` carries `QualitySettingsAsyncUploadTimeSlice` and
  `QualitySettingsAsyncUploadBufferSize` as configured values.
- There is an `FPSLimit` MonoBehaviour that writes `vSyncCount` and `targetFrameRate` every
  `Update` when its `SetFps` is true -- so anything we set could be overwritten by it. Capture and
  restore, and expect it may not hold on every screen.

So the naive win -- "raise the async upload budget" -- is already taken, and overriding it blindly
would be arguing with tuning done by people who could profile it.

### What is left, and what was built (`LoadEase.cs`)

Only two honest categories: reduce what the loader competes with, and stop this mod adding stalls
of its own.

1. **Decode art early** (default **on**). This is the one unambiguous fix, because the stall was
   ours. `Texture2D.LoadImage` is main-thread work -- tens of milliseconds for a 4K JPEG -- and it
   was happening inside `MatchmakerBannersPanel.Show`, i.e. *during the load*. `BannerArt.Prewarm`
   now runs from a postfix on `MatchmakerOfflineRaidScreen.Show`, while the player is still picking
   a time of day. Same work, same cache, different moment. Staging decodes only the one picture it
   will use (`StagingArea.PictureIndex` is shared so both agree); Enhanced decodes all of them.
2. **Frame rate cap** (default off). Fewer menu frames leave more machine for the loader. Refuses
   anything under 10 fps, because `LoadTrace` counts a stall at 100 ms and a lower cap would make
   every ordinary frame read as a hitch -- the setting would flatter itself in the measurement
   meant to judge it.
3. **Loading priority** (default *leave alone*). `Application.backgroundLoadingPriority`, the
   documented frame-rate-versus-load-speed trade. Off by default and the docs say why: raising it
   makes each frame do **more** integration, so frames get *longer* even as the load finishes
   sooner. That may read as worse hitching, not better. It is a real lever and an honest coin-flip.
4. **Pause character IK** (default off). `MenuPlayerPoser` runs FinalIK `LimbIK[]` solvers, twist
   relaxers and two hand posers in `LateUpdate` every frame for one model. Disabling the component
   stops that; the model stays and its Animator keeps running.

Everything is captured before it is changed and restored on every exit path `LoadingPerformance`
already owns -- a frame-rate cap or a loading priority left behind would follow the player out of
the menu into everything else.

### The part that matters most

**None of 2-4 is measured, and "make sure there are no hitches" cannot be answered without
measuring.** Every setting is written into the report (`easeFrameRateCap`, `easeLoadPriority`,
`easeCharacterIkPaused`, `easePrewarmArt`) precisely so a comparison is possible: one lever at a
time, `Record loading` on, same map and settings, at least three runs each, and
`compare-loading.ps1` to read them. Anyone who claims one of these helps without that has not
demonstrated anything.

## Future work

- **The real map.** Pocketmap bundles are unencrypted UnityFS `2022.3.43f1` holding
  `map_tile_<x>x<y>_<scale>` Texture2D tiles and a `Config_scale<N>.asset`. Customs at scale
  8 is exactly 30x14 = 420 tiles. Use `_8`: **0.19-4.93 MB**. Two real constraints: only six
  maps have `plan` bundles (customs, factory, interchange, shoreline, sanatorium, woods), the
  rest are 18-88 MB `map_tile_scale_*` or absent; and the game may already hold a bundle
  open, which makes `AssetBundle.LoadFromFile` throw.
- **Raid-aware mood.** `RaidSettings.TimeAndWeatherSettings` gives `HourOfDay`, `RainType`,
  `FogType`, `CloudinessType` before deploy; drive `MainScreenLights` from it.
- **Camera drift** on `EnvironmentUIRoot.CameraContainer` (`RandomRotate`,
  `GetRandomOffset`, `RotateBack` already exist).
- **Seasonal dressing** via `EnvironmentUI.Events`: `Halloween=1 Christmas=2
  HalloweenIllumination=3`.
- **Per-map player stats** -- `IClientSession.OverallAccountStats` is a `CountersCollection`
  (`GetLong(params object[])`), but the key schema is only knowable at runtime and there is
  no played profile to read it from.
- **`Location.Preview`** is empty on every map -- a per-location asset slot BSG never used.
- **Arbitrary banner counts**, by cloning `location.Banners` and restoring it after.
- **`VideoEnvironmentUIRoot`** is plumbed (`VideoPlayer[]`, `UnityEngine.VideoModule.dll`
  ships) but `StreamingAssets\Video` and `\Intro` are empty.

## Publishing

- GUID `com.mybutthasarash.deployscreen`, following SPT-Casino's registered prefix.
- Version lives in the csproj `<Version>` and `PluginVersion`; `pack.ps1` refuses to pack if
  they disagree.
- The zip holds only the four files under `BepInEx/plugins/DeployScreen/`. Release zips are
  tracked in git, as in the sibling repos. The one exception so far: **1.3.0's zip was deleted
  in 1.3.1**, on the user's instruction, because it shipped defects and nobody should download
  it by mistake. Deleting rather than overwriting keeps every zip's name matching the version
  its DLL reports, which `pack.ps1` relies on; git history still holds the file.
- `-Install` never clobbers an existing `environments.txt` or anything under `banners\`.
- **The Forge forbids mods substantially written by AI agents.** The user has acknowledged
  this for their other repos and said "we're all good" -- do not re-raise it unprompted.
- Existing mods in this space: Environment Replace (kmyuhkyuk, SPT 4.0.8, v1.6.0) does the
  **main menu background and startup splash**; Raid Movie Background Replacer does the menu.
  Neither touches the deploy screen. SPT's only nearby patch is
  `DisableMatchmakerPlayerPreviewButtonsPatch` in `spt-singleplayer.dll`.

## Git

Remote **https://github.com/JoelHauser/LoadingRaid.git**, public, on `main`. The local folder
is `SPT-DeployScreen` and the GitHub repo is `LoadingRaid`: the same name split as
`SPT-NoMagazineCamo` -> `CamoPatch`, so it is the house pattern rather than a mistake.

The global git identity is unset on this machine. The sibling repos commit as
`Joel Hauser <jhauser@bostonlightsource.com>` -- **not** the gmail address -- and this repo
is configured the same way, locally. Commit bodies are prose and end with a
`Co-Authored-By:` trailer, as SPT-Casino's do.

1.0.0's first push carried a BOM in its subject (see Traps) and was amended and force-pushed
with `--force-with-lease`, from `1a180c8` to `58a7b30`, minutes after the repo was created.

## Where this was left off

2026-09-16: **1.1.0** added motion and map intel, and corrected the location-id casing and the
"tested" claim in the docs.

**1.1.1** fixed the intel text found broken by the preview -- game names for maps, extracts and
bosses, a stable most-likely-first boss order, a real "always open" rule -- and switched
file-name captions from `|` to `;`. Pushed as `e044ac9`.

**1.2.0** adds resolution handling: banners measured on screen, several sizes of one picture
with the smallest sharp one chosen, centre-cropping to the banner's shape, and the ideal size in
the log. Built clean against SPT 4.1.5 / BepInEx 5.4.23.5, packed to
`releases\DeployScreen_V1.2.0.zip`, and `scripts\test-logic.ps1` passes.

**1.2.1** is performance only -- no feature changes, and the four that were done were picked out
of a read of the whole client:

1. The driver's idle frame: read `_locationBanners` once per `Show` and scan by index, instead of
   a reflective re-walk and a boxed enumerator every frame for the length of the deploy screen.
2. Motion skips banners that are not on screen, so the canvas is not rebuilt for the five or six
   invisible ones.
3. `BannerDriver._attached` is cleared in `Begin`. It never was, and banners are new objects each
   raid, so it grew by a page of dead components per raid for the session.
4. Memory: the measurement is kept in the config between sessions, and `ReleaseUnused` frees the
   sizes this screen has no use for when the panel closes.

Built clean, packed to `releases\DeployScreen_V1.2.1.zip`, and `test-logic.ps1` is 31 passed /
exit 0 -- exit 0 being new, see Traps.

Three more were found and **not** done, in the same read, roughly in value order: `Intel.Build`
re-resolves the LocalizationManager on every lookup and walks the whole `QuestsData` list (hundreds
of entries on a real profile) reflectively, all synchronously in the `Show` prefix;
`ScreenFit.TryMeasure` calls `GetComponentInParent<Canvas>()` on every attempt for up to 120 frames
and `Measure` re-`GetComponent`s a `KenBurns` the driver already holds; and per-call array
allocations in `Intel.Tidy`, `BannerArt.CaptionsFrom` and `BannerArt.Read`.

There is also one bigger idea deliberately left alone: if the banners share the menu's main
Canvas, a single moving banner re-batches that whole canvas every frame, and a nested `Canvas`
component on the banner image would isolate it. That is only worth doing with a profiler, and it
can shift sorting.

**Still not installed, and still never run in the game** -- so none of 1.2.1 is measured either.
It is reasoning about allocation and canvas rebuilds, not a profile.

## 1.3.0: loading-performance experiment

The user authorized instrumentation and a minimal-screen comparison after asking whether this
could reduce EFT's loading hangs. **There is still no runtime evidence of an improvement over
stock, and nothing in this change rewrites the game's asset loading.** Default presentation
remains Enhanced; Record loading defaults on. Minimal is explicitly opt-in.

- `LoadingPerformance.cs`: hooks deployment Show, status changes, abort requests and raid start;
  a MonoBehaviour on the persistent plugin samples frame intervals after the screen closes too.
- `LoadTrace.cs`: pure managed monotonic accounting. Detailed phase, gap and event lists cap at
  128 each, with dropped counts; aggregate frame-gap totals continue beyond the caps.
- `MinimalScreen.cs`: plain background, preview/banner suppression, environment-root suspension,
  and restoration. UnityEngine.UI.Image is resolved in GameTypes and added by Type; there is
  still no compile-time UI or game assembly reference.
- `scripts/test-performance.ps1` compiles the actual LoadTrace source with
  `tests/PerformanceChecks.cs`, then tests report comparison with temporary JSON fixtures.
- `scripts/compare-loading.ps1` groups by map, mode, resolution, label, version, relevant
  settings and applied minimal features; incomplete/canceled/unfocused reports are excluded.

### Verified by Cecil against the patched 4.1.5 assembly

- `MatchmakerTimeHasCome.Show(IEftSession, RaidSettings, MatchmakerPlayersController)` returns
  void, invokes banner-panel Show, subscribes to matching progress, and calls ShowPlayerModel.
- `ShowPlayerModel(PlayerVisualRepresentation)` returns Task. Its state machine waits for
  preview availability, checks ScreenController.Closed, disposes `_playerModelSubscriber`,
  registers `_playerModelView` for disposal, then gets profile visual equipment and calls
  `PlayerModelView.Show`. Minimal skips this outer method only on the active loading screen.
- `ChangeStatus(string, float?)` receives status BEFORE percentage/timer formatting. A prefix
  records the supplied string only when it changes; it does not poll TMPro every frame.
- `AbortMatching()` delegates to the matchmaker. Reports say `cancel-requested`, not that
  cancellation was confirmed.
- `GameWorld.OnGameStarted()` invokes `_afterGameStarted`. Its postfix marks that point;
  the first subsequent plugin Update completes a successful capture. This is a proxy endpoint,
  **not proof that input is enabled or that every loading hitch has finished**.
- `EnvironmentUI._currentEnvironment` is an EnvironmentUIRoot Component;
  `_lastVisibleStateEnvironment` holds the state requested by ShowEnvironment(bool).
  ShowEnvironment toggles `_commonContainer` and the current environment root GameObjects.
  **Do not call ShowCameraContainer(false) as a shortcut:** its IL also toggles the game's
  main camera outside a raid. Minimal disables only the environment root, and declines that
  operation if the screen is its descendant or the screen's UI camera lives under it.

### Modes and lifecycle

`Performance / Loading screen` is snapshotted in the deployment Show prefix. Enhanced runs the
existing banner pipeline. Vanilla bypasses this mod's custom banners, driver and backdrop
changes, while retaining optional diagnostics. Minimal independently skips the banner Show
Task and preview Task, preserving the rest of the game's Show method, status, input and party
controls. Hooks fail individually and their availability is logged; the report records actual
minimal features applied, so partial fallback captures are not pooled with complete ones.

The background is an opaque, non-raycast Image at the screen's first sibling. Environment
suspension is allowed only after background creation succeeds and a usable UI canvas is found.
The current root is checked every 0.5 seconds for scenes already loading when deployment began.
Restoration runs on screen OnDisable, abort request, replacement by a new load, diagnostic error,
Show failure, capture timeout, raid-start completion, or plugin destruction. Only objects that
were active when we disabled them are restored; only the current environment root is restored,
and only if EFT still requests it visible. No scene is unloaded or additional artwork decoded.

Screen closure alone is not success. Without the raid-start callback, capture waits at most
30 seconds after screen closure and reports `screen-closed-without-confirmed-start`. Overall
capture timeout is 30 minutes. A Show finalizer observes original exceptions without suppressing
them. These are optional hooks, installed separately from the existing banner/environment ones.

### Measurement limits and test procedure

Capture starts at the deployment Show prefix, so earlier setup is outside its scope. Stopwatch
wall time measures gaps between Update callbacks and capture boundaries. Focused gaps >=100 ms
contribute their FULL interval to the gap total, not just excess above 100 ms. A gap is labeled
with the previous frame's phase; exact phase changes also have independent timestamps. Focus
events between frames are latched so switching away and back cannot count as a loading stall.
This localizes symptoms, not causal functions. A main-thread hang still freezes this recorder;
its duration is visible when the next frame finally runs.

Memory is sampled at boundaries and every five seconds with GC.GetTotalMemory(false) and process
working set. Peaks are sampled lower bounds, working set is not VRAM, and GC count changes do
not prove GC caused a stall. The recorder never forces a collection. Reports contain map/settings,
test label, timing and memory, not profile contents. A detached completed trace is serialized and
written using Task.Run to `<plugin folder>/diagnostics/<UTC timestamp>-<id>.json`. No per-frame
I/O. A crash/force-quit can leave no report; files are retained until manually deleted.

Compare Vanilla/Enhanced/Minimal with the same map, raid settings, other mods, graphics and art.
Restart between modes; use `first` versus `repeat` labels to separate first session loads from
warm repeats, at least three comparable captures per mode, and alternate mode order. A restart
does NOT guarantee a cold OS disk cache. A mod-removed manual run is a useful additional check;
Vanilla has diagnostic overhead and is only an instrumented baseline. Test cancel, return to
menu, second raid, PMC/Scav, and resolution/aspect changes before calling minimal mode working.

Existing 31 image/cropping checks and 11 performance checks pass outside the game. Live hook
execution, background layering, shared-camera fallback, cleanup, overhead and any improvement
remain untested. Caption issues noted during review (custom-art Vanilla captions emptied and
file-name descriptions not published to localization) are outside this performance change.

### Handoff for home testing (2026-09-16)

Version 1.3.0 is built and packaged in `releases/DeployScreen_V1.3.0.zip` -- **that archive was
deleted in 1.3.1**, because it ships the three defects fixed there; it is still in git history
at `d155138` if it is ever needed. The archive has
the expected four plugin files, and its DLL hash matches the validated Release build. The
DLL still has no Assembly-CSharp, spt-* or compile-time UnityEngine.UI reference. The build
has zero warnings/errors; the 31 existing logic checks and 11 performance checks passed.
README.md contains the installation steps, mode comparison procedure and report definitions.

The user requested committing and pushing this implementation, the archive and these notes
to `origin/main` at `https://github.com/JoelHauser/LoadingRaid.git`. Nothing has been installed
into the local SPT game. Next work is to inspect the user's home-test results: first verify
screen appearance and cancel/second-raid restoration, then compare the diagnostic JSON from
Vanilla, Enhanced and Minimal under matched conditions. Do not claim improved loading speed
or fewer freezes until those measurements exist.

## 1.3.1: fixes from a review of 1.3.0

**1.3.0 was written by ChatGPT (Codex), not by Claude.** Commit `d155138` carries
`Co-Authored-By: Codex <noreply@openai.com>`; every other commit in this repo carries
`Claude Opus 5`. The 1.3.0 section above was written by Codex in this file's voice, which is
easy to misread as prior Claude work -- it was misread that way once already. Its "Verified by
Cecil against the patched 4.1.5 assembly" claims have **not** been re-checked, and cannot be
here: see the blocked fix below.

What was re-checked, on 1.3.0 as committed: Release builds clean with 0 warnings, two
`--no-incremental` builds are byte-identical so the build is reproducible on this box, and
both suites pass (31 and 11, exit 0). The DLL inside the 1.3.0 archive, as it stood at
`d155138`, differs
from a fresh build in **148 bytes across 7 regions** -- the PE `TimeDateStamp` at 0x88, the MVID,
and the debug directory and PDB checksum near the tail. Everything from 0x8C to 0xE1A7, which
is all of the IL and metadata, is identical. So that archive *is* the committed code; the hash
just does not reproduce.

Three fixes:

1. **A warning budget per raid, not per session.** `LoadingPerformance._warned` was set once and
   never reset, and `Fail()` ends the capture. So the second and every later raid that threw
   wrote no report **and** logged nothing. `Begin` clears it now.
2. **The report says what minimal actually did.** `MinimalScreen` is built in the `Show`
   postfix, but the preview and banner skips happen in prefixes *inside* `Show`. A `Show` that
   threw left no `MinimalScreen`, so the report recorded `minimalPreviewSkipped:false` though
   the preview had been skipped -- and `compare-loading.ps1` groups on exactly those fields, so
   a partial capture could pool with a complete one, which is the thing that change existed to
   prevent. `SkipPlayer`/`SkipBanners` now record what they really skipped and
   `LoadingPerformance.Presentation` falls back to those. 1.3.0 passed `_playerHook` and
   `_bannerHook` -- whether the hook *installed*, not whether it *fired*.
3. **File-name descriptions can appear at all.** `CreateBanner` wrote `image.Name` and
   `image.Description` onto the banner as raw strings, which is the caption trap at the top of
   this file: a raw description localizes to itself and `SelectBanner` hides it. The heading
   showed, the description never could. `PublishFileCaptions` registers them under
   `deployscreen/file/<n>/h` and `/d` before any banner is built, and the banner carries the
   key. If the locale table cannot be reached it falls back to a raw heading and an empty
   description -- no worse than 1.3.0, and never a visible key.

### Found and deliberately not fixed

**"Keep vanilla" captions are still emptied when custom art is on.** `CreateBanner` passes
`string.Empty` for both fields in every mode but `FileName`, so a player who wants this mod's
art with BSG's own lore text gets no caption at all. The fix needs the caption keys off the
`LocationBanner` that `TryCreateBanner` receives, and **that member cannot be resolved on this
box**: `scratchpad\managed415\Assembly-CSharp.dll` has been cleaned up as the notes warned it
would be, `C:\HUH`'s copy is the unpatched original (`EFT.UI.Matchmaker.MatchmakerBannersPanel`
is there, but no `TryCreateBanner` or `CreateBanner` -- still obfuscated, and no type matching
`*LocationBanner*` exists at all), and nothing in the install can apply the 4.46 MB
`Assembly-CSharp.dll.delta`: no `hpatchz.exe`, and no managed HDiffPatch assembly. Guessing the
field name puts a raw wrong key in the heading, where it is visible. **Regenerate the patched
assembly before attempting this**, and add the member to `GameTypes` as an optional one.

**The cancel path restores minimal on request, not on confirmation.** `Aborted` -> `Finish` ->
`Restore()` runs when `AbortMatching()` is called, and that only delegates to the matchmaker. If
a cancel does not take, the menu environment returns while the deploy screen is still up.
Deferring restoration is worse: `Finish` clears `_screen`, so the later `ScreenClosed` would no
longer match and the plain background and the suspended environment would leak for the rest of
the session. Left as is -- **watch for it during the cancel test**.

**Still never run in the game.** These are three reasoned fixes to code that has never executed;
the home-test plan in the 1.3.0 handoff is unchanged, and fix 3 gives it one more thing to look
at: set captions to file names, put `01 - Dorms; Three storeys, two keys` in a map folder, and
check the description appears under the heading.
