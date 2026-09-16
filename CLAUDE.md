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
scripts/test-logic.ps1  ImageHeader, cropping, size choice and captions, checked against real files
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
  tracked in git, as in the sibling repos.
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

Version 1.3.0 is built and packaged in `releases/DeployScreen_V1.3.0.zip`. The archive has
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
both suites pass (31 and 11, exit 0). The DLL inside `releases\DeployScreen_V1.3.0.zip` differs
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
