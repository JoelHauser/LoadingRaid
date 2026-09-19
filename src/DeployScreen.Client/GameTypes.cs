using System;
using System.Reflection;
using HarmonyLib;

namespace DeployScreen.Client
{
    /// <summary>
    /// Every game type, method and field this plugin touches, resolved by name at load.
    ///
    /// Nothing here is compiled against Assembly-CSharp on purpose. The copy in Managed is
    /// the unpatched original until the SPT Launcher applies its delta, and that delta
    /// renames obfuscated types -- so a compile-time binding is a binding to names the
    /// running game may not have. Looking them up by their patched name at runtime works on
    /// either, and fails loudly in one place instead of silently at the patch site.
    ///
    /// Each Resolve* returns false if its own feature cannot work, and the plugin then
    /// installs only the parts that can. New game members belong here, never at the patch
    /// site: this is the only place a rename has to be noticed.
    /// </summary>
    internal static class GameTypes
    {
        // ---------------------------------------------------------------- banners

        internal static Type BannersPanel;
        internal static MethodInfo BannersPanel_Show;
        internal static MethodInfo BannersPanel_TryCreateBanner;
        internal static MethodInfo BannersPanel_CreateBanner;
        internal static MethodInfo BannersPanel_Close;

        /// <summary>JsonType.LocationSettings+Location -- the map being deployed to.</summary>
        internal static Type Location;
        internal static FieldInfo Location_Id;

        // ----------------------------------------------------------- banner driver

        internal static FieldInfo BannersPanel_LocationBanners;
        internal static Type BannerWithToggle;
        internal static FieldInfo BannerWithToggle_Banner;
        internal static Type Banner;
        internal static FieldInfo Banner_BannerImage;
        internal static FieldInfo Banner_BannerCanvasGroup;
        internal static FieldInfo Banner_BannerName;
        internal static FieldInfo Banner_BannerDescription;

        // ------------------------------------------------------------------ intel

        internal static FieldInfo Location_MongoId;
        internal static FieldInfo Location_Name;
        internal static FieldInfo Location_EscapeTimeLimit;
        internal static FieldInfo Location_AveragePlayTime;
        internal static FieldInfo Location_AveragePlayerLevel;
        internal static FieldInfo Location_BossLocationSpawn;
        internal static FieldInfo Location_Exits;

        internal static FieldInfo BossSpawn_BossName;
        internal static FieldInfo BossSpawn_BossChance;
        internal static FieldInfo Exit_Name;
        internal static FieldInfo Exit_Chance;
        internal static FieldInfo Exit_PassageRequirement;

        internal static Type LocalizationManager;
        internal static PropertyInfo LocalizationManager_Instance;
        internal static PropertyInfo LocalizationManager_Culture;
        internal static MethodInfo LocalizationManager_UpdateLocale;
        internal static MethodInfo LocalizationManager_LocalizedValue;
        internal static MethodInfo LocalizationManager_TryGetLocalization;

        /// <summary>EFT.Locale, which is itself a Dictionary&lt;string, string&gt;.</summary>
        internal static Type Locale;

        internal static FieldInfo Profile_QuestsData;
        internal static FieldInfo QuestData_Status;
        internal static FieldInfo QuestData_Template;
        internal static FieldInfo QuestTemplate_LocationId;
        internal static FieldInfo QuestTemplate_TemplateId;
        internal static FieldInfo QuestTemplate_Id;

        // ------------------------------------------------------------ environment

        internal static Type EnvironmentUI;
        internal static PropertyInfo EnvironmentUI_Instance;
        internal static PropertyInfo EnvironmentUI_Instantiated;
        internal static MethodInfo EnvironmentUI_SetEnvironmentAsync;
        internal static FieldInfo EnvironmentUI_Environments;

        /// <summary>EFT.EEnvironmentUIType -- Random/Factory/Wood/Laboratory/Unheard/Cyber.</summary>
        internal static Type EEnvironmentUIType;

        internal static Type EnvironmentData;
        internal static FieldInfo EnvironmentData_Type;

        // ----------------------------------------------------- environment state

        /// <summary>
        /// EnvironmentUI._currentEnvironmentUiType -- the environment that is actually live.
        ///
        /// This is the field that makes restoring possible. The player's choice lives in
        /// GameSettingsGroup.EnvironmentUiType, and EnvironmentUI.Awake() binds to it: when the
        /// setting changes, the binding calls SetEnvironmentAsync. Calling SetEnvironmentAsync
        /// ourselves changes this field and the live scene but never touches the setting, so
        /// nothing puts it back -- the override then outlives the deploy screen. Capturing this
        /// before we change anything is what lets us undo it exactly.
        /// </summary>
        internal static FieldInfo EnvironmentUI_CurrentEnvType;

        /// <summary>ShowEnvironment(bool) -- also what sets _lastVisibleStateEnvironment.</summary>
        internal static MethodInfo EnvironmentUI_ShowEnvironment;

        /// <summary>EnableOverlay(bool) -- the game's own readability scrim, alpha 0.4.</summary>
        internal static MethodInfo EnvironmentUI_EnableOverlay;

        /// <summary>
        /// Comfort.Common.Singleton&lt;EFT.CustomizationSolver&gt; and
        /// GetAvailableEnvironmentUIs(EPlayerSide) -- the environments the player has actually
        /// unlocked, which is the list GetRandomEnvironment picks from. The _environments array
        /// is only which scenes the build ships. Optional: unreadable means "do not block".
        /// </summary>
        internal static PropertyInfo CustomizationSolver_Instance;
        internal static PropertyInfo CustomizationSolver_Instantiated;
        internal static MethodInfo CustomizationSolver_GetAvailable;
        internal static FieldInfo CustomizationEnvironment_Type;

        // ------------------------------------------------------------- scene depth

        /// <summary>EFT.UI.EnvironmentUIRoot -- the backdrop scene's own root.</summary>
        internal static Type EnvironmentRoot;

        /// <summary>Transform CameraContainer -- public, and the whole reason parallax is free.</summary>
        internal static FieldInfo EnvRoot_CameraContainer;

        /// <summary>Light[] MainScreenLights -- public; real lights in a real 3D scene.</summary>
        internal static FieldInfo EnvRoot_MainScreenLights;

        /// <summary>PlayerModelView.ModelPlayerPoser -> MenuPlayerPoser.</summary>
        internal static PropertyInfo PlayerModelView_Poser;

        /// <summary>MenuPlayerPoser.BottomShadow -- a public GameObject, the PMC's ground contact.</summary>
        internal static FieldInfo MenuPoser_BottomShadow;

        /// <summary>
        /// MenuPlayerPoser.set_Patrol. The property is write-only -- there is no readable backing
        /// field -- so what it was before cannot be recovered. That is why patrol is opt-in.
        /// </summary>
        internal static MethodInfo MenuPoser_SetPatrol;

        // ---------------------------------------------------------- the staging area

        /// <summary>GameObject[] MainScreenObjects -- the menu scene's own set dressing.</summary>
        internal static FieldInfo EnvRoot_MainScreenObjects;

        /// <summary>
        /// LayersMaskController.WeaponPreview, a public static int.
        ///
        /// This is the layer the PMC is loaded onto -- PlayerModelView.Show passes it to
        /// PlayerModelLoader.Load along with its own transform as the parent. The model is
        /// therefore a UI-space preview drawn by its own camera, not an object in the backdrop's
        /// 3D scene, and the two are composited.
        ///
        /// That is what makes lighting the character possible at all: a Light whose cullingMask is
        /// just this layer falls on the PMC and on nothing else in the scene.
        /// </summary>
        internal static FieldInfo Layers_WeaponPreview;

        /// <summary>
        /// MatchmakerTimeHasCome._subCaption -- the line under the map name.
        ///
        /// Borrowed for intel rather than building a TextMeshPro object of our own, which would
        /// mean referencing TMPro and guessing at a font and a position. Nothing in
        /// MatchmakerTimeHasCome writes it (ChangeStatus and UpdateStatusText both write
        /// _deployingText), so it can be set and put back.
        /// </summary>
        internal static FieldInfo Loading_SubCaption;

        /// <summary>The staging area can be built: a backdrop camera and somewhere to put art.</summary>
        internal static bool StagingReady { get; private set; }

        // -------------------------------------------------------- vanilla captions

        /// <summary>
        /// JsonType.LocationSettings+Location+LocationBanner.id, and it is public.
        ///
        /// This is what 1.3.1 recorded as unresolvable, which is why "keep vanilla captions"
        /// silently emptied them whenever custom art was on. TryCreateBanner's own IL is
        /// unambiguous about what to do with it:
        ///
        ///     CreateBanner(banner.id + " Name", banner.id + " Description", sprite)
        ///
        /// So the caption keys are simply the id with those two suffixes, and passing them on
        /// gives the player this mod's art with BSG's own text under it.
        /// </summary>
        internal static FieldInfo LocationBanner_Id;

        // ------------------------------------------------------- this raid's weather

        /// <summary>
        /// RaidSettings.TimeAndWeatherSettings -- a public struct, settled before deploy, so the
        /// staging area can be lit for the raid you are about to load rather than for the map in
        /// the abstract. All of its fields are public.
        /// </summary>
        internal static FieldInfo RaidSettings_TimeAndWeather;

        internal static FieldInfo Weather_HourOfDay;
        internal static FieldInfo Weather_RainType;
        internal static FieldInfo Weather_FogType;
        internal static FieldInfo Weather_Cloudiness;
        internal static FieldInfo Weather_WindType;

        /// <summary>This raid's time and weather can be read.</summary>
        internal static bool WeatherReady { get; private set; }

        // ----------------------------------------------------------- raid screens

        internal static Type OfflineRaidScreen;
        internal static MethodInfo OfflineRaidScreen_Show;
        internal static Type TimeHasCome;
        internal static MethodInfo TimeHasCome_Show;

        internal static Type RaidSettings;
        internal static PropertyInfo RaidSettings_SelectedLocation;

        internal static MethodInfo Loading_Show, Loading_Status, Loading_Abort, World_Started;

        /// <summary>
        /// MatchmakerTimeHasCome.ChangeCancelButtonVisibility(bool), which is why the back button
        /// reads active=False early in the screen's life: the game brings it up partway through,
        /// so a click before that lands on nothing at all.
        /// </summary>
        internal static MethodInfo Loading_CancelButton;
        internal static MethodInfo Loading_ShowPlayer;
        internal static FieldInfo Loading_PlayerModel, Loading_Banners;
        internal static FieldInfo Environment_Current, Environment_Visible;
        internal static Type BackgroundImage;
        internal static PropertyInfo Background_Color, Background_Raycast, Background_Sprite;

        /// <summary>Custom banner art can be substituted.</summary>
        internal static bool BannersReady { get; private set; }

        /// <summary>Banners can be reached after creation, for motion and captions.</summary>
        internal static bool DriverReady { get; private set; }

        /// <summary>Map intel can be built and published.</summary>
        internal static bool IntelReady { get; private set; }

        /// <summary>The backdrop can be switched.</summary>
        internal static bool EnvironmentReady { get; private set; }

        /// <summary>
        /// The live environment can be read back and put where it was found. Everything that
        /// changes the backdrop is gated on this: without it we decline to change it at all,
        /// because a change we cannot undo is the bug this exists to prevent.
        /// </summary>
        internal static bool EnvironmentRestoreReady { get; private set; }

        /// <summary>The backdrop scene's camera and lights can be reached, so depth can run.</summary>
        internal static bool DepthReady { get; private set; }

        internal static bool Resolve()
        {
            BannersReady = ResolveBanners();
            DriverReady = BannersReady && ResolveDriver();
            IntelReady = DriverReady && ResolveIntel();
            EnvironmentReady = ResolveEnvironment();
            EnvironmentRestoreReady = EnvironmentReady && ResolveEnvironmentState();
            DepthReady = ResolveDepth();
            ResolvePerformance();
            StagingReady = ResolveStaging();
            ResolveExtras();

            return BannersReady || EnvironmentReady || Loading_Show != null;
        }

        private static void ResolvePerformance()
        {
            var screen = AccessTools.TypeByName("EFT.UI.Matchmaker.MatchmakerTimeHasCome");
            var settings = AccessTools.TypeByName("EFT.RaidSettings");
            Loading_Show = settings == null ? null : FindShowTaking(screen, settings);
            if (screen != null)
            {
                Loading_Status = AccessTools.Method(screen, "ChangeStatus", new[] { typeof(string), typeof(float?) });
                Loading_Abort = AccessTools.Method(screen, "AbortMatching", Type.EmptyTypes);
                Loading_CancelButton = AccessTools.Method(
                    screen, "ChangeCancelButtonVisibility", new[] { typeof(bool) });
                Loading_ShowPlayer = AccessTools.Method(screen, "ShowPlayerModel");
                if (Loading_ShowPlayer != null && Loading_ShowPlayer.ReturnType != typeof(System.Threading.Tasks.Task))
                    Loading_ShowPlayer = null;
                Loading_PlayerModel = AccessTools.Field(screen, "_playerModelView");
                Loading_Banners = AccessTools.Field(screen, "_bannersPanel");
            }
            var world = AccessTools.TypeByName("EFT.GameWorld");
            if (world != null) World_Started = AccessTools.Method(world, "OnGameStarted", Type.EmptyTypes);
            if (EnvironmentUI != null)
            {
                Environment_Current = AccessTools.Field(EnvironmentUI, "_currentEnvironment");
                Environment_Visible = AccessTools.Field(EnvironmentUI, "_lastVisibleStateEnvironment");
            }
            // The UI assembly stays a runtime dependency, just as it is for the stock banner Image.
            BackgroundImage = AccessTools.TypeByName("UnityEngine.UI.Image");
            if (BackgroundImage != null)
            {
                Background_Color = AccessTools.Property(BackgroundImage, "color");
                Background_Raycast = AccessTools.Property(BackgroundImage, "raycastTarget");
                Background_Sprite = AccessTools.Property(BackgroundImage, "sprite");
            }
            if (Loading_Show == null) Missing("loading screen Show (performance modes and diagnostics unavailable)");
            if (Loading_Status == null) Missing("loading ChangeStatus (phase markers unavailable)");
            if (World_Started == null) Missing("GameWorld.OnGameStarted (reports cannot confirm raid start)");
        }

        private static bool ResolveBanners()
        {
            BannersPanel = AccessTools.TypeByName("EFT.UI.Matchmaker.MatchmakerBannersPanel");
            Location = AccessTools.TypeByName("JsonType.LocationSettings+Location");

            if (BannersPanel == null) return Missing("EFT.UI.Matchmaker.MatchmakerBannersPanel");
            if (Location == null) return Missing("JsonType.LocationSettings+Location");

            // Show(Location, ESideType, ProfileStats, IImageLoader) -- where the panel learns
            // which map it is drawing banners for, and where the session arrives.
            BannersPanel_Show = AccessTools.Method(BannersPanel, "Show");

            // TryCreateBanner(LocationBanner, IImageLoader) -- one call per banner the map
            // declares. This is the seam: replacing it swaps the art without touching the
            // key-binding banners, which reach CreateBanner by a different path.
            BannersPanel_TryCreateBanner = AccessTools.Method(BannersPanel, "TryCreateBanner");

            // CreateBanner(string, string, Sprite) -- the game's own UI wiring, reused so the
            // toggles, fade and selection behave exactly as they do in vanilla.
            BannersPanel_CreateBanner = AccessTools.Method(BannersPanel, "CreateBanner");

            // Only used to drop the panel from the in-flight table. Not essential: without it
            // the table holds one dead entry per panel, which is a leak but not a failure.
            BannersPanel_Close = AccessTools.Method(BannersPanel, "Close");

            Location_Id = AccessTools.Field(Location, "Id");

            if (BannersPanel_Show == null) return Missing("MatchmakerBannersPanel.Show");
            if (BannersPanel_TryCreateBanner == null) return Missing("MatchmakerBannersPanel.TryCreateBanner");
            if (BannersPanel_CreateBanner == null) return Missing("MatchmakerBannersPanel.CreateBanner");
            if (Location_Id == null) return Missing("Location.Id");

            return true;
        }

        private static bool ResolveDriver()
        {
            // The map's own banners, and only those -- key-binding banners never land here.
            BannersPanel_LocationBanners = AccessTools.Field(BannersPanel, "_locationBanners");

            BannerWithToggle = AccessTools.TypeByName("EFT.UI.Matchmaker.BannerWithToggle");
            Banner = AccessTools.TypeByName("EFT.UI.Matchmaker.MatchmakerBanner");

            if (BannersPanel_LocationBanners == null) return Missing("MatchmakerBannersPanel._locationBanners");
            if (BannerWithToggle == null) return Missing("EFT.UI.Matchmaker.BannerWithToggle");
            if (Banner == null) return Missing("EFT.UI.Matchmaker.MatchmakerBanner");

            BannerWithToggle_Banner = AccessTools.Field(BannerWithToggle, "Banner");
            Banner_BannerImage = AccessTools.Field(Banner, "_bannerImage");
            Banner_BannerName = AccessTools.Field(Banner, "BannerName");
            Banner_BannerDescription = AccessTools.Field(Banner, "BannerDescription");

            // SetSelected(true) sets this group's alpha to 1, and the fade-out's completion
            // callback sets it back to 0 -- so it says exactly whether a banner is on screen.
            // Optional: without it, motion runs on every banner as it did in 1.2.0.
            Banner_BannerCanvasGroup = AccessTools.Field(Banner, "_bannerCanvasGroup");

            if (BannerWithToggle_Banner == null) return Missing("BannerWithToggle.Banner");
            if (Banner_BannerImage == null) return Missing("MatchmakerBanner._bannerImage");

            return true;
        }

        private static bool ResolveIntel()
        {
            Location_MongoId = AccessTools.Field(Location, "_Id");
            Location_Name = AccessTools.Field(Location, "Name");
            Location_EscapeTimeLimit = AccessTools.Field(Location, "EscapeTimeLimit");
            Location_AveragePlayTime = AccessTools.Field(Location, "AveragePlayTime");
            Location_AveragePlayerLevel = AccessTools.Field(Location, "AveragePlayerLevel");
            Location_BossLocationSpawn = AccessTools.Field(Location, "BossLocationSpawn");
            Location_Exits = AccessTools.Field(Location, "exits");

            var bossSpawn = AccessTools.TypeByName("BossLocationSpawn");
            if (bossSpawn != null)
            {
                BossSpawn_BossName = AccessTools.Field(bossSpawn, "BossName");
                BossSpawn_BossChance = AccessTools.Field(bossSpawn, "BossChance");
            }

            var exit = AccessTools.TypeByName("JsonType.BackendExitTriggerSettings");
            if (exit != null)
            {
                Exit_Name = AccessTools.Field(exit, "Name");
                Exit_Chance = AccessTools.Field(exit, "Chance");

                // EFT.Interactive.ERequirementState; None = 0, which is also what an exit whose
                // data omits the field comes through as (every Factory exit).
                Exit_PassageRequirement = AccessTools.Field(exit, "PassageRequirement");
            }

            // Intel is delivered through the locale table -- see Localization.cs for why a raw
            // description string would be hidden by the panel.
            LocalizationManager = AccessTools.TypeByName("EFT.LocalizationManager");
            Locale = AccessTools.TypeByName("EFT.Locale");

            if (LocalizationManager == null) return Missing("EFT.LocalizationManager");
            if (Locale == null) return Missing("EFT.Locale");

            const BindingFlags StaticPublic =
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

            LocalizationManager_Instance = LocalizationManager.GetProperty("Instance", StaticPublic);
            LocalizationManager_Culture = AccessTools.Property(LocalizationManager, "Culture");
            LocalizationManager_UpdateLocale = AccessTools.Method(LocalizationManager, "UpdateLocale");

            // Two overloads; the one-argument one applies the current culture itself.
            LocalizationManager_LocalizedValue =
                AccessTools.Method(LocalizationManager, "LocalizedValue", new[] { typeof(string) });

            // TryGetLocalization(string id, string locale, out string localizedValue) -- whether a
            // key exists, which is not the same as whether its text differs from the key.
            LocalizationManager_TryGetLocalization = AccessTools.Method(LocalizationManager, "TryGetLocalization");

            if (LocalizationManager_Instance == null) return Missing("LocalizationManager.Instance");
            if (LocalizationManager_Culture == null) return Missing("LocalizationManager.Culture");
            if (LocalizationManager_UpdateLocale == null) return Missing("LocalizationManager.UpdateLocale");

            // Quests are optional: without them the other cards still build.
            var profile = AccessTools.TypeByName("EFT.Profile");
            if (profile != null) Profile_QuestsData = AccessTools.Field(profile, "QuestsData");

            var questData = AccessTools.TypeByName("EFT.Quests.QuestDataClass");
            if (questData != null)
            {
                QuestData_Status = AccessTools.Field(questData, "Status");
                QuestData_Template = AccessTools.Field(questData, "Template");
            }

            var questTemplate = AccessTools.TypeByName("EFT.Quests.QuestTemplate");
            if (questTemplate != null)
            {
                QuestTemplate_LocationId = AccessTools.Field(questTemplate, "_locationId");
                QuestTemplate_TemplateId = AccessTools.Field(questTemplate, "_templateId");
                QuestTemplate_Id = AccessTools.Field(questTemplate, "_id");
            }

            return true;
        }

        private static bool ResolveEnvironment()
        {
            EnvironmentUI = AccessTools.TypeByName("EFT.UI.EnvironmentUI");
            EEnvironmentUIType = AccessTools.TypeByName("EFT.EEnvironmentUIType");
            RaidSettings = AccessTools.TypeByName("EFT.RaidSettings");

            if (EnvironmentUI == null) return Missing("EFT.UI.EnvironmentUI");
            if (EEnvironmentUIType == null) return Missing("EFT.EEnvironmentUIType");
            if (RaidSettings == null) return Missing("EFT.RaidSettings");

            // Instance and Instantiated are public statics on MonoBehaviourSingleton<T>.
            // FlattenHierarchy is what surfaces a static member declared on the base.
            const BindingFlags StaticPublic =
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

            EnvironmentUI_Instance = EnvironmentUI.GetProperty("Instance", StaticPublic);
            EnvironmentUI_Instantiated = EnvironmentUI.GetProperty("Instantiated", StaticPublic);
            EnvironmentUI_SetEnvironmentAsync = AccessTools.Method(EnvironmentUI, "SetEnvironmentAsync");
            EnvironmentUI_Environments = AccessTools.Field(EnvironmentUI, "_environments");

            EnvironmentData = AccessTools.TypeByName("EFT.UI.EnvironmentUI+EnvironmentData");
            if (EnvironmentData != null)
            {
                EnvironmentData_Type = AccessTools.Field(EnvironmentData, "Type");
            }

            RaidSettings_SelectedLocation = AccessTools.Property(RaidSettings, "SelectedLocation");

            OfflineRaidScreen = AccessTools.TypeByName("EFT.UI.Matchmaker.MatchmakerOfflineRaidScreen");
            TimeHasCome = AccessTools.TypeByName("EFT.UI.Matchmaker.MatchmakerTimeHasCome");

            // Both screens have two Show overloads; the three-argument one carries RaidSettings.
            OfflineRaidScreen_Show = FindShowTaking(OfflineRaidScreen, RaidSettings);
            TimeHasCome_Show = FindShowTaking(TimeHasCome, RaidSettings);

            if (EnvironmentUI_Instance == null) return Missing("EnvironmentUI.Instance");
            if (EnvironmentUI_SetEnvironmentAsync == null) return Missing("EnvironmentUI.SetEnvironmentAsync");
            if (RaidSettings_SelectedLocation == null) return Missing("RaidSettings.SelectedLocation");

            // One hook is enough; the offline screen is only the preferred one.
            if (OfflineRaidScreen_Show == null && TimeHasCome_Show == null)
            {
                return Missing("a Show(..., RaidSettings, ...) on either raid screen");
            }

            return true;
        }

        /// <summary>
        /// What is needed to read the live backdrop back and set it again. Every one of these is
        /// required: a half-resolved restore is worse than none, because it would let us change
        /// the environment without being able to put it back.
        /// </summary>
        private static bool ResolveEnvironmentState()
        {
            EnvironmentUI_CurrentEnvType = AccessTools.Field(EnvironmentUI, "_currentEnvironmentUiType");
            EnvironmentUI_ShowEnvironment =
                AccessTools.Method(EnvironmentUI, "ShowEnvironment", new[] { typeof(bool) });
            EnvironmentUI_EnableOverlay =
                AccessTools.Method(EnvironmentUI, "EnableOverlay", new[] { typeof(bool) });

            // Optional, and fail-open: this only ever narrows what we are willing to ask for.
            var solver = AccessTools.TypeByName("EFT.CustomizationSolver");
            var singleton = AccessTools.TypeByName("Comfort.Common.Singleton`1");
            if (solver != null && singleton != null)
            {
                try
                {
                    var closed = singleton.MakeGenericType(solver);
                    const BindingFlags StaticPublic =
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

                    CustomizationSolver_Instance = closed.GetProperty("Instance", StaticPublic);
                    CustomizationSolver_Instantiated = closed.GetProperty("Instantiated", StaticPublic);
                    CustomizationSolver_GetAvailable =
                        AccessTools.Method(solver, "GetAvailableEnvironmentUIs");
                }
                catch
                {
                    // A generic that will not close is simply an availability check we do without.
                    CustomizationSolver_Instance = null;
                }
            }

            var customization = AccessTools.TypeByName("EFT.Customization.CustomizationEnvironmentUI");
            if (customization != null)
            {
                CustomizationEnvironment_Type = AccessTools.Field(customization, "EnvironmentUIType")
                    ?? AccessTools.Field(customization, "Type");
            }

            if (EnvironmentUI_CurrentEnvType == null)
            {
                return Missing("EnvironmentUI._currentEnvironmentUiType "
                    + "(the backdrop will not be changed at all, since it could not be put back)");
            }

            return true;
        }

        /// <summary>
        /// The backdrop scene's camera and lights, and the PMC's poser. All optional in the sense
        /// that each effect checks its own member -- but with no camera container there is no
        /// parallax, which is most of the point, so that one gates the group.
        /// </summary>
        private static bool ResolveDepth()
        {
            EnvironmentRoot = AccessTools.TypeByName("EFT.UI.EnvironmentUIRoot");
            if (EnvironmentRoot == null) return Missing("EFT.UI.EnvironmentUIRoot (scene depth is off)");

            EnvRoot_CameraContainer = AccessTools.Field(EnvironmentRoot, "CameraContainer");
            EnvRoot_MainScreenLights = AccessTools.Field(EnvironmentRoot, "MainScreenLights");

            // The PMC. Its poser owns the ground shadow and the idle animation.
            var view = AccessTools.TypeByName("EFT.UI.PlayerModelView");
            if (view != null) PlayerModelView_Poser = AccessTools.Property(view, "ModelPlayerPoser");

            var poser = AccessTools.TypeByName("MenuPlayerPoser");
            if (poser != null)
            {
                MenuPoser_BottomShadow = AccessTools.Field(poser, "BottomShadow");
                MenuPoser_SetPatrol = AccessTools.PropertySetter(poser, "Patrol");
            }

            if (EnvRoot_CameraContainer == null)
            {
                return Missing("EnvironmentUIRoot.CameraContainer (scene depth is off)");
            }

            return true;
        }

        /// <summary>
        /// What the staging area needs on top of depth: somewhere to hang art in the backdrop's
        /// 3D space, the menu's own set dressing to get out of the way, and the PMC's layer so it
        /// can be lit separately.
        ///
        /// The camera itself is not resolved here -- it is found at runtime the same way the game
        /// finds it, CameraContainer.GetComponentInChildren&lt;Camera&gt;(), which is exactly what
        /// EnvironmentUIRoot.SetCameraActive does.
        /// </summary>
        private static bool ResolveStaging()
        {
            if (!DepthReady) return false;

            EnvRoot_MainScreenObjects = AccessTools.Field(EnvironmentRoot, "MainScreenObjects");

            var layers = AccessTools.TypeByName("LayersMaskController");
            if (layers != null) Layers_WeaponPreview = AccessTools.Field(layers, "WeaponPreview");

            if (TimeHasCome != null) Loading_SubCaption = AccessTools.Field(TimeHasCome, "_subCaption");

            // Only the camera is essential. Without the set dressing field the menu scene stays
            // visible behind the art; without the layer the PMC simply is not relit; without the
            // sub-caption the intel has nowhere to go. Each degrades on its own.
            if (EnvRoot_MainScreenObjects == null) Missing("EnvironmentUIRoot.MainScreenObjects (the menu scene will stay visible behind the map)");
            if (Layers_WeaponPreview == null) Missing("LayersMaskController.WeaponPreview (the character will not be relit)");
            if (Loading_SubCaption == null) Missing("MatchmakerTimeHasCome._subCaption (intel has nowhere to go)");

            return true;
        }

        /// <summary>
        /// The banner id behind the game's own captions, and this raid's time and weather. Both
        /// are optional and independent: without the id, "keep vanilla" captions fall back to
        /// empty as they did before; without the weather, the grade stays per-map.
        /// </summary>
        private static void ResolveExtras()
        {
            var locationBanner = AccessTools.TypeByName("JsonType.LocationSettings+Location+LocationBanner");
            if (locationBanner != null) LocationBanner_Id = AccessTools.Field(locationBanner, "id");
            if (LocationBanner_Id == null) Missing("LocationBanner.id (the game's own banner captions cannot be kept)");

            if (RaidSettings != null)
                RaidSettings_TimeAndWeather = AccessTools.Field(RaidSettings, "TimeAndWeatherSettings");

            var weather = AccessTools.TypeByName("EFT.TimeAndWeatherSettings");
            if (weather != null)
            {
                Weather_HourOfDay = AccessTools.Field(weather, "HourOfDay");
                Weather_RainType = AccessTools.Field(weather, "RainType");
                Weather_FogType = AccessTools.Field(weather, "FogType");
                Weather_Cloudiness = AccessTools.Field(weather, "CloudinessType");
                Weather_WindType = AccessTools.Field(weather, "WindType");
            }

            WeatherReady = RaidSettings_TimeAndWeather != null
                && Weather_HourOfDay != null
                && Weather_FogType != null
                && Weather_RainType != null;

            if (!WeatherReady) Missing("RaidSettings.TimeAndWeatherSettings (the light will not follow the raid's time or weather)");
        }

        /// <summary>The Show overload that takes a RaidSettings, whatever else it takes.</summary>
        private static MethodInfo FindShowTaking(Type screen, Type carried)
        {
            if (screen == null) return null;

            foreach (var method in screen.GetMethods(AccessTools.all))
            {
                if (method.Name != "Show") continue;

                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.ParameterType == carried) return method;
                }
            }

            return null;
        }

        private static bool Missing(string what)
        {
            DeployScreenPlugin.Log.LogWarning(
                "[DeployScreen] could not find " + what + " -- that part of the mod is off. "
                + "This usually means a game version this build does not know.");
            return false;
        }
    }
}
