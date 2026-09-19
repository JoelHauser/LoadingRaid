using System.Collections.Generic;
using System.Globalization;
using System.Text;
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

        /// <summary>Screen sizes the ideal-size line has been logged for this session.</summary>
        private static readonly HashSet<string> Logged = new HashSet<string>();

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
        private static RectTransform _lastMeasured;
        private static Canvas _lastCanvas;

        /// <summary>Drops the cached canvas, for a panel that has been rebuilt.</summary>
        internal static void ForgetCanvas()
        {
            _lastMeasured = null;
            _lastCanvas = null;
        }

        internal static bool TryMeasure(RectTransform rect, float zoom, out BannerFit fit)
        {
            fit = default(BannerFit);

            if (rect == null || !rect.gameObject.activeInHierarchy) return false;

            // Cached across attempts: the driver retries this for up to 120 frames, and
            // GetComponentInParent walks the hierarchy every single time. The banner does not
            // change canvas between one frame and the next.
            Canvas canvas;
            if (ReferenceEquals(rect, _lastMeasured) && _lastCanvas != null)
            {
                canvas = _lastCanvas;
            }
            else
            {
                canvas = rect.GetComponentInParent<Canvas>();
                if (canvas == null) return false;
                canvas = canvas.rootCanvas;
                _lastMeasured = rect;
                _lastCanvas = canvas;
            }

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
        /// Keeps a measurement for the current screen and, the first time a screen size is seen
        /// this session, says in the log what size to make custom images.
        /// </summary>
        internal static void Record(BannerFit fit)
        {
            var key = ScreenKey;

            BannerFit known;
            var changed = !ByScreen.TryGetValue(key, out known)
                          || known.Width != fit.Width
                          || known.Height != fit.Height;

            ByScreen[key] = fit;

            if (changed) Save();

            if (!Logged.Add(key)) return;

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

        // -------------------------------------------------------------- remembering

        /// <summary>
        /// Measurements only last a session, and the first raid of one therefore has none -- so it
        /// loads the *largest* size of every picture, and the smaller one it settles on from the
        /// second raid onwards is decoded and kept as well. At around 22 MB for a banner sized for
        /// 4K that is worth avoiding, so what was measured is written to the config file and read
        /// back at load.
        ///
        /// It is only a head start: every Show measures again and overwrites this, so a screen or
        /// interface-scale change costs one raid of the old numbers, exactly as it did before.
        /// </summary>
        internal static void Remember(string saved)
        {
            ByScreen.Clear();

            if (string.IsNullOrEmpty(saved)) return;

            // The array overload on purpose. This assembly compiles against the game's own
            // mscorlib -- Managed is on the search path -- and Unity's Mono carries several
            // string methods plain .NET Framework does not: Split(char, StringSplitOptions) is
            // one, and it is what the compiler picks from Split(';'). The game runs it fine; the
            // test harness, which loads this DLL on .NET Framework, gets MissingMethodException
            // and cannot check any of this code. Asking for the overload both runtimes have
            // costs nothing and keeps the tests able to run.
            foreach (var entry in saved.Split(new[] { ';' }))
            {
                var equals = entry.IndexOf('=');
                if (equals <= 0) continue;

                var key = entry.Substring(0, equals).Trim();
                int width, height;

                if (key.Length > 0 && TryReadSize(entry.Substring(equals + 1), out width, out height))
                {
                    ByScreen[key] = new BannerFit { Width = width, Height = height };
                }
            }
        }

        /// <summary>"765x460" -- the shape both halves of the saved line are written in.</summary>
        private static bool TryReadSize(string text, out int width, out int height)
        {
            width = 0;
            height = 0;

            var by = text.IndexOf('x');
            if (by <= 0) return false;

            return int.TryParse(text.Substring(0, by).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
                   && int.TryParse(text.Substring(by + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
                   && width > 0 && height > 0;
        }

        /// <summary>
        /// Writes the table back to the config file. Setting a ConfigEntry saves the whole file
        /// synchronously, which is why Record only calls this when the numbers actually changed:
        /// that is once for a screen size the player has never used, and never again.
        /// </summary>
        private static void Save()
        {
            // Null outside the game -- scripts\test-logic.ps1 loads this assembly with no plugin.
            if (DeployScreenPlugin.MeasuredSizes == null) return;

            var text = new StringBuilder();

            foreach (var entry in ByScreen)
            {
                if (text.Length > 0) text.Append(';');

                text.Append(entry.Key).Append('=').Append(entry.Value.Width).Append('x').Append(entry.Value.Height);
            }

            DeployScreenPlugin.MeasuredSizes.Value = text.ToString();
        }
    }
}
