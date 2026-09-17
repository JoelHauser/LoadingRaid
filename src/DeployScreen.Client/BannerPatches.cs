using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// What replaces the art on the deploy screen, and what starts the driver that dresses it.
    ///
    /// The panel draws one banner per entry in the map's Banners array, and reaches each one
    /// through TryCreateBanner(LocationBanner, IImageLoader) -- which normally pulls the image off
    /// the server by path. Replacing that call with a local sprite changes the art and nothing
    /// else: the game's own CreateBanner still builds the UI, so the page toggles, the fade and
    /// the selection all behave as they do in vanilla.
    ///
    /// The count stays vanilla too. A map showing four banners shows four; extra pictures in its
    /// folder are not reached, and fewer pictures than banners are cycled. That is deliberate --
    /// changing the count means rewriting the Banners array on a live game object, which is
    /// shared state this mod has no business editing.
    ///
    /// Key-binding banners come from KeyBannerGenerator by a different path and are never
    /// touched.
    /// </summary>
    internal static class BannerPatches
    {
        /// <summary>
        /// The art in play for a panel, and how far through it the current Show has got. Keyed by
        /// panel so a second panel cannot walk the first one's index.
        /// </summary>
        private sealed class Progress
        {
            internal List<BannerImage> Images;
            internal int Next;

            /// <summary>File-name captions reached the locale table, so banners can carry keys.</summary>
            internal bool CaptionKeys;
        }

        private static readonly Dictionary<object, Progress> InFlight =
            new Dictionary<object, Progress>();

        private static bool _warnedOnce;

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(
                GameTypes.BannersPanel_Show,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BannerPatches), nameof(BeforeShow))));

            harmony.Patch(
                GameTypes.BannersPanel_TryCreateBanner,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BannerPatches), nameof(BeforeTryCreateBanner))));

            // The offline raid screen is up while the player sets time of day, long before
            // anything is loading. Decoding the art there instead of during the load is the
            // cheapest hitch this mod can remove, because it is one it was causing itself.
            if (GameTypes.OfflineRaidScreen_Show != null)
            {
                harmony.Patch(
                    GameTypes.OfflineRaidScreen_Show,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(BannerPatches), nameof(AfterOfflineRaidScreen))));
            }

            // InFlight is keyed by panel, which is a strong reference to a Unity object the game
            // will destroy. Without this the table pins every panel it ever saw.
            if (GameTypes.BannersPanel_Close != null)
            {
                harmony.Patch(
                    GameTypes.BannersPanel_Close,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(BannerPatches), nameof(AfterClose))));
            }
        }

        /// <summary>
        /// The raid is being configured: decode this map's art now, while nothing is loading.
        /// </summary>
        private static void AfterOfflineRaidScreen(object[] __args)
        {
            try
            {
                if (!DeployScreenPlugin.EasePrewarmArt.Value) return;
                if (!DeployScreenPlugin.BannersEnabled.Value) return;
                if (__args == null || GameTypes.RaidSettings == null) return;

                var mode = LoadingPerformance.Mode;
                if (mode != LoadingScreenMode.Enhanced && mode != LoadingScreenMode.Staging) return;

                string locationId = null;
                foreach (var argument in __args)
                {
                    if (argument == null || !GameTypes.RaidSettings.IsInstanceOfType(argument)) continue;

                    var location = GameTypes.RaidSettings_SelectedLocation.GetValue(argument, null);
                    if (location != null) locationId = GameTypes.Location_Id.GetValue(location) as string;
                    break;
                }

                if (string.IsNullOrEmpty(locationId)) return;

                if (mode == LoadingScreenMode.Staging)
                {
                    // Staging shows one picture filling the screen, so decode that one and no more.
                    var images = BannerArt.For(locationId);
                    if (images == null || images.Count == 0) return;

                    var height = Screen.height > 0 ? Screen.height : 1080;
                    var aspect = Screen.width > 0 && Screen.height > 0
                        ? (float)Screen.width / Screen.height
                        : ScreenFit.StockAspect;

                    var screenFit = new BannerFit
                    {
                        Width = Mathf.RoundToInt(height * aspect),
                        Height = height,
                    };

                    BannerArt.Prewarm(locationId, screenFit, true, StagingArea.PictureIndex(locationId, images.Count));
                    return;
                }

                // Enhanced draws every banner, so every picture is wanted.
                BannerFit fit;
                var measured = ScreenFit.TryCurrent(out fit);
                BannerArt.Prewarm(locationId, fit, measured, -1);
            }
            catch (Exception error)
            {
                WarnOnce(error);
            }
        }

        /// <summary>Close(): the panel is done with, so let go of it and stop the driver.</summary>
        private static void AfterClose(object __instance)
        {
            InFlight.Remove(__instance);

            var component = __instance as Component;
            if (component != null)
            {
                // Disabling it is what puts every banner transform back where it was found.
                var driver = component.GetComponent<BannerDriver>();
                if (driver != null) driver.enabled = false;
            }

            // The banners are gone with the panel, so the pictures behind them can go too --
            // all but the size this screen would ask for next time.
            BannerArt.ReleaseUnused();
        }

        /// <summary>
        /// Show(Location, ESideType, ProfileStats, IImageLoader): note which map's pictures the
        /// banners about to be created should come from, and start the driver.
        ///
        /// The fourth argument is the session -- ClientBackendSession implements IImageLoader --
        /// which is how the profile, and so the quest list, is reached without hunting for a
        /// singleton.
        /// </summary>
        private static void BeforeShow(object __instance, object __0, object __3)
        {
            try
            {
                InFlight.Remove(__instance);

                if (LoadingPerformance.Mode != LoadingScreenMode.Enhanced) return;
                if (__0 == null) return;

                List<BannerImage> images = null;

                if (DeployScreenPlugin.BannersEnabled.Value)
                {
                    var locationId = GameTypes.Location_Id.GetValue(__0) as string;

                    images = BannerArt.For(locationId);
                    if (images != null)
                    {
                        InFlight[__instance] = new Progress
                        {
                            Images = images,
                            Next = 0,
                            CaptionKeys = PublishFileCaptions(images)
                        };
                    }
                }

                StartDriver(__instance, __0, __3, images);
            }
            catch (Exception error)
            {
                WarnOnce(error);
            }
        }

        /// <summary>
        /// TryCreateBanner(LocationBanner, IImageLoader): build the banner from a file on disk and
        /// skip the original, which would otherwise fetch the stock image from the server.
        ///
        /// The size of the picture is chosen for this screen if a banner has already been measured
        /// at this resolution, and is the largest available if not.
        /// </summary>
        private static bool BeforeTryCreateBanner(object __instance, object __0, ref Task __result)
        {
            try
            {
                Progress progress;
                if (!InFlight.TryGetValue(__instance, out progress)) return true;

                // Fewer pictures than the map has banners: cycle, so every slot is filled.
                var index = progress.Next % progress.Images.Count;
                var image = progress.Images[index];
                progress.Next++;

                BannerFit fit;
                var measured = ScreenFit.TryCurrent(out fit);

                var sprite = image.Sprite(fit, measured);
                if (sprite == null) return true;

                CreateBanner(__instance, image, sprite, index, progress.CaptionKeys, VanillaCaptionId(__0));

                // The caller awaits this; a completed task keeps its loop moving.
                __result = Task.CompletedTask;
                return false;
            }
            catch (Exception error)
            {
                WarnOnce(error);
                return true;
            }
        }

        /// <summary>
        /// The id on the LocationBanner this call was for, which is what the game's own captions
        /// are filed under. TryCreateBanner's IL is explicit about the shape:
        ///
        ///     CreateBanner(banner.id + " Name", banner.id + " Description", sprite)
        ///
        /// Null when the field could not be resolved, which puts us back where 1.3.1 was.
        /// </summary>
        private static string VanillaCaptionId(object locationBanner)
        {
            if (locationBanner == null || GameTypes.LocationBanner_Id == null) return null;

            try { return GameTypes.LocationBanner_Id.GetValue(locationBanner) as string; }
            catch { return null; }
        }

        private static void CreateBanner(object panel, BannerImage image, Sprite sprite, int index,
            bool captionKeys, string vanillaId)
        {
            // Intel captions are applied later, by the driver, once the banner exists.
            var name = string.Empty;
            var description = string.Empty;

            // "Keep vanilla" used to mean "no caption at all" whenever custom art was on, because
            // the banner id could not be resolved on the box this was written on. It can now, and
            // the keys are simply the id with the game's two suffixes -- so a player can have this
            // mod's pictures under BSG's own lore text, which is what the setting always promised.
            if (DeployScreenPlugin.BannerCaptions.Value == CaptionSource.Vanilla
                && !string.IsNullOrEmpty(vanillaId))
            {
                name = vanillaId + " Name";
                description = vanillaId + " Description";
            }

            if (DeployScreenPlugin.BannerCaptions.Value == CaptionSource.FileName)
            {
                // The key is what makes a description appear at all -- raw text localizes to
                // itself and SelectBanner hides it. Without the locale table the heading is
                // still right as raw text, and the description stays empty rather than
                // showing a key nobody can read.
                name = captionKeys && !string.IsNullOrEmpty(image.Name)
                    ? FileCaptionKeys.Header(index)
                    : image.Name ?? string.Empty;

                description = captionKeys && !string.IsNullOrEmpty(image.Description)
                    ? FileCaptionKeys.Body(index)
                    : string.Empty;
            }

            GameTypes.BannersPanel_CreateBanner.Invoke(panel, new object[] { name, description, sprite });
        }

        /// <summary>
        /// Registers the pictures' own captions under this mod's locale keys, so a banner can
        /// carry a key instead of raw text -- see Localization.cs for why raw text is hidden.
        ///
        /// Returns false when there is nothing to register or the locale table cannot be
        /// reached, which leaves headings raw and descriptions empty: no worse than before,
        /// and never a visible key.
        /// </summary>
        private static bool PublishFileCaptions(List<BannerImage> images)
        {
            if (DeployScreenPlugin.BannerCaptions.Value != CaptionSource.FileName) return false;

            var entries = new Dictionary<string, string>(images.Count * 2);

            for (var i = 0; i < images.Count; i++)
            {
                if (!string.IsNullOrEmpty(images[i].Name)) entries[FileCaptionKeys.Header(i)] = images[i].Name;
                if (!string.IsNullOrEmpty(images[i].Description)) entries[FileCaptionKeys.Body(i)] = images[i].Description;
            }

            return entries.Count > 0 && Localization.Publish(entries);
        }

        // ----------------------------------------------------------------- driver

        /// <summary>
        /// Started on every Show, even with motion and intel both off: the driver is also what
        /// measures the banners on screen, which chooses picture sizes and logs the size to make
        /// custom images.
        /// </summary>
        private static void StartDriver(object panel, object location, object session, List<BannerImage> images)
        {
            if (!GameTypes.DriverReady) return;

            var component = panel as Component;
            if (component == null) return;

            List<IntelCard> cards = null;

            if (DeployScreenPlugin.BannerCaptions.Value == CaptionSource.Intel && GameTypes.IntelReady)
            {
                cards = Intel.Build(location, session);
                PublishIntel(cards);
            }

            var driver = component.GetComponent<BannerDriver>();
            if (driver == null) driver = component.gameObject.AddComponent<BannerDriver>();

            driver.Begin(panel, cards, images);
        }

        /// <summary>
        /// Registers the cards under this mod's own locale keys. The banner then carries the key
        /// rather than the text -- see Localization.cs for why raw text would be hidden.
        /// </summary>
        private static void PublishIntel(List<IntelCard> cards)
        {
            if (cards == null || cards.Count == 0) return;

            var entries = new Dictionary<string, string>(cards.Count * 2);

            for (var i = 0; i < cards.Count; i++)
            {
                entries[IntelKeys.Header(i)] = cards[i].Header ?? string.Empty;
                entries[IntelKeys.Body(i)] = cards[i].Body ?? string.Empty;
            }

            Localization.Publish(entries);
        }

        /// <summary>
        /// One warning per session. A patch that throws every frame on a broken install would
        /// otherwise bury the log, and the failure mode here is cosmetic.
        /// </summary>
        private static void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning(
                "[DeployScreen] banner replacement failed, falling back to vanilla art: " + error);
        }
    }
}
