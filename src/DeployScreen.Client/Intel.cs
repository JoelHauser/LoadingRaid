using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

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
    /// </summary>
    internal static class Intel
    {
        /// <summary>The key the game files boss role names under.</summary>
        private const string BotRoleKey = "QuestCondition/Elimination/Kill/BotRole/";

        /// <summary>EQuestStatus.Started.</summary>
        private const int QuestStarted = 2;

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
            var name = Str(GameTypes.Location_Name, location);
            var escape = Int(GameTypes.Location_EscapeTimeLimit, location);
            var average = Int(GameTypes.Location_AveragePlayTime, location);
            var level = Int(GameTypes.Location_AveragePlayerLevel, location);

            var parts = new List<string>();
            if (escape > 0) parts.Add(escape + " min raid");
            if (average > 0) parts.Add("~" + average + " min typical");
            if (level > 0) parts.Add("avg level " + level);

            if (parts.Count == 0) return;

            cards.Add(new IntelCard(
                string.IsNullOrEmpty(name) ? "BRIEFING" : name.ToUpperInvariant(),
                string.Join("  ·  ", parts.ToArray())));
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
                if (string.IsNullOrEmpty(role)) continue;

                // pmcBEAR and pmcUSEC are the PMC waves, not bosses, and sit at 50% on every
                // map -- listing them as bosses would be wrong and identical everywhere.
                if (role.StartsWith("pmc", StringComparison.OrdinalIgnoreCase)) continue;

                var chance = Float(GameTypes.BossSpawn_BossChance, spawn);
                if (chance <= 0f) continue;

                if (!best.ContainsKey(role))
                {
                    best[role] = chance;
                    order.Add(role);
                }
                else if (chance > best[role])
                {
                    best[role] = chance;
                }
            }

            if (order.Count == 0) return;

            var text = new StringBuilder();
            for (var i = 0; i < order.Count && i < 4; i++)
            {
                if (i > 0) text.Append("  ·  ");
                text.Append(BossName(order[i])).Append(' ').Append((int)Math.Round(best[order[i]])).Append('%');
            }

            cards.Add(new IntelCard("BOSSES", text.ToString()));
        }

        /// <summary>
        /// The game already localizes role names -- bossBully is "Reshala", bossKojaniy is
        /// "Shturman". Not every role has a key, so an unknown one is tidied up instead.
        /// </summary>
        private static string BossName(string role)
        {
            var localized = Localization.Lookup(BotRoleKey + role);
            if (!string.IsNullOrEmpty(localized)) return localized;

            var trimmed = role;
            foreach (var prefix in new[] { "boss", "sectant", "arenaFighter", "exUsec" })
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > prefix.Length)
                {
                    trimmed = trimmed.Substring(prefix.Length);
                    break;
                }
            }

            if (trimmed.Length == 0) return role;

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
            var guaranteed = new List<string>();

            foreach (var exit in exits)
            {
                if (exit == null) continue;

                var name = Str(GameTypes.Exit_Name, exit);
                if (string.IsNullOrEmpty(name)) continue;

                total++;

                if (Float(GameTypes.Exit_Chance, exit) >= 100f && guaranteed.Count < 3)
                {
                    guaranteed.Add(name);
                }
            }

            if (total == 0) return;

            var body = total + (total == 1 ? " extract" : " extracts");
            if (guaranteed.Count > 0)
            {
                body += "  ·  always open: " + string.Join(", ", guaranteed.ToArray());
            }

            cards.Add(new IntelCard("EXTRACTS", body));
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
            var body = string.Join("  ·  ", shown.ToArray());
            if (quests.Count > shown.Count) body += "  ·  +" + (quests.Count - shown.Count) + " more";

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
