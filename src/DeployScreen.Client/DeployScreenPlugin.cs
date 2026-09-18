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
    public enum LoadingScreenMode
    {
        Enhanced,
        Vanilla,
        Minimal,

        /// <summary>
        /// The staging area: the map's own art becomes the world the PMC is standing in, the menu
        /// scene's furniture and the banner panel get out of the way, the backdrop is lit for the
        /// destination and the character is lit to match it.
        /// </summary>
        Staging,
    }

    /// <summary>Unity's backgroundLoadingPriority, as an option rather than a raw enum.</summary>
    public enum LoadPriority
    {
        [Description("Leave alone")]
        Unchanged,

        [Description("Below normal")]
        BelowNormal,

        Normal,
        High,
    }

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
        public const string PluginVersion = "1.7.2";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> BannersEnabled;
        internal static ConfigEntry<CaptionSource> BannerCaptions;
        internal static ConfigEntry<bool> MotionEnabled;
        internal static ConfigEntry<float> MotionZoom;
        internal static ConfigEntry<float> MotionPeriod;
        internal static ConfigEntry<bool> IntelQuests;
        internal static ConfigEntry<bool> MatchEnvironment;
        internal static ConfigEntry<bool> DepthEnabled;
        internal static ConfigEntry<bool> StagingRearrange;
        internal static ConfigEntry<bool> ReportLayout;
        internal static ConfigEntry<bool> StagingVignetteOff;
        internal static ConfigEntry<bool> StagingCastShadowOff;
        internal static ConfigEntry<bool> StagingSimplePreview;
        internal static ConfigEntry<float> DepthDrift;
        internal static ConfigEntry<float> DepthSway;
        internal static ConfigEntry<float> DepthLight;
        internal static ConfigEntry<bool> DepthGroundShadow;
        internal static ConfigEntry<bool> DepthPatrol;
        internal static ConfigEntry<bool> DepthOverlay;
        internal static ConfigEntry<float> StagingDistance;
        internal static ConfigEntry<float> StagingOverscan;
        internal static ConfigEntry<float> StagingVignette;
        internal static ConfigEntry<bool> StagingHideMenuScene;
        internal static ConfigEntry<bool> StagingGradeScene;
        internal static ConfigEntry<float> StagingGradeStrength;
        internal static ConfigEntry<bool> StagingLightCharacter;
        internal static ConfigEntry<float> StagingKeyIntensity;
        internal static ConfigEntry<float> StagingRimIntensity;
        internal static ConfigEntry<bool> StagingFollowWeather;
        internal static ConfigEntry<bool> StagingIntelLine;
        internal static ConfigEntry<float> StagingIntelSeconds;
        internal static ConfigEntry<string> MeasuredSizes;
        internal static ConfigEntry<LoadingScreenMode> ScreenMode;
        internal static ConfigEntry<bool> RecordLoading;
        internal static ConfigEntry<string> TestLabel;
        internal static ConfigEntry<bool> EasePrewarmArt;
        internal static ConfigEntry<int> EaseFrameRate;
        internal static ConfigEntry<LoadPriority> EasePriority;
        internal static ConfigEntry<bool> EasePauseIk;

        private void Awake()
        {
            Log = Logger;

            ScreenMode = Config.Bind("Performance", "Loading screen", LoadingScreenMode.Enhanced,
                "Staging area: your map art becomes the world your PMC is standing in -- the menu "
                + "scene and the banner panel step aside, the backdrop is lit for the destination "
                + "and the character is lit to match. Needs art in the map's banners folder.\n"
                + "Enhanced: this mod's banners and intel, on the normal screen.\n"
                + "Vanilla: stock presentation with diagnostics only.\n"
                + "Minimal: skip loading-screen banners and character preview, and suspend the menu "
                + "scene behind a plain background when supported.\n"
                + "Takes effect next raid.");
            RecordLoading = Config.Bind("Performance", "Record loading", true,
                "Record loading phases, frame gaps of at least 100 ms, focus changes, GC counts and memory. "
                + "Writes a bounded JSON report after loading, on a background thread. No per-frame disk writes. Next raid.");
            TestLabel = Config.Bind("Performance", "Test label", "",
                "Optional label for reports, for example first or repeat. Reuse the same label for "
                + "comparable runs; keep first loads and repeat loads separate. Next raid.");

            EasePrewarmArt = Config.Bind(
                "Ease the load",
                "Decode art early",
                true,
                "Decode this map's pictures while you are still setting the raid up, instead of "
                + "during the load.\n"
                + "Reading a 4K image is tens of milliseconds of main-thread work, and doing it "
                + "while the map is loading puts a stall exactly where you least want one. This "
                + "moves the same work to the previous screen, where nobody will notice it. Only "
                + "does anything if you use custom art.");

            EaseFrameRate = Config.Bind(
                "Ease the load",
                "Frame rate cap while loading",
                0,
                new ConfigDescription(
                    "Cap the frame rate while the deploy screen is up, so drawing the menu "
                    + "competes less with loading the raid. 0 leaves your own setting alone.\n"
                    + "Your cap is restored when the screen closes. Values below 10 are refused: "
                    + "the diagnostics count a stall at 100 ms, and a cap that low would look "
                    + "like hitching in this mod's own measurements.",
                    new AcceptableValueRange<int>(0, 240)));

            EasePriority = Config.Bind(
                "Ease the load",
                "Loading priority",
                LoadPriority.Unchanged,
                "How much of each frame Unity may spend integrating loaded assets.\n"
                + "Higher finishes the load sooner but makes each frame do more, so frames get "
                + "longer even as there are fewer of them -- it trades a smoother number for a "
                + "shorter wait, and which you prefer is a question only measuring on your machine "
                + "can answer. The game lowers this itself while streaming a raid, so 'Leave "
                + "alone' is the safe default and is not a cop-out.");

            EasePauseIk = Config.Bind(
                "Ease the load",
                "Pause character IK while loading",
                false,
                "Stop the PMC solving inverse kinematics while the raid loads.\n"
                + "The character stays on screen and keeps animating; it just stops running its "
                + "limb solvers and hand posers every frame. Off by default because it is the one "
                + "setting here that changes what you see.");

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
                "Override YOUR chosen backdrop with one picked to suit the destination map.\n"
                + "Off by default, and off is what this mod now assumes: your backdrop is a real "
                + "game setting, and it is treated as the foundation to build on rather than "
                + "something to replace. With this on, the backdrop is put back the moment you "
                + "leave the deploy screen -- cancelled, or returned from the raid -- so it can no "
                + "longer follow you to the main menu. A map with no backdrop of its own restores "
                + "yours instead of keeping the last map's. Per-map choices live in "
                + EnvironmentMatch.OverrideFile + ".");

            DepthEnabled = Config.Bind(
                "Scene",
                "Depth and atmosphere",
                true,
                "Give the deploy screen depth by moving the backdrop's own camera and breathing "
                + "its own lights.\n"
                + "The backdrop is a real 3D scene, not a picture, so drifting the camera a few "
                + "centimetres parallaxes near geometry against far geometry for real. Nothing is "
                + "added to the scene, and everything is put back on the way out. Next raid.");

            DepthDrift = Config.Bind(
                "Scene",
                "Camera drift",
                0.05f,
                new ConfigDescription(
                    "How far the backdrop camera drifts, in world units. This is the setting that "
                    + "creates the parallax, and the one most likely to want tuning: the scenes' "
                    + "scale is not something the mod can measure, so raise it if the motion is "
                    + "invisible and lower it if the scene swims. 0 turns the drift off.",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            DepthSway = Config.Bind(
                "Scene",
                "Camera sway",
                0.12f,
                new ConfigDescription(
                    "A slow rotation on top of the drift, in degrees. Rotation alone gives no "
                    + "parallax -- it shifts near and far by the same angle -- so this is only here "
                    + "to keep the drift from reading as a slider. 0 turns it off.",
                    new AcceptableValueRange<float>(0f, 1.5f)));

            DepthLight = Config.Bind(
                "Scene",
                "Light wander",
                0.06f,
                new ConfigDescription(
                    "How much the scene's own lights breathe, as a fraction of their set intensity. "
                    + "0.06 reads as air moving rather than as a flicker. 0 leaves them alone.",
                    new AcceptableValueRange<float>(0f, 0.4f)));

            DepthGroundShadow = Config.Bind(
                "Scene",
                "Ground the character",
                true,
                "Switch on the PMC's own contact shadow if it is off.\n"
                + "MenuPlayerPoser.BottomShadow already exists in the game, so this needs no new "
                + "art. Without it the character reads as pasted in front of the scene rather than "
                + "standing in it.");

            DepthPatrol = Config.Bind(
                "Scene",
                "Idle movement",
                false,
                "Ask the PMC's own animator for its idle patrol motion.\n"
                + "Off by default because the property that sets it cannot be read back, so the mod "
                + "cannot know what it was before and simply turns it off again on the way out. If "
                + "your character already shifts its weight on the deploy screen, leave this alone.");

            DepthOverlay = Config.Bind(
                "Scene",
                "Dim behind the text",
                false,
                "Use the game's own scrim (EnvironmentUI.EnableOverlay) behind the loading text.\n"
                + "Helps readability on a bright backdrop, at the cost of flattening the scene -- "
                + "which is the opposite of what the rest of this section is for. Off unless you "
                + "find your backdrop is fighting the text.");

            MeasuredSizes = Config.Bind(
                "Banners",
                "Measured sizes",
                string.Empty,
                "Written by the mod, not meant to be edited: how big banners were last measured on "
                + "each screen size, as '1920x1080=765x460', semicolons between.\n"
                + "It is kept so the first raid of a session already knows which size of each "
                + "picture to load, instead of loading the largest and keeping it for the session. "
                + "Deleting it costs one raid of that, nothing else -- every raid measures again.");

            StagingDistance = Config.Bind(
                "Staging area",
                "Near plane distance",
                3f,
                new ConfigDescription(
                    "How far in front of the backdrop camera the near haze plane sits, in world "
                    + "units. The map itself is placed further back again, and the gap between "
                    + "them is what the camera drift shears to make parallax. Larger values "
                    + "flatten the effect; smaller ones exaggerate it.",
                    new AcceptableValueRange<float>(0.5f, 20f)));

            StagingOverscan = Config.Bind(
                "Staging area",
                "Overscan",
                1.02f,
                new ConfigDescription(
                    "How much bigger than the frame each plane is built, so drifting the camera "
                    + "cannot reveal an edge. This is a floor, not the answer: the mod works out "
                    + "what your drift, screen shape and field of view actually need and uses "
                    + "whichever is larger. Anything above that is pure crop -- at the default "
                    + "drift the real requirement is about x1.01, and every 0.10 here costs you "
                    + "roughly 9% of your picture. Raise it only if you actually see the art end "
                    + "at the side of the screen.",
                    new AcceptableValueRange<float>(1f, 1.6f)));

            StagingVignette = Config.Bind(
                "Staging area",
                "Near haze",
                0.30f,
                new ConfigDescription(
                    "How dark the near plane's frame is. This plane exists mainly to sit at a "
                    + "different depth from the map so the two shear against each other; the "
                    + "darkening is the visible part. 0 removes the plane entirely.",
                    new AcceptableValueRange<float>(0f, 1f)));

            StagingHideMenuScene = Config.Bind(
                "Staging area",
                "Hide the menu scene",
                true,
                "Switch off the menu backdrop's own furniture while the map art is up.\n"
                + "With it on you are looking at the place you are going to. With it off, the "
                + "factory crates or mall shutters sit in front of it, which is someone else's "
                + "location in the way of yours.");

            StagingGradeScene = Config.Bind(
                "Staging area",
                "Light the scene for the map",
                true,
                "Tint the backdrop's own lights toward the destination -- sodium for Streets, "
                + "cold green for Woods, clinical blue for Labs.");

            StagingGradeStrength = Config.Bind(
                "Staging area",
                "Grade strength",
                0.65f,
                new ConfigDescription(
                    "How far the lights and the art are pulled toward the destination's colour. "
                    + "1 is the full grade; lower keeps more of the original look.",
                    new AcceptableValueRange<float>(0f, 1f)));

            StagingLightCharacter = Config.Bind(
                "Staging area",
                "Light the character to match",
                true,
                "Add a key and a rim light that fall on your PMC and nothing else, coloured for "
                + "the destination.\n"
                + "This is the setting that stops the character reading as a cut-out: a composite "
                + "gives itself away through light that disagrees with its surroundings, not "
                + "through geometry. It is masked to the character's own render layer, so it "
                + "cannot touch the backdrop, the interface or anything in a raid.");

            StagingKeyIntensity = Config.Bind(
                "Staging area",
                "Key light",
                0.85f,
                new ConfigDescription(
                    "The main light on the character, from front-left and above.",
                    new AcceptableValueRange<float>(0f, 3f)));

            StagingRimIntensity = Config.Bind(
                "Staging area",
                "Rim light",
                1.25f,
                new ConfigDescription(
                    "The light from behind that draws an edge along the character and separates "
                    + "them from the map. Usually wants to be brighter than the key.",
                    new AcceptableValueRange<float>(0f, 3f)));

            StagingFollowWeather = Config.Bind(
                "Staging area",
                "Follow the raid weather",
                true,
                "Light the screen for the raid you are actually loading, not just for the map.\n"
                + "Time of day, fog, rain and cloud are all settled before you deploy, so a 03:00 "
                + "foggy Woods can look like one and a clear midday Streets like another. Off "
                + "keeps the map's own fixed look whatever the conditions.");

            StagingSimplePreview = Config.Bind(
                "Staging area",
                "Simplify the character preview",
                true,
                "The character preview runs its own ambient occlusion and shadow-catcher pass. "
                + "Both are tuned for a PMC standing in a dim room, and against a photograph they "
                + "show up as a dark halo around him that belongs to neither the character nor "
                + "the picture. This switches those two off while the deploy screen is up and "
                + "puts them back afterwards. Turn it off if your character looks flat or ends up "
                + "in a box.");

            StagingCastShadowOff = Config.Bind(
                "Staging area",
                "Remove the cast shadow",
                true,
                "The character preview renders a cast shadow through the game's MaskAndShadow "
                + "component. In the stock menu it falls on the room's wall and looks right. With "
                + "the room hidden and a photograph behind instead, it has nothing to fall on and "
                + "hangs in mid-air as a dark blob beside your PMC. Off keeps the shadow.");

            StagingVignetteOff = Config.Bind(
                "Staging area",
                "Turn off the menu vignette",
                true,
                "The menu camera darkens its own edges through the game's post-processing "
                + "(PrismEffects, vignette strength 1). Over the stock dim backdrop you never see "
                + "it; over a photograph it is a black frame around all four sides. This switches "
                + "that one effect off while the deploy screen is up and puts it back afterwards. "
                + "It does not touch bloom, colour or anything else in the stack.");

            ReportLayout = Config.Bind(
                "Performance",
                "Report the screen layout",
                false,
                "Write the deploy screen's whole hierarchy to the log once per session: every "
                + "object, where it sits, how it is anchored, what it says and what colour it is. "
                + "Off by default because it is sixty lines nobody needs, on when the game has "
                + "been updated and something the mod moves has been renamed -- it is how the "
                + "names in ScreenLayout were found in the first place.");

            StagingRearrange = Config.Bind(
                "Staging area",
                "Rearrange the screen",
                true,
                "Move the game's own deploy screen out of the middle: the map name large in the "
                + "top-left with its intel under it, the progress line bottom-left, the way out "
                + "bottom-right, and the Escape from Tarkov logo -- which is anchored across the "
                + "centre and through your character -- out of the way. Off keeps the stock "
                + "arrangement, which is built around the banner panel the staging area removes.");

            StagingIntelLine = Config.Bind(
                "Staging area",
                "Intel under the map name",
                true,
                "Show the map briefing, bosses, extracts and your tasks in the line beneath the "
                + "location name, cycling slowly, instead of on banner captions.");

            StagingIntelSeconds = Config.Bind(
                "Staging area",
                "Seconds per intel line",
                7f,
                new ConfigDescription(
                    "How long each line stays before the next.",
                    new AcceptableValueRange<float>(3f, 30f)));

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
            var performance = gameObject.AddComponent<LoadingPerformance>();
            performance.Initialize(folder);
            LoadingPerformance.Install(harmony);

            try
            {
                if (GameTypes.BannersReady) BannerPatches.Install(harmony);
                if (GameTypes.EnvironmentRestoreReady) EnvironmentState.Install(harmony);
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
                + " (motion=" + GameTypes.DriverReady + " intel=" + GameTypes.IntelReady
                + " depth=" + GameTypes.DepthReady
                + " backdrop-restore=" + GameTypes.EnvironmentRestoreReady + ")");
        }
    }
}
