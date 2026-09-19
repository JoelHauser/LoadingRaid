using System;
using System.Collections.Generic;
using System.Reflection;
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

        /// <summary>A light belonging to the game's character preview, as it was found.</summary>
        private struct Studio
        {
            internal Light Light;
            internal Color Colour;
            internal LightShadows Shadows;
        }

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

        /// <summary>
        /// This raid's time and weather, settled before deploy.
        ///
        /// The weather figures are normalised to 0..1 here rather than kept in the units they
        /// were read in, because there are two sources with different scales -- a custom raid's
        /// enums and the live node's floats -- and every reader downstream wants the same
        /// question answered: how much. Converting once, at the edge, is what keeps the grade
        /// from having to know which of the two it got.
        /// </summary>
        internal struct Weather
        {
            internal bool Known;

            /// <summary>The raid's hour as a fraction, so 18:09 grades as dusk and not as 18:00.</summary>
            internal float Hour;

            /// <summary>The same time, for the log and the report.</summary>
            internal int HourOfDay;
            internal int MinuteOfHour;

            internal bool HourFromClock;

            /// <summary>Where the hour came from, in words, for the log.</summary>
            internal string HourSource;

            /// <summary>Where the weather came from, in words, or null if none could be read.</summary>
            internal string WeatherSource;

            internal float Rain;   // 0 = dry,   1 = shower
            internal float Fog;    // 0 = clear, 1 = continuous
            internal float Cloud;  // 0 = clear, 1 = thundercloud
            internal float Wind;   // 0 = still, 1 = hurricane
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
            var hour = Mathf.Repeat(weather.Hour, 24f);
            var daylight = Mathf.Clamp01(Mathf.Min((hour - 4.5f) / 2.5f, (20.5f - hour) / 2.5f));

            // The low sun: strongest where daylight is climbing or falling, absent at noon and at
            // midnight. A raid at 06:00 or 19:00 should look like it.
            var lowSun = Mathf.Clamp01(1f - Mathf.Abs(daylight - 0.5f) * 2.4f) * daylight;

            // Which side of the day that low sun is on. Dawn and dusk are the same sun height and
            // they do not look remotely alike: morning is cold and pink because the air has been
            // still all night, evening is amber because the ground has been heating it all day.
            // Grading them the same makes an 18:09 Lighthouse deploy look like an 06:09 one, and
            // the whole reason for reading the raid's own hour was that those are different raids.
            var evening = hour >= 12f;
            var dawnness = evening ? 0f : lowSun;
            var duskness = evening ? lowSun : 0f;

            grade.Key = Color.Lerp(NightKey, grade.Key, daylight);
            grade.Rim = Color.Lerp(NightRim, grade.Rim, daylight * 0.75f + 0.25f);
            grade.Wash = Color.Lerp(NightKey, grade.Wash, daylight * 0.8f + 0.2f);

            grade.Key = Color.Lerp(grade.Key, Warm(grade.Key), duskness * 0.55f);
            grade.Wash = Color.Lerp(grade.Wash, Warm(grade.Wash), duskness * 0.35f);

            grade.Key = Color.Lerp(grade.Key, Rose(grade.Key), dawnness * 0.45f);
            grade.Wash = Color.Lerp(grade.Wash, Rose(grade.Wash), dawnness * 0.30f);

            // First light is colder and dimmer than last light at the same sun height.
            grade.Rim = Color.Lerp(grade.Rim, Cool(grade.Rim), dawnness * 0.35f);
            grade.Exposure *= Mathf.Lerp(1f, 0.92f, dawnness);

            // Night is darker, but not so dark the art disappears; the rim carries it instead.
            grade.Exposure *= Mathf.Lerp(0.42f, 1f, daylight);

            // --- fog ---------------------------------------------------------------------
            var fog = Mathf.Clamp01(weather.Fog);
            if (fog > 0f)
            {
                var grey = Grey(grade.Key);
                grade.Key = Color.Lerp(grade.Key, grey, fog * 0.6f);
                grade.Rim = Color.Lerp(grade.Rim, grey, fog * 0.45f);
                grade.Wash = Color.Lerp(grade.Wash, Grey(grade.Wash), fog * 0.5f);
                grade.Exposure *= Mathf.Lerp(1f, 0.88f, fog);
            }

            // --- rain --------------------------------------------------------------------
            var rain = Mathf.Clamp01(weather.Rain);
            if (rain > 0f)
            {
                grade.Key = Color.Lerp(grade.Key, Cool(Desaturate(grade.Key, 0.35f)), rain * 0.65f);
                grade.Wash = Color.Lerp(grade.Wash, Cool(Desaturate(grade.Wash, 0.3f)), rain * 0.5f);
                grade.Exposure *= Mathf.Lerp(1f, 0.82f, rain);
            }

            // --- cloud -------------------------------------------------------------------
            var cloud = Mathf.Clamp01(weather.Cloud);
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

        /// <summary>
        /// First light: red lifted, green pulled down, and the blue left alone. Warm cannot serve
        /// here -- it warms by taking blue away, and run at dawn that gives a sunset. What makes
        /// the morning the morning is that it keeps its cold.
        /// </summary>
        private static Color Rose(Color c) { return new Color(Mathf.Min(1f, c.r * 1.10f), c.g * 0.94f, Mathf.Min(1f, c.b * 1.04f), 1f); }

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
        /// This raid's time and weather, off the two things the deploy screen is handed: the
        /// RaidSettings and the session.
        ///
        /// Both halves have the same shape -- a custom raid states what it wants, and an ordinary
        /// raid states nothing and has to be read from the live game. So each is tried in that
        /// order, and which one answered is recorded, because a light that cannot be traced back
        /// to a reading is a light nobody can argue with.
        /// </summary>
        internal static Weather ReadWeather(object raidSettings, object session)
        {
            var weather = default(Weather);
            if (raidSettings == null) return weather;

            try
            {
                GameTypes.BindSession(session);

                object settings = null;
                if (GameTypes.WeatherReady)
                    settings = GameTypes.RaidSettings_TimeAndWeather.GetValue(raidSettings);

                // Whether the player stated this raid's conditions, decided once and used by
                // both halves. HourOfDay is the only field that carries a sentinel; see StatedHour.
                var stated = StatedHour(settings) >= 0;

                var hourKnown = ReadHour(raidSettings, session, settings, stated, ref weather);
                ReadConditions(session, settings, stated, ref weather);

                // Unknown rather than guessed. If neither the raid nor the session could be read
                // then the wall clock is all that is left, and the map's own grade is a better
                // answer than a time taken from the room the player is sitting in.
                weather.Known = hourKnown || weather.WeatherSource != null;
                return weather;
            }
            catch
            {
                return default(Weather);
            }
        }

        /// <summary>
        /// Factory is shown against a fixed pair rather than the running clock, so it is the one
        /// map where the hour is not the session's. 15:28 and 03:28 are the game's own constants,
        /// off LocationConditionsPanel.
        /// </summary>
        private static readonly DateTime FactoryDay = new DateTime(2016, 8, 4, 15, 28, 0);
        private static readonly DateTime FactoryNight = new DateTime(2016, 8, 4, 3, 28, 0);

        /// <summary>
        /// The hour this raid will be at.
        ///
        /// There is **one clock**, and it is the session's. Every map is shown against it, which
        /// is the thing the previous version of this got wrong: it read Location.UnixDateTime and
        /// gave each map its own hour, so Customs deployed at 14:45 and Streets at 11:50 on the
        /// same evening. The player is never offered those. What they are offered is two readings
        /// of the single clock the location screen prints -- as it stands, or twelve hours back --
        /// and the screen should be lit for whichever of the two they picked and nothing else.
        ///
        /// Order: a custom raid's stated hour, then Factory's fixed pair, then the session clock,
        /// then the wall clock because something has to be said.
        /// </summary>
        /// <summary>
        /// The hour the player stated for this raid, or -1 for "they stated nothing".
        ///
        /// **This is the only field in TimeAndWeatherSettings that can answer that question**, and
        /// finding that out cost a live run. The struct is not left at -1 across the board when a
        /// raid states nothing: something sets HourOfDay to -1 and leaves everything else at zero,
        /// and zero is a perfectly valid ERainType, EFogType and ECloudinessType -- NoRain, NoFog
        /// and Clear. So a test of "is RainType set" can never distinguish an unset struct from a
        /// clear day, which is exactly the mistake 1.9.0 shipped with: the enum path won every
        /// ordinary raid, reported dead clear, and the live weather was never once read.
        ///
        /// The hour is the tell, for the weather as much as for itself.
        /// </summary>
        private static int StatedHour(object settings)
        {
            try
            {
                if (settings == null || GameTypes.Weather_HourOfDay == null) return -1;

                var hour = Convert.ToInt32(GameTypes.Weather_HourOfDay.GetValue(settings));
                return hour >= 0 && hour <= 23 ? hour : -1;
            }
            catch { return -1; }
        }

        /// <returns>False when nothing but the wall clock answered.</returns>
        private static bool ReadHour(object raidSettings, object session, object settings, bool stated, ref Weather weather)
        {
            // A custom raid states its hour outright, and a stated hour beats an inferred one.
            if (stated)
            {
                Set(ref weather, StatedHour(settings), 0, "the raid's own setting");
                return true;
            }

            var night = Picked(raidSettings) == 1;

            if (IsFactory(raidSettings))
            {
                var fixedTime = night ? FactoryNight : FactoryDay;
                Set(ref weather, fixedTime.Hour, fixedTime.Minute,
                    night ? "Factory at night" : "Factory by day");
                return true;
            }

            if (GameTypes.Session_LocationTime != null && session != null)
            {
                var now = (DateTime)GameTypes.Session_LocationTime.GetValue(session, null);

                // PAST is the other half of the day. The game subtracts twelve hours rather than
                // adding them -- the same clock face either way, but this matches what the
                // conditions panel printed, and agreeing with the player's screen is the point.
                if (night) now = now.AddHours(-12);

                Set(ref weather, now.Hour, now.Minute,
                    night ? "the game clock, twelve hours back" : "the game clock");
                return true;
            }

            var wall = DateTime.Now;
            Set(ref weather, wall.Hour, wall.Minute, "the clock");
            weather.HourFromClock = true;
            return false;
        }

        private static void Set(ref Weather weather, int hour, int minute, string source)
        {
            weather.HourOfDay = hour;
            weather.MinuteOfHour = minute;
            weather.Hour = hour + minute / 60f;
            weather.HourSource = source;
        }

        /// <summary>The day/night toggle, as an int so a missing enum type costs nothing.</summary>
        private static int Picked(object raidSettings)
        {
            try
            {
                if (GameTypes.RaidSettings_SelectedDateTime == null) return 0;
                return Convert.ToInt32(GameTypes.RaidSettings_SelectedDateTime.GetValue(raidSettings));
            }
            catch { return 0; }
        }

        private static bool IsFactory(object raidSettings)
        {
            try
            {
                if (GameTypes.RaidSettings_SelectedLocation == null || GameTypes.Location_Id == null)
                    return false;

                var location = GameTypes.RaidSettings_SelectedLocation.GetValue(raidSettings, null);
                if (location == null) return false;

                var id = GameTypes.Location_Id.GetValue(location) as string;
                return id != null && id.StartsWith("factory4", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Rain, fog, cloud and wind, as fractions of their worst.
        ///
        /// A custom raid states them as enums. An ordinary raid does not state them at all --
        /// RainType, FogType and CloudinessType all read -1, which is why for six versions the
        /// exposure only ever moved with the hour. The live weather is somewhere else entirely:
        /// the server sends it at menu time and the session holds it as a WeatherNode, and it is
        /// the same node the location screen picked its weather icon from. So the player has
        /// already been shown this raid's weather; the deploy screen was simply looking in the
        /// wrong place for it.
        ///
        /// The node's floats are on the game's own scales, and the bounds here are read off
        /// WeatherNode.GetWeatherTypeByNode rather than guessed: Cloudness runs -1 clear to +1
        /// thundercloud, Rain crosses 1 at a drizzle and 3 at a downpour, and fog density sits at
        /// 0.004 when there is none and 0.1 when it is thick.
        /// </summary>
        private static void ReadConditions(object session, object settings, bool stated, ref Weather weather)
        {
            // A custom raid's stated weather. The gate is the *hour*, not RainType -- see
            // StatedHour for why testing the weather fields themselves cannot work.
            if (stated && settings != null && GameTypes.Weather_RainType != null && GameTypes.Weather_FogType != null)
            {
                var rain = Convert.ToInt32(GameTypes.Weather_RainType.GetValue(settings));

                if (rain >= 0)
                {
                    var fog = Convert.ToInt32(GameTypes.Weather_FogType.GetValue(settings));
                    var cloud = GameTypes.Weather_Cloudiness != null
                        ? Convert.ToInt32(GameTypes.Weather_Cloudiness.GetValue(settings))
                        : 0;
                    var wind = GameTypes.Weather_WindType != null
                        ? Convert.ToInt32(GameTypes.Weather_WindType.GetValue(settings))
                        : 0;

                    weather.Rain = Mathf.Clamp01(rain / 4f);        // NoRain .. Shower
                    weather.Fog = Mathf.Clamp01(Math.Max(fog, 0) / 4f);   // NoFog .. Continuous
                    weather.Cloud = Mathf.Clamp01(Math.Max(cloud, 0) / 5f); // Clear .. Thundercloud
                    weather.Wind = Mathf.Clamp01(Math.Max(wind, 0) / 4f);   // Light .. Hurricane
                    weather.WeatherSource = "the raid's own setting";
                    return;
                }
            }

            if (!GameTypes.WeatherNodeReady || GameTypes.Session_Weather == null || session == null) return;

            var node = GameTypes.Session_Weather.GetValue(session, null);

            // Null until the first /client/weather reply lands. It arrives during the menu
            // prepare, long before any deploy, but a raid loaded out of a fresh session could
            // still beat it, and no weather is a better answer than an invented one.
            if (node == null) return;

            weather.Cloud = Mathf.Clamp01(Mathf.InverseLerp(-1f, 1f, Single(GameTypes.Node_Cloudness, node)));
            weather.Rain = Mathf.Clamp01(Mathf.InverseLerp(0f, 4f, Single(GameTypes.Node_Rain, node)));
            weather.Fog = Mathf.Clamp01(Mathf.InverseLerp(0.004f, 0.1f, Single(GameTypes.Node_ScatteringFog, node)));

            if (GameTypes.Node_Wind != null)
                weather.Wind = Mathf.Clamp01(Mathf.InverseLerp(0f, 4f, Single(GameTypes.Node_Wind, node)));

            weather.WeatherSource = "the live weather";
        }

        private static float Single(FieldInfo field, object node)
        {
            if (field == null || node == null) return 0f;
            try { return Convert.ToSingle(field.GetValue(node)); }
            catch { return 0f; }
        }

        /// <summary>A short description of the raid's conditions, for the log and the report.</summary>
        internal static string Describe(Weather weather)
        {
            if (!weather.Known) return "conditions unknown";

            var parts = weather.HourOfDay.ToString("00") + ":" + weather.MinuteOfHour.ToString("00")
                + (string.IsNullOrEmpty(weather.HourSource) ? "" : " (from " + weather.HourSource + ")");

            if (string.IsNullOrEmpty(weather.WeatherSource)) return parts + ", weather unread";

            parts += ", " + weather.WeatherSource + ":";
            parts += " cloud " + Tenths(weather.Cloud);
            if (weather.Fog > 0f) parts += ", fog " + Tenths(weather.Fog);
            if (weather.Rain > 0f) parts += ", rain " + Tenths(weather.Rain);
            if (weather.Wind > 0f) parts += ", wind " + Tenths(weather.Wind);
            return parts;
        }

        private static string Tenths(float value) { return value.ToString("0.00"); }

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
        private Studio[] _studio;
        private GameObject _rig;
        private bool _warnedOnce;

        /// <summary>Whether the character is currently carrying our key and rim.</summary>
        internal bool CharacterLit { get { return _rig != null; } }

        /// <summary>
        /// Takes the character down with the picture while a cancel is being waited out.
        ///
        /// He is lit by a rig masked to his own layer, so this reaches him and nothing else. A
        /// character left at full key over a backdrop that has just gone dim is the one thing on
        /// screen insisting nothing has happened, which is the opposite of what the dim is for.
        ///
        /// Scaled from what the raid asked for rather than set to a number, so the weather grade
        /// is still underneath it and a night deploy dims from where it already was.
        /// </summary>
        internal void DimCharacter(float scale)
        {
            if (_rig == null) return;

            foreach (var light in _rig.GetComponentsInChildren<Light>(true))
            {
                if (light == null) continue;

                var full = light.name == "Rim"
                    ? DeployScreenPlugin.StagingRimIntensity.Value
                    : DeployScreenPlugin.StagingKeyIntensity.Value;

                light.intensity = full * _lift * Mathf.Clamp01(scale);
            }
        }

        /// <summary>What the exposure multiplied the configured intensities by, kept for the dim.</summary>
        private float _lift = 1f;

        internal string Description
        {
            get
            {
                return "scene-lights=" + (_sceneLights == null ? 0 : _sceneLights.Length)
                    + "; character-lights=" + (_rig != null)
                    + "; studio-lights=" + (_studio == null ? 0 : _studio.Length);
            }
        }

        /// <summary>
        /// Brings the game's own character rig into the same day as the picture behind it.
        ///
        /// The preview lights a PMC for a menu: four fixed studio lights -- key, fill, hair and a
        /// down light -- that are right for a dim backdrop and wrong for a photograph taken at a
        /// particular hour in particular weather. Left alone the character reads as a cut-out
        /// standing in front of a screen, which is exactly what it is.
        ///
        /// Two changes, both put back afterwards:
        ///
        ///   1. **Shadows off.** The rig casts the character's shadow onto the preview's own
        ///      catcher surface. Over a dark menu scene nobody sees it; over a photograph it is a
        ///      grey halo hanging behind the character with nothing to fall on.
        ///   2. **Pulled toward the destination.** Each light is lerped toward the map's key
        ///      colour by the grade strength, so a sunlit Customs warms the rig and a foggy Woods
        ///      cools it, and the light on the character agrees with the light in the picture.
        ///
        /// Intensities are left alone. They are balanced against each other for a face, and this
        /// is about colour agreement, not exposure.
        /// </summary>
        internal void TakeCharacterRig(Component screen, Grade grade, float strength)
        {
            if (screen == null || GameTypes.Loading_PlayerModel == null) return;

            try
            {
                var view = GameTypes.Loading_PlayerModel.GetValue(screen) as Component;
                if (view == null) return;

                var lights = view.GetComponentsInChildren<Light>(true);
                if (lights == null || lights.Length == 0) return;

                var taken = new Studio[lights.Length];
                var count = 0;

                foreach (var light in lights)
                {
                    if (light == null) continue;

                    taken[count++] = new Studio
                    {
                        Light = light,
                        Colour = light.color,
                        Shadows = light.shadows,
                    };

                    light.shadows = LightShadows.None;
                    light.color = Color.Lerp(light.color, grade.Key, Mathf.Clamp01(strength));
                }

                if (count == 0) return;

                _studio = new Studio[count];
                Array.Copy(taken, _studio, count);
            }
            catch (Exception error) { WarnOnce(error); }
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

            // Exposure, which until now was computed and never read by anything. Grade builds it
            // out of the hour, the fog, the rain and the cloud and clamps it to 0.25..1.6, and it
            // is the one number that says how *bright* the destination is rather than what colour
            // it is. The key and the rim were taking their colour from the raid and their
            // brightness from a fixed setting, so a midnight deploy in a downpour lit the
            // character exactly as hard as noon in clear weather -- right hue, wrong amount, and
            // the disagreement between a character and the place behind him is the whole reason
            // the composite reads as a cut-out.
            //
            // Through the same strength the scene grade uses, so the config numbers still mean
            // what they say at strength 0 and the two halves of the picture move together.
            var strength = Mathf.Clamp01(DeployScreenPlugin.StagingGradeStrength.Value);
            var lift = Mathf.Lerp(1f, grade.Exposure, strength);
            _lift = lift;

            _rig = new GameObject("DeployScreen Character Light");
            UnityEngine.Object.DontDestroyOnLoad(_rig);

            AddLight(_rig, "Key", grade.Key,
                DeployScreenPlugin.StagingKeyIntensity.Value * lift,
                Quaternion.Euler(32f, -38f, 0f), mask);

            AddLight(_rig, "Rim", grade.Rim,
                DeployScreenPlugin.StagingRimIntensity.Value * lift,
                Quaternion.Euler(8f, 158f, 0f), mask);

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] character light: exposure=" + grade.Exposure.ToString("0.00")
                + " lift=" + lift.ToString("0.00")
                + " key=" + (DeployScreenPlugin.StagingKeyIntensity.Value * lift).ToString("0.00")
                + " rim=" + (DeployScreenPlugin.StagingRimIntensity.Value * lift).ToString("0.00"));
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
                if (_studio != null)
                {
                    foreach (var one in _studio)
                    {
                        if (one.Light == null) continue;
                        one.Light.color = one.Colour;
                        one.Light.shadows = one.Shadows;
                    }
                }
            }
            catch (Exception error) { WarnOnce(error); }

            _studio = null;

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
