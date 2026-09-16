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

        [Description("Map intel")]
        Intel,
    }

    /// <summary>
    /// Replaces what you look at while a raid loads.
    ///
    /// The deploy screen -- MatchmakerTimeHasCome, the one with your PMC and "Deploying in:" --
    /// has no background image of its own. What you see is two separate things: a rotating
    /// banner panel in front, drawn from the map's own Banners list, and the menu environment
    /// scene still rendering behind it. This mod takes both:
    ///
    ///   Banners      -- your own images per map, from a folder next to this DLL, in as many sizes
    ///                   as you like. Banners are measured on the screen, the size that suits it
    ///                   is used, and pictures are cropped to the banner's shape, never stretched.
    ///   Motion       -- a slow zoom and drift, because nothing on that screen moves.
    ///   Intel        -- bosses, extracts and your active tasks for the map you are entering.
    ///   Backdrop     -- the menu environment switched to suit the destination map.
    ///
    /// Motion and intel need nothing on disk and are on by default. Custom art does need
    /// files, and with none the banner images are left exactly as they were.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class DeployScreenPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mybutthasarash.deployscreen";
        public const string PluginName = "Deploy Screen";
        public const string PluginVersion = "1.2.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> BannersEnabled;
        internal static ConfigEntry<CaptionSource> BannerCaptions;
        internal static ConfigEntry<bool> MotionEnabled;
        internal static ConfigEntry<float> MotionZoom;
        internal static ConfigEntry<float> MotionPeriod;
        internal static ConfigEntry<bool> IntelQuests;
        internal static ConfigEntry<bool> MatchEnvironment;
        internal static ConfigEntry<string> MeasuredSizes;

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
                CaptionSource.Intel,
                "Map intel: bosses and their chances, extract count, and the tasks you have "
                + "active on this map.\n"
                + "From file name: 'Dorms; Three storeys, two keys.png' becomes that heading and that "
                + "line under it. A leading '01 - ' is treated as ordering and dropped.\n"
                + "Keep vanilla: the game's own banner headings.\n"
                + "Takes effect on the next raid.");

            MotionEnabled = Config.Bind(
                "Motion",
                "Enabled",
                true,
                "Slowly zoom and drift each banner. Nothing on the deploy screen moves in vanilla, "
                + "which is most of why it reads as a still image. Takes effect on the next raid.");

            MotionZoom = Config.Bind(
                "Motion",
                "Zoom",
                1.06f,
                new ConfigDescription(
                    "How far in the drift zooms, as a multiplier. Kept small on purpose: if the "
                    + "banner is not clipped by its frame, a large value will show the edges.",
                    new AcceptableValueRange<float>(1f, 1.3f)));

            MotionPeriod = Config.Bind(
                "Motion",
                "Seconds per cycle",
                18f,
                new ConfigDescription(
                    "How long one in-and-out drift takes. Longer is calmer.",
                    new AcceptableValueRange<float>(4f, 60f)));

            IntelQuests = Config.Bind(
                "Intel",
                "Show your tasks",
                true,
                "Include a card listing the quests you have started that are pinned to this map.\n"
                + "Off leaves the boss, extract and briefing cards alone.");

            MatchEnvironment = Config.Bind(
                "Backdrop",
                "Match the map",
                false,
                "Switch the menu environment behind the deploy screen to suit the destination -- "
                + "the Factory backdrop for Factory, the woods one for Woods, and so on.\n"
                + "This loads a Unity scene while you are setting up the raid. It is off by default "
                + "because that is a real scene load on a screen you are about to leave; turn it on "
                + "and watch for a hitch. Per-map choices live in " + EnvironmentMatch.OverrideFile + ".");

            MeasuredSizes = Config.Bind(
                "Banners",
                "Measured sizes",
                string.Empty,
                "Written by the mod, not meant to be edited: how big banners were last measured on "
                + "each screen size, as '1920x1080=765x460', semicolons between.\n"
                + "It is kept so the first raid of a session already knows which size of each "
                + "picture to load, instead of loading the largest and keeping it for the session. "
                + "Deleting it costs one raid of that, nothing else -- every raid measures again.");

            ScreenFit.Remember(MeasuredSizes.Value);

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

            Log.LogInfo(
                "[DeployScreen] loaded -- banners from " + BannerArt.RootFolder
                + " (motion=" + GameTypes.DriverReady + " intel=" + GameTypes.IntelReady + ")");
        }
    }
}
