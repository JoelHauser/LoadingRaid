using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>How big a banner really is on the screen, in pixels.</summary>
    internal struct BannerFit
    {
        internal int Width;
        internal int Height;

        /// <summary>The banner's shape. Falls back to the stock art's when there is no measurement.</summary>
        internal float Aspect
        {
            get { return Height > 0 ? (float)Width / Height : ScreenFit.StockAspect; }
        }
    }

    /// <summary>
    /// Measures banners on the screen, rather than working their size out from the resolution.
    ///
    /// Working it out would mean knowing how the game scales its interface -- which CanvasScaler
    /// mode, which reference resolution, whether it keeps the interface inside a 16:9 area on an
    /// ultrawide screen. None of that is in code; it is serialized into prefabs. Reading a
    /// banner's corners in screen space sidesteps all of it and is right on any resolution and
    /// any shape of screen, which is what 1080p, 4K, 21:9 and 16:10 support comes down to.
    ///
    /// A measurement is kept per screen size for the session, so a player who changes resolution
    /// is measured again rather than handed the old numbers.
    /// </summary>
    internal static class ScreenFit
    {
        /// <summary>The stock banner art is 765x460 -- the size, presumably, it is drawn at on 1080p.</summary>
        internal const int StockWidth = 765;
        internal const int StockHeight = 460;
        internal const float StockAspect = StockWidth / (float)StockHeight;

        /// <summary>
        /// How close to the banner's size a picture must be to count as sharp. A couple of
        /// percent short is not visible, and without the slack a 1529-pixel image would be
        /// rejected for a 1530-pixel banner.
        /// </summary>
        private const float SharpEnough = 0.98f;

        private static readonly Dictionary<string, BannerFit> ByScreen = new Dictionary<string, BannerFit>();
        private static readonly Vector3[] Corners = new Vector3[4];

        internal static string ScreenKey
        {
            get { return Screen.width + "x" + Screen.height; }
        }

        /// <summary>The measurement for the screen as it is now, if a banner has been measured at this size.</summary>
        internal static bool TryCurrent(out BannerFit fit)
        {
            return ByScreen.TryGetValue(ScreenKey, out fit);
        }

        internal static bool IsSharp(float width, float height, BannerFit fit)
        {
            return width >= fit.Width * SharpEnough && height >= fit.Height * SharpEnough;
        }

        /// <summary>
        /// One banner image's size in screen pixels. zoom is how far motion has scaled it at this
        /// moment; it is divided back out, since KenBurns zooms about the centre and the size that
        /// matters is the banner at rest.
        /// </summary>
        internal static bool TryMeasure(RectTransform rect, float zoom, out BannerFit fit)
        {
            fit = default(BannerFit);

            if (rect == null || !rect.gameObject.activeInHierarchy) return false;

            var canvas = rect.GetComponentInParent<Canvas>();
            if (canvas == null) return false;
            canvas = canvas.rootCanvas;

            // An overlay canvas already draws in screen pixels and takes no camera; the others
            // are projected through theirs.
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;

            // Corners run bottom-left, top-left, top-right, bottom-right.
            rect.GetWorldCorners(Corners);
            var bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, Corners[0]);
            var topLeft = RectTransformUtility.WorldToScreenPoint(camera, Corners[1]);
            var bottomRight = RectTransformUtility.WorldToScreenPoint(camera, Corners[3]);

            var scale = zoom > 0.01f ? zoom : 1f;
            var width = Vector2.Distance(bottomLeft, bottomRight) / scale;
            var height = Vector2.Distance(bottomLeft, topLeft) / scale;

            // Before layout has run a banner can be zero-sized; that is "not yet", not a size.
            if (width < 2f || height < 2f) return false;

            fit = new BannerFit { Width = Mathf.RoundToInt(width), Height = Mathf.RoundToInt(height) };
            return true;
        }

        /// <summary>
        /// Keeps a measurement for the current screen and, the first time a screen size is seen,
        /// says in the log what size to make custom images.
        /// </summary>
        internal static void Record(BannerFit fit)
        {
            var key = ScreenKey;
            var isNew = !ByScreen.ContainsKey(key);

            ByScreen[key] = fit;

            if (!isNew) return;

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] banners show at " + fit.Width + "x" + fit.Height + " px on this "
                + key + " screen (" + Shape(Screen.width, Screen.height) + "). Custom images at least "
                + "that size will look sharp; the stock art is " + StockWidth + "x" + StockHeight + ".");
        }

        private static string Shape(int width, int height)
        {
            return height > 0
                ? ((float)width / height).ToString("0.00", CultureInfo.InvariantCulture) + ":1"
                : "unknown shape";
        }
    }
}
