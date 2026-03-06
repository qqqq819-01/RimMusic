using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using RimMusic.Data;

namespace RimMusic.Core
{
    /// <summary>
    /// Core calculation engine for cultural and instrumental parameters.
    /// Centralizes faction hash generation, instrument flattening, and multicultural fusion algorithms.
    /// Eliminates redundancy across UI, core components, and context processing.
    /// </summary>
    public static class CultureCalculator
    {
        /// <summary>
        /// Global unified hash generator for faction and cultural identification.
        /// </summary>
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

        /// <summary>
        /// Flattens the instrument roster. 
        /// [V0.1.1 Beta] Direct list processing. Override profiles are deprecated as UI now mutates base data.
        /// </summary>
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

        /// <summary>
        /// Retrieves the base instrument configuration for a specific pawn.
        /// </summary>
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

        /// <summary>
        /// Global multicultural fusion algorithm. 
        /// Allocates instrument slots based on population demographics and active threat ratios.
        /// </summary>
        public static string CalculateCulturalInstruments(Map map, bool isRaid, Pawn focusPawn = null)
        {
            var musicComp = Current.Game.GetComponent<CulturalMusicComponent>();
            if (musicComp == null) return "Orchestral Strings, Brass, Percussion";

            Pawn actualFocus = focusPawn ?? (Find.Selector.SingleSelectedThing as Pawn);

            if (actualFocus != null && !isRaid)
            {
                var focusRoster = musicComp.GetOrGenerateRosterFor(actualFocus);
                if (focusRoster != null)
                {
                    List<string> focusInsts = GetFlattenedInstruments(focusRoster);
                    if (focusInsts.Count > 0) return string.Join(", ", focusInsts);
                }
            }

            int totalSlots = RimMusicMod.Settings?.TargetInstrumentCount ?? 5;
            float minorityThreshold = RimMusicMod.Settings?.FusionMinorityThreshold ?? 0.15f;
            float threatStealThreshold = RimMusicMod.Settings?.ThreatStealThreshold ?? 0.3f;
            float threatDominateThreshold = RimMusicMod.Settings?.ThreatDominateThreshold ?? 0.6f;
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
                var dominantCultures = popCounts.Where(kvp => (float)kvp.Value / validColonists >= minorityThreshold).ToList();
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

            if (isRaid)
            {
                int hostileCount = map.mapPawns.AllPawnsSpawned.Count(p => p.HostileTo(Faction.OfPlayer) && !p.Downed && !p.Dead);
                float threatRatio = (float)hostileCount / (validColonists + hostileCount + 0.001f);

                CulturalRoster threatRoster = null;
                Pawn leadThreat = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.HostileTo(Faction.OfPlayer) && !p.Downed);

                if (leadThreat != null)
                {
                    threatRoster = musicComp.GetOrGenerateRosterFor(leadThreat, forceCulture: !allowRaceFusion);
                }

                if (threatRoster != null)
                {
                    if (threatRatio >= threatDominateThreshold)
                    {
                        allocatedSlots.Clear();
                        allocatedSlots[threatRoster] = totalSlots;
                    }
                    else if (threatRatio >= threatStealThreshold)
                    {
                        int stolen = 2;
                        if (!allocatedSlots.ContainsKey(threatRoster)) allocatedSlots[threatRoster] = 0;
                        allocatedSlots[threatRoster] += stolen;

                        var topDomestic = allocatedSlots.Where(x => x.Key != threatRoster).OrderByDescending(x => x.Value).FirstOrDefault();
                        if (topDomestic.Key != null && allocatedSlots[topDomestic.Key] >= stolen)
                        {
                            allocatedSlots[topDomestic.Key] -= stolen;
                        }
                    }
                }
            }

            List<int> availableRoles = new List<int> { 0, 1, 2, 3, 4 }.OrderBy(x => Rand.Value).ToList();
            List<string> finalInstruments = new List<string>();
            int roleIndex = 0;

            foreach (var kvp in allocatedSlots)
            {
                CulturalRoster roster = kvp.Key;
                int seats = kvp.Value;

                List<string> maskedInsts = GetFlattenedInstruments(roster);
                if (maskedInsts.Count == 0) continue;

                for (int i = 0; i < seats; i++)
                {
                    if (roleIndex >= availableRoles.Count) break;
                    int assignedRole = availableRoles[roleIndex];
                    int index = assignedRole % maskedInsts.Count;
                    string instrument = maskedInsts[index];

                    if (!finalInstruments.Contains(instrument))
                    {
                        finalInstruments.Add(instrument);
                    }
                    roleIndex++;
                }
            }

            if (finalInstruments.Count == 0) return "Acoustic Guitar, Frame Drum";
            return string.Join(", ", finalInstruments);
        }
    }
}