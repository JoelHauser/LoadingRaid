using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace DeployScreen.Client
{
    /// <summary>One banner's worth of intel: a heading and a line under it.</summary>
    internal sealed class IntelCard
    {
        internal string Header;
        internal string Body;

        internal IntelCard(string header, string body)
        {
            Header = header;
            Body = body;
        }
    }

    /// <summary>
    /// Builds what the deploy screen tells you about where you are going.
    ///
    /// Everything except the quest list comes off the Location object the panel is already
    /// handed -- BossLocationSpawn, exits, EscapeTimeLimit, AveragePlayTime are all public
    /// fields on it. Nothing here reads the server's files: on a remote server they would not
    /// be there, and the client already has the same data.
    ///
    /// Quests come from the profile hanging off the session, matched on
    /// QuestTemplate.LocationId == Location._Id -- the same MongoID the database keys quests
    /// by, with "any" for the ones not tied to a map.
    ///
    /// Almost every name the raw data holds is internal: "ReserveBase", "EXFIL_Train", "E1",
    /// "exUsec". What a player should see is looked up in the game's own text. 1.1.0 showed the
    /// raw values and it read badly on ten of twelve maps.
    /// </summary>
    internal static class Intel
    {
        private const string Separator = "  ·  ";

        /// <summary>Where the game files boss role names.</summary>
        private const string BotRoleKey = "QuestCondition/Elimination/Kill/BotRole/";

        /// <summary>Where the few roles missing from BotRole live -- "ScavRole/ExUsec" is "Rogue".</summary>
        private const string ScavRoleKey = "ScavRole/";

        /// <summary>EQuestStatus.Started.</summary>
        private const int QuestStarted = 2;

        /// <summary>ERequirementState.None -- also the value an exit gets when its data omits the field.</summary>
        private const int NoRequirement = 0;

        private const int MaxBosses = 4;
        private const int MaxOpenExtracts = 3;

        /// <summary>
        /// Roles the game has no text for under either key. They are proper nouns, so the same
        /// spelling is right in every language.
        /// </summary>
        private static readonly Dictionary<string, string> KnownBosses =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "bossBoar", "Kaban" },
                { "bossKolontay", "Kollontay" },
                { "bossKnight", "Knight" },
                { "bossPartisan", "Partisan" },
                { "bossZryachiy", "Zryachiy" },
                { "peacemaker", "Peacemaker" },
            };

        private static bool _warnedOnce;

        internal static List<IntelCard> Build(object location, object session)
        {
            var cards = new List<IntelCard>();
            if (location == null) return cards;

            try
            {
                AddBriefing(cards, location);
                AddBosses(cards, location);
                AddExtracts(cards, location);

                if (DeployScreenPlugin.IntelQuests.Value)
                {
                    AddQuests(cards, location, session);
                }
            }
            catch (Exception error)
            {
                WarnOnce(error);
            }

            return cards;
        }

        // ---------------------------------------------------------------- briefing

        private static void AddBriefing(List<IntelCard> cards, object location)
        {
            var escape = Int(GameTypes.Location_EscapeTimeLimit, location);
            var average = Int(GameTypes.Location_AveragePlayTime, location);
            var level = Int(GameTypes.Location_AveragePlayerLevel, location);

            var parts = new List<string>();
            if (escape > 0) parts.Add(escape + " min raid");
            if (average > 0) parts.Add("~" + average + " min typical");
            if (level > 0) parts.Add("avg level " + level);

            if (parts.Count == 0) return;

            var name = MapName(location);

            cards.Add(new IntelCard(
                string.IsNullOrEmpty(name) ? "BRIEFING" : name.ToUpperInvariant(),
                string.Join(Separator, parts.ToArray())));
        }

        /// <summary>
        /// The name the game shows, which is "&lt;_Id&gt; Name" in its text -- exactly what
        /// Location.LocalizedName looks up. The raw Name field is internal: "ReserveBase" for
        /// Reserve, "Sandbox" for Ground Zero, "Laboratory" for The Lab.
        /// </summary>
        private static string MapName(object location)
        {
            var mongoId = Str(GameTypes.Location_MongoId, location);
            var name = string.IsNullOrEmpty(mongoId) ? null : Localization.Lookup(mongoId + " Name");

            return string.IsNullOrEmpty(name) ? Str(GameTypes.Location_Name, location) : name;
        }

        // ------------------------------------------------------------------ bosses

        private static void AddBosses(List<IntelCard> cards, object location)
        {
            var spawns = GameTypes.Location_BossLocationSpawn == null
                ? null
                : GameTypes.Location_BossLocationSpawn.GetValue(location) as IEnumerable;

            if (spawns == null) return;

            // Keyed by role: a boss is listed once per zone it can use, and the chance is the
            // same on each, so the highest is the one worth showing.
            var best = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            foreach (var spawn in spawns)
            {
                if (spawn == null) continue;

                var role = Str(GameTypes.BossSpawn_BossName, spawn);
                if (string.IsNullOrEmpty(role) || !IsBoss(role)) continue;

                var chance = Float(GameTypes.BossSpawn_BossChance, spawn);
                if (chance <= 0f) continue;

                float known;
                if (!best.TryGetValue(role, out known))
                {
                    best[role] = chance;
                    order.Add(role);
                }
                else if (chance > known)
                {
                    best[role] = chance;
                }
            }

            if (order.Count == 0) return;

            // Most likely first. OrderByDescending is stable, so equal chances keep the order
            // the map lists them in. Capping before sorting is what dropped Shoreline's 40%
            // Cultist Priest in 1.1.0 while a 15% spawn was shown.
            var shown = order
                .OrderByDescending(role => best[role])
                .Take(MaxBosses)
                .Select(role => BossName(role) + " " + (int)Math.Round(best[role]) + "%")
                .ToArray();

            cards.Add(new IntelCard("BOSSES", string.Join(Separator, shown)));
        }

        /// <summary>
        /// BossLocationSpawn lists more than bosses. Left out: the PMC waves and Raiders
        /// (pmcBEAR, pmcUSEC, pmcBot), escorts (follower*), a boss's sniper guards listed at
        /// 100% (bossBoarSniper), and event-only spawns at 5% (arenaFighterEvent,
        /// crazyAssaultEvent).
        /// </summary>
        private static bool IsBoss(string role)
        {
            if (role.StartsWith("pmc", StringComparison.OrdinalIgnoreCase)) return false;
            if (role.StartsWith("follower", StringComparison.OrdinalIgnoreCase)) return false;
            if (role.IndexOf("sniper", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (role.EndsWith("Event", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }

        private static string BossName(string role)
        {
            var name = Localization.Lookup(BotRoleKey + role);
            if (!string.IsNullOrEmpty(name)) return name;

            name = Localization.Lookup(ScavRoleKey + char.ToUpperInvariant(role[0]) + role.Substring(1));
            if (!string.IsNullOrEmpty(name)) return name;

            string known;
            if (KnownBosses.TryGetValue(role, out known)) return known;

            return Tidy(role);
        }

        /// <summary>Last resort for a role nobody has named: drop the prefix, capitalise.</summary>
        private static string Tidy(string role)
        {
            var trimmed = role;
            foreach (var prefix in new[] { "boss", "sectant", "arenaFighter", "exUsec" })
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > prefix.Length)
                {
                    trimmed = trimmed.Substring(prefix.Length);
                    break;
                }
            }

            return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
        }

        // ---------------------------------------------------------------- extracts

        private static void AddExtracts(List<IntelCard> cards, object location)
        {
            var exits = GameTypes.Location_Exits == null
                ? null
                : GameTypes.Location_Exits.GetValue(location) as IEnumerable;

            if (exits == null) return;

            var total = 0;
            var open = new List<string>();

            foreach (var exit in exits)
            {
                if (exit == null) continue;

                var id = Str(GameTypes.Exit_Name, exit);
                if (string.IsNullOrEmpty(id)) continue;

                total++;

                if (open.Count >= MaxOpenExtracts || !IsAlwaysOpen(exit, id)) continue;

                var name = ExtractName(id);
                if (name != null) open.Add(name);
            }

            if (total == 0) return;

            var body = total + (total == 1 ? " extract" : " extracts") + Separator
                       + (open.Count > 0 ? "always open: " + string.Join(", ", open.ToArray()) : "none always open");

            cards.Add(new IntelCard("EXTRACTS", body));
        }

        /// <summary>
        /// A 100% chance and nothing asked of you. 1.1.0 checked only the chance, which listed
        /// Reserve's armored train, its co-op exit and the climbing route as always open.
        ///
        /// Flare exits are the exception PassageRequirement does not catch: they say None but
        /// only open when you fire a flare. Their ids all contain "sniper" -- customs_sniper_exit,
        /// E9_sniper, wood_sniper_exit, Sniper_exit.
        /// </summary>
        private static bool IsAlwaysOpen(object exit, string id)
        {
            if (Float(GameTypes.Exit_Chance, exit) < 100f) return false;

            if (GameTypes.Exit_PassageRequirement != null
                && Int(GameTypes.Exit_PassageRequirement, exit) != NoRequirement)
            {
                return false;
            }

            return id.IndexOf("sniper", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>
        /// An exit's id is its key in the game's text: "EXFIL_Train" is "Armored Train", "E1" is
        /// "Stylobate Building Elevator". Some ids translate to themselves -- "Crossroads" -- which
        /// is why this asks whether a translation exists rather than whether it differs. An id
        /// with no translation is shown only when it already reads as a name ("Factory Gate"),
        /// never when it is plainly internal ("tunnel_shared").
        /// </summary>
        private static string ExtractName(string id)
        {
            string name;
            if (Localization.TryTranslate(id, out name) && !string.IsNullOrEmpty(name)) return name;

            return id.IndexOf('_') < 0 ? id : null;
        }

        // ------------------------------------------------------------------ quests

        private static void AddQuests(List<IntelCard> cards, object location, object session)
        {
            var mongoId = Str(GameTypes.Location_MongoId, location);
            if (string.IsNullOrEmpty(mongoId) || session == null) return;

            var quests = ActiveQuestsOn(mongoId, session);
            if (quests == null) return;

            if (quests.Count == 0)
            {
                cards.Add(new IntelCard("YOUR TASKS", "Nothing active here"));
                return;
            }

            var shown = quests.Count > 3 ? quests.GetRange(0, 3) : quests;
            var body = string.Join(Separator, shown.ToArray());
            if (quests.Count > shown.Count) body += Separator + "+" + (quests.Count - shown.Count) + " more";

            cards.Add(new IntelCard("YOUR TASKS  (" + quests.Count + ")", body));
        }

        /// <summary>
        /// Started quests whose template is pinned to this map. Returns null when the profile
        /// could not be read at all, which is different from "none here".
        /// </summary>
        private static List<string> ActiveQuestsOn(string mongoId, object session)
        {
            try
            {
                var profileProperty = session.GetType().GetProperty("Profile");
                if (profileProperty == null) return null;

                var profile = profileProperty.GetValue(session, null);
                if (profile == null) return null;

                if (GameTypes.Profile_QuestsData == null) return null;

                var questsData = GameTypes.Profile_QuestsData.GetValue(profile) as IEnumerable;
                if (questsData == null) return null;

                var names = new List<string>();

                foreach (var quest in questsData)
                {
                    if (quest == null) continue;

                    if (Int(GameTypes.QuestData_Status, quest) != QuestStarted) continue;

                    var template = GameTypes.QuestData_Template == null
                        ? null
                        : GameTypes.QuestData_Template.GetValue(quest);

                    if (template == null) continue;

                    var questLocation = Str(GameTypes.QuestTemplate_LocationId, template);
                    if (!string.Equals(questLocation, mongoId, StringComparison.OrdinalIgnoreCase)) continue;

                    names.Add(QuestName(template));
                }

                return names;
            }
            catch (Exception error)
            {
                WarnOnce(error);
                return null;
            }
        }

        /// <summary>Quest names are filed under "&lt;templateId&gt; name".</summary>
        private static string QuestName(object template)
        {
            var templateId = Str(GameTypes.QuestTemplate_TemplateId, template);
            if (string.IsNullOrEmpty(templateId)) templateId = Str(GameTypes.QuestTemplate_Id, template);
            if (string.IsNullOrEmpty(templateId)) return "Unknown task";

            var name = Localization.Lookup(templateId + " name");

            // Better a short id than the raw 24-character one filling the line.
            return string.IsNullOrEmpty(name) ? templateId.Substring(0, Math.Min(8, templateId.Length)) : name;
        }

        // ------------------------------------------------------------------ typing

        private static string Str(System.Reflection.FieldInfo field, object target)
        {
            if (field == null || target == null) return null;
            try { return field.GetValue(target) as string; } catch { return null; }
        }

        private static int Int(System.Reflection.FieldInfo field, object target)
        {
            if (field == null || target == null) return 0;
            try { return Convert.ToInt32(field.GetValue(target)); } catch { return 0; }
        }

        private static float Float(System.Reflection.FieldInfo field, object target)
        {
            if (field == null || target == null) return 0f;
            try { return Convert.ToSingle(field.GetValue(target)); } catch { return 0f; }
        }

        private static void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning("[DeployScreen] intel could not be built: " + error);
        }
    }
}
