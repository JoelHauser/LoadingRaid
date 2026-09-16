using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// Watches one banner panel, dresses each banner as it appears, and measures one of them on
    /// the screen.
    ///
    /// The panel fills _locationBanners over several frames -- each banner waits on its image
    /// before being added -- so there is no single moment afterwards to do this in. A Harmony
    /// postfix is no help either: Show is async, so it returns its Task long before any banner
    /// exists. Watching the list is simply what fits.
    ///
    /// _locationBanners holds only the map's banners. The key-binding banners the tutorial adds
    /// go through a different path and never appear here, which is exactly what we want: panning
    /// an instructional diagram or captioning it with boss chances would be wrong.
    /// </summary>
    internal sealed class BannerDriver : MonoBehaviour
    {
        /// <summary>
        /// Frames to keep trying to measure once banners exist. Layout can take a frame or two; a
        /// banner that has no size after a couple of seconds is not going to get one.
        /// </summary>
        private const int MeasureAttempts = 120;

        private object _panel;
        private List<IntelCard> _cards;
        private List<BannerImage> _images;

        /// <summary>
        /// The panel's own _locationBanners. The panel builds it in its constructor and never
        /// replaces it -- Close only clears it -- so reading the field once per Show stands for
        /// the whole raid, and an idle frame costs a Count and nothing else.
        /// </summary>
        private IList _banners;

        private readonly List<KenBurns> _attached = new List<KenBurns>();
        private readonly List<RectTransform> _measurable = new List<RectTransform>();

        /// <summary>How far along _banners this has already dressed. The list only grows within a Show.</summary>
        private int _scanned;

        private int _nextCard;
        private bool _measured;
        private int _attempts;
        private bool _warnedOnce;

        /// <summary>
        /// Starts a fresh pass. Called on every Show, because the panel is reused between raids
        /// and the map -- and so the intel and the pictures -- will usually have changed.
        /// </summary>
        internal void Begin(object panel, List<IntelCard> cards, List<BannerImage> images)
        {
            // Every banner of the last raid is a fresh object built by CreateBanner, so holding
            // the old components would grow this list by a page of dead entries per raid.
            RestoreAttached();
            _attached.Clear();

            _panel = panel;
            _cards = cards;
            _images = images;
            _nextCard = 0;
            _scanned = 0;
            _measurable.Clear();
            _measured = false;
            _attempts = 0;

            _banners = GameTypes.BannersPanel_LocationBanners == null
                ? null
                : GameTypes.BannersPanel_LocationBanners.GetValue(panel) as IList;

            enabled = true;
        }

        private void Update()
        {
            if (_panel == null || _banners == null)
            {
                enabled = false;
                return;
            }

            try
            {
                var count = _banners.Count;

                // Close clears the list. If that happened without the postfix disabling this --
                // an install where Close could not be resolved -- start the pass over.
                if (count < _scanned) _scanned = 0;

                for (; _scanned < count; _scanned++)
                {
                    var entry = _banners[_scanned];
                    if (entry == null) continue;

                    var banner = GameTypes.BannerWithToggle_Banner == null
                        ? null
                        : GameTypes.BannerWithToggle_Banner.GetValue(entry);

                    if (banner == null) continue;

                    Dress(banner);
                }

                if (!_measured) Measure();
            }
            catch (Exception error)
            {
                WarnOnce(error);
                enabled = false;
            }
        }

        private void Dress(object banner)
        {
            var rect = ImageRect(banner);

            if (rect != null)
            {
                _measurable.Add(rect);

                if (DeployScreenPlugin.MotionEnabled.Value) AddMotion(rect, BannerGroup(banner));
            }

            if (DeployScreenPlugin.BannerCaptions.Value == CaptionSource.Intel) AddIntel(banner);
        }

        /// <summary>
        /// The banner's CanvasGroup, which is how motion tells whether it is worth moving. Null
        /// when the field could not be resolved, and motion then treats every banner as visible.
        /// </summary>
        private static CanvasGroup BannerGroup(object banner)
        {
            return GameTypes.Banner_BannerCanvasGroup == null
                ? null
                : GameTypes.Banner_BannerCanvasGroup.GetValue(banner) as CanvasGroup;
        }

        /// <summary>
        /// The banner image's RectTransform. Read as Component rather than Image so this assembly
        /// needs no UnityEngine.UI reference; the transform is all that is used.
        /// </summary>
        private static RectTransform ImageRect(object banner)
        {
            var image = GameTypes.Banner_BannerImage == null
                ? null
                : GameTypes.Banner_BannerImage.GetValue(banner) as Component;

            return image == null ? null : image.transform as RectTransform;
        }

        // ------------------------------------------------------------------ motion

        private void AddMotion(RectTransform rect, CanvasGroup group)
        {
            if (rect.GetComponent<KenBurns>() != null) return;

            var motion = rect.gameObject.AddComponent<KenBurns>();
            motion.Zoom = DeployScreenPlugin.MotionZoom.Value;
            motion.Period = DeployScreenPlugin.MotionPeriod.Value;
            motion.Group = group;

            _attached.Add(motion);
        }

        // ------------------------------------------------------------ measurement

        /// <summary>
        /// Measures the first banner that has a size, records it for this screen, and warns about
        /// custom pictures too small for it. Every banner is the same size, so one is enough.
        ///
        /// The measurement only affects which picture sizes are chosen from the next raid on:
        /// the banners on screen now were built before it existed.
        /// </summary>
        private void Measure()
        {
            if (_measurable.Count == 0) return;

            if (++_attempts > MeasureAttempts)
            {
                _measured = true;
                return;
            }

            foreach (var rect in _measurable)
            {
                if (rect == null) continue;

                var motion = rect.GetComponent<KenBurns>();
                var zoom = motion != null ? motion.CurrentZoom : 1f;

                BannerFit fit;
                if (!ScreenFit.TryMeasure(rect, zoom, out fit)) continue;

                ScreenFit.Record(fit);
                BannerArt.WarnIfSoft(_images, fit);

                _measured = true;
                return;
            }
        }

        // ------------------------------------------------------------------- intel

        /// <summary>
        /// Points the banner's caption fields at this mod's locale keys. The panel reads them when
        /// a banner is selected, so setting them as the banner appears is enough -- and if the
        /// very first selection happens to beat this, the next switch corrects it.
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

            // No locale table: the heading still shows raw text, but a raw description would be
            // hidden by the panel's own equality check, so do not set one.
            GameTypes.Banner_BannerName.SetValue(banner, _cards[index].Header);
            GameTypes.Banner_BannerDescription.SetValue(banner, string.Empty);
        }

        private void OnDisable()
        {
            RestoreAttached();
        }

        /// <summary>
        /// Puts every banner this pass moved back where it was found. A banner destroyed with the
        /// panel restores itself anyway; this is for the ones still on screen when it closes.
        /// </summary>
        private void RestoreAttached()
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

    /// <summary>
    /// The same idea for captions taken from a picture's file name. They are registered rather
    /// than written straight onto the banner for the reason in Localization.cs: SelectBanner
    /// hides a description that localizes to itself, which is what raw text does.
    /// </summary>
    internal static class FileCaptionKeys
    {
        internal static string Header(int index)
        {
            return Localization.KeyPrefix + "file/" + index + "/h";
        }

        internal static string Body(int index)
        {
            return Localization.KeyPrefix + "file/" + index + "/d";
        }
    }
}
