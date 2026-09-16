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

        // ----------------------------------------------------------- raid screens

        internal static Type OfflineRaidScreen;
        internal static MethodInfo OfflineRaidScreen_Show;
        internal static Type TimeHasCome;
        internal static MethodInfo TimeHasCome_Show;

        internal static Type RaidSettings;
        internal static PropertyInfo RaidSettings_SelectedLocation;

        /// <summary>Custom banner art can be substituted.</summary>
        internal static bool BannersReady { get; private set; }

        /// <summary>Banners can be reached after creation, for motion and captions.</summary>
        internal static bool DriverReady { get; private set; }

        /// <summary>Map intel can be built and published.</summary>
        internal static bool IntelReady { get; private set; }

        /// <summary>The backdrop can be switched.</summary>
        internal static bool EnvironmentReady { get; private set; }

        internal static bool Resolve()
        {
            BannersReady = ResolveBanners();
            DriverReady = BannersReady && ResolveDriver();
            IntelReady = DriverReady && ResolveIntel();
            EnvironmentReady = ResolveEnvironment();

            return BannersReady || EnvironmentReady;
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
