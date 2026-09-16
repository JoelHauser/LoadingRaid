using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace DeployScreen.Client
{
    public enum CaptionSource
    {
        [Description("Keep vanilla")]
        Vanilla,

        [Description("From file name")]
        FileName,
    }

    /// <summary>
    /// Replaces what you look at while a raid loads.
    ///
    /// The deploy screen -- MatchmakerTimeHasCome, the one with your PMC and "Deploying in:" --
    /// has no background image of its own. What you see is two separate things: a rotating
    /// banner panel in front, drawn from the map's own Banners list, and the menu environment
    /// scene still rendering behind it. This mod takes both:
    ///
    ///   Banners      -- your own images per map, from a folder next to this DLL.
    ///   Backdrop     -- the menu environment switched to suit the destination map.
    ///
    /// Both are off unless you give them something to do: with no images on disk the banner
    /// half does nothing at all, and backdrop matching starts disabled.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class DeployScreenPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mybutthasarash.deployscreen";
        public const string PluginName = "Deploy Screen";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> BannersEnabled;
        internal static ConfigEntry<CaptionSource> BannerCaptions;
        internal static ConfigEntry<bool> MatchEnvironment;

        private void Awake()
        {
            Log = Logger;

            BannersEnabled = Config.Bind(
                "Banners",
                "Enabled",
                true,
                "Replace the deploy screen's banners with images from this mod's banners folder.\n"
                + "With no images on disk this does nothing either way. Takes effect on the next raid.");

            BannerCaptions = Config.Bind(
                "Banners",
                "Captions",
                CaptionSource.Vanilla,
                "Keep vanilla: the game's own banner headings.\n"
                + "From file name: 'Dorms|Three storey, two keys.png' becomes that heading and that "
                + "line under it. A leading '01 - ' is treated as ordering and dropped.\n"
                + "Takes effect on the next raid.");

            MatchEnvironment = Config.Bind(
                "Backdrop",
                "Match the map",
                false,
                "Switch the menu environment behind the deploy screen to suit the destination -- "
                + "the Factory backdrop for Factory, the woods one for Woods, and so on.\n"
                + "This loads a Unity scene while you are setting up the raid. It is off by default "
                + "because that is a real scene load on a screen you are about to leave; turn it on "
                + "and watch for a hitch. Per-map choices live in " + EnvironmentMatch.OverrideFile + ".");

            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            BannerArt.RootFolder = Path.Combine(folder, "banners");
            EnvironmentMatch.ReadOverrides(folder);

            if (!GameTypes.Resolve())
            {
                Log.LogWarning("[DeployScreen] nothing to patch -- the mod is inert this session.");
                return;
            }

            var harmony = new Harmony(PluginGuid);

            try
            {
                if (GameTypes.BannersReady) BannerPatches.Install(harmony);
                if (GameTypes.EnvironmentReady) EnvironmentMatch.Install(harmony);
            }
            catch (Exception error)
            {
                Log.LogError("[DeployScreen] could not install patches: " + error);
                return;
            }

            // Editing files on disk should not need a restart, and the cache is what would
            // otherwise hold the old ones.
            BannersEnabled.SettingChanged += (sender, e) => BannerArt.Forget();
            BannerCaptions.SettingChanged += (sender, e) => BannerArt.Forget();

            Log.LogInfo("[DeployScreen] loaded -- banners from " + BannerArt.RootFolder);
        }
    }
}
