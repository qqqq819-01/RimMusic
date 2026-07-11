using RimWorld;
using System.Collections.Generic;
using Verse;

namespace RimMusic.Data
{
    /// <summary>
    /// Ethnomusicology Database: Core style dictionary.
    /// Each Def instance represents an independent musical genre (e.g., Celtic, Cyberpunk).
    /// Used for scoring heuristic selection and instrumental fallback.
    /// </summary>
    public class MusicStyleDef : Def
    {
        // Technology level alignment for default fallback logic.
        public TechLevel minTechLevel = TechLevel.Undefined;
        public TechLevel maxTechLevel = TechLevel.Undefined;

        /// <summary>
        /// Heuristic Scoring Engine: The Keyword Targets.
        /// Used for intersection calculation with LLM-extracted cultural keywords.
        /// </summary>
        public List<string> matchKeywords = new List<string>();

        // ================= CORE INSTRUMENTATION POOLS =================

        // Category 1: Lead/Melodic (Wind / Bowed Strings - High-frequency focus)
        [MustTranslate]
        public List<string> leadInstruments = new List<string>();

        // Category 2: Harmony/Accompaniment (Plucked Strings - Mid-frequency filling)
        [MustTranslate]
        public List<string> harmonyInstruments = new List<string>();

        // Category 3: Rhythm/Percussion (Drums / Grooves)
        [MustTranslate]
        public List<string> percussionInstruments = new List<string>();

        // Category 4: Keys/Pads (Ambient / Chordal textures)
        // Protocol: For Pre-Industrial styles, this list should remain NULL.
        [MustTranslate]
        public List<string> padInstruments = new List<string>();

        // Category 5: Bass/Foundational (Low-frequency architecture)
        [MustTranslate]
        public List<string> bassInstruments = new List<string>();

        // ===============================================================

        /// <summary>
        /// Aggregates total instrument count across all categorized pools.
        /// </summary>
        public int TotalInstrumentCount =>
            (leadInstruments?.Count ?? 0) +
            (harmonyInstruments?.Count ?? 0) +
            (percussionInstruments?.Count ?? 0) +
            (padInstruments?.Count ?? 0) +
            (bassInstruments?.Count ?? 0);
    }
}