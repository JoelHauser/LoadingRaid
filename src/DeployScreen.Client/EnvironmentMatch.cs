using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace DeployScreen.Client
{
    /// <summary>The six backdrops the game ships. Values are EFT.EEnvironmentUIType.</summary>
    internal enum Backdrop
    {
        Random = 0,
        Factory = 1,
        Wood = 2,
        Laboratory = 3,
        TheUnheardEdition = 4,
        Cyber = 5,
    }

    /// <summary>
    /// Makes the menu backdrop match the map being deployed to.
    ///
    /// The deploy screen has no background of its own -- what is behind it is the menu
    /// environment, one of six baked Unity scenes that EnvironmentUI loads by name. So the
    /// same backdrop sits behind every raid no matter where you are going. Asking
    /// EnvironmentUI for the one that suits the destination is the whole feature.
    ///
    /// It is hooked on the offline raid screen rather than the deploy screen because that
    /// fires while the raid is still being configured, which leaves the scene load a few
    /// seconds of slack. The deploy screen is hooked too, for the paths that skip the
    /// offline screen, but by then it is a race against the raid itself -- which is why this
    /// is off by default.
    /// </summary>
    internal static class EnvironmentMatch
    {
        /// <summary>
        /// Where each map sends the backdrop. Only Factory, Wood and Laboratory are used:
        /// the other two are edition-themed and look like a mistake behind a raid.
        /// Override any of it in environments.txt.
        /// </summary>
        private static readonly Dictionary<string, Backdrop> Defaults =
            new Dictionary<string, Backdrop>(StringComparer.OrdinalIgnoreCase)
            {
                { "factory4_day",   Backdrop.Factory },
                { "factory4_night", Backdrop.Factory },
                { "bigmap",         Backdrop.Factory },   // Customs
                { "interchange",    Backdrop.Factory },
                { "rezervbase",     Backdrop.Factory },   // Reserve
                { "tarkovstreets",  Backdrop.Factory },   // Streets
                { "laboratory",     Backdrop.Laboratory },
                { "labyrinth",      Backdrop.Laboratory },
                { "woods",          Backdrop.Wood },
                { "shoreline",      Backdrop.Wood },
                { "lighthouse",     Backdrop.Wood },
                { "sandbox",        Backdrop.Wood },      // Ground Zero
                { "sandbox_high",   Backdrop.Wood },
                { "suburbs",        Backdrop.Wood },
                { "town",           Backdrop.Wood },
                { "terminal",       Backdrop.Wood },
            };

        private static readonly Dictionary<string, Backdrop> Overrides =
            new Dictionary<string, Backdrop>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What this mod last asked for, so a repeat is not a second scene load.</summary>
        private static Backdrop _lastRequested = Backdrop.Random;

        private static bool _warnedOnce;

        internal const string OverrideFile = "environments.txt";

        internal static void Install(Harmony harmony)
        {
            var after = new HarmonyMethod(AccessTools.Method(typeof(EnvironmentMatch), nameof(AfterShow)));

            if (GameTypes.OfflineRaidScreen_Show != null)
            {
                harmony.Patch(GameTypes.OfflineRaidScreen_Show, postfix: after);
            }

            if (GameTypes.TimeHasCome_Show != null)
            {
                harmony.Patch(GameTypes.TimeHasCome_Show, postfix: after);
            }
        }

        /// <summary>
        /// Both hooked overloads take a RaidSettings somewhere in their arguments; find it
        /// rather than binding to a position, since the two screens order them differently.
        /// </summary>
        private static void AfterShow(object[] __args)
        {
            try
            {
                if (LoadingPerformance.Mode != LoadingScreenMode.Enhanced) return;
                if (!DeployScreenPlugin.MatchEnvironment.Value) return;
                if (__args == null) return;

                object raidSettings = null;
                foreach (var argument in __args)
                {
                    if (argument != null && GameTypes.RaidSettings.IsInstanceOfType(argument))
                    {
                        raidSettings = argument;
                        break;
                    }
                }

                if (raidSettings == null) return;

                var location = GameTypes.RaidSettings_SelectedLocation.GetValue(raidSettings, null);
                if (location == null) return;

                var locationId = GameTypes.Location_Id.GetValue(location) as string;
                if (string.IsNullOrEmpty(locationId)) return;

                Request(locationId);
            }
            catch (Exception error)
            {
                WarnOnce(error);
            }
        }

        private static void Request(string locationId)
        {
            Backdrop wanted;
            if (!Overrides.TryGetValue(locationId, out wanted) &&
                !Defaults.TryGetValue(locationId, out wanted))
            {
                return;
            }

            if (wanted == Backdrop.Random) return;
            if (wanted == _lastRequested) return;
            if (!IsAvailable(wanted))
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] backdrop " + wanted + " is not in this install, leaving it alone");
                return;
            }

            var environmentUI = Instance();
            if (environmentUI == null) return;

            _lastRequested = wanted;

            DeployScreenPlugin.Log.LogInfo("[DeployScreen] " + locationId + " -> " + wanted + " backdrop");

            // Fire and forget: the returned Task finishes when the scene has swapped, and
            // nothing here needs to wait for it. Faulting it must not surface as an unhandled
            // task exception, so it is observed and swallowed.
            var task = GameTypes.EnvironmentUI_SetEnvironmentAsync.Invoke(
                environmentUI, new[] { Enum.ToObject(GameTypes.EEnvironmentUIType, (int)wanted) });

            Observe(task);
        }

        private static void Observe(object task)
        {
            var asTask = task as System.Threading.Tasks.Task;
            if (asTask == null) return;

            asTask.ContinueWith(finished =>
            {
                if (finished.Exception == null) return;

                WarnOnce(finished.Exception);
                _lastRequested = Backdrop.Random;
            });
        }

        private static object Instance()
        {
            if (GameTypes.EnvironmentUI_Instantiated != null)
            {
                var live = GameTypes.EnvironmentUI_Instantiated.GetValue(null, null) as bool?;
                if (live != true) return null;
            }

            return GameTypes.EnvironmentUI_Instance.GetValue(null, null);
        }

        /// <summary>
        /// Whether the install actually has this backdrop. EnvironmentData carries an
        /// EligibleVersions list, and the edition-themed scenes are not in every build --
        /// asking for one that is missing is how you get a black menu.
        /// </summary>
        private static bool IsAvailable(Backdrop backdrop)
        {
            if (GameTypes.EnvironmentUI_Environments == null || GameTypes.EnvironmentData_Type == null)
            {
                return true;
            }

            try
            {
                var environmentUI = Instance();
                if (environmentUI == null) return false;

                var environments = GameTypes.EnvironmentUI_Environments.GetValue(environmentUI) as IEnumerable;
                if (environments == null) return true;

                foreach (var environment in environments)
                {
                    if (environment == null) continue;

                    var type = GameTypes.EnvironmentData_Type.GetValue(environment);
                    if (type != null && Convert.ToInt32(type) == (int)backdrop) return true;
                }

                return false;
            }
            catch
            {
                // Unreadable is not the same as absent; let the game decide.
                return true;
            }
        }

        /// <summary>
        /// Reads environments.txt, one "locationId = Backdrop" per line, # for comments.
        /// A map named here overrides the built-in choice; anything unnamed keeps it.
        /// </summary>
        internal static void ReadOverrides(string folder)
        {
            Overrides.Clear();

            var path = Path.Combine(folder, OverrideFile);
            if (!File.Exists(path)) return;

            foreach (var line in File.ReadAllLines(path))
            {
                var text = line.Trim();
                if (text.Length == 0 || text[0] == '#') continue;

                var equals = text.IndexOf('=');
                if (equals <= 0) continue;

                var key = text.Substring(0, equals).Trim();
                var value = text.Substring(equals + 1).Trim();

                try
                {
                    Overrides[key] = (Backdrop)Enum.Parse(typeof(Backdrop), value, true);
                }
                catch
                {
                    DeployScreenPlugin.Log.LogWarning(
                        "[DeployScreen] " + OverrideFile + ": '" + value + "' is not a backdrop name");
                }
            }

            if (Overrides.Count > 0)
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] " + Overrides.Count + " backdrop override(s) from " + OverrideFile);
            }
        }

        private static void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning("[DeployScreen] backdrop matching failed: " + error);
        }
    }
}
