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
                if (__args == null) return;

                // Without a way to read the live backdrop back, we decline to move it at all.
                // An override that cannot be undone is precisely the bug this feature caused.
                if (!GameTypes.EnvironmentRestoreReady) return;

                if (!DeployScreenPlugin.MatchEnvironment.Value)
                {
                    // Turned off mid-session, or never on: make sure nothing of ours is left on
                    // the backdrop before we stop looking at it.
                    EnvironmentState.Restore();
                    return;
                }

                // Note the player's backdrop before anything is asked of it. Idempotent.
                EnvironmentState.Capture();

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

        /// <summary>
        /// Points the backdrop at the destination map, or -- and this is the part that was missing
        /// -- puts the player's own backdrop back when the destination has no mapping.
        ///
        /// Leaving an unmapped map alone was what made this feel inconsistent: the backdrop simply
        /// stayed on whatever the *previous* map had asked for, so the same map showed a different
        /// scene depending on where you went last. Every path through here now ends with the
        /// backdrop in a defined state.
        /// </summary>
        private static void Request(string locationId)
        {
            Backdrop wanted;
            var mapped = Overrides.TryGetValue(locationId, out wanted)
                || Defaults.TryGetValue(locationId, out wanted);

            if (!mapped || wanted == Backdrop.Random)
            {
                // No opinion about this map. The player's choice is the answer, not the last map's.
                if (EnvironmentState.Overridden)
                {
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] " + locationId + " has no backdrop of its own -- "
                        + "restoring the player's");
                    EnvironmentState.Restore();
                }

                return;
            }

            // Available means both "this build ships the scene" and "the player has it unlocked".
            // Asking for one they do not own is how you get an empty backdrop.
            if (!EnvironmentState.IsAvailable((int)wanted))
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] backdrop " + wanted + " is not available to this player, "
                    + "leaving their own in place");
                EnvironmentState.Restore();
                return;
            }

            DeployScreenPlugin.Log.LogInfo("[DeployScreen] " + locationId + " -> " + wanted + " backdrop");

            // Everything that changes the backdrop goes through EnvironmentState, which captured
            // the player's value first and owns putting it back.
            EnvironmentState.Apply((int)wanted);
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
