using System;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// How bright the picture is in the two corners the writing has to live in.
    /// </summary>
    internal struct ToneReading
    {
        /// <summary>False when nothing could be measured, and the caller should use its defaults.</summary>
        internal bool Known;

        /// <summary>0 for black, 1 for white, where the map name and its intel line sit.</summary>
        internal float Top;

        /// <summary>The same, along the bottom, where the progress line sits.</summary>
        internal float Bottom;

        /// <summary>
        /// Plain mean colours, through the wash, for lighting the PMC: the top third (sky, or a
        /// ceiling), the bottom quarter (the ground he would be standing on) and the whole
        /// picture. Full width, unlike the corners above -- light comes from all of it.
        /// </summary>
        internal Color Sky;
        internal Color Ground;
        internal Color Average;
        internal bool HasColour;
    }

    /// <summary>
    /// Measures the art behind the type.
    ///
    /// The scrims under the corners were a pair of fixed numbers, and a fixed number cannot be
    /// right: these are the player's own screenshots. Woods at dawn is dark enough that white
    /// type reads over it unaided and any dimming is just a bruise across the picture; a snow
    /// map or a bright sky is brighter than the type and no amount of dimming-by-eyeball is
    /// going to be set correctly for both. Same scrim, two wrong answers.
    ///
    /// So it is measured, like everything else here. Not on the CPU, though: the decoded
    /// picture has no pixels on this side -- LoadImage is told to drop the copy, because a
    /// banner sized for a 4K screen is 22 MB and keeping both would double it. Reading them
    /// back would mean keeping that copy for every picture to sample two corners of one.
    ///
    /// The GPU already has the picture. Blitting a corner into a 32-pixel-wide render texture
    /// is a downscale the hardware does for free, and reading back a thumbnail that size is a
    /// kilobyte. It costs one small stall, once per raid, on a frame where the game is already
    /// stalling on scene integration.
    /// </summary>
    internal static class ArtTone
    {
        /// <summary>The reading for the picture now on the screen. Unknown until one is hung.</summary>
        internal static ToneReading Current;

        private const int Wide = 32;
        private const int TopTall = 10;
        private const int BottomTall = 8;

        /// <summary>
        /// Where the writing sits, as a fraction of the picture on screen. Not the full width:
        /// a bright sky over on the right is not a reason to dim the corner the type is in, and
        /// the scrim that does the dimming is full width whatever this says.
        /// </summary>
        private static readonly Rect TopBand = new Rect(0f, 0.70f, 0.58f, 0.30f);
        private static readonly Rect BottomBand = new Rect(0f, 0f, 0.62f, 0.20f);

        internal static void Forget()
        {
            Current = default(ToneReading);
        }

        /// <summary>
        /// Reads the picture as it will appear -- through the wash the map's light puts over it,
        /// which is the difference between what the file holds and what reaches the screen.
        /// </summary>
        internal static void Measure(Sprite sprite, Color wash)
        {
            Current = default(ToneReading);

            if (sprite == null || sprite.texture == null) return;

            try
            {
                var texture = sprite.texture;
                var crop = sprite.textureRect;

                if (crop.width < 2f || crop.height < 2f) return;
                if (texture.width < 2 || texture.height < 2) return;

                float top, bottom;
                if (!Read(texture, crop, TopBand, Wide, TopTall, out top)) return;
                if (!Read(texture, crop, BottomBand, Wide, BottomTall, out bottom)) return;

                // The plane is tinted toward the destination's own light, so a night map arrives
                // on screen darker than its file. Measuring the file and ignoring the tint would
                // dim the corners of a picture that was never bright.
                var tint = Luminance(wash.r, wash.g, wash.b) * (wash.a <= 0f ? 1f : Mathf.Clamp01(wash.a));

                Current = new ToneReading
                {
                    Known = true,
                    Top = Mathf.Clamp01(top * tint),
                    Bottom = Mathf.Clamp01(bottom * tint),
                };

                // Colour is a second, optional reading: a failure here costs the PMC his
                // picture-coloured light and nothing else, so it must not lose the corners.
                Color sky, ground, average;
                if (ReadColour(texture, crop, SkyBand, out sky)
                    && ReadColour(texture, crop, GroundBand, out ground)
                    && ReadColour(texture, crop, WholeBand, out average))
                {
                    var through = new Color(wash.r, wash.g, wash.b, 1f);
                    Current.Sky = sky * through;
                    Current.Ground = ground * through;
                    Current.Average = average * through;
                    Current.HasColour = true;
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] art tone: top=" + Current.Top.ToString("0.00")
                    + " bottom=" + Current.Bottom.ToString("0.00")
                    + " (picture " + top.ToString("0.00") + "/" + bottom.ToString("0.00")
                    + " through a wash of " + tint.ToString("0.00") + ")"
                    + (Current.HasColour
                        ? "; sky " + Rgb(Current.Sky) + " ground " + Rgb(Current.Ground)
                          + " average " + Rgb(Current.Average)
                        : "; no colour reading"));
            }
            catch (Exception error)
            {
                Current = default(ToneReading);

                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not measure the art, using the standing scrim: " + error.Message);
            }
        }

        /// <summary>
        /// One corner of one picture, downscaled by the hardware and read back as a thumbnail.
        ///
        /// The scale and offset are in the whole texture's coordinates, not the sprite's: the
        /// sprite is a cover-crop of the file and the blit knows nothing about that, so the
        /// band is placed inside the crop and the crop inside the file, in that order.
        /// </summary>
        private static bool Read(Texture texture, Rect crop, Rect band, int wide, int tall, out float level)
        {
            level = 0f;

            var pixels = Sample(texture, crop, band, wide, tall);
            if (pixels == null) return false;

            level = Level(pixels);
            return true;
        }

        /// <summary>Bands for the colour reading, in the picture's own 0..1, y up.</summary>
        private static readonly Rect SkyBand = new Rect(0f, 0.67f, 1f, 0.33f);
        private static readonly Rect GroundBand = new Rect(0f, 0f, 1f, 0.25f);
        private static readonly Rect WholeBand = new Rect(0f, 0f, 1f, 1f);

        /// <summary>One band's plain mean colour. A mean, not ArtTone's bright-weighted level:
        /// light is the sum of everything in the band, not its brightest line.</summary>
        private static bool ReadColour(Texture texture, Rect crop, Rect band, out Color colour)
        {
            colour = Color.grey;

            var pixels = Sample(texture, crop, band, 16, 8);
            if (pixels == null || pixels.Length == 0) return false;

            float r = 0f, g = 0f, b = 0f;
            for (var i = 0; i < pixels.Length; i++)
            {
                r += pixels[i].r;
                g += pixels[i].g;
                b += pixels[i].b;
            }

            var n = pixels.Length * 255f;
            colour = new Color(r / n, g / n, b / n, 1f);
            return true;
        }

        private static string Rgb(Color c)
        {
            return c.r.ToString("0.00") + "/" + c.g.ToString("0.00") + "/" + c.b.ToString("0.00");
        }

        /// <summary>The band, downscaled by the hardware and read back. Null when the band is empty.</summary>
        private static Color32[] Sample(Texture texture, Rect crop, Rect band, int wide, int tall)
        {
            var scale = new Vector2(
                crop.width * band.width / texture.width,
                crop.height * band.height / texture.height);

            var offset = new Vector2(
                (crop.x + crop.width * band.x) / texture.width,
                (crop.y + crop.height * band.y) / texture.height);

            if (scale.x <= 0f || scale.y <= 0f) return null;

            RenderTexture sheet = null;
            Texture2D read = null;
            var was = RenderTexture.active;

            try
            {
                // Default rather than Linear or sRGB on purpose: the project's own colour space
                // decides, so the bytes that come back are the ones a person would see in an
                // image editor whichever way the game is set up.
                sheet = RenderTexture.GetTemporary(
                    wide, tall, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);

                Graphics.Blit(texture, sheet, scale, offset);

                RenderTexture.active = sheet;

                read = new Texture2D(wide, tall, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0f, 0f, wide, tall), 0, 0, false);
                read.Apply(false, false);

                return read.GetPixels32();
            }
            finally
            {
                RenderTexture.active = was;

                if (sheet != null) RenderTexture.ReleaseTemporary(sheet);
                if (read != null) UnityEngine.Object.Destroy(read);
            }
        }

        /// <summary>
        /// One number for a corner, weighted toward its bright end.
        ///
        /// The average is the wrong answer: a line of white type is unreadable over the
        /// brightest thing it crosses, not over the mean of everything around it. A horizon
        /// through the lower third of the band would barely move an average and is exactly what
        /// swallows the map name.
        /// </summary>
        private static float Level(Color32[] pixels)
        {
            if (pixels == null || pixels.Length == 0) return 0f;

            var values = new float[pixels.Length];
            var total = 0f;

            for (var i = 0; i < pixels.Length; i++)
            {
                var one = Luminance(pixels[i].r / 255f, pixels[i].g / 255f, pixels[i].b / 255f);
                values[i] = one;
                total += one;
            }

            Array.Sort(values);

            var high = values[Mathf.Clamp(Mathf.RoundToInt((values.Length - 1) * 0.85f), 0, values.Length - 1)];
            var mean = total / values.Length;

            return Mathf.Clamp01(Mathf.Lerp(mean, high, 0.65f));
        }

        /// <summary>Rec. 709, because green is most of what an eye calls brightness.</summary>
        private static float Luminance(float r, float g, float b)
        {
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }
    }
}
