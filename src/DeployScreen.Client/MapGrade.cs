using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>One map's light: what colour the place is, and how bright.</summary>
    internal struct Grade
    {
        /// <summary>The dominant light. Sodium for Factory, cold green for Woods, and so on.</summary>
        internal Color Key;

        /// <summary>The light that separates the character from what is behind them.</summary>
        internal Color Rim;

        /// <summary>A multiplier on the scene's own lights, for time of day and interiors.</summary>
        internal float Exposure;

        /// <summary>A tint laid over the map art itself, so the picture agrees with the light.</summary>
        internal Color Wash;
    }

    /// <summary>
    /// Makes the deploy screen look like the place you are going to.
    ///
    /// The PMC and the backdrop are drawn by different cameras -- the character is loaded onto
    /// LayersMaskController.WeaponPreview and parented to the PlayerModelView's own UI transform,
    /// so it is a preview composited over the scene rather than an object inside it. That is a
    /// hard architectural fact and it cannot be undone cheaply.
    ///
    /// It is also the opening. A composite is how film puts an actor somewhere they never went,
    /// and the thing that sells a composite is not geometry, it is **light agreement**. A Light
    /// whose cullingMask is only the WeaponPreview layer falls on the character and on nothing
    /// else, so the PMC can be lit by the same colour as the backdrop without touching the scene.
    /// Colour-match a key and a rim to the destination and the character stops reading as a
    /// cut-out standing in front of a picture.
    ///
    /// Nothing here needs an asset. It is two lights and a table of colours.
    /// </summary>
    internal sealed class MapGrade
    {
        private struct Lit { internal Light Light; internal Color Colour; }

        /// <summary>
        /// Light per map. Only Factory, Wood and Laboratory backdrops exist as scenes, but the
        /// *light* can say far more than three things, and it is the light that carries the place.
        /// </summary>
        private static readonly Dictionary<string, Grade> Grades =
            new Dictionary<string, Grade>(StringComparer.OrdinalIgnoreCase)
            {
                // Customs -- overcast, industrial, dust in the air.
                { "bigmap",         G(0xC8C0AE, 0x7A8A9E, 1.00f, 0xBFC3BE) },

                // Factory -- sodium light off wet concrete, no sky.
                { "factory4_day",   G(0xD8A45C, 0x4A5A6E, 0.92f, 0xC4A382) },
                { "factory4_night", G(0xC08A44, 0x36465C, 0.70f, 0x9C8062) },

                // Interchange -- dead mall fluorescents, warm and stale.
                { "interchange",    G(0xD8CBA8, 0x6E7E96, 0.88f, 0xC6BCA4) },

                // Labs -- clinical, too clean, faintly blue.
                { "laboratory",     G(0xDDE8F0, 0x6FA8C8, 1.05f, 0xC8D6E0) },
                { "labyrinth",      G(0xB8CAD8, 0x5A8CA8, 0.80f, 0xA6B8C6) },

                // Lighthouse -- grey coastal light, salt haze.
                { "lighthouse",     G(0xB6C4CE, 0x7E96A8, 0.95f, 0xB0BCC4) },

                // Reserve -- cold steel and concrete under a flat sky.
                { "rezervbase",     G(0xB4B8BC, 0x6A7684, 0.90f, 0xAEB2B6) },

                // Ground Zero -- warm urban daylight, glass and dust.
                { "sandbox",        G(0xE0D2B4, 0x8A9AAE, 1.05f, 0xCEC4B0) },
                { "sandbox_high",   G(0xE0D2B4, 0x8A9AAE, 1.05f, 0xCEC4B0) },

                // Shoreline -- pale green-grey, wet and overcast.
                { "shoreline",      G(0xC2C8B8, 0x7E94A0, 0.95f, 0xB6BEB4) },

                // Streets -- sodium night, amber against deep blue.
                { "tarkovstreets",  G(0xD2A268, 0x5A6E8C, 0.82f, 0xB09070) },

                // Woods -- cold desaturated green, first light.
                { "woods",          G(0xB8C4A8, 0x7A8E7E, 0.98f, 0xAEBAA6) },

                // The rest, kept plausible rather than invented with confidence.
                { "suburbs",        G(0xC6C2B4, 0x76869A, 0.92f, 0xB8B6AC) },
                { "town",           G(0xCCBFA0, 0x6E7E92, 0.90f, 0xBCB2A0) },
                { "terminal",       G(0xC8C4B6, 0x72829A, 0.94f, 0xBAB8AE) },
            };

        /// <summary>Neutral, for a map with no entry -- no colour opinion, just the rim.</summary>
        private static readonly Grade Neutral = G(0xC6C6C6, 0x7E8A96, 1.00f, 0xC0C0C0);

        /// <summary>This raid's time and weather, as read off RaidSettings before deploy.</summary>
        internal struct Weather
        {
            internal bool Known;
            internal int HourOfDay;
            internal bool HourFromClock;
            internal int Rain;        // ERainType     NoRain .. Shower
            internal int Fog;         // EFogType      NoFog  .. Continuous
            internal int Cloudiness;  // ECloudiness   Clear  .. Thundercloud
        }

        /// <summary>Night light: what everything drifts toward in the small hours.</summary>
        private static readonly Color NightKey = Rgb(0x6E7E9A);
        private static readonly Color NightRim = Rgb(0x8899BE);

        /// <summary>
        /// Bends a map's light to the raid actually being loaded.
        ///
        /// The per-map table says what a place looks like in the abstract. This says what it looks
        /// like *tonight* -- and it is the difference between the deploy screen describing Woods
        /// and the deploy screen describing the raid you are ten seconds from starting.
        ///
        /// Four things move, in the order they matter:
        ///
        ///   Hour        A smooth day/night curve rather than a switch, with dusk and dawn getting
        ///               their own warmth. Night pulls colour toward moonlight, drops the key a
        ///               long way and lifts the rim, because at night a figure reads by its edge.
        ///   Fog         Lifts everything toward grey and collapses the gap between key and rim.
        ///               Fog is a contrast eater before it is a colour.
        ///   Rain        Cools and darkens, and takes some saturation with it.
        ///   Cloudiness  Flattens: less key, less rim, no direction.
        ///
        /// All of it is bounded, so no combination can black the screen out or blow it white.
        /// </summary>
        internal static Grade ForRaid(string locationId, Weather weather)
        {
            var grade = For(locationId);
            if (!weather.Known) return grade;

            // --- hour of day -------------------------------------------------------------
            // Daylight from about 06:00 to 19:00, with an hour of transition at each end.
            var hour = Mathf.Repeat(weather.HourOfDay, 24f);
            var daylight = Mathf.Clamp01(Mathf.Min((hour - 4.5f) / 2.5f, (20.5f - hour) / 2.5f));

            // Golden hour: strongest where daylight is climbing or falling, absent at noon and
            // at midnight. A raid at 06:00 or 19:00 should look like it.
            var goldenness = Mathf.Clamp01(1f - Mathf.Abs(daylight - 0.5f) * 2.4f) * daylight;

            grade.Key = Color.Lerp(NightKey, grade.Key, daylight);
            grade.Rim = Color.Lerp(NightRim, grade.Rim, daylight * 0.75f + 0.25f);
            grade.Wash = Color.Lerp(NightKey, grade.Wash, daylight * 0.8f + 0.2f);

            grade.Key = Color.Lerp(grade.Key, Warm(grade.Key), goldenness * 0.55f);
            grade.Wash = Color.Lerp(grade.Wash, Warm(grade.Wash), goldenness * 0.35f);

            // Night is darker, but not so dark the art disappears; the rim carries it instead.
            grade.Exposure *= Mathf.Lerp(0.42f, 1f, daylight);

            // --- fog ---------------------------------------------------------------------
            var fog = Mathf.Clamp01(weather.Fog / 4f);
            if (fog > 0f)
            {
                var grey = Grey(grade.Key);
                grade.Key = Color.Lerp(grade.Key, grey, fog * 0.6f);
                grade.Rim = Color.Lerp(grade.Rim, grey, fog * 0.45f);
                grade.Wash = Color.Lerp(grade.Wash, Grey(grade.Wash), fog * 0.5f);
                grade.Exposure *= Mathf.Lerp(1f, 0.88f, fog);
            }

            // --- rain --------------------------------------------------------------------
            var rain = Mathf.Clamp01(weather.Rain / 4f);
            if (rain > 0f)
            {
                grade.Key = Color.Lerp(grade.Key, Cool(Desaturate(grade.Key, 0.35f)), rain * 0.65f);
                grade.Wash = Color.Lerp(grade.Wash, Cool(Desaturate(grade.Wash, 0.3f)), rain * 0.5f);
                grade.Exposure *= Mathf.Lerp(1f, 0.82f, rain);
            }

            // --- cloud -------------------------------------------------------------------
            var cloud = Mathf.Clamp01(weather.Cloudiness / 5f);
            if (cloud > 0f)
            {
                // Overcast has no direction: the key and the rim converge.
                var flat = Color.Lerp(grade.Key, grade.Rim, 0.5f);
                grade.Key = Color.Lerp(grade.Key, flat, cloud * 0.4f);
                grade.Rim = Color.Lerp(grade.Rim, flat, cloud * 0.3f);
                grade.Exposure *= Mathf.Lerp(1f, 0.9f, cloud);
            }

            grade.Exposure = Mathf.Clamp(grade.Exposure, 0.25f, 1.6f);
            return grade;
        }

        private static Color Warm(Color c) { return new Color(Mathf.Min(1f, c.r * 1.14f), c.g * 1.02f, c.b * 0.82f, 1f); }
        private static Color Cool(Color c) { return new Color(c.r * 0.88f, c.g * 0.96f, Mathf.Min(1f, c.b * 1.12f), 1f); }

        private static Color Grey(Color c)
        {
            var l = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
            return new Color(l, l, l, 1f);
        }

        private static Color Desaturate(Color c, float amount)
        {
            return Color.Lerp(c, Grey(c), Mathf.Clamp01(amount));
        }

        /// <summary>
        /// This raid's weather off the RaidSettings the screen was handed. Unknown rather than
        /// guessed when it cannot be read: the per-map grade on its own is a good answer, and a
        /// made-up midnight would not be.
        /// </summary>
        internal static Weather ReadWeather(object raidSettings)
        {
            var weather = default(Weather);

            if (raidSettings == null || !GameTypes.WeatherReady) return weather;

            try
            {
                var settings = GameTypes.RaidSettings_TimeAndWeather.GetValue(raidSettings);
                if (settings == null) return weather;

                // -1 is the game's "this raid has no time set", which is every PvE raid the
                // player has not given one. Taken literally it wraps to 23:00 and lights the
                // whole screen for midnight whatever the hour really is, so fall back to the
                // clock -- what 1.5.0 did -- and say in the log which of the two it was.
                var hour = Convert.ToInt32(GameTypes.Weather_HourOfDay.GetValue(settings));
                weather.HourFromClock = hour < 0 || hour > 23;
                weather.HourOfDay = weather.HourFromClock ? DateTime.Now.Hour : hour;
                weather.Rain = Convert.ToInt32(GameTypes.Weather_RainType.GetValue(settings));
                weather.Fog = Convert.ToInt32(GameTypes.Weather_FogType.GetValue(settings));

                if (GameTypes.Weather_Cloudiness != null)
                    weather.Cloudiness = Convert.ToInt32(GameTypes.Weather_Cloudiness.GetValue(settings));

                weather.Known = true;
                return weather;
            }
            catch
            {
                return default(Weather);
            }
        }

        /// <summary>A short description of the raid's conditions, for the log and the report.</summary>
        internal static string Describe(Weather weather)
        {
            if (!weather.Known) return "conditions unknown";

            var parts = weather.HourOfDay.ToString("00") + ":00"
                + (weather.HourFromClock ? " (from the clock; the raid has no time set)" : "");
            if (weather.Fog > 0) parts += ", fog " + weather.Fog;
            if (weather.Rain > 0) parts += ", rain " + weather.Rain;
            if (weather.Cloudiness > 0) parts += ", cloud " + weather.Cloudiness;
            return parts;
        }

        private static Grade G(int key, int rim, float exposure, int wash)
        {
            return new Grade
            {
                Key = Rgb(key),
                Rim = Rgb(rim),
                Exposure = exposure,
                Wash = Rgb(wash),
            };
        }

        private static Color Rgb(int packed)
        {
            return new Color(
                ((packed >> 16) & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f,
                (packed & 0xFF) / 255f,
                1f);
        }

        internal static Grade For(string locationId)
        {
            Grade grade;
            if (!string.IsNullOrEmpty(locationId) && Grades.TryGetValue(locationId, out grade)) return grade;
            return Neutral;
        }

        // ------------------------------------------------------------------ state

        private Lit[] _sceneLights;
        private GameObject _rig;
        private bool _warnedOnce;

        /// <summary>Whether the character is currently carrying our key and rim.</summary>
        internal bool CharacterLit { get { return _rig != null; } }

        internal string Description
        {
            get
            {
                return "scene-lights=" + (_sceneLights == null ? 0 : _sceneLights.Length)
                    + "; character-lights=" + (_rig != null);
            }
        }

        // ------------------------------------------------------------------ apply

        /// <summary>
        /// Tints the backdrop's own lights toward the destination, and lights the character to
        /// match. Both are captured first and put back by Restore.
        /// </summary>
        internal void Begin(Component environmentRoot, Grade grade)
        {
            try
            {
                if (DeployScreenPlugin.StagingGradeScene.Value) TintScene(environmentRoot, grade);
                if (DeployScreenPlugin.StagingLightCharacter.Value) LightCharacter(grade);
            }
            catch (Exception error)
            {
                WarnOnce(error);
                Restore();
            }
        }

        /// <summary>
        /// Pulls the scene lights toward the destination colour. Only the colour -- SceneDepth
        /// owns intensity, and the two must not write the same property.
        /// </summary>
        private void TintScene(Component environmentRoot, Grade grade)
        {
            if (environmentRoot == null || GameTypes.EnvRoot_MainScreenLights == null) return;

            var array = GameTypes.EnvRoot_MainScreenLights.GetValue(environmentRoot) as Array;
            if (array == null || array.Length == 0) return;

            var taken = new Lit[array.Length];
            var count = 0;
            var strength = Mathf.Clamp01(DeployScreenPlugin.StagingGradeStrength.Value);

            foreach (var entry in array)
            {
                var light = entry as Light;
                if (light == null) continue;

                taken[count++] = new Lit { Light = light, Colour = light.color };
                light.color = Color.Lerp(light.color, grade.Key, strength);
            }

            if (count == 0) return;

            _sceneLights = new Lit[count];
            Array.Copy(taken, _sceneLights, count);
        }

        /// <summary>
        /// A key and a rim that fall on the character and nothing else.
        ///
        /// cullingMask is the whole trick: the PMC is the only thing on the WeaponPreview layer
        /// during a deploy, so a light masked to it cannot touch the backdrop, the UI or anything
        /// in a raid. Directional lights are used because they have no position to get wrong --
        /// only a direction -- and the PMC's own transform is somewhere inside a canvas.
        ///
        /// Shadows are left off deliberately. The character already has its own contact shadow
        /// (MenuPlayerPoser.BottomShadow, which the game switches on at load), and real-time
        /// shadows on a preview layer are a cost and a risk for no visible gain.
        /// </summary>
        private void LightCharacter(Grade grade)
        {
            if (GameTypes.Layers_WeaponPreview == null) return;

            var layer = Convert.ToInt32(GameTypes.Layers_WeaponPreview.GetValue(null));
            if (layer < 0 || layer > 31) return;

            var mask = 1 << layer;

            _rig = new GameObject("DeployScreen Character Light");
            UnityEngine.Object.DontDestroyOnLoad(_rig);

            AddLight(_rig, "Key", grade.Key,
                DeployScreenPlugin.StagingKeyIntensity.Value,
                Quaternion.Euler(32f, -38f, 0f), mask);

            AddLight(_rig, "Rim", grade.Rim,
                DeployScreenPlugin.StagingRimIntensity.Value,
                Quaternion.Euler(8f, 158f, 0f), mask);
        }

        private static void AddLight(GameObject parent, string name, Color colour, float intensity,
            Quaternion rotation, int mask)
        {
            var holder = new GameObject(name);
            holder.transform.SetParent(parent.transform, false);
            holder.transform.rotation = rotation;

            var light = holder.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = colour;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            light.cullingMask = mask;
            light.renderMode = LightRenderMode.ForcePixel;
        }

        // ---------------------------------------------------------------- restore

        internal void Restore()
        {
            try
            {
                if (_sceneLights != null)
                {
                    foreach (var lit in _sceneLights)
                    {
                        if (lit.Light != null) lit.Light.color = lit.Colour;
                    }
                }
            }
            catch (Exception error) { WarnOnce(error); }

            _sceneLights = null;

            try
            {
                if (_rig != null) UnityEngine.Object.Destroy(_rig);
            }
            catch (Exception error) { WarnOnce(error); }

            _rig = null;
        }

        private void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning("[DeployScreen] map grade failed: " + error);
        }
    }
}
