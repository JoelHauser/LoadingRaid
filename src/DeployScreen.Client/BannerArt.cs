using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>One image on disk, and the captions drawn over it.</summary>
    internal sealed class BannerImage
    {
        internal string Path;
        internal string Name;
        internal string Description;

        private Sprite _sprite;
        private bool _failed;

        /// <summary>
        /// The sprite, decoded on first use and kept. Null if the file will not decode --
        /// asked again it stays null rather than re-reading a file that already failed.
        /// </summary>
        internal Sprite Sprite()
        {
            if (_sprite != null || _failed) return _sprite;

            try
            {
                var bytes = File.ReadAllBytes(Path);

                // Size and format are replaced by LoadImage; these are only placeholders.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                if (!ImageConversion.LoadImage(texture, bytes))
                {
                    UnityEngine.Object.Destroy(texture);
                    _failed = true;
                    DeployScreenPlugin.Log.LogWarning("[DeployScreen] not a readable PNG/JPG: " + Path);
                    return null;
                }

                // Clamp stops the edge pixels bleeding across when the banner is scaled.
                texture.wrapMode = TextureWrapMode.Clamp;

                _sprite = UnityEngine.Sprite.Create(
                    texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);

                return _sprite;
            }
            catch (Exception error)
            {
                _failed = true;
                DeployScreenPlugin.Log.LogWarning("[DeployScreen] could not load " + Path + ": " + error.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// The art on disk, found once per folder and cached.
    ///
    /// Layout, under the plugin's own folder:
    ///
    ///     banners/&lt;locationId&gt;/*.png|jpg     art for one map -- bigmap, woods, factory4_day...
    ///     banners/_default/*.png|jpg           used by any map with no folder of its own
    ///
    /// A map with neither folder is left completely alone, which is what makes an empty
    /// install behave exactly like vanilla.
    /// </summary>
    internal static class BannerArt
    {
        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

        private static readonly Dictionary<string, List<BannerImage>> Cache =
            new Dictionary<string, List<BannerImage>>(StringComparer.OrdinalIgnoreCase);

        internal static string RootFolder;

        internal const string DefaultFolder = "_default";

        /// <summary>Drops the cache so edits on disk are picked up without a restart.</summary>
        internal static void Forget()
        {
            Cache.Clear();
        }

        /// <summary>
        /// The images for a map, or null when there are none -- null means "leave vanilla
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
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] " + found.Count + " custom banner(s) for '" + key + "'");
            }

            return found;
        }

        private static List<BannerImage> Read(string folder)
        {
            if (!Directory.Exists(folder)) return null;

            var images = new List<BannerImage>();

            var files = Directory.GetFiles(folder);

            // Sorted so the order on the deploy screen is the order in the folder listing,
            // which is what anyone numbering their files 01..09 expects.
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (Array.IndexOf(Extensions, Path.GetExtension(file).ToLowerInvariant()) < 0) continue;

                string name, description;
                CaptionsFrom(Path.GetFileNameWithoutExtension(file), out name, out description);

                images.Add(new BannerImage { Path = file, Name = name, Description = description });
            }

            return images.Count > 0 ? images : null;
        }

        /// <summary>
        /// Captions read out of the file name: "Dorms|Three storey, two keys" splits on the
        /// pipe, and a leading "01 - " or "3." is treated as ordering and dropped.
        /// </summary>
        internal static void CaptionsFrom(string fileName, out string name, out string description)
        {
            description = string.Empty;

            var pipe = fileName.IndexOf('|');
            if (pipe >= 0)
            {
                description = fileName.Substring(pipe + 1).Trim();
                fileName = fileName.Substring(0, pipe);
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
            var rest = text.Substring(at).TrimStart(' ');

            if (rest.Length == 0 || ".-_)".IndexOf(rest[0]) < 0) return text;

            return rest.TrimStart('.', '-', '_', ')', ' ');
        }
    }
}
