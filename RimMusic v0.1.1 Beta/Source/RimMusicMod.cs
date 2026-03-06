using HarmonyLib;
using RimMusic.Data;
using RimMusic.UI;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace RimMusic
{
    public class RimMusicMod : Mod
    {
        public static RimMusicSettings Settings;

        public RimMusicMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimMusicSettings>();
            var harmony = new Harmony("com.RimMusic.Patch");
            harmony.PatchAll();

            Log.Message("[RimMusic] Initialization sequence complete. AI Audio Engine v0.1.1 Beta online.");
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (Settings.Preset == null) Settings.Preset = PromptPreset.CreateDefault();

            if (Find.WindowStack.WindowOfType<RimMusic.UI.MusicSettingsFloatingWindow>() == null)
            {
                Window vanillaSettings = null;
                foreach (Window window in Find.WindowStack.Windows)
                {
                    if (window.GetType().Name == "Dialog_ModSettings")
                    {
                        vanillaSettings = window;
                        break;
                    }
                }
                if (vanillaSettings != null) vanillaSettings.Close();
                Find.WindowStack.Add(new RimMusic.UI.MusicSettingsFloatingWindow());
            }
        }

        public override string SettingsCategory() => "RimMusic AI";
    }

    /// <summary>
    /// Core configuration data. 
    /// Note: Field names and Scribe keys are retained in English to preserve cross-version save compatibility.
    /// </summary>
    public class RimMusicSettings : ModSettings
    {
        public PromptPreset Preset;

        public List<PromptPreset> SavedPresets = new List<PromptPreset>();

        public int TargetInstrumentCount = 5;
        public int CultureVibeWordLimit = 30;
        public bool UseRealisticInstruments = true;

        public bool EnableCulturalFusion = true;
        public float FusionMinorityThreshold = 0.20f;
        public float ThreatStealThreshold = 0.30f;
        public float ThreatDominateThreshold = 0.75f;

        public bool EnableRaceOverride = false;
        public bool AllowRaceFusion = false;

        public int MaxOutputWords = 150;
        public int DialogueLineLimit = 3;

        // LLM Text Generation Engine Parameters
        public bool UseRimTalkTextApi = true;
        public string CustomTextApiUrl = "https://api.siliconflow.cn";
        public string CustomTextApiKey = "";
        public string CustomTextModelName = "deepseek-ai/DeepSeek-V3";

        public float HoverRadius = 3f;
        public float FocusCacheDuration = 5f;
        public float MacroZoomThreshold = 25f;
        public float SwitchDelay = 3f;
        public float MacroExitDelay = 1f;
        public float EventMemoryDuration = 60f;

        public bool EnableEnvironmentalRadar = false;
        public int RadarBaseRadius = 5;
        public int RadarMaxRadius = 15;
        public float RadarExpandTime = 3.0f;
        public float RadarFastMoveThreshold = 15f;
        public float RadarIdleThreshold = 0.5f;
        public float RadarTetherDistance = 15f;
        public bool DrawRadarRadius = false;

        // 302.ai API & Jukebox parameters
        public bool EnableRealtimeMusic = false;
        public string CustomAudioApiUrl = "https://api.302.ai";
        public string SunoApiKey = "";
        public string SunoModelVersion = "chirp-bluejay";
        public bool SunoMakeInstrumental = true;
        public bool AutoPlayNextTrack = false;
        public string MusicSavePath = "";

        public bool ForceChineseOutput = true;
        public bool DebugMode = false;
        public float HUDTransparency = 0.8f;

        public bool SelectColonist = true; public bool HoverColonist = true;
        public bool SelectSlave = true; public bool HoverSlave = true;
        public bool SelectPrisoner = true; public bool HoverPrisoner = false;
        public bool SelectEnemy = true; public bool HoverEnemy = false;
        public bool SelectNeutral = true; public bool HoverNeutral = false;

        public Rect HUDRect = Rect.zero;

        public string GetActualSavePath()
        {
            string path = MusicSavePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(GenFilePaths.ConfigFolderPath, "RimMusic_Generations");
            }

            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            }
            catch
            {
                path = Path.Combine(GenFilePaths.ConfigFolderPath, "RimMusic_Generations");
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            }

            return path;
        }

        public override void ExposeData()
        {
            Scribe_Deep.Look(ref Preset, "Preset");

            Scribe_Collections.Look(ref SavedPresets, "SavedPresets", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (SavedPresets == null) SavedPresets = new List<PromptPreset>();
            }

            Scribe_Values.Look(ref TargetInstrumentCount, "TargetInstrumentCount", 5);
            Scribe_Values.Look(ref CultureVibeWordLimit, "CultureVibeWordLimit", 30);
            Scribe_Values.Look(ref UseRealisticInstruments, "UseRealisticInstruments", true);

            Scribe_Values.Look(ref EnableCulturalFusion, "EnableCulturalFusion", true);
            Scribe_Values.Look(ref FusionMinorityThreshold, "FusionMinorityThreshold", 0.20f);
            Scribe_Values.Look(ref ThreatStealThreshold, "ThreatStealThreshold", 0.30f);
            Scribe_Values.Look(ref ThreatDominateThreshold, "ThreatDominateThreshold", 0.75f);

            Scribe_Values.Look(ref EnableRaceOverride, "EnableRaceOverride", false);
            Scribe_Values.Look(ref AllowRaceFusion, "AllowRaceFusion", false);

            Scribe_Values.Look(ref MaxOutputWords, "MaxOutputWords", 120);
            Scribe_Values.Look(ref DialogueLineLimit, "DialogueLineLimit", 3);
            Scribe_Values.Look(ref HoverRadius, "HoverRadius", 3f);
            Scribe_Values.Look(ref FocusCacheDuration, "FocusCacheDuration", 5f);
            Scribe_Values.Look(ref MacroZoomThreshold, "MacroZoomThreshold", 25f);
            Scribe_Values.Look(ref SwitchDelay, "SwitchDelay", 3f);
            Scribe_Values.Look(ref MacroExitDelay, "MacroExitDelay", 1f);
            Scribe_Values.Look(ref EventMemoryDuration, "EventMemoryDuration", 60f);

            Scribe_Values.Look(ref EnableEnvironmentalRadar, "EnableEnvironmentalRadar", false);
            Scribe_Values.Look(ref RadarBaseRadius, "RadarBaseRadius", 5);
            Scribe_Values.Look(ref RadarMaxRadius, "RadarMaxRadius", 15);
            Scribe_Values.Look(ref RadarExpandTime, "RadarExpandTime", 3.0f);
            Scribe_Values.Look(ref RadarFastMoveThreshold, "RadarFastMoveThreshold", 15f);
            Scribe_Values.Look(ref RadarIdleThreshold, "RadarIdleThreshold", 0.5f);
            Scribe_Values.Look(ref RadarTetherDistance, "RadarTetherDistance", 15f);
            Scribe_Values.Look(ref DrawRadarRadius, "DrawRadarRadius", false);

            Scribe_Values.Look(ref UseRimTalkTextApi, "UseRimTalkTextApi", true);
            Scribe_Values.Look(ref CustomTextApiUrl, "CustomTextApiUrl", "https://api.siliconflow.cn");
            Scribe_Values.Look(ref CustomTextApiKey, "CustomTextApiKey", "");
            Scribe_Values.Look(ref CustomTextModelName, "CustomTextModelName", "deepseek-ai/DeepSeek-V3");

            Scribe_Values.Look(ref EnableRealtimeMusic, "EnableRealtimeMusic", false);
            Scribe_Values.Look(ref CustomAudioApiUrl, "CustomAudioApiUrl", "https://api.302.ai");
            Scribe_Values.Look(ref SunoApiKey, "SunoApiKey", "");
            Scribe_Values.Look(ref SunoModelVersion, "SunoModelVersion", "chirp-bluejay");
            Scribe_Values.Look(ref SunoMakeInstrumental, "SunoMakeInstrumental", true);
            Scribe_Values.Look(ref AutoPlayNextTrack, "AutoPlayNextTrack", false);
            Scribe_Values.Look(ref MusicSavePath, "MusicSavePath", "");

            Scribe_Values.Look(ref ForceChineseOutput, "ForceChineseOutput", true);
            Scribe_Values.Look(ref DebugMode, "DebugMode", false);
            Scribe_Values.Look(ref HUDTransparency, "HUDTransparency", 0.8f);

            Scribe_Values.Look(ref SelectColonist, "SelectColonist", true); Scribe_Values.Look(ref HoverColonist, "HoverColonist", true);
            Scribe_Values.Look(ref SelectSlave, "SelectSlave", true); Scribe_Values.Look(ref HoverSlave, "HoverSlave", true);
            Scribe_Values.Look(ref SelectPrisoner, "SelectPrisoner", true); Scribe_Values.Look(ref HoverPrisoner, "HoverPrisoner", false);
            Scribe_Values.Look(ref SelectEnemy, "SelectEnemy", true); Scribe_Values.Look(ref HoverEnemy, "HoverEnemy", false);
            Scribe_Values.Look(ref SelectNeutral, "SelectNeutral", true); Scribe_Values.Look(ref HoverNeutral, "HoverNeutral", false);

            float rectX = HUDRect.x; float rectY = HUDRect.y; float rectW = HUDRect.width; float rectH = HUDRect.height;
            Scribe_Values.Look(ref rectX, "HUD_X", 0f); Scribe_Values.Look(ref rectY, "HUD_Y", 0f);
            Scribe_Values.Look(ref rectW, "HUD_W", 0f); Scribe_Values.Look(ref rectH, "HUD_H", 0f);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (rectW != 0f && rectH != 0f) HUDRect = new Rect(rectX, rectY, rectW, rectH);
                else HUDRect = Rect.zero;
            }
            base.ExposeData();
        }
    }
}