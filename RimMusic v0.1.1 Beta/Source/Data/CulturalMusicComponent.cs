using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Verse;
using RimWorld;
using RimMusic.Core;

namespace RimMusic.Data
{
    public class CulturalRoster : IExposable
    {
        public string sourceId;
        public string cultureVibe = "Standard RimWorld survival ambiance.";
        public List<string> activeInstruments = new List<string>();
        public bool isPendingOracle = false;
        public bool isCustomProfile = false;

        public void ExposeData()
        {
            Scribe_Values.Look(ref sourceId, "sourceId");
            Scribe_Values.Look(ref cultureVibe, "cultureVibe", "Standard RimWorld survival ambiance.");
            Scribe_Collections.Look(ref activeInstruments, "activeInstruments", LookMode.Value);
            Scribe_Values.Look(ref isPendingOracle, "isPendingOracle", false);
            Scribe_Values.Look(ref isCustomProfile, "isCustomProfile", false);
        }
    }

    public class CulturalMusicComponent : GameComponent
    {
        private Dictionary<string, CulturalRoster> rosters = new Dictionary<string, CulturalRoster>();
        private List<string> tmpKeys;
        private List<CulturalRoster> tmpValues;

        private ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();

        public CulturalMusicComponent(Game game) { }

        // =========================================================================
        // Protected Data Access Interfaces
        // =========================================================================
        public Dictionary<string, CulturalRoster> GetAllRosters() => rosters;
        public void RemoveRoster(string sourceId) { if (rosters.ContainsKey(sourceId)) rosters.Remove(sourceId); }
        public void ResetAllRosters()
        {
            rosters.Clear();
            Log.Message("[RimMusic] Cultural rosters purged and reset.");
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();

            GodsEyeRadar.UpdateRadarMotor();
            GodsEyeRadar.DrawDebugRadius();
            RealtimeMusicEngine.Update();

            while (_mainThreadActions.TryDequeue(out Action action))
            {
                try { action?.Invoke(); }
                catch (Exception ex) { Log.Error($"[RimMusic] Main thread dispatch exception: {ex.Message}"); }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref rosters, "culturalMusicRosters", LookMode.Value, LookMode.Deep, ref tmpKeys, ref tmpValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && rosters == null)
            {
                rosters = new Dictionary<string, CulturalRoster>();
            }
        }

        // =========================================================================
        // Entities Radar (Silent high-speed cache execution)
        // =========================================================================
        private static MethodInfo _cachedGetStaticIDMethod = null;
        private static bool _hasAttemptedToFindMethod = false;

        private string GetStaticIDFromPawn(Pawn pawn)
        {
            if (pawn?.kindDef == null) return null;
            try
            {
                if (!_hasAttemptedToFindMethod)
                {
                    Type registryType = GenTypes.AllTypes.FirstOrDefault(t => t.Name == "SpecialPawnRegistry");
                    if (registryType != null)
                    {
                        _cachedGetStaticIDMethod = registryType.GetMethod("GetStaticID", BindingFlags.Public | BindingFlags.Static);
                    }
                    _hasAttemptedToFindMethod = true;
                }

                if (_cachedGetStaticIDMethod != null)
                {
                    return _cachedGetStaticIDMethod.Invoke(null, new object[] { pawn.kindDef }) as string;
                }
            }
            catch { /* Absolute silence enforced */ }
            return null;
        }

        public void ScanGlobalFactions()
        {
            if (Find.FactionManager == null) return;
            bool raceOverrideEnabled = RimMusicMod.Settings?.EnableRaceOverride ?? false;

            foreach (Faction f in Find.FactionManager.AllFactions)
            {
                if (f.def.defName == "Animals") continue;
                bool hasProfile = DefDatabase<MusicProfileDef>.AllDefs.Any(p => p.linkedFactions.Contains(f.def.defName));

                if (!f.def.humanlikeFaction)
                {
                    GetOrGenerateFactionRoster(f);
                }
                else if (raceOverrideEnabled)
                {
                    if (hasProfile || !ModsConfig.IdeologyActive)
                    {
                        GetOrGenerateFactionRoster(f);
                    }
                }

                if (ModsConfig.IdeologyActive && f.ideos != null)
                {
                    foreach (var ideo in f.ideos.AllIdeos)
                    {
                        GetOrGenerateIdeoRoster(ideo, f.def.techLevel);
                    }
                }
            }
        }

        public CulturalRoster GetOrGenerateRosterFor(Pawn pawn, bool forceCulture = false)
        {
            if (pawn == null) return null;
            Faction f = pawn.Faction;
            Ideo i = ModsConfig.IdeologyActive ? pawn.ideo?.Ideo : null;

            if (f != null && f.def.defName == "Animals") return null;
            if (f == null && i == null) return null;

            // Tier -1: Player-designated protagonist override
            if (rosters.ContainsKey("Pawn_" + pawn.ThingID)) return rosters["Pawn_" + pawn.ThingID];

            string staticID = GetStaticIDFromPawn(pawn);
            if (!string.IsNullOrEmpty(staticID) && rosters.ContainsKey("OC_" + staticID)) return rosters["OC_" + staticID];

            bool raceOverrideEnabled = RimMusicMod.Settings?.EnableRaceOverride ?? false;

            if (!forceCulture && raceOverrideEnabled)
            {
                // Tier 0: External mod registry
                if (!string.IsNullOrEmpty(staticID))
                {
                    var ocProfile = DefDatabase<MusicProfileDef>.AllDefs.FirstOrDefault(p => p.linkedOCs.Contains(staticID));
                    if (ocProfile != null) return BuildRosterFromProfile($"OC_{staticID}", ocProfile);
                }

                // Tier 1: Xenotype DNA priority
                if (ModsConfig.BiotechActive && pawn.genes != null && pawn.genes.Xenotype != null)
                {
                    var xenoProfile = DefDatabase<MusicProfileDef>.AllDefs.FirstOrDefault(p => p.linkedXenotypes.Contains(pawn.genes.Xenotype.defName));
                    if (xenoProfile != null) return BuildRosterFromProfile($"Xeno_{pawn.genes.Xenotype.defName}", xenoProfile);
                }

                // Tier 2: Alien race baseline
                var raceProfile = DefDatabase<MusicProfileDef>.AllDefs.FirstOrDefault(p => p.linkedRaces.Contains(pawn.def.defName));
                if (raceProfile != null) return BuildRosterFromProfile($"Race_{pawn.def.defName}", raceProfile);

                // Tier 3: Political faction override
                if (f != null)
                {
                    var facProfile = DefDatabase<MusicProfileDef>.AllDefs.FirstOrDefault(p => p.linkedFactions.Contains(f.def.defName));
                    if (facProfile != null || !f.def.humanlikeFaction) return GetOrGenerateFactionRoster(f);
                }
            }

            // Tier 4: Ideological baseline fallback
            if (i != null) return GetOrGenerateIdeoRoster(i, f?.def.techLevel ?? TechLevel.Industrial);
            else if (f != null) return GetOrGenerateFactionRoster(f);

            return null;
        }

        private CulturalRoster BuildRosterFromProfile(string sourceId, MusicProfileDef profile)
        {
            if (rosters.ContainsKey(sourceId)) return rosters[sourceId];

            CulturalRoster roster = new CulturalRoster
            {
                sourceId = sourceId,
                // [V0.1.1 Beta] Read directly from XML. If null, keep it strictly empty.
                cultureVibe = string.IsNullOrEmpty(profile.cultureVibe) ? "" : profile.cultureVibe,
                // [V0.1.1 Beta] Grant VIP purple status for OC/Xeno/Race API configs
                isCustomProfile = true
            };

            foreach (string inst in profile.instruments)
            {
                if (string.IsNullOrWhiteSpace(inst) || inst.ToLower() == "null" || inst.ToLower() == "none") roster.activeInstruments.Add("");
                else roster.activeInstruments.Add(inst);
            }

            rosters.Add(sourceId, roster);
            return roster;
        }

        private CulturalRoster GetOrGenerateFactionRoster(Faction faction)
        {
            if (faction == null) return null;
            string identityId = "Faction_" + faction.def.defName;
            if (rosters.ContainsKey(identityId)) return rosters[identityId];

            CulturalRoster roster = new CulturalRoster { sourceId = identityId };
            string tech = faction.def.techLevel.ToString();

            var profile = DefDatabase<MusicProfileDef>.AllDefs.FirstOrDefault(p => p.linkedFactions.Contains(faction.def.defName));

            if (profile != null && profile.instruments != null && profile.instruments.Count > 0)
            {
                roster.isCustomProfile = true; // [V0.1.1 Beta] Grant VIP purple status
                roster.cultureVibe = string.IsNullOrEmpty(profile.cultureVibe) ? "" : profile.cultureVibe; // Leave blank if omitted
                roster.activeInstruments.Clear();
                foreach (string inst in profile.instruments)
                {
                    if (!string.IsNullOrWhiteSpace(inst) && inst.ToLower() != "null" && inst.ToLower() != "none")
                        roster.activeInstruments.Add(inst);
                }
                rosters.Add(identityId, roster);
                return roster;
            }

            ProcessLLMRequest(roster, "UnknownOrigin", "NoMemes", tech, false, faction.Name, faction.def.description ?? "Unknown entity.");
            rosters.Add(identityId, roster);
            return roster;
        }

        private CulturalRoster GetOrGenerateIdeoRoster(Ideo ideo, TechLevel fallbackTech)
        {
            if (ideo == null) return null;
            string identityId = "Ideo_" + ideo.id.ToString();
            if (rosters.ContainsKey(identityId)) return rosters[identityId];

            CulturalRoster roster = new CulturalRoster { sourceId = identityId };
            string tech = fallbackTech.ToString();
            string origin = ideo.culture?.defName ?? "UnknownOrigin";
            string memes = ideo.memes != null ? string.Join("_", ideo.memes.Select(m => m.defName).OrderBy(m => m)) : "NoMemes";

            Faction primaryFaction = Find.FactionManager.AllFactions.FirstOrDefault(f => f.ideos?.PrimaryIdeo == ideo);
            if (primaryFaction != null)
            {
                var profile = DefDatabase<MusicProfileDef>.AllDefs.FirstOrDefault(p => p.linkedFactions.Contains(primaryFaction.def.defName));
                if (profile != null && profile.instruments != null && profile.instruments.Count > 0)
                {
                    roster.isCustomProfile = true; // [V0.1.1 Beta] Grant VIP purple status
                    roster.cultureVibe = string.IsNullOrEmpty(profile.cultureVibe) ? "" : profile.cultureVibe; // Leave blank if omitted
                    roster.activeInstruments.Clear();
                    foreach (string inst in profile.instruments)
                    {
                        if (!string.IsNullOrWhiteSpace(inst) && inst.ToLower() != "null" && inst.ToLower() != "none")
                            roster.activeInstruments.Add(inst);
                    }
                    rosters.Add(identityId, roster);
                    return roster;
                }
            }

            string flavorText = $"Culture: {ideo.name}, Ideology Rules: {ideo.description ?? "None"}";
            ProcessLLMRequest(roster, origin, memes, tech, true, "Human", flavorText);

            rosters.Add(identityId, roster);
            return roster;
        }

        private void ProcessLLMRequest(CulturalRoster roster, string origin, string memes, string tech, bool isHumanlike, string factionLabel, string flavorDesc)
        {
            roster.isPendingOracle = true;
            roster.cultureVibe = "Analyzing cultural frequencies...";
            roster.activeInstruments.Clear();
            roster.activeInstruments.AddRange(new[] { "Acoustic Guitar", "Frame Drum" }); // Temporary placeholders

            RequestOracleAsync(roster, origin, memes, tech, flavorDesc, isHumanlike, factionLabel, flavorDesc);
        }

        private async void RequestOracleAsync(CulturalRoster roster, string origin, string memes, string tech, string flavor, bool isHumanlike, string factionLabel, string factionDesc)
        {
            try
            {
                int targetCount = RimMusicMod.Settings?.TargetInstrumentCount ?? 5;
                bool useRealistic = RimMusicMod.Settings?.UseRealisticInstruments ?? true;

                string instrumentPrompt = useRealistic
                    ? $"1. 'instruments': EXACTLY {targetCount} REAL, standard Earth musical instruments recognizable by DAW software (e.g., Cello, Moog Synth, Taiko). ABSOLUTELY NO fictional materials or sci-fi anatomy in the name."
                    : $"1. 'instruments': EXACTLY {targetCount} incredibly creative and lore-friendly instrument names (can include sci-fi, organic, or fantasy elements).";

                string systemPrompt;

                if (isHumanlike)
                {
                    systemPrompt = $"[System] You are a RimWorld Audio Director. Analyze this human faction/ideology:\n" +
                                   $"Tech Level: {tech}\nOrigin Culture: {origin}\nMemes: {memes}\nFlavor: {flavor}\n\n";
                }
                else
                {
                    systemPrompt = $"[System] You are a RimWorld Audio Director. Analyze this NON-HUMAN entity faction:\n" +
                                   $"Entity Name: {factionLabel}\nLore Description: {factionDesc}\nTech Level: {tech}\n" +
                                   $"Ignore human culture. Design their acoustic atmosphere.\n\n";
                }

                int wordLimit = RimMusicMod.Settings?.CultureVibeWordLimit ?? 30;

                string prompt = systemPrompt +
                                $"Output EXACTLY a JSON format with two keys:\n" +
                                $"{instrumentPrompt}\n" +
                                $"2. 'culture_vibe': A description (STRICTLY UNDER {wordLimit} WORDS) of their musical GENRE, playing techniques, and audio engineering effects.\n" +
                                $"CRITICAL WARNING: OUTPUT PURE JSON ONLY. NO MARKDOWN FORMATTING. NO ```json. JUST THE RAW BRACES.";

                string rawResponse = await new MusicAIClient().SendRequestViaRimTalkConfig(prompt, 300);

                if (MusicAIClient.IsCircuitTripped || rawResponse.StartsWith("Error"))
                {
                    ApplyFallback(roster, tech);
                    return;
                }

                // [V0.1.1 Beta] Armored Regex Extraction to bypass JsonUtility limitations
                string unescaped = rawResponse.Replace("\\\"", "\"").Replace("\\n", " ").Trim();

                // Strip Markdown block if LLM ignored the warning
                if (unescaped.StartsWith("```json")) unescaped = unescaped.Substring(7);
                else if (unescaped.StartsWith("```")) unescaped = unescaped.Substring(3);
                if (unescaped.EndsWith("```")) unescaped = unescaped.Substring(0, unescaped.Length - 3);
                unescaped = unescaped.Trim();

                string parsedVibe = "";
                List<string> parsedInsts = new List<string>();

                // Extract culture_vibe using Regex
                Match vibeMatch = Regex.Match(unescaped, @"\""culture_vibe\""\s*:\s*\""([^\""\\]*(?:\\.[^\""\\]*)*)\""");
                if (vibeMatch.Success)
                {
                    parsedVibe = vibeMatch.Groups[1].Value;
                }

                // Extract instruments array using Regex
                Match instMatch = Regex.Match(unescaped, @"\""instruments\""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
                if (instMatch.Success)
                {
                    string arrayContent = instMatch.Groups[1].Value;
                    MatchCollection items = Regex.Matches(arrayContent, @"\""([^\""\\]*(?:\\.[^\""\\]*)*)\""");
                    foreach (Match m in items)
                    {
                        parsedInsts.Add(m.Groups[1].Value);
                    }
                }

                _mainThreadActions.Enqueue(() =>
                {
                    // If Regex extraction succeeded and found instruments
                    if (parsedInsts.Count > 0)
                    {
                        roster.cultureVibe = !string.IsNullOrEmpty(parsedVibe) ? parsedVibe : $"Generic {tech} era audio profile.";
                        roster.activeInstruments.Clear();
                        roster.activeInstruments.AddRange(parsedInsts);
                    }
                    else
                    {
                        // Extraction completely failed
                        Log.Warning($"[RimMusic] Regex extraction failed for LLM response. Applying fallback. Raw Response: {rawResponse}");
                        roster.cultureVibe = "Failed to parse cultural data. Fallback ambiance applied.";
                        roster.activeInstruments.Clear();
                        roster.activeInstruments.AddRange(new[] { "Acoustic Guitar", "Drum Kit", "Synth Pad" });
                    }
                    roster.isPendingOracle = false;
                });
            }
            catch (Exception ex)
            {
                Log.Error($"[RimMusic] Critical error in RequestOracleAsync: {ex.Message}");
                ApplyFallback(roster, tech);
            }
        }

        private void ApplyFallback(CulturalRoster roster, string tech)
        {
            _mainThreadActions.Enqueue(() =>
            {
                roster.cultureVibe = $"Generic {tech} era survival ambiance (API Offline/Error).";
                roster.activeInstruments.Clear();
                roster.activeInstruments.AddRange(new[] { "Acoustic Guitar", "Drum Kit", "Synth Pad" });
                roster.isPendingOracle = false;
            });
        }
    }
}