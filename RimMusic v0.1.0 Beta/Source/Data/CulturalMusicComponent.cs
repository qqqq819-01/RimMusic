using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Reflection;
using Verse;
using RimWorld;
using RimMusic.Core;

namespace RimMusic.Data
{
    public class CulturalRoster : IExposable
    {
        public string sourceId;
        public string styleDefName;
        public List<string> activeInstruments = new List<string>();
        public bool isPendingOracle = false;
        public int lastMatchScore = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref sourceId, "sourceId");
            Scribe_Values.Look(ref styleDefName, "styleDefName");
            Scribe_Collections.Look(ref activeInstruments, "activeInstruments", LookMode.Value);
            Scribe_Values.Look(ref isPendingOracle, "isPendingOracle", false);
            Scribe_Values.Look(ref lastMatchScore, "lastMatchScore", -1);
        }
    }

    public class CulturalMusicComponent : GameComponent
    {
        // Legacy nomenclature retained for UI reflection compatibility.
        // Protected by standard accessor interfaces.
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

            CulturalRoster roster = new CulturalRoster { sourceId = sourceId, styleDefName = "API_Profile_Override", lastMatchScore = 9999 };
            foreach (string inst in profile.instruments)
            {
                if (string.IsNullOrWhiteSpace(inst) || inst.ToLower() == "null" || inst.ToLower() == "none") roster.activeInstruments.Add("");
                else roster.activeInstruments.Add(inst);
            }

            string hashKey = CultureCalculator.GetHashForSourceId(sourceId);
            var cacheData = CultureOracleCache.GetOrInitData(hashKey);
            if (!cacheData.OverrideProfile.IsActive) CultureOracleCache.SetData(hashKey, new List<string> { "API_Privilege" }, roster.activeInstruments);

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
                roster.styleDefName = "API_Profile_Override";
                roster.activeInstruments.Clear();
                foreach (string inst in profile.instruments)
                {
                    if (!string.IsNullOrWhiteSpace(inst) && inst.ToLower() != "null" && inst.ToLower() != "none")
                        roster.activeInstruments.Add(inst);
                }
                roster.lastMatchScore = 9999;

                string apiHash = CultureCalculator.GetHashForSourceId(identityId);
                var cacheData = CultureOracleCache.GetOrInitData(apiHash);
                if (!cacheData.OverrideProfile.IsActive) CultureOracleCache.SetData(apiHash, new List<string> { "API_Privilege" }, roster.activeInstruments);

                rosters.Add(identityId, roster);
                return roster;
            }

            string hashKey = CultureCalculator.GetHashForSourceId(identityId);
            ProcessLLMOrCache(roster, hashKey, "UnknownOrigin", "NoMemes", tech, false, faction.Name, faction.def.description ?? "Unknown entity.");
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
            string hashKey = CultureCalculator.GetHashForSourceId(identityId);

            Faction primaryFaction = Find.FactionManager.AllFactions.FirstOrDefault(f => f.ideos?.PrimaryIdeo == ideo);
            if (primaryFaction != null)
            {
                var profile = DefDatabase<MusicProfileDef>.AllDefs.FirstOrDefault(p => p.linkedFactions.Contains(primaryFaction.def.defName));
                if (profile != null && profile.instruments != null && profile.instruments.Count > 0)
                {
                    roster.styleDefName = "API_Inherited_Culture";
                    roster.activeInstruments.Clear();
                    foreach (string inst in profile.instruments)
                    {
                        if (!string.IsNullOrWhiteSpace(inst) && inst.ToLower() != "null" && inst.ToLower() != "none")
                            roster.activeInstruments.Add(inst);
                    }
                    roster.lastMatchScore = 9999;

                    var cacheData = CultureOracleCache.GetOrInitData(hashKey);
                    if (!cacheData.OverrideProfile.IsActive) CultureOracleCache.SetData(hashKey, new List<string> { "API_Inherited" }, roster.activeInstruments);

                    rosters.Add(identityId, roster);
                    return roster;
                }
            }

            string flavorText = $"Culture: {ideo.name}, Ideology Rules: {ideo.description ?? "None"}";
            ProcessLLMOrCache(roster, hashKey, origin, memes, tech, true, "Human", flavorText);

            rosters.Add(identityId, roster);
            return roster;
        }

        private void ProcessLLMOrCache(CulturalRoster roster, string hashKey, string origin, string memes, string tech, bool isHumanlike, string factionLabel, string flavorDesc)
        {
            var cachedData = CultureOracleCache.GetData(hashKey);
            bool cacheValid = false;

            if (cachedData != null && cachedData.Keywords != null && cachedData.Keywords.Count > 0)
            {
                int cacheScore = EvaluateKeywords(cachedData.Keywords, out _);
                int threshold = RimMusicMod.Settings?.MinMatchScore ?? 2;

                if (cacheScore >= threshold)
                {
                    cacheValid = true;
                    roster.lastMatchScore = cacheScore;
                }
                else
                {
                    Log.Warning($"[RimMusic] Low quality cache detected (Score {cacheScore} < Threshold {threshold}). Purging and requesting re-calculation.");
                    CultureOracleCache.EvictData(hashKey);
                }
            }

            if (cacheValid)
            {
                ApplyScoredStyleAndInstruments(roster, cachedData.Keywords, cachedData.Instruments, tech);
            }
            else
            {
                roster.isPendingOracle = true;
                roster.styleDefName = "Pending_Oracle";
                roster.activeInstruments.AddRange(new[] { "Acoustic Guitar", "Frame Drum" });

                RequestOracleAsync(roster, hashKey, origin, memes, tech, flavorDesc, isHumanlike, factionLabel, flavorDesc);
            }
        }

        [System.Serializable] public class OracleJsonResponse { public List<string> keywords; public List<string> instruments; }

        private async void RequestOracleAsync(CulturalRoster roster, string hashKey, string origin, string memes, string tech, string flavor, bool isHumanlike, string factionLabel, string factionDesc)
        {
            try
            {
                int targetCount = RimMusicMod.Settings?.TargetInstrumentCount ?? 5;
                int targetKeywordCount = RimMusicMod.Settings?.TargetKeywordCount ?? 10;
                bool useRealistic = RimMusicMod.Settings?.UseRealisticInstruments ?? true;

                string instrumentPrompt = useRealistic
                    ? $"2. 'instruments': EXACTLY {targetCount} REAL and RECOGNIZABLE musical instruments modified with ONE thematic adjective. MUST BE IN ENGLISH."
                    : $"2. 'instruments': EXACTLY {targetCount} incredibly creative and lore-friendly instrument names. MUST BE IN ENGLISH.";

                string systemPrompt;

                if (isHumanlike)
                {
                    systemPrompt = $"[System] You are a RimWorld Ethnomusicologist. Analyze this human faction/ideology:\n" +
                                   $"Tech Level: {tech}\nOrigin Culture: {origin}\nMemes: {memes}\nFlavor: {flavor}\n\n";
                }
                else
                {
                    systemPrompt = $"[System] You are a RimWorld Ethnomusicologist. Analyze this NON-HUMAN entity faction:\n" +
                                   $"Entity Name: {factionLabel}\nLore Description: {factionDesc}\nTech Level: {tech}\n" +
                                   $"Ignore human culture. Design their acoustic atmosphere. Include physical descriptors (steel, slime, gears) in keywords!\n\n";
                }

                string prompt = systemPrompt +
                                $"Output EXACTLY a JSON format with two arrays:\n" +
                                $"1. 'keywords': {targetKeywordCount} atmospheric music keywords. MUST BE IN ENGLISH.\n" +
                                $"{instrumentPrompt}\n" +
                                $"CRITICAL WARNING: JSON ONLY. Lowercase keys 'keywords' and 'instruments'.";

                string rawResponse = await new MusicAIClient().SendRequestViaRimTalkConfig(prompt, 300);

                if (MusicAIClient.IsCircuitTripped || rawResponse.StartsWith("Error"))
                {
                    _mainThreadActions.Enqueue(() =>
                    {
                        string targetStyle = null;
                        if (hashKey.Contains("Mechanoid")) targetStyle = "RimMusic_Style_Mechanoid";
                        else if (hashKey.Contains("Insectoid")) targetStyle = "RimMusic_Style_Insectoid";

                        if (targetStyle != null)
                        {
                            var style = DefDatabase<MusicStyleDef>.AllDefs.FirstOrDefault(s => s.defName == targetStyle);
                            if (style != null)
                            {
                                List<string> fakeKeywords = style.matchKeywords.Take(10).ToList();
                                List<string> fakeInsts = new List<string>();
                                if (!style.leadInstruments.NullOrEmpty()) fakeInsts.Add(style.leadInstruments.RandomElement());
                                if (!style.harmonyInstruments.NullOrEmpty()) fakeInsts.Add(style.harmonyInstruments.RandomElement());
                                if (!style.percussionInstruments.NullOrEmpty()) fakeInsts.Add(style.percussionInstruments.RandomElement());
                                CultureOracleCache.SetData(hashKey, fakeKeywords, fakeInsts);
                                ApplyScoredStyleAndInstruments(roster, fakeKeywords, fakeInsts, tech);
                            }
                        }
                        else
                        {
                            roster.lastMatchScore = 0;
                            ApplyFallbackStyle(roster, tech);
                        }
                        roster.isPendingOracle = false;
                    });
                    return;
                }

                string unescaped = rawResponse.Replace("\\\"", "\"").Replace("\\n", "\n");
                var match = System.Text.RegularExpressions.Regex.Match(unescaped, @"\{\s*""keywords""[\s\S]*?\]\s*\}");
                string cleanJson = match.Success ? match.Value : "{}";

                OracleJsonResponse result = null;
                try { result = UnityEngine.JsonUtility.FromJson<OracleJsonResponse>(cleanJson); } catch { }

                _mainThreadActions.Enqueue(() =>
                {
                    if (result != null && result.keywords != null && result.keywords.Count > 0)
                    {
                        int tempScore = EvaluateKeywords(result.keywords, out _);
                        if (tempScore > 0) CultureOracleCache.SetData(hashKey, result.keywords, result.instruments);
                        ApplyScoredStyleAndInstruments(roster, result.keywords, result.instruments, tech);
                    }
                    else
                    {
                        roster.lastMatchScore = 0;
                        ApplyFallbackStyle(roster, tech);
                    }
                    roster.isPendingOracle = false;
                });
            }
            catch (Exception)
            {
                _mainThreadActions.Enqueue(() =>
                {
                    roster.lastMatchScore = 0;
                    ApplyFallbackStyle(roster, tech);
                    roster.isPendingOracle = false;
                });
            }
        }

        private int EvaluateKeywords(List<string> keywords, out List<MusicStyleDef> topStyles)
        {
            topStyles = new List<MusicStyleDef>();
            int highestScore = -1;

            if (keywords == null || keywords.Count == 0) return 0;

            foreach (var styleDef in DefDatabase<MusicStyleDef>.AllDefsListForReading)
            {
                int score = 0;
                if (styleDef.matchKeywords != null)
                {
                    foreach (var word in keywords)
                    {
                        if (string.IsNullOrWhiteSpace(word)) continue;
                        if (styleDef.matchKeywords.Any(k =>
                            k.IndexOf(word.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 ||
                            word.IndexOf(k.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            score += 1;
                        }
                    }
                }

                if (score > highestScore)
                {
                    highestScore = score;
                    topStyles.Clear();
                    topStyles.Add(styleDef);
                }
                else if (score == highestScore && highestScore >= 0)
                {
                    topStyles.Add(styleDef);
                }
            }
            return highestScore;
        }

        private void ApplyScoredStyleAndInstruments(CulturalRoster roster, List<string> llmKeywords, List<string> llmInstruments, string techLevel)
        {
            int threshold = RimMusicMod.Settings?.MinMatchScore ?? 2;
            int highestScore = EvaluateKeywords(llmKeywords, out List<MusicStyleDef> topStyles);

            roster.lastMatchScore = highestScore;

            if (highestScore < threshold || topStyles.Count == 0)
            {
                ApplyFallbackStyle(roster, techLevel);
                return;
            }

            MusicStyleDef bestStyle = topStyles.RandomElement();
            roster.styleDefName = bestStyle.defName;
            roster.activeInstruments.Clear();
            if (llmInstruments != null) roster.activeInstruments.AddRange(llmInstruments);
        }

        private void ApplyFallbackStyle(CulturalRoster roster, string techStr)
        {
            TechLevel tech = Enum.TryParse(techStr, out TechLevel t) ? t : TechLevel.Industrial;
            var validStyles = DefDatabase<MusicStyleDef>.AllDefsListForReading.Where(s => tech >= s.minTechLevel && tech <= s.maxTechLevel).ToList();
            var fallback = validStyles.Count > 0 ? validStyles.RandomElement() : DefDatabase<MusicStyleDef>.AllDefsListForReading.RandomElementWithFallback();

            roster.styleDefName = fallback.defName;
            roster.activeInstruments.Clear();

            if (!fallback.leadInstruments.NullOrEmpty()) roster.activeInstruments.Add(fallback.leadInstruments.RandomElement());
            if (!fallback.harmonyInstruments.NullOrEmpty()) roster.activeInstruments.Add(fallback.harmonyInstruments.RandomElement());
            if (!fallback.percussionInstruments.NullOrEmpty()) roster.activeInstruments.Add(fallback.percussionInstruments.RandomElement());
        }
    }
}