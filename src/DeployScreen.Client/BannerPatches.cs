using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// What replaces the art on the deploy screen.
    ///
    /// The panel draws one banner per entry in the map's Banners array, and reaches each one
    /// through TryCreateBanner(LocationBanner, IImageLoader) -- which normally pulls the
    /// image off the server by path. Replacing that call with a local sprite changes the art
    /// and nothing else: the game's own CreateBanner still builds the UI, so the page
    /// toggles, the fade and the selection all behave as they do in vanilla.
    ///
    /// The count stays vanilla too. A map showing four banners shows four; extra files in its
    /// folder are not reached, and fewer files than banners are cycled. That is deliberate --
    /// changing the count means rewriting the Banners array on a live game object, which is
    /// shared state this mod has no business editing.
    ///
    /// Key-binding banners come from KeyBannerGenerator by a different path and are never
    /// touched.
    /// </summary>
    internal static class BannerPatches
    {
        /// <summary>
        /// The art in play for a panel, and how far through it the current Show has got.
        /// Keyed by panel so a second panel cannot walk the first one's index.
        /// </summary>
        private sealed class Progress
        {
            internal List<BannerImage> Images;
            internal int Next;
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

            // InFlight is keyed by panel, which is a strong reference to a Unity object the
            // game will destroy. Without this the table pins every panel it ever saw.
            if (GameTypes.BannersPanel_Close != null)
            {
                harmony.Patch(
                    GameTypes.BannersPanel_Close,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(BannerPatches), nameof(AfterClose))));
            }
        }

        /// <summary>Close(): the panel is done with, so let go of it.</summary>
        private static void AfterClose(object __instance)
        {
            InFlight.Remove(__instance);
        }

        /// <summary>
        /// Show(Location, ESideType, ProfileStats, IImageLoader): note which map's art the
        /// banners about to be created should come from.
        /// </summary>
        private static void BeforeShow(object __instance, object __0)
        {
            try
            {
                InFlight.Remove(__instance);

                if (!DeployScreenPlugin.BannersEnabled.Value) return;
                if (__0 == null) return;

                var locationId = GameTypes.Location_Id.GetValue(__0) as string;

                var images = BannerArt.For(locationId);
                if (images == null) return;

                InFlight[__instance] = new Progress { Images = images, Next = 0 };
            }
            catch (Exception error)
            {
                WarnOnce(error);
            }
        }

        /// <summary>
        /// TryCreateBanner(LocationBanner, IImageLoader): build the banner from a file on disk
        /// and skip the original, which would otherwise fetch the stock image from the server.
        /// </summary>
        private static bool BeforeTryCreateBanner(object __instance, ref Task __result)
        {
            try
            {
                Progress progress;
                if (!InFlight.TryGetValue(__instance, out progress)) return true;

                // Fewer files than the map has banners: cycle, so every slot is filled.
                var image = progress.Images[progress.Next % progress.Images.Count];
                progress.Next++;

                var sprite = image.Sprite();
                if (sprite == null) return true;

                CreateBanner(__instance, image, sprite);

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

        private static void CreateBanner(object panel, BannerImage image, Sprite sprite)
        {
            var captions = DeployScreenPlugin.BannerCaptions.Value;

            var name = captions == CaptionSource.FileName ? image.Name : string.Empty;
            var description = captions == CaptionSource.FileName ? image.Description : string.Empty;

            GameTypes.BannersPanel_CreateBanner.Invoke(panel, new object[] { name, description, sprite });
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
