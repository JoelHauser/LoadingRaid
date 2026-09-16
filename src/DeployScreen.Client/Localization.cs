using System;
using System.Collections.Generic;

namespace DeployScreen.Client
{
    /// <summary>
    /// Puts this mod's strings into the game's own locale table, and reads the game's names
    /// out of it.
    ///
    /// Publishing exists because of one line in MatchmakerBannersPanel.SelectBanner:
    ///
    ///     var localized = BannerDescription.Localized();
    ///     if (localized.IsNullOrEmpty() || localized.Equals(BannerDescription)) hide it;
    ///
    /// LocalizedValue returns the key unchanged when it does not resolve, so a raw string put
    /// straight into BannerDescription localizes to itself, compares equal, and the game
    /// **hides it**. The banner heading has no such check and would show raw text, but the
    /// description would silently never appear.
    ///
    /// So instead of raw text, the banner carries a key -- "deployscreen/intel/0/d" -- and
    /// that key is registered here. Then the lookup resolves, the result differs from the key,
    /// and the description is shown.
    ///
    /// LocalizationManager.UpdateLocale merges into the live Locale for the current culture
    /// (Locale is itself a Dictionary&lt;string, string&gt;). Every key is namespaced under
    /// "deployscreen/", so nothing of the game's can be shadowed.
    /// </summary>
    internal static class Localization
    {
        internal const string KeyPrefix = "deployscreen/";

        private static bool _warnedOnce;

        /// <summary>True if the last Publish actually reached the locale table.</summary>
        internal static bool Available { get; private set; }

        /// <summary>
        /// Registers every pair, replacing any registered earlier. Returns false when the
        /// locale table could not be reached, which is the caller's cue to fall back to
        /// headings only.
        /// </summary>
        internal static bool Publish(Dictionary<string, string> entries)
        {
            Available = false;

            if (entries == null || entries.Count == 0) return false;

            try
            {
                object manager;
                string culture;
                if (!Reach(out manager, out culture)) return false;

                // Locale's only constructor takes the dictionary to seed it from.
                var locale = Activator.CreateInstance(GameTypes.Locale, new object[] { entries });

                GameTypes.LocalizationManager_UpdateLocale.Invoke(manager, new[] { culture, locale });

                Available = true;
                return true;
            }
            catch (Exception error)
            {
                WarnOnce(error);
                return false;
            }
        }

        /// <summary>
        /// The game's text for a key, or null when it has none -- so callers can tell "unknown"
        /// from "known" and fall back.
        /// </summary>
        internal static string Lookup(string key)
        {
            string value;
            return TryTranslate(key, out value) && !string.IsNullOrEmpty(value) ? value : null;
        }

        /// <summary>
        /// Whether the game has text for a key, and what it is.
        ///
        /// This asks TryGetLocalization whether the key **exists**, rather than calling
        /// LocalizedValue and checking whether the answer differs from the key. Some keys
        /// translate to themselves -- the exit "Crossroads" is "Crossroads" -- and the
        /// differs-from-key test reads those as missing. That is the mistake that made the
        /// preview's first pass drop every Customs extract.
        /// </summary>
        internal static bool TryTranslate(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) return false;

            if (GameTypes.LocalizationManager_TryGetLocalization == null)
            {
                value = DiffersFromKey(key);
                return value != null;
            }

            try
            {
                object manager;
                string culture;
                if (!Reach(out manager, out culture)) return false;

                // TryGetLocalization(string id, string locale, out string localizedValue)
                var arguments = new object[] { key, culture, null };
                var found = (bool)GameTypes.LocalizationManager_TryGetLocalization.Invoke(manager, arguments);

                value = arguments[2] as string;
                return found;
            }
            catch (Exception error)
            {
                WarnOnce(error);
                return false;
            }
        }

        /// <summary>
        /// The fallback when TryGetLocalization cannot be found: LocalizedValue hands back the
        /// key when it does not know it. Wrong for keys that translate to themselves, which is
        /// why it is only the fallback.
        /// </summary>
        private static string DiffersFromKey(string key)
        {
            try
            {
                object manager;
                string culture;
                if (!Reach(out manager, out culture)) return null;

                var value = GameTypes.LocalizationManager_LocalizedValue
                    .Invoke(manager, new object[] { key }) as string;

                return string.IsNullOrEmpty(value) || value == key ? null : value;
            }
            catch (Exception error)
            {
                WarnOnce(error);
                return null;
            }
        }

        private static bool Reach(out object manager, out string culture)
        {
            manager = null;
            culture = null;

            if (GameTypes.LocalizationManager_Instance == null) return false;

            manager = GameTypes.LocalizationManager_Instance.GetValue(null, null);
            if (manager == null) return false;

            culture = GameTypes.LocalizationManager_Culture.GetValue(manager, null) as string;
            return !string.IsNullOrEmpty(culture);
        }

        private static void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning(
                "[DeployScreen] the locale table could not be reached, so intel will show "
                + "headings only: " + error.Message);
        }
    }
}
