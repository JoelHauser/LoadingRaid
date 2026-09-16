using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// Watches one banner panel and dresses each banner as it appears.
    ///
    /// The panel fills _locationBanners over several frames -- each banner waits on its image
    /// before being added -- so there is no single moment afterwards to do this in. A Harmony
    /// postfix is no help either: Show is async, so it returns its Task long before any banner
    /// exists. Watching the list is simply what fits.
    ///
    /// _locationBanners holds only the map's banners. The key-binding banners the tutorial
    /// adds go through a different path and never appear here, which is exactly what we want:
    /// panning an instructional diagram or captioning it with boss chances would be wrong.
    /// </summary>
    internal sealed class BannerDriver : MonoBehaviour
    {
        private object _panel;
        private List<IntelCard> _cards;

        private readonly HashSet<object> _seen = new HashSet<object>();
        private readonly List<KenBurns> _attached = new List<KenBurns>();

        private int _nextCard;
        private bool _warnedOnce;

        /// <summary>
        /// Starts a fresh pass. Called on every Show, because the panel is reused between
        /// raids and the map -- and so the intel -- will usually have changed.
        /// </summary>
        internal void Begin(object panel, List<IntelCard> cards)
        {
            _panel = panel;
            _cards = cards;
            _nextCard = 0;
            _seen.Clear();

            enabled = true;
        }

        private void Update()
        {
            if (_panel == null || GameTypes.BannersPanel_LocationBanners == null)
            {
                enabled = false;
                return;
            }

            try
            {
                var banners = GameTypes.BannersPanel_LocationBanners.GetValue(_panel) as IEnumerable;
                if (banners == null) return;

                foreach (var entry in banners)
                {
                    if (entry == null) continue;

                    var banner = GameTypes.BannerWithToggle_Banner == null
                        ? null
                        : GameTypes.BannerWithToggle_Banner.GetValue(entry);

                    if (banner == null || !_seen.Add(banner)) continue;

                    Dress(banner);
                }
            }
            catch (Exception error)
            {
                WarnOnce(error);
                enabled = false;
            }
        }

        private void Dress(object banner)
        {
            if (DeployScreenPlugin.MotionEnabled.Value) AddMotion(banner);

            if (DeployScreenPlugin.BannerCaptions.Value == CaptionSource.Intel) AddIntel(banner);
        }

        // ------------------------------------------------------------------ motion

        private void AddMotion(object banner)
        {
            var image = GameTypes.Banner_BannerImage == null
                ? null
                : GameTypes.Banner_BannerImage.GetValue(banner) as Component;

            if (image == null) return;

            // Read as Component rather than Image so this assembly needs no UnityEngine.UI
            // reference; the RectTransform is all the motion touches.
            var rect = image.transform as RectTransform;
            if (rect == null) return;

            if (rect.GetComponent<KenBurns>() != null) return;

            var motion = rect.gameObject.AddComponent<KenBurns>();
            motion.Zoom = DeployScreenPlugin.MotionZoom.Value;
            motion.Period = DeployScreenPlugin.MotionPeriod.Value;

            _attached.Add(motion);
        }

        // ------------------------------------------------------------------- intel

        /// <summary>
        /// Points the banner's caption fields at this mod's locale keys. The panel reads them
        /// when a banner is selected, so setting them as the banner appears is enough -- and if
        /// the very first selection happens to beat this, the next switch corrects it.
        /// </summary>
        private void AddIntel(object banner)
        {
            if (_cards == null || _cards.Count == 0) return;
            if (GameTypes.Banner_BannerName == null || GameTypes.Banner_BannerDescription == null) return;

            var index = _nextCard % _cards.Count;
            _nextCard++;

            if (Localization.Available)
            {
                GameTypes.Banner_BannerName.SetValue(banner, IntelKeys.Header(index));
                GameTypes.Banner_BannerDescription.SetValue(banner, IntelKeys.Body(index));
                return;
            }

            // No locale table: the heading still shows raw text, but a raw description would
            // be hidden by the panel's own equality check, so do not set one.
            GameTypes.Banner_BannerName.SetValue(banner, _cards[index].Header);
            GameTypes.Banner_BannerDescription.SetValue(banner, string.Empty);
        }

        private void OnDisable()
        {
            foreach (var motion in _attached)
            {
                if (motion != null) motion.Restore();
            }
        }

        private void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning("[DeployScreen] banner driver stopped: " + error);
        }
    }

    /// <summary>The locale keys intel is published and referenced under.</summary>
    internal static class IntelKeys
    {
        internal static string Header(int index)
        {
            return Localization.KeyPrefix + "intel/" + index + "/h";
        }

        internal static string Body(int index)
        {
            return Localization.KeyPrefix + "intel/" + index + "/d";
        }
    }
}
