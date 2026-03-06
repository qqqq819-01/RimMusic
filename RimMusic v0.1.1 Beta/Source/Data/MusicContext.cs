using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Text.RegularExpressions;
using Verse;
using RimWorld;
using UnityEngine;
using RimMusic.Core;
using RimTalk.Data;
using RimTalk.Service;
using Verse.AI.Group;

namespace RimMusic.Data
{
    /// <summary>
    /// Intelligence Aggregator: Compiles live game telemetry into a structured context matrix.
    /// Internal strings are hardcoded in English to ensure consistent LLM parsing.
    /// </summary>
    public class MusicContext
    {
        public string culture_vibe = "";
        // Core Context Fields (LLM Input Targets)
        public string state = "Macro (Global)";
        public string season = "Unknown", weather = "Clear", danger = "Safe";
        public string time_speed = "Normal (1x)";
        public string colony_avg_mood = "Neutral";
        public string event_log = "None";
        public string radar_log = "None";

        // External Integration Buffers (RimTalk/God's Eye)
        public string rt_full_context = "No Data";
        public string music_nearby = "None";
        public string music_location = "Unknown";
        public string music_wealth = "Unknown";

        // Narrative Focus Parameters
        public string focus_name = "The Colony";
        public string activity = "Observing";
        public string mood_level = "Unknown", thoughts = "None";
        public string rt_persona = "None", rt_relations = "None";
        public string rt_dialogue = "No recent dialogue.", rt_backstory = "Unknown";

        // Cultural Acoustic Parameters
        public string culture_instruments = "Standard Orchestral";

        // Static Telemetry Cache
        private static int _lastScanTick = -9999;
        private static string _cachedEventLog = "None";
        private static string _cachedAtmosphere = "";

        /// <summary>
        /// Flushes static telemetry buffers to prevent cross-session memory leakage.
        /// </summary>
        public static void ResetStaticCache()
        {
            _lastScanTick = -9999;
            _cachedEventLog = "None";
            _cachedAtmosphere = "";
        }

        /// <summary>
        /// Constructs a new context matrix based on current map state and focal entities.
        /// </summary>
        public static MusicContext Build(Pawn forcedFocus = null, bool isRealGeneration = false)
        {
            MusicContext ctx = new MusicContext();

            // Time Dilation Analysis
            if (Find.TickManager.Paused) ctx.time_speed = "Paused (Thinking/Reading)";
            else if (Find.TickManager.CurTimeSpeed == TimeSpeed.Normal) ctx.time_speed = "Normal (1x)";
            else if (Find.TickManager.CurTimeSpeed == TimeSpeed.Fast) ctx.time_speed = "Fast (2x)";
            else if (Find.TickManager.CurTimeSpeed == TimeSpeed.Superfast) ctx.time_speed = "Superfast (3x)";
            else if (Find.TickManager.CurTimeSpeed == TimeSpeed.Ultrafast) ctx.time_speed = "Ultrafast (4x)";
            else ctx.time_speed = "Unknown";

            if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
            {
                Map map = Find.CurrentMap;
                ctx.season = GenLocalDate.Season(map).LabelCap();

                PerformHybridScan(map);
                ctx.event_log = _cachedEventLog;

                string baseWeather = map.weatherManager.curWeather.LabelCap;
                if (!string.IsNullOrEmpty(_cachedAtmosphere)) ctx.weather = $"{baseWeather}, {_cachedAtmosphere}";
                else ctx.weather = baseWeather;

                // Tactical Threat Assessment
                StoryDanger d = map.dangerWatcher.DangerRating;
                ctx.danger = d == StoryDanger.None ? "Safe" : (d == StoryDanger.High ? "High Danger" : "Danger");

                if (map.mapPawns.FreeColonistsCount > 0)
                    ctx.colony_avg_mood = map.mapPawns.FreeColonists.Average(x => x.needs?.mood?.CurLevel ?? 0.5f).ToString("P0");

                ctx.music_wealth = map.wealthWatcher.WealthTotal > 100000 ? "Rich" : (map.wealthWatcher.WealthTotal < 20000 ? "Impecunious" : "Moderate");
            }

            Pawn narrativePawn = forcedFocus;
            bool isRaid = ctx.danger != "Safe";
            bool isMacro = MusicGameComponent.IsMacroMode;

            // Narrative State Logic
            if (narrativePawn != null)
            {
                bool isEnemy = narrativePawn.Faction != null && narrativePawn.Faction.HostileTo(Faction.OfPlayer);
                if (narrativePawn.IsPrisoner) ctx.state = "Micro (Prisoner Target)";
                else if (isEnemy) ctx.state = "Micro (Enemy Target)";
                else ctx.state = isRaid ? "Combat (Individual)" : "Micro (Selected)";
            }
            else
            {
                if (isMacro)
                {
                    ctx.state = isRaid ? "Combat (Global Battlefield)" : "Macro (Global View)";
                    ctx.focus_name = isRaid ? "The Colony (Under Attack)" : "The Colony";
                }
                else
                {
                    ctx.state = isRaid ? "Combat (Global Battlefield)" : "Micro (Observing)";
                    ctx.focus_name = isRaid ? "The Colony (Under Attack)" : "The Colony";
                }
            }

            if (narrativePawn != null)
            {
                ctx.focus_name = narrativePawn.LabelShort;
                ctx.activity = GetActivity(narrativePawn);
                ctx.mood_level = narrativePawn.needs?.mood?.CurLevel.ToString("P0") ?? "Unknown";
                ctx.thoughts = GetTopThoughts(narrativePawn);

                bool isEnemy = narrativePawn.Faction != null && narrativePawn.Faction.HostileTo(Faction.OfPlayer);
                if (isRaid && !isEnemy)
                {
                    ctx.activity += " [Fighting]";
                    ctx.thoughts = "[THREAT: Combat], " + ctx.thoughts;
                }

                FillRimTalkData(ctx, narrativePawn);
                ctx.rt_full_context = BuildFullContext(narrativePawn, ctx);
            }
            else
            {
                bool isMacroState = ctx.state.Contains("Macro") || ctx.state.Contains("Global");
                if (isRaid)
                {
                    ctx.radar_log = "[Combat Mode] Radar overridden by global threat.";
                    ctx.rt_full_context = $"[COMBAT MODE]\nScope: Global Defense\nFriendly Activity: {GetFriendlyActivity()}";
                    ctx.rt_persona = "None"; ctx.rt_dialogue = "COMBAT SHOUTS";
                }
                else if (isMacroState)
                {
                    ctx.radar_log = "[Macro View] Radar offline.";
                    ctx.rt_full_context = "[SILENCE]\nGlobal view active. No focus.";
                    ctx.rt_persona = "None"; ctx.rt_dialogue = "Silence."; ctx.rt_relations = "None"; ctx.thoughts = "None";
                }
                else
                {
                    string radarReport = GodsEyeRadar.GenerateRadarReport(null, Find.CurrentMap);
                    if (!string.IsNullOrEmpty(radarReport))
                    {
                        ctx.radar_log = radarReport;
                    }
                    else
                    {
                        ctx.radar_log = "[Radar Inactive] Focal point lost or out of bounds.";
                    }
                    ctx.rt_full_context = "[OBSERVING]\nScanning colony sectors. No specific focus assigned.";
                }
            }

            // Acoustic Configuration
            if (Find.CurrentMap != null && Current.Game != null)
            {
                if (isRealGeneration && RimMusicMod.Settings.EnableCulturalFusion)
                {
                    ctx.culture_instruments = CultureCalculator.CalculateCulturalInstruments(Find.CurrentMap, isRaid, narrativePawn);
                }
                else
                {
                    ctx.culture_instruments = CultureCalculator.GetStaticBaseInstruments(narrativePawn);
                }

                var musicComp = Current.Game.GetComponent<CulturalMusicComponent>();
                if (musicComp != null)
                {
                    Pawn vibePawn = narrativePawn;

                    if (vibePawn == null)
                    {
                        if (isRaid)
                            vibePawn = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.HostileTo(Faction.OfPlayer) && !p.Downed);
                        else
                            vibePawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                    }

                    CulturalRoster primaryRoster = musicComp.GetOrGenerateRosterFor(vibePawn);
                    if (primaryRoster != null && !string.IsNullOrWhiteSpace(primaryRoster.cultureVibe))
                    {
                        ctx.culture_vibe = primaryRoster.cultureVibe;
                    }
                    else
                    {
                        ctx.culture_vibe = "Standard cinematic game score, adaptive and immersive.";
                    }
                }
            }

            return ctx;
        }

        private static void PerformHybridScan(Map map)
        {
            int currentTick = Find.TickManager.TicksGame;
            if (currentTick - _lastScanTick < 60) return;

            _lastScanTick = currentTick;

            var eventSb = new StringBuilder();
            var atmoSb = new StringBuilder();
            int count = 1;

            var currentLetterLabels = Find.LetterStack.LettersListForReading.Select(l => l.Label).ToHashSet();

            foreach (var evt in MusicGameComponent.EventMemory.AsEnumerable().Reverse().Take(5))
            {
                bool isActive = currentLetterLabels.Contains(evt.Label);
                string prefix = isActive ? "[Letter]" : "[Recent]";

                string cleanBody = CleanRichText(evt.Text);
                if (cleanBody.Length > 150) cleanBody = cleanBody.Substring(0, 150) + "...";

                eventSb.AppendLine($"{count}) {prefix} {evt.Label}");
                if (!string.IsNullOrEmpty(cleanBody)) eventSb.AppendLine($"   {cleanBody}");
                count++;
            }

            // Tactical Lord Analysis
            if (map != null && map.lordManager != null)
            {
                Dictionary<string, int> threatCounts = new Dictionary<string, int>();
                Dictionary<string, string> threatTactics = new Dictionary<string, string>();

                foreach (var lord in map.lordManager.lords)
                {
                    bool hasVisibleThreats = lord.ownedPawns.Any(p =>
                        !p.Dead && !p.Downed && p.Spawned && !p.IsPrisoner && !p.Position.Fogged(p.Map)
                    );

                    if (!hasVisibleThreats) continue;

                    string threatName = null;
                    bool isHostile = lord.faction != null && lord.faction.HostileTo(Faction.OfPlayer);
                    string tactic = GetDeepTacticalState(lord);

                    string jobName = lord.LordJob?.GetType().Name ?? "";
                    if (jobName.Contains("Mechanoid")) threatName = "Mechanoid Hive";
                    else if (jobName.Contains("Assault")) threatName = "Hostile Force";
                    else if (jobName.Contains("Siege")) threatName = "Siege Party";
                    else if (jobName.Contains("Manhunter")) threatName = "Manhunter Pack";
                    else if (jobName.Contains("Infestation")) threatName = "Insect Infestation";
                    else if (isHostile) threatName = "Hostile Group";

                    if (threatName != null)
                    {
                        string key = threatName;
                        if (lord.faction != null) key += $" ({lord.faction.Name})";

                        if (threatCounts.ContainsKey(key)) threatCounts[key]++; else threatCounts[key] = 1;
                        if (!threatTactics.ContainsKey(key)) threatTactics[key] = tactic;
                    }
                }

                foreach (var kvp in threatCounts)
                {
                    string suffix = kvp.Value > 1 ? $" (x{kvp.Value} Groups)" : " (x1 Group)";
                    string tactic = threatTactics.ContainsKey(kvp.Key) ? threatTactics[kvp.Key] : "Unknown";

                    eventSb.AppendLine($"{count}) [Active Threat] {kvp.Key}{suffix}");
                    eventSb.AppendLine($"   Tactics: {tactic}");
                    count++;
                }
            }

            // Game Condition Analysis
            if (map != null && map.gameConditionManager != null)
            {
                foreach (var cond in map.gameConditionManager.ActiveConditions)
                {
                    bool isHidden = false;
                    try
                    {
                        PropertyInfo hProp = typeof(GameCondition).GetProperty("Hidden", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (hProp != null) isHidden = (bool)hProp.GetValue(cond);
                    }
                    catch { }

                    if (!isHidden && !string.IsNullOrEmpty(cond.LabelCap))
                    {
                        string name = cond.def.defName.ToLower();
                        bool isAtmosphere = name.Contains("wind") || name.Contains("fog") || name.Contains("bloom");

                        if (isAtmosphere)
                        {
                            if (atmoSb.Length > 0) atmoSb.Append(", ");
                            atmoSb.Append(cond.LabelCap);
                        }
                        else
                        {
                            eventSb.AppendLine($"{count}) [Condition] {cond.LabelCap}");
                            count++;
                        }
                    }
                }
            }

            _cachedEventLog = eventSb.Length > 0 ? eventSb.ToString().TrimEnd() : "None";
            _cachedAtmosphere = atmoSb.ToString();
        }

        private static string CleanRichText(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            string text = Regex.Replace(input, @"\(\*.*?\)", "");
            text = Regex.Replace(text, @"\(/.*?\)", "");
            text = Regex.Replace(text, "<.*?>", "");
            text = Regex.Replace(text, @"\n{2,}", "\n");
            return text.Trim();
        }

        private static string GetDeepTacticalState(Lord lord)
        {
            if (lord.CurLordToil != null)
            {
                string toilName = lord.CurLordToil.GetType().Name;
                if (toilName.Contains("Stage")) return "Staging";
                if (toilName.Contains("AssaultColony") || toilName.Contains("Hunt")) return "Assaulting";
                if (toilName.Contains("Siege")) return "Sieging";
                if (toilName.Contains("ExitMap") || toilName.Contains("Panic")) return "Fleeing";
                if (toilName.Contains("Kidnap")) return "Kidnapping";
                if (toilName.Contains("Defend")) return "Defending";
                if (toilName.Contains("Sleep") || toilName.Contains("Dormant")) return "Dormant";
            }

            if (lord.LordJob != null)
            {
                string jobName = lord.LordJob.GetType().Name;
                if (jobName.Contains("Sapper")) return "Sapper";
                if (jobName.Contains("Manhunter")) return "Manhunter";
            }

            return "Moving";
        }

        private static string GetFriendlyActivity()
        {
            if (Find.CurrentMap == null) return "Unknown";
            int drafted = Find.CurrentMap.mapPawns.FreeColonists.Count(p => p.Drafted);
            if (drafted > 0) return $"{drafted} Colonists Drafted";
            return "Colonists Idle";
        }

        private static string BuildFullContext(Pawn p, MusicContext ctx)
        {
            List<string> modules = new List<string>();
            modules.Add($"[P1]\n{p.Name.ToStringFull} ({p.gender.GetLabel()}, {p.ageTracker.AgeBiologicalYears})");

            var idSb = new StringBuilder();

            string roleLabel = "Colonist";
            if (p.IsSlave) roleLabel = "Slave";
            else if (p.IsPrisoner) roleLabel = "Prisoner";
            else if (p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer)) roleLabel = "Enemy";
            else if (!p.IsColonist) roleLabel = "Neutral/Guest";
            idSb.AppendLine($"Role: {roleLabel}");

            if (p.Faction != null) idSb.AppendLine($"Faction: {p.Faction.Name}");

            if (ModsConfig.BiotechActive && p.genes?.Xenotype != null) idSb.AppendLine($"Race: {p.genes.XenotypeLabel}");
            else idSb.AppendLine($"Race: {p.def.label}");

            if (ModsConfig.IdeologyActive && p.ideo?.Ideo != null)
            {
                idSb.Append($"Ideology: {p.ideo.Ideo.name}");
                var memes = p.ideo.Ideo.memes?.Where(m => m != null).Select(m => m.LabelCap.Resolve()).Where(l => !string.IsNullOrEmpty(l));
                if (memes != null && memes.Any()) idSb.Append($"\nMemes: {string.Join(", ", memes)}");
                idSb.AppendLine();
            }

            if (p.story != null)
            {
                if (p.story.Childhood != null) idSb.AppendLine($"Childhood: {p.story.Childhood.title}");
                if (p.story.Adulthood != null) idSb.AppendLine($"Adulthood: {p.story.Adulthood.title}");
                var traits = p.story.traits?.allTraits.Select(t => t.LabelCap);
                if (traits != null && traits.Any()) idSb.AppendLine($"Traits: {string.Join(", ", traits)}");
            }
            if (p.skills != null)
            {
                var skills = p.skills.skills.Select(s => $"{s.def.label}: {s.Level}");
                idSb.AppendLine($"Skills: {string.Join(", ", skills)}");
            }
            idSb.AppendLine($"Personality: {ctx.rt_persona}");
            idSb.AppendLine($"Mood: {p.needs?.mood?.CurLevel.ToString("P0") ?? "N/A"} | Memory: {ctx.thoughts}");

            List<string> equip = new List<string>();
            if (p.equipment?.Primary != null) equip.Add(p.equipment.Primary.LabelCap);
            if (p.apparel != null) equip.AddRange(p.apparel.WornApparel.Select(a => a.LabelCap));
            if (equip.Any()) idSb.AppendLine($"Equipment: {string.Join(", ", equip)}");

            modules.Add(idSb.ToString().TrimEnd());

            if (p.Map != null)
            {
                string radarReport = GodsEyeRadar.GenerateRadarReport(p, p.Map);
                if (!string.IsNullOrEmpty(radarReport))
                {
                    ctx.radar_log = radarReport;
                }
                else
                {
                    ctx.radar_log = "[Radar Inactive] Context out of resolution.";
                }
            }

            modules.Add($"[Dialogue Flow]\n{ctx.rt_dialogue}");
            return string.Join("\n\n", modules);
        }

        private static void FillRimTalkData(MusicContext ctx, Pawn p)
        {
            try
            {
                ctx.rt_persona = PersonaService.GetPersonality(p);
                ctx.rt_dialogue = GetRealDialogue(p);
                ctx.rt_relations = ContextBuilder.GetRelationsContext(p, PromptService.InfoLevel.Short) ?? "None";
                ctx.rt_backstory = ContextBuilder.GetBackstoryContext(p, PromptService.InfoLevel.Short) ?? "Unknown";
            }
            catch (Exception)
            {
                ctx.rt_persona = "Data Unavailable (Incompatible Entity)";
            }
        }

        private static string GetRealDialogue(Pawn p)
        {
            int limit = RimMusicMod.Settings.DialogueLineLimit;
            if (limit <= 0) return "Dialogue telemetry disabled.";

            var history = TalkHistory.GetMessageHistory(p, true);
            if (history == null || history.Count == 0) return "No recent log.";

            StringBuilder sb = new StringBuilder();
            int count = 0;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var msg = history[i];
                string content = msg.message;
                if (msg.role == Role.User)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(content, @"^(.*?said to.*?: '.*?')");
                    if (match.Success) content = match.Result("$1");
                    else if (content.Length > 60) content = content.Substring(0, 60) + "...";
                }
                if (content.StartsWith(p.LabelShort + ":")) content = content.Substring(p.LabelShort.Length + 1).Trim();
                string speaker = msg.role == Role.User ? "Context" : p.LabelShort;
                sb.AppendLine($"[{speaker}]: {content}");
                count++;
                if (count >= limit) break;
            }
            return sb.ToString().Trim();
        }

        private static string GetActivity(Pawn p) => p.CurJob != null ? p.CurJob.def.reportString.Replace("TargetA", p.CurJob.targetA.Thing?.LabelShort ?? "Target") : "Idle";
        private static string GetTopThoughts(Pawn p) { if (p.needs?.mood?.thoughts?.memories == null) return "None"; var mems = p.needs.mood.thoughts.memories.Memories.OrderByDescending(m => Mathf.Abs(m.MoodOffset())).Take(3).Select(m => m.LabelCap.ToString()); return string.Join(", ", mems); }
    }
}