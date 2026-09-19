using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>One file: one size of one picture.</summary>
    internal sealed class BannerVariant
    {
        internal string Path;

        /// <summary>Read from the file's header without decoding it. Zero when it could not be read.</summary>
        internal int Width;
        internal int Height;

        private Texture2D _texture;
        private bool _failed;
        private readonly Dictionary<int, Sprite> _sprites = new Dictionary<int, Sprite>();

        /// <summary>
        /// The pixels this file covers once cropped to a banner of the given shape. Sizes are
        /// compared on this, not on the raw file size, so a wide screenshot is not mistaken for
        /// a sharp one: most of its width is cropped away.
        /// </summary>
        internal void CroppedSize(float aspect, out float width, out float height)
        {
            var crop = BannerArt.CoverRect(Width, Height, aspect);
            width = crop.width;
            height = crop.height;
        }

        /// <summary>
        /// The sprite for a banner of this shape, decoded on first use and kept. Null if the file
        /// will not decode -- asked again it stays null rather than re-reading a file that already
        /// failed.
        /// </summary>
        internal Sprite SpriteFor(float aspect)
        {
            var texture = Texture();
            if (texture == null) return null;

            // Keyed by shape to two decimals. A new shape is only a new crop of the same texture,
            // which costs nothing to make.
            var key = Mathf.RoundToInt(aspect * 100f);

            Sprite sprite;
            if (_sprites.TryGetValue(key, out sprite) && sprite != null) return sprite;

            // FullRect: a tight mesh would need the texture's pixels on the CPU, and those are
            // let go of when it is decoded.
            sprite = Sprite.Create(
                texture,
                BannerArt.CoverRect(texture.width, texture.height, aspect),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);

            _sprites[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// Lets go of the decoded picture and the sprites cut from it. Asked for again it reads
        /// the file afresh; a file that already failed to decode stays failed and is not retried.
        ///
        /// False when there was nothing to let go of, so a caller can say how much it freed.
        /// </summary>
        internal bool Release()
        {
            if (_texture == null) return false;

            foreach (var sprite in _sprites.Values)
            {
                if (sprite != null) UnityEngine.Object.Destroy(sprite);
            }

            _sprites.Clear();

            UnityEngine.Object.Destroy(_texture);
            _texture = null;

            return true;
        }

        private Texture2D Texture()
        {
            if (_texture != null || _failed) return _texture;

            var started = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var bytes = File.ReadAllBytes(Path);

                // Size and format are replaced by LoadImage; these are only placeholders.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                // markNonReadable drops the CPU copy once the image is on the GPU. A banner sized
                // for a 4K screen is about 22 MB, and keeping both copies would double it.
                if (!ImageConversion.LoadImage(texture, bytes, true))
                {
                    UnityEngine.Object.Destroy(texture);
                    _failed = true;
                    DeployScreenPlugin.Log.LogWarning("[DeployScreen] not a readable PNG/JPG: " + Path);
                    return null;
                }

                // Clamp stops the edge pixels bleeding across when the banner is scaled.
                texture.wrapMode = TextureWrapMode.Clamp;

                _texture = texture;

                BannerArt.DecodeMillis += started.Elapsed.TotalMilliseconds;
                BannerArt.DecodeCount++;

                return _texture;
            }
            catch (Exception error)
            {
                _failed = true;
                DeployScreenPlugin.Log.LogWarning("[DeployScreen] could not load " + Path + ": " + error.Message);
                return null;
            }
        }
    }

    /// <summary>One picture, in however many sizes its folder holds, and the captions drawn over it.</summary>
    internal sealed class BannerImage
    {
        /// <summary>The file name without extension or size tag -- "01 - Dorms".</summary>
        internal string Label;
        internal string Name;
        internal string Description;

        internal readonly List<BannerVariant> Variants = new List<BannerVariant>();

        /// <summary>
        /// Which size to use. With the banner measured on this screen: the smallest file that is
        /// still sharp at that size, or the largest if none is. Before any measurement -- the first
        /// raid at a resolution -- the largest, so it looks sharp rather than soft.
        /// </summary>
        internal BannerVariant Choose(BannerFit fit, bool measured)
        {
            var aspect = measured ? fit.Aspect : ScreenFit.StockAspect;

            BannerVariant largest = null;
            BannerVariant smallestSharp = null;
            var largestArea = -1.0;
            var smallestSharpArea = double.MaxValue;

            foreach (var variant in Variants)
            {
                float width, height;
                variant.CroppedSize(aspect, out width, out height);

                var area = (double)width * height;

                if (area > largestArea)
                {
                    largestArea = area;
                    largest = variant;
                }

                if (measured && ScreenFit.IsSharp(width, height, fit) && area < smallestSharpArea)
                {
                    smallestSharpArea = area;
                    smallestSharp = variant;
                }
            }

            return smallestSharp ?? largest;
        }

        internal Sprite Sprite(BannerFit fit, bool measured)
        {
            var variant = Choose(fit, measured);
            return variant == null ? null : variant.SpriteFor(measured ? fit.Aspect : ScreenFit.StockAspect);
        }
    }

    /// <summary>
    /// The art on disk, found once per folder and cached.
    ///
    /// Layout, under the plugin's own folder:
    ///
    ///     banners/&lt;locationId&gt;/*.png|jpg     art for one map -- bigmap, Woods, factory4_day...
    ///     banners/_default/*.png|jpg           used by any map with no folder of its own
    ///
    /// Several sizes of one picture share a name with a size tag after an @:
    /// "01 - Dorms.png", "01 - Dorms@1440p.png", "01 - Dorms@4k.jpg". The tag is only a label;
    /// the real size is read from each file.
    ///
    /// A map with neither folder is left completely alone, which is what makes an empty install
    /// behave exactly like vanilla.
    /// </summary>
    internal static class BannerArt
    {
        /// <summary>
        /// What decoding pictures has cost this session, in milliseconds, and how many were
        /// decoded. Both only ever go up; a reader takes two readings and subtracts.
        ///
        /// This exists because "the art is stalling the load" was argued for two versions from
        /// whole-run gap totals taken in different sessions, which cannot settle it: a map's own
        /// scene integration is seconds of main-thread work and swamps anything this mod does.
        /// A number here ends the argument in one run -- either the decode is a visible slice of
        /// the load or it is a rounding error, and until now nobody could say which.
        /// </summary>
        internal static double DecodeMillis;
        internal static int DecodeCount;

        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

        private static readonly Dictionary<string, List<BannerImage>> Cache =
            new Dictionary<string, List<BannerImage>>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> WarnedSoft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static string RootFolder;

        internal const string DefaultFolder = "_default";

        /// <summary>Drops the cache so edits on disk are picked up without a restart.</summary>
        internal static void Forget()
        {
            Cache.Clear();
        }

        /// <summary>
        /// Frees every decoded picture this screen has no use for -- the sizes of each picture
        /// other than the one that would be chosen now.
        ///
        /// Called once the banners panel has closed, so nothing freed is still being drawn, and
        /// at the one moment in a raid where giving memory back is most useful: the map is about
        /// to load. Anything freed is read from disk again if it is wanted later.
        ///
        /// What makes it worth doing is the first raid at a resolution, which runs before any
        /// measurement exists and so loads the largest size of everything.
        /// </summary>
        internal static void ReleaseUnused()
        {
            try
            {
                BannerFit fit;
                if (!ScreenFit.TryCurrent(out fit)) return;

                var freed = 0;

                foreach (var images in Cache.Values)
                {
                    if (images == null) continue;

                    foreach (var image in images)
                    {
                        var keep = image.Choose(fit, true);

                        foreach (var variant in image.Variants)
                        {
                            if (variant != keep && variant.Release()) freed++;
                        }
                    }
                }

                if (freed > 0)
                {
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] freed " + freed + " banner picture(s) this screen has no use for");
                }
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning("[DeployScreen] could not free banner pictures: " + error.Message);
            }
        }

        /// <summary>
        /// The pictures for a map, or null when there are none -- null means "leave vanilla
        /// alone", and every caller treats it that way.
        /// </summary>
        internal static List<BannerImage> For(string locationId)
        {
            if (string.IsNullOrEmpty(RootFolder)) return null;

            var key = string.IsNullOrEmpty(locationId) ? DefaultFolder : locationId;

            List<BannerImage> cached;
            if (Cache.TryGetValue(key, out cached)) return cached;

            var found = Read(Path.Combine(RootFolder, key));

            // A map with no folder of its own falls back to the shared one.
            if (found == null && key != DefaultFolder)
            {
                found = Read(Path.Combine(RootFolder, DefaultFolder));
            }

            Cache[key] = found;

            if (found != null)
            {
                var files = 0;
                foreach (var image in found) files += image.Variants.Count;

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] " + found.Count + " custom banner(s) in " + files + " file(s) for '" + key + "'");
            }

            return found;
        }

        private static List<BannerImage> Read(string folder)
        {
            if (!Directory.Exists(folder)) return null;

            var byPicture = new Dictionary<string, BannerImage>(StringComparer.OrdinalIgnoreCase);
            var images = new List<BannerImage>();

            foreach (var file in Directory.GetFiles(folder))
            {
                if (Array.IndexOf(Extensions, Path.GetExtension(file).ToLowerInvariant()) < 0) continue;

                var picture = PictureName(Path.GetFileNameWithoutExtension(file));

                BannerImage image;
                if (!byPicture.TryGetValue(picture, out image))
                {
                    string name, description;
                    CaptionsFrom(picture, out name, out description);

                    image = new BannerImage { Label = picture, Name = name, Description = description };
                    byPicture[picture] = image;
                    images.Add(image);
                }

                int width, height;
                ImageHeader.TryReadSize(file, out width, out height);

                image.Variants.Add(new BannerVariant { Path = file, Width = width, Height = height });
            }

            // Sorted by picture, so the order on the deploy screen is the order of the names --
            // which is what anyone numbering their files 01..09 expects -- whatever size tags or
            // extensions the files carry.
            images.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label));

            return images.Count > 0 ? images : null;
        }

        /// <summary>
        /// "01 - Dorms@4k" and "01 - Dorms@2560x1540" are both the picture "01 - Dorms". Everything
        /// after the last @ is a size tag and is ignored.
        /// </summary>
        internal static string PictureName(string stem)
        {
            var at = stem.LastIndexOf('@');

            // TrimEnd(null), not TrimEnd(). See ScreenFit.Remember: this assembly compiles
            // against the game's own mscorlib, which carries string methods plain .NET
            // Framework does not, and the test harness loads the DLL on .NET Framework.
            return at > 0 ? stem.Substring(0, at).TrimEnd(null) : stem;
        }

        /// <summary>
        /// The largest centred area of an image that has the banner's shape: cropped, never
        /// stretched. A screenshot from a 21:9 monitor loses its sides; a tall one loses top and
        /// bottom. Whole pixels, and never outside the image, since Sprite.Create refuses a rect
        /// that strays past the texture by any amount.
        /// </summary>
        internal static Rect CoverRect(int width, int height, float aspect)
        {
            if (width <= 0 || height <= 0) return new Rect(0f, 0f, 0f, 0f);
            if (aspect <= 0f) return new Rect(0f, 0f, width, height);

            // Rounded, then clamped: rounding down would shave a column off an image that is
            // already the banner's exact shape (765 * 460/460 comes out as 764.9999), and the clamp
            // plus integer centring keeps the rect inside the image either way.
            // At least one pixel on each side. A very small source against a very wide shape
            // rounds the short side to zero -- a 1x1 image cropped to 48:9 wants 0.19 of a pixel --
            // and Sprite.Create throws on a zero-sized rect. One pixel is wrong-looking; none is a
            // crash.
            if ((float)width / height > aspect)
            {
                var croppedWidth = Math.Max(1, Math.Min(width, Mathf.RoundToInt(height * aspect)));
                return new Rect((width - croppedWidth) / 2, 0f, croppedWidth, height);
            }

            var croppedHeight = Math.Max(1, Math.Min(height, Mathf.RoundToInt(width / aspect)));
            return new Rect(0f, (height - croppedHeight) / 2, width, croppedHeight);
        }

        /// <summary>
        /// Decodes this map's pictures now, while the raid is still being configured, so the decode
        /// does not land in the middle of the load.
        ///
        /// Texture2D.LoadImage is a main-thread stall -- a 4K JPEG is tens of milliseconds of it --
        /// and until now it happened inside MatchmakerBannersPanel.Show, which is to say while the
        /// raid was loading and the frame budget was already the scarcest thing on the machine.
        /// The offline raid screen is up for as long as it takes you to pick a time of day, so the
        /// same work there costs nothing anyone will notice.
        ///
        /// Everything it decodes goes into the same cache the deploy screen reads, so this is
        /// purely a matter of *when*, not of extra work. Cheap to call twice.
        /// </summary>
        internal static void Prewarm(string locationId, BannerFit fit, bool measured, int onlyIndex)
        {
            if (string.IsNullOrEmpty(locationId)) return;

            try
            {
                var images = For(locationId);
                if (images == null || images.Count == 0) return;

                if (onlyIndex >= 0)
                {
                    if (onlyIndex < images.Count) images[onlyIndex].Sprite(fit, measured);
                    return;
                }

                for (var i = 0; i < images.Count; i++) images[i].Sprite(fit, measured);
            }
            catch (Exception error)
            {
                // A picture that will not decode is already handled where it is used; there is
                // nothing here worth interrupting the player's raid setup for.
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not pre-warm art for " + locationId + ": " + error.Message);
            }
        }

        /// <summary>
        /// Once a banner has been measured: a warning for each picture with no size sharp enough
        /// for this screen, once per file per screen size.
        /// </summary>
        internal static void WarnIfSoft(List<BannerImage> images, BannerFit fit)
        {
            if (images == null) return;

            foreach (var image in images)
            {
                var variant = image.Choose(fit, true);
                if (variant == null || variant.Width <= 0) continue;

                float width, height;
                variant.CroppedSize(fit.Aspect, out width, out height);
                if (ScreenFit.IsSharp(width, height, fit)) continue;

                if (!WarnedSoft.Add(variant.Path + "@" + ScreenFit.ScreenKey)) continue;

                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] " + Relative(variant.Path) + " is " + variant.Width + "x" + variant.Height
                    + " but banners show at " + fit.Width + "x" + fit.Height + " on this screen, so it will "
                    + "look soft. Save a bigger version as \"" + image.Label + "@large"
                    + Path.GetExtension(variant.Path) + "\" and it will be used instead.");
            }
        }

        /// <summary>A file's path from the plugin folder, so logs read "banners\bigmap\01 - Dorms.png".</summary>
        private static string Relative(string path)
        {
            var pluginFolder = Path.GetDirectoryName(RootFolder);

            return !string.IsNullOrEmpty(pluginFolder) && path.StartsWith(pluginFolder, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(pluginFolder.Length).TrimStart('\\', '/')
                : path;
        }

        /// <summary>
        /// Captions read out of the file name: "Dorms; Three storeys, two keys" splits on the
        /// first semicolon, and a leading "01 - " or "3." is treated as ordering and dropped.
        ///
        /// 1.1.0 split on a pipe, which Windows does not allow in a file name -- so no file could
        /// ever have carried a description. A pipe is still accepted, for files named somewhere
        /// that does allow it.
        /// </summary>
        internal static void CaptionsFrom(string fileName, out string name, out string description)
        {
            description = string.Empty;

            var split = fileName.IndexOfAny(new[] { ';', '|' });
            if (split >= 0)
            {
                description = fileName.Substring(split + 1).Trim();
                fileName = fileName.Substring(0, split);
            }

            name = StripOrderPrefix(fileName).Trim();
        }

        private static string StripOrderPrefix(string text)
        {
            var at = 0;
            while (at < text.Length && char.IsDigit(text[at])) at++;

            if (at == 0 || at >= text.Length) return text;

            // The separator has to be punctuation, not just a space. Otherwise "24 Hour Shift"
            // reads as banner 24 called "Hour Shift", while "01 - Dorms" and "3. Dorms" are
            // exactly what the digits-are-ordering rule is for.
            // The array, not the single char, for the same reason as PictureName.
            var rest = text.Substring(at).TrimStart(new[] { ' ' });

            if (rest.Length == 0 || ".-_)".IndexOf(rest[0]) < 0) return text;

            return rest.TrimStart('.', '-', '_', ')', ' ');
        }
    }
}
