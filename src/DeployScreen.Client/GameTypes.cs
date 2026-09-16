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
    /// <see cref="Resolve"/> returns false if anything essential is missing, and the plugin
    /// then installs no patches at all rather than half of them.
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

        /// <summary>True when everything the plugin needs was found.</summary>
        internal static bool BannersReady { get; private set; }

        /// <summary>True when the environment switch has everything it needs.</summary>
        internal static bool EnvironmentReady { get; private set; }

        internal static bool Resolve()
        {
            BannersReady = ResolveBanners();
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
            // which map it is drawing banners for.
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
                "[DeployScreen] could not find " + what + " -- that half of the mod is off. "
                + "This usually means a game version this build does not know.");
            return false;
        }
    }
}
