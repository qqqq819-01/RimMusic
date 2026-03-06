using System.Collections.Generic;
using Verse;
using RimWorld;

namespace RimMusic.Data
{
    /// <summary>
    /// RimMusic Unified Audio Profile System.
    /// Defines binding protocols for factions, races, xenotypes, and unique characters.
    /// Aggregates instrumental configurations for LLM telemetry.
    /// </summary>
    public class MusicProfileDef : Def
    {
        // Ideological Shield Protocol (Highest priority directive)
        public bool ignoreIdeo = false;

        // Macro Faction Binding (FactionDef defNames)
        public List<string> linkedFactions = new List<string>();

        // Biological Chassis Binding (AlienRace or standard ThingDef defNames)
        public List<string> linkedRaces = new List<string>();

        // Genetic Lineage Binding (XenotypeDef defNames)
        public List<string> linkedXenotypes = new List<string>();

        // Specialized Entity Binding (StaticIDs for specific framework registries)
        public List<string> linkedOCs = new List<string>();

        // [V0.1.1 Beta] Optional: Musical genre and audio engineering effects. 
        // Leaves blank if omitted by the modder, empowering players to define it themselves.
        public string cultureVibe = "";

        // Core Instrumentation Matrix (Flattened list for LLM parsing)
        public List<string> instruments = new List<string>();

        /// <summary>
        /// Engine integrity self-test: Validates database consistency during startup.
        /// </summary>
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (var error in base.ConfigErrors())
            {
                yield return error;
            }

            if (instruments.NullOrEmpty())
            {
                yield return $"[RimMusic] Critical Warning: MusicProfileDef '{defName}' contains no instrumental data.";
            }

            if (linkedFactions.NullOrEmpty() && linkedRaces.NullOrEmpty() && linkedXenotypes.NullOrEmpty() && linkedOCs.NullOrEmpty())
            {
                yield return $"[RimMusic] Orphan Warning: MusicProfileDef '{defName}' is not bound to any faction, race, xenotype, or unique entity ID.";
            }
        }
    }
}