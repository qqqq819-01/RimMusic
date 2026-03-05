using System;
using System.Collections.Generic;
using System.IO;
using Verse;
using RimWorld;

namespace RimMusic.Data
{
    // =====================================================================
    // Player Override Profile
    // Absolute physical isolation: Records only player modifications.
    // Prevents contamination of base AI-generated data.
    // =====================================================================
    public class PlayerOverrideProfile : IExposable
    {
        public List<bool> MutedSlots = new List<bool>();         // Records disabled slots
        public List<string> CustomSlots = new List<string>();    // Records custom instrument names (empty defaults to AI original)
        public List<string> ExtraInstruments = new List<string>(); // Extra appended instruments

        public void ExposeData()
        {
            Scribe_Collections.Look(ref MutedSlots, "MutedSlots", LookMode.Value);
            Scribe_Collections.Look(ref CustomSlots, "CustomSlots", LookMode.Value);
            Scribe_Collections.Look(ref ExtraInstruments, "ExtraInstruments", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (MutedSlots == null) MutedSlots = new List<bool>();
                if (CustomSlots == null) CustomSlots = new List<string>();
                if (ExtraInstruments == null) ExtraInstruments = new List<string>();
            }
        }

        public void EnsureCapacity(int count)
        {
            while (MutedSlots.Count < count) MutedSlots.Add(false);
            while (CustomSlots.Count < count) CustomSlots.Add("");
        }

        // Evaluates if manual override status is active
        public bool IsActive => MutedSlots.Contains(true) || CustomSlots.Exists(s => !string.IsNullOrEmpty(s)) || ExtraInstruments.Count > 0;
    }

    // =====================================================================
    // Native Data Structure
    // =====================================================================
    public class CachedCultureData : IExposable
    {
        public List<string> Keywords = new List<string>();
        public List<string> Instruments = new List<string>();

        // Mounts the player override profile
        public PlayerOverrideProfile OverrideProfile = new PlayerOverrideProfile();

        public void ExposeData()
        {
            Scribe_Collections.Look(ref Keywords, "Keywords", LookMode.Value);
            Scribe_Collections.Look(ref Instruments, "Instruments", LookMode.Value);
            Scribe_Deep.Look(ref OverrideProfile, "OverrideProfile");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (Keywords == null) Keywords = new List<string>();
                if (Instruments == null) Instruments = new List<string>();
                if (OverrideProfile == null) OverrideProfile = new PlayerOverrideProfile();
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class CultureOracleCache
    {
        private static Dictionary<string, CachedCultureData> _cache = new Dictionary<string, CachedCultureData>();

        private static List<string> tmpKeys;
        private static List<CachedCultureData> tmpValues;

        private static string CacheFilePath => Path.Combine(GenFilePaths.ConfigFolderPath, "RimMusic_CultureCache.xml");

        static CultureOracleCache() { Load(); }

        public static void Load()
        {
            if (File.Exists(CacheFilePath))
            {
                try
                {
                    Scribe.loader.InitLoading(CacheFilePath);
                    Scribe_Collections.Look(ref _cache, "OracleData", LookMode.Value, LookMode.Deep, ref tmpKeys, ref tmpValues);
                    Scribe.loader.FinalizeLoading();

                    if (_cache == null) _cache = new Dictionary<string, CachedCultureData>();
                    Log.Message($"[RimMusic] Culture oracle cache loaded successfully. Registry contains {_cache.Count} faction audio profiles.");
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimMusic] Cache initialization failed: {ex.Message}. Rebuilding database.");
                    Scribe.loader.ForceStop();
                    _cache = new Dictionary<string, CachedCultureData>();
                }
            }
        }

        // Exposes manual save interface for UI operations
        public static void ForceSave() { Save(); }

        private static void Save()
        {
            try
            {
                Scribe.saver.InitSaving(CacheFilePath, "RimMusicCacheVault");
                Scribe_Collections.Look(ref _cache, "OracleData", LookMode.Value, LookMode.Deep, ref tmpKeys, ref tmpValues);
                Scribe.saver.FinalizeSaving();
            }
            catch (Exception ex)
            {
                Log.Error($"[RimMusic] Cache save operation failed: {ex.Message}");
                Scribe.saver.ForceStop();
            }
        }

        // Initializes an empty shell if cache is missing, enabling manual overrides prior to AI generation
        public static CachedCultureData GetOrInitData(string hashKey)
        {
            if (string.IsNullOrEmpty(hashKey)) return null;
            if (!_cache.ContainsKey(hashKey))
            {
                _cache[hashKey] = new CachedCultureData();
                Save();
            }
            return _cache[hashKey];
        }

        public static CachedCultureData GetData(string hashKey)
        {
            if (string.IsNullOrEmpty(hashKey)) return null;
            if (_cache.TryGetValue(hashKey, out var data)) return data;
            return null;
        }

        public static void SetData(string hashKey, List<string> keywords, List<string> instruments)
        {
            if (string.IsNullOrEmpty(hashKey)) return;

            var existing = GetOrInitData(hashKey);
            existing.Keywords = keywords ?? new List<string>();
            existing.Instruments = instruments ?? new List<string>();
            Save();
        }

        public static void EvictData(string hashKey)
        {
            if (!string.IsNullOrEmpty(hashKey) && _cache.ContainsKey(hashKey))
            {
                _cache.Remove(hashKey);
                Save();
                Log.Message($"[RimMusic] Cache data evicted: {hashKey}");
            }
        }

        // =====================================================================
        // Database Self-Governance Interfaces
        // =====================================================================
        public static void CleanOrphans(HashSet<string> activeHashes)
        {
            var keysToRemove = new List<string>();
            foreach (var key in _cache.Keys)
            {
                if (!activeHashes.Contains(key)) keysToRemove.Add(key);
            }

            foreach (var key in keysToRemove) _cache.Remove(key);

            if (keysToRemove.Count > 0)
            {
                Save();
                Messages.Message("RimMusic_CacheCleaned".Translate(keysToRemove.Count), MessageTypeDefOf.PositiveEvent, false);
            }
            else
            {
                Messages.Message("RimMusic_CacheAlreadyClean".Translate(), MessageTypeDefOf.NeutralEvent, false);
            }
        }
    }
}