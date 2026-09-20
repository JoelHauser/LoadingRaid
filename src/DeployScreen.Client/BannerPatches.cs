using System;
using HarmonyLib;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// Gets the map's picture decoded before the load needs it.
    ///
    /// This file used to be the banner replacement: it swapped the art the deploy screen's banner
    /// panel pulls off the server, published file-name captions into the locale table, and started
    /// a driver that moved each banner and cycled the intel through their captions. All of that
    /// belonged to Enhanced mode, and Enhanced is gone -- the staging area does not draw banners at
    /// all, it hangs one picture as the world and takes the panel away.
    ///
    /// What is left is the one thing here that was never really about banners. The offline raid
    /// screen is up while the player picks time of day, long before anything is loading, and
    /// decoding the picture there rather than during the load removes a hitch the mod was causing
    /// itself. It is patched here because this is where the location for the coming raid first
    /// becomes readable.
    /// </summary>
    internal static class BannerPatches
    {
        private static bool _warnedOnce;

        internal static void Install(Harmony harmony)
        {
            if (GameTypes.OfflineRaidScreen_Show == null) return;

            harmony.Patch(
                GameTypes.OfflineRaidScreen_Show,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BannerPatches), nameof(AfterOfflineRaidScreen))));
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

                string locationId = null;
                foreach (var argument in __args)
                {
                    if (argument == null || !GameTypes.RaidSettings.IsInstanceOfType(argument)) continue;

                    var location = GameTypes.RaidSettings_SelectedLocation.GetValue(argument, null);
                    if (location != null) locationId = GameTypes.Location_Id.GetValue(location) as string;
                    break;
                }

                if (string.IsNullOrEmpty(locationId)) return;

                // One picture fills the screen, so decode that one and no more. Which one it will
                // be is the rotation's business, and asking it here rather than guessing is what
                // keeps the picture that was prepared and the picture that is shown the same file.
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
            }
            catch (Exception error)
            {
                WarnOnce(error);
            }
        }

        private static void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning(
                "[DeployScreen] preparing the map art failed, and the load will decode it instead: " + error);
        }
    }
}
