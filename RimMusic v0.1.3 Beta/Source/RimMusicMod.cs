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

            Log.Message("[RimMusic] Initialization sequence complete. AI Audio Engine v0.1.3 Beta online.");
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
        // 战歌紧张线:≥此值进入高BPM战歌(~150),但仍保留文化乐器(仅紧张度提升,不全面接管)。
        // 独立于末日抢夺线:后者≥0.90才完全接管(Heavy/EDM,丢弃文化乐器)。
        public float ThreatHighTempoThreshold = 0.75f;
        public float ThreatDominateThreshold = 0.90f;

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
        public string MusicSavePath = "";

        // Audio backend selector + MiniMax Web (free-quota) parameters
        // 0 = 302.ai (Suno, paid), 1 = MiniMax Web (free, player's own account quota),
        // 2 = Flow Music Web (Google Labs, free, player's own account quota)
        public int AudioBackendMode = 0;
        public string MiniMaxWebToken = "";
        public string MiniMaxWebCookie = "";
        public string MiniMaxDeviceId = "";
        public int MiniMaxYyVariant = 0;

        // Flow Music (flowmusic.app) Web backend — Supabase refresh_token is the only
        // long-lived credential needed. Copied from the browser's Network tab when
        // logging in via Google. Rotated automatically by the backend on each refresh.
        public string FlowMusicRefreshToken = "";

        public bool ForceChineseOutput = true;
        public bool DebugMode = false;
        public float HUDTransparency = 0.8f;

        // 注入原版的 AI 曲在原版随机池里的权重(commonality)。
        // 原版曲权重为 0, AI 曲权重 > 0 即可被 RandomElementByWeight 选中。
        // 默认 2f: AI 曲优先于原版但不至于彻底压倒。玩家可在 Mod 选项调。
        public float InjectedSongWeight = 2f;

        // ================================================================
        // 自动生成 (Auto Gen) 参数
        // ================================================================
        // 触发阈值:威胁度 >= 此值 且 处于袭击(原版会放战时音乐的场景)才生成袭击曲。
        // 低于此值视为和平期。默认 0.30f(与 ThreatStealThreshold 同语义,但独立设便于调)。
        public float AutoGenThreatThreshold = 0.30f;

        // 和平期生成冷却(秒),默认 40 分钟。袭击生成冷却(秒),默认 5 分钟。分开独立计时。
        public float AutoGenPeaceCooldown = 40f * 60f;
        public float AutoGenWarCooldown = 5f * 60f;

        // 每日(游戏内一天)自动生成上限,默认 3 次。可在 Mod 选项调。
        public int AutoGenDailyLimit = 3;

        // 进入存档后和平期冷却默认起始 5 分钟(秒)。袭击冷却默认起始 0。
        public float AutoGenPeaceInitialCooldown = 5f * 60f;
        public float AutoGenWarInitialCooldown = 0f;

        // 新生成曲临时极高权重,保证下一首加权随机必中。播放开始一次后立刻恢复。
        // 注意:原版 ChooseNextSong 末尾用 RandomElementByWeight(s => s.commonality) 选曲,
        // 这是**加权随机抽样**,不是"取权重最高"。boost 曲被选中概率 = boost / (boost + 池内其他曲权重和)。
        // 池里可能有几十首权重 2f 的 AI 曲,总权重可达上百;若 boost 仅 1000f,漏选率约 9%,
        // 表现为"切 1-2 首才切到刚生成的"。故 boost 必须远大于池总权重,使漏选率趋近 0。
        // 100000f:即便池里 100 首 AI 曲(总权重 200),漏选率也仅 0.2%,实际场景下必中。
        public float AutoGenTemporaryBoostWeight = 100000f;

        // 生成完成后立刻切歌(让原版 StartNewSong 选下一首,boost 权重会选中刚生成的)。
        // 关=只注入不切,等原版自然切换时才选到;开=生成完立即切到刚生成的曲。
        public bool AutoGenSwitchOnComplete = false;

        // HUD 悬浮窗呼出快捷键 — 由 Mod 选项自定义，持久化在 Mod 自己的设置文件中，
        // 不依赖 RimWorld 全局按键绑定配置(后者被清理时游戏自带快捷键会失效)。
        // 默认 Ctrl + M (KeyCode.M == 39)。
        public int HUDHotkeyKeyCode = 39; // KeyCode.M
        public bool HUDHotkeyRequireCtrl = true;
        public bool HUDHotkeyRequireShift = false;
        public bool HUDHotkeyRequireAlt = false;

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
                // 默认存 mod 文件夹内 Generated/ 子目录——不依赖游戏 Config,玩家分发 mod 即带测试曲
                try
                {
                    var meta = ModLister.GetActiveModWithIdentifier("qm.rimmusic", true);
                    path = meta != null ? Path.Combine(meta.RootDir.FullName, "Generated") : Path.Combine(GenFilePaths.ConfigFolderPath, "RimMusic_Generations");
                }
                catch { path = Path.Combine(GenFilePaths.ConfigFolderPath, "RimMusic_Generations"); }
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
            Scribe_Values.Look(ref ThreatHighTempoThreshold, "ThreatHighTempoThreshold", 0.75f);
            Scribe_Values.Look(ref ThreatDominateThreshold, "ThreatDominateThreshold", 0.90f);

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
            Scribe_Values.Look(ref MusicSavePath, "MusicSavePath", "");

            Scribe_Values.Look(ref AudioBackendMode, "AudioBackendMode", 0);
            Scribe_Values.Look(ref MiniMaxWebToken, "MiniMaxWebToken", "");
            Scribe_Values.Look(ref MiniMaxWebCookie, "MiniMaxWebCookie", "");
            Scribe_Values.Look(ref MiniMaxDeviceId, "MiniMaxDeviceId", "");
            Scribe_Values.Look(ref MiniMaxYyVariant, "MiniMaxYyVariant", 0);
            Scribe_Values.Look(ref FlowMusicRefreshToken, "FlowMusicRefreshToken", "");

            Scribe_Values.Look(ref ForceChineseOutput, "ForceChineseOutput", true);
            Scribe_Values.Look(ref DebugMode, "DebugMode", false);
            Scribe_Values.Look(ref HUDTransparency, "HUDTransparency", 0.8f);
            Scribe_Values.Look(ref InjectedSongWeight, "InjectedSongWeight", 2f);

            // 自动生成参数持久化
            Scribe_Values.Look(ref AutoGenThreatThreshold, "AutoGenThreatThreshold", 0.30f);
            Scribe_Values.Look(ref AutoGenPeaceCooldown, "AutoGenPeaceCooldown", 40f * 60f);
            Scribe_Values.Look(ref AutoGenWarCooldown, "AutoGenWarCooldown", 5f * 60f);
            Scribe_Values.Look(ref AutoGenDailyLimit, "AutoGenDailyLimit", 3);
            Scribe_Values.Look(ref AutoGenPeaceInitialCooldown, "AutoGenPeaceInitialCooldown", 5f * 60f);
            Scribe_Values.Look(ref AutoGenWarInitialCooldown, "AutoGenWarInitialCooldown", 0f);
            Scribe_Values.Look(ref AutoGenTemporaryBoostWeight, "AutoGenTemporaryBoostWeight", 100000f);
            Scribe_Values.Look(ref AutoGenSwitchOnComplete, "AutoGenSwitchOnComplete", false);

            // HUD 悬浮窗快捷键 — 持久化到 Mod 自己的设置文件，独立于游戏全局按键绑定配置
            Scribe_Values.Look(ref HUDHotkeyKeyCode, "HUDHotkeyKeyCode", 39); // KeyCode.M
            Scribe_Values.Look(ref HUDHotkeyRequireCtrl, "HUDHotkeyRequireCtrl", true);
            Scribe_Values.Look(ref HUDHotkeyRequireShift, "HUDHotkeyRequireShift", false);
            Scribe_Values.Look(ref HUDHotkeyRequireAlt, "HUDHotkeyRequireAlt", false);

            // 兜底：Config 全清后若读到非法值(0/越界)，回退到默认 Ctrl+M
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (HUDHotkeyKeyCode <= 0) HUDHotkeyKeyCode = 39;
            }

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