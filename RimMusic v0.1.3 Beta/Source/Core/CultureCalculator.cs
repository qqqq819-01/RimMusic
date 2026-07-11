using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using RimMusic.Data;
using Verse.AI.Group;

namespace RimMusic.Core
{
    /// <summary>
    /// Core calculation engine for cultural and instrumental parameters.
    /// Employs an advanced "Holographic Combat Power Balance" to weigh gear, bionics, and base defenses.
    /// </summary>
    public static class CultureCalculator
    {
        public static string GetHashForSourceId(string sourceId)
        {
            string origin = "UnknownOrigin";
            string memes = "NoMemes";
            string tech = "Industrial";

            if (sourceId.StartsWith("Ideo_") && ModsConfig.IdeologyActive)
            {
                int ideoId = int.Parse(sourceId.Substring(5));
                Ideo ideo = Find.IdeoManager.IdeosListForReading.FirstOrDefault(i => i.id == ideoId);
                if (ideo != null)
                {
                    origin = ideo.culture?.defName ?? "UnknownOrigin";
                    memes = ideo.memes != null ? string.Join("_", ideo.memes.Select(m => m.defName).OrderBy(m => m)) : "NoMemes";
                    var fac = Find.FactionManager.AllFactions.FirstOrDefault(f => f.ideos?.PrimaryIdeo == ideo);
                    if (fac != null) tech = fac.def.techLevel.ToString();
                }
            }
            else if (sourceId.StartsWith("Faction_"))
            {
                string defName = sourceId.Substring(8);
                var fac = Find.FactionManager.AllFactions.FirstOrDefault(f => f.def.defName == defName);
                if (fac != null)
                {
                    tech = fac.def.techLevel.ToString();
                    Ideo ideo = ModsConfig.IdeologyActive ? fac.ideos?.PrimaryIdeo : null;
                    if (ideo != null)
                    {
                        origin = ideo.culture?.defName ?? "UnknownOrigin";
                        memes = ideo.memes != null ? string.Join("_", ideo.memes.Select(m => m.defName).OrderBy(m => m)) : "NoMemes";
                    }
                    if (!fac.def.humanlikeFaction) return $"Hash_Entity_{fac.def.defName}";
                }
                return $"Hash_Entity_{defName}";
            }
            else if (sourceId.StartsWith("Race_") || sourceId.StartsWith("Xeno_") || sourceId.StartsWith("OC_") || sourceId.StartsWith("Pawn_"))
            {
                return $"Hash_Entity_{sourceId}";
            }

            return $"Hash_{origin}_{memes}_{tech}";
        }

        public static List<string> GetFlattenedInstruments(CulturalRoster roster)
        {
            List<string> finalInsts = new List<string>();
            if (roster == null || roster.activeInstruments == null) return finalInsts;

            foreach (string inst in roster.activeInstruments)
            {
                if (!string.IsNullOrWhiteSpace(inst) && inst != "Fallback Instrument" && inst != "Unknown Instrument")
                {
                    finalInsts.Add(inst);
                }
            }

            return finalInsts;
        }

        public static string GetStaticBaseInstruments(Pawn p)
        {
            if (p == null) return "Standard Orchestral (No Focus)";
            var musicComp = Current.Game.GetComponent<CulturalMusicComponent>();
            if (musicComp == null) return "Standard Orchestral";

            var roster = musicComp.GetOrGenerateRosterFor(p);
            if (roster == null || roster.activeInstruments.Count == 0) return "Standard Orchestral";

            List<string> maskedInsts = GetFlattenedInstruments(roster);
            if (maskedInsts.Count == 0) return "Silence (All Muted)";

            return string.Join(", ", maskedInsts);
        }

        private static bool IsRhythmOrBass(string inst)
        {
            if (string.IsNullOrEmpty(inst)) return false;
            string lower = inst.ToLowerInvariant();
            return lower.Contains("drum") || lower.Contains("bass") || lower.Contains("percussion") ||
                   lower.Contains("beat") || lower.Contains("taiko") || lower.Contains("stomp") ||
                   lower.Contains("timpani") || lower.Contains("sub") || lower.Contains("rhythm");
        }

        // =====================================================================
        // [NEW] Three-Tier Active Threat Intelligence Filter
        // Bypasses exhaustive job checks by reading pure AI combat intent.
        // =====================================================================
        public static bool IsActiveThreat(Pawn p)
        {
            if (p == null || p.Dead || p.Downed || p.IsPrisoner || !p.Spawned) return false;
            if (!p.HostileTo(Faction.OfPlayer)) return false;

            // Base Filter: Undiscovered (Ancient Danger) or Sleeping (Dormant Hives/Mechs)
            if (p.Position.Fogged(p.Map) || !p.Awake()) return false;

            // Base Filter: Fleeing / Panicking - Threat is neutralized
            if (p.CurJob != null && (p.CurJob.def.defName.Contains("Flee") || p.CurJob.def.defName.Contains("Panic"))) return false;

            // TIER 1: Personal Aggro & Mental State (Overrides everything, catches charging Fleshbeasts)
            bool isAggroed = p.mindState != null && p.mindState.enemyTarget != null;
            bool isManhunter = p.InMentalState && (p.MentalStateDef == MentalStateDefOf.Manhunter || p.MentalStateDef == MentalStateDefOf.ManhunterPermanent);

            if (isAggroed || isManhunter) return true;

            // TIER 2: Command Chain (Lord Analysis for organized raids)
            var lord = p.GetLord();
            if (lord != null)
            {
                string toilName = lord.CurLordToil?.GetType().Name ?? "";
                string jobName = lord.LordJob?.GetType().Name ?? "";

                // Defending a specific point peacefully (e.g., Hive, Crashed Ship) is NOT an active base threat
                if (toilName.Contains("DefendPoint") || jobName.Contains("DefendPoint")) return false;

                // If they are just waiting/staging and NOT part of an active assault
                if (toilName == "LordToil_Wait" && !jobName.Contains("Assault")) return false;

                // If the raid is defeated and they are leaving the map
                if (toilName.Contains("ExitMap") || jobName.Contains("ExitMap")) return false;

                // Any other lord directive (Assault, Siege, Sap, Kidnap) implies an active raid.
                return true;
            }

            // TIER 3: Ambient Stragglers (The Ultimate False-Flag Catcher)
            // No Lord + No Aggro + Not Manhunter = Just existing on the map (e.g., Sorne insects, random hostile goats).
            return false;
        }

        // =====================================================================
        // Unified Holographic Radar Methods
        // =====================================================================

        public static float GetColonyDefensePower(Map map)
        {
            if (map == null) return 0f;
            float power = 0f;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                power += p.GetStatValue(StatDefOf.MarketValue) / 50f;
            }
            float buildingWealth = map.wealthWatcher.WealthBuildings;
            power += Mathf.Sqrt(buildingWealth) * 2.5f;
            return power;
        }

        public static float GetThreatRatio(Map map)
        {
            if (map == null) return 0f;
            float totalThreat = 0f;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (IsActiveThreat(p))
                {
                    totalThreat += p.kindDef.combatPower > 0 ? p.kindDef.combatPower : 50f;
                }
            }
            if (totalThreat <= 0f) return 0f;
            float defense = GetColonyDefensePower(map);
            return totalThreat / (totalThreat + defense + 0.001f);
        }

        // =====================================================================
        // Wartime / Peacetime binary classifier for SongDef tagging
        // 规则：threatRatio < ThreatStealThreshold → 和平时(peace)
        //       threatRatio >= ThreatStealThreshold → 战时(war)
        // 注：注入 SongDef 时一律 tense=false，让玩家在 Music Manager 自改；
        //     此处仅用于生成产物的英文文件名标签。
        // =====================================================================
        public static bool IsWartime(Map map)
        {
            float ratio = GetThreatRatio(map);
            float threshold = RimMusicMod.Settings?.ThreatStealThreshold ?? 0.30f;
            return ratio >= threshold;
        }

        public static string ThreatTagFor(Map map)
        {
            return IsWartime(map) ? "War" : "Peace";
        }

        // =====================================================================
        // Telemetry Extractors for the UI HUD
        // =====================================================================

        public static string GetCultureDemographics(Map map)
        {
            if (map == null) return "Unknown";
            Dictionary<string, int> counts = new Dictionary<string, int>();
            int total = 0;

            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                string name = "Unknown";
                if (ModsConfig.IdeologyActive && p.ideo?.Ideo != null) name = p.ideo.Ideo.name;
                else if (p.Faction != null) name = p.Faction.Name;
                else if (p.kindDef != null) name = p.kindDef.label;

                if (!counts.ContainsKey(name)) counts[name] = 0;
                counts[name]++;
                total++;
            }

            if (total == 0) return "No Data";

            var sorted = counts.OrderByDescending(kvp => kvp.Value).ToList();
            List<string> parts = new List<string>();
            foreach (var kvp in sorted)
            {
                float pct = (kvp.Value / (float)total) * 100f;
                if (pct >= 5f) parts.Add($"{kvp.Key} ({pct:F0}%)");
            }
            return string.Join(" | ", parts);
        }

        public static void GetThreatTelemetry(Map map, out float threatRatio, out string topThreatDesc)
        {
            threatRatio = 0f;
            topThreatDesc = "None";
            if (map == null) return;

            float colonyPower = GetColonyDefensePower(map);
            float totalThreat = 0f;
            Dictionary<string, float> threatFactions = new Dictionary<string, float>();

            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (IsActiveThreat(p))
                {
                    float pPower = p.kindDef.combatPower > 0 ? p.kindDef.combatPower : 50f;
                    string facName = p.Faction != null ? p.Faction.Name : "Unknown Entity";

                    if (!threatFactions.ContainsKey(facName)) threatFactions[facName] = 0f;
                    threatFactions[facName] += pPower;
                    totalThreat += pPower;
                }
            }

            if (totalThreat > 0f)
            {
                threatRatio = totalThreat / (totalThreat + colonyPower + 0.001f);
                var topThreat = threatFactions.OrderByDescending(kvp => kvp.Value).FirstOrDefault();
                if (topThreat.Key != null)
                {
                    float pct = (topThreat.Value / totalThreat) * 100f;
                    topThreatDesc = $"{topThreat.Key} ({pct:F1}%)";
                }
            }
        }

        // =====================================================================
        // Cultural & Instrument Generation
        // =====================================================================

        public static string CalculateCulturalInstruments(Map map, bool isRaid, Pawn focusPawn = null)
        {
            string dummyVibe;
            return CalculateCulturalInstruments(map, isRaid, focusPawn, out dummyVibe);
        }

        public static string CalculateCulturalInstruments(Map map, bool isRaid, Pawn focusPawn, out string dominantVibe)
        {
            dominantVibe = "Standard cinematic game score, adaptive and immersive.";

            var musicComp = Current.Game.GetComponent<CulturalMusicComponent>();
            if (musicComp == null) return "Orchestral Strings, Brass, Percussion";

            Pawn actualFocus = focusPawn ?? (Find.Selector.SingleSelectedThing as Pawn);

            if (actualFocus != null && !isRaid)
            {
                var focusRoster = musicComp.GetOrGenerateRosterFor(actualFocus);
                if (focusRoster != null)
                {
                    dominantVibe = focusRoster.cultureVibe;
                    List<string> focusInsts = GetFlattenedInstruments(focusRoster);
                    if (focusInsts.Count > 0) return string.Join(", ", focusInsts);
                }
            }

            int totalSlots = RimMusicMod.Settings?.TargetInstrumentCount ?? 5;
            float minorityThreshold = RimMusicMod.Settings?.FusionMinorityThreshold ?? 0.15f;
            float threatStealThreshold = RimMusicMod.Settings?.ThreatStealThreshold ?? 0.3f;
            float threatDominateThreshold = RimMusicMod.Settings?.ThreatDominateThreshold ?? 0.75f;
            bool allowRaceFusion = RimMusicMod.Settings?.AllowRaceFusion ?? false;

            Dictionary<CulturalRoster, int> popCounts = new Dictionary<CulturalRoster, int>();
            int validColonists = 0;

            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                var roster = musicComp.GetOrGenerateRosterFor(p, forceCulture: !allowRaceFusion);
                if (roster != null)
                {
                    if (!popCounts.ContainsKey(roster)) popCounts[roster] = 0;
                    popCounts[roster]++;
                    validColonists++;
                }
            }

            Dictionary<CulturalRoster, int> allocatedSlots = new Dictionary<CulturalRoster, int>();

            if (validColonists > 0)
            {
                var dominantCultures = popCounts.Where(kvp => (float)kvp.Value / validColonists >= minorityThreshold)
                                                .OrderByDescending(kvp => kvp.Value)
                                                .ThenBy(kvp => Rand.Value)
                                                .ToList();

                if (dominantCultures.Count > 0)
                {
                    dominantVibe = dominantCultures.First().Key.cultureVibe;
                }

                int dominantTotal = dominantCultures.Sum(x => x.Value);
                int slotsGiven = 0;
                foreach (var kvp in dominantCultures)
                {
                    int slots = Mathf.RoundToInt(((float)kvp.Value / dominantTotal) * totalSlots);
                    allocatedSlots[kvp.Key] = slots;
                    slotsGiven += slots;
                }

                if (slotsGiven < totalSlots && dominantCultures.Count > 0)
                    allocatedSlots[dominantCultures.First().Key] += (totalSlots - slotsGiven);
                if (slotsGiven > totalSlots && dominantCultures.Count > 0)
                    allocatedSlots[dominantCultures.First().Key] -= (slotsGiven - totalSlots);
            }

            Dictionary<CulturalRoster, float> hostilePowerCounts = new Dictionary<CulturalRoster, float>();
            if (isRaid)
            {
                float totalThreatPower = 0f;
                float totalColonyDefensePower = GetColonyDefensePower(map);

                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (IsActiveThreat(p))
                    {
                        var roster = musicComp.GetOrGenerateRosterFor(p, forceCulture: !allowRaceFusion);
                        if (roster != null)
                        {
                            float pPower = p.kindDef.combatPower > 0 ? p.kindDef.combatPower : 50f;
                            if (!hostilePowerCounts.ContainsKey(roster)) hostilePowerCounts[roster] = 0f;
                            hostilePowerCounts[roster] += pPower;
                            totalThreatPower += pPower;
                        }
                    }
                }

                if (totalThreatPower > 0)
                {
                    float threatRatio = totalThreatPower / (totalThreatPower + totalColonyDefensePower + 0.001f);

                    var sortedThreats = hostilePowerCounts.OrderByDescending(kvp => kvp.Value)
                                                          .ThenBy(kvp => Rand.Value)
                                                          .ToList();
                    CulturalRoster primaryThreatRoster = sortedThreats.First().Key;

                    if (threatRatio >= threatDominateThreshold)
                    {
                        dominantVibe = primaryThreatRoster.cultureVibe;

                        allocatedSlots.Clear();
                        int threatSlotsGiven = 0;
                        foreach (var kvp in sortedThreats)
                        {
                            int slots = Mathf.RoundToInt((kvp.Value / totalThreatPower) * totalSlots);
                            allocatedSlots[kvp.Key] = slots;
                            threatSlotsGiven += slots;
                        }

                        if (threatSlotsGiven < totalSlots) allocatedSlots[primaryThreatRoster] += (totalSlots - threatSlotsGiven);
                        if (threatSlotsGiven > totalSlots) allocatedSlots[primaryThreatRoster] -= (threatSlotsGiven - totalSlots);
                    }
                    else if (threatRatio >= threatStealThreshold)
                    {
                        int stolen = Mathf.Max(1, Mathf.RoundToInt(totalSlots * 0.4f));
                        if (!allocatedSlots.ContainsKey(primaryThreatRoster)) allocatedSlots[primaryThreatRoster] = 0;
                        allocatedSlots[primaryThreatRoster] += stolen;

                        var topDomestic = allocatedSlots.Where(x => !hostilePowerCounts.ContainsKey(x.Key)).OrderByDescending(x => x.Value).FirstOrDefault();
                        if (topDomestic.Key != null && allocatedSlots[topDomestic.Key] >= stolen)
                        {
                            allocatedSlots[topDomestic.Key] -= stolen;
                        }
                    }
                }
            }

            List<string> finalInstruments = new List<string>();

            foreach (var kvp in allocatedSlots)
            {
                CulturalRoster roster = kvp.Key;
                int seats = kvp.Value;
                if (seats <= 0) continue;

                bool isThreat = isRaid && hostilePowerCounts.ContainsKey(roster);

                List<string> maskedInsts = GetFlattenedInstruments(roster);
                if (maskedInsts.Count == 0) continue;

                if (isThreat)
                {
                    maskedInsts = maskedInsts.OrderByDescending(inst => IsRhythmOrBass(inst) ? 1 : 0)
                                             .ThenBy(x => Rand.Value).ToList();
                }
                else
                {
                    maskedInsts = maskedInsts.OrderByDescending(inst => IsRhythmOrBass(inst) ? 0 : 1)
                                             .ThenBy(x => Rand.Value).ToList();
                }

                int added = 0;
                foreach (string inst in maskedInsts)
                {
                    if (added >= seats) break;
                    if (!finalInstruments.Contains(inst))
                    {
                        finalInstruments.Add(inst);
                        added++;
                    }
                }
            }

            if (finalInstruments.Count > totalSlots)
            {
                finalInstruments = finalInstruments.Take(totalSlots).ToList();
            }

            if (finalInstruments.Count == 0) return "Acoustic Guitar, Frame Drum";
            return string.Join(", ", finalInstruments);
        }
    }
}