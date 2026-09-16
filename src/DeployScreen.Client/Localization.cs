using System;
using System.Collections.Generic;

namespace DeployScreen.Client
{
    /// <summary>
    /// Puts this mod's strings into the game's own locale table.
    ///
    /// This exists because of one line in MatchmakerBannersPanel.SelectBanner:
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
            if (GameTypes.LocalizationManager_Instance == null) return false;

            try
            {
                var manager = GameTypes.LocalizationManager_Instance.GetValue(null, null);
                if (manager == null) return false;

                var culture = GameTypes.LocalizationManager_Culture.GetValue(manager, null) as string;
                if (string.IsNullOrEmpty(culture)) return false;

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
        /// The game's own lookup, used for names it already knows -- boss roles under
        /// "QuestCondition/Elimination/Kill/BotRole/..." and quests under "&lt;id&gt; name".
        /// Returns null when the key does not resolve, rather than the key itself, so callers
        /// can tell the difference and fall back.
        /// </summary>
        internal static string Lookup(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (GameTypes.LocalizationManager_Instance == null) return null;

            try
            {
                var manager = GameTypes.LocalizationManager_Instance.GetValue(null, null);
                if (manager == null) return null;

                var value = GameTypes.LocalizationManager_LocalizedValue
                    .Invoke(manager, new object[] { key }) as string;

                // LocalizedValue hands back the key when it does not know it.
                if (string.IsNullOrEmpty(value) || value == key) return null;

                return value;
            }
            catch (Exception error)
            {
                WarnOnce(error);
                return null;
            }
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
