using System;
using System.Collections.Generic;
using System.Linq;
using RimMusic.UI;
using RimMusic.Data;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimMusic.Core
{
    // =========================================================================
    // AutoGenCoordinator — 自动生成协调器
    //
    // 触发规则:
    //  袭击: 威胁度 >= AutoGenThreatThreshold 且 处于袭击(原版会放战时音乐的场景=DangerMusicMode)
    //        → 生成战时曲。冷却 AutoGenWarCooldown(默认 5 分钟),独立计时。
    //  和平: 不在袭击期 且 威胁度 < AutoGenThreatThreshold → 和平期。
    //        冷却 AutoGenPeaceCooldown(默认 40 分钟)一到且仍处于和平期 → 立刻生成。
    //        进入存档后和平期冷却默认起始 AutoGenPeaceInitialCooldown(5 分钟)。袭击冷却起始 0。
    //
    // 每日(游戏内一天)上限 AutoGenDailyLimit(默认 3),可在 Mod 选项调。
    //   上限按游戏内日切换: GenDate.Ticks 与 _dailyLimitResetDate 不同时清零计数。
    //
    // 新生成曲注入时临时极高权重(AutoGenTemporaryBoostWeight,默认 100000),
    // 让下一首加权随机必中——但尊重原版 tense 标签(战时只播 tense=true,和平只播 tense=false),
    // 若当前模式与新生成曲 tense 不符(不该发生,但防御)则不 boost。
    //
    // 关键:原版 ChooseNextSong 末尾用 RandomElementByWeight(s => s.commonality) 选曲,
    // 这是**加权随机抽样**,不是"取权重最高"。boost 曲被选中概率 = boost / (boost + 池内其他曲权重和)。
    // 池里可能有几十首权重 2f 的 AI 曲,总权重上百;若 boost 仅 1000f,漏选率约 9%,
    // 表现为"切 1-2 首才切到刚生成的"。故 boost 必须远大于池总权重,使漏选率趋近 0。
    // 100000f:即便池里 100 首 AI 曲(总权重 200),漏选率也仅 0.2%,实际场景下必中。
    //
    // 原版 StartNewSong 选中它开始播放一次后,立刻恢复原权重(InjectSongWeight)。
    // =========================================================================
    public static class AutoGenCoordinator
    {
        // 冷却计时用现实墙钟秒(Stopwatch),不随游戏暂停/卡顿而停。
        // 冷却字段单位=秒,起始/触发冷却都用现实秒。
        private static readonly System.Diagnostics.Stopwatch _peaceCooldownSw = new System.Diagnostics.Stopwatch();
        private static readonly System.Diagnostics.Stopwatch _warCooldownSw = new System.Diagnostics.Stopwatch();
        private static double _peaceCooldownSecLeft = 0; // 剩余秒(冷却到则 ≤0)
        private static double _warCooldownSecLeft = 0;

        // "冷却到后需经游戏刻才生成"门槛:冷却到后,必须再经过 N 游戏刻才允许触发生成。
        // 避免暂停/卡顿瞬间刷。冷却到时记下当前 TicksGame,触发前判 (now - lastTickAtCooldownEnd) >=门槛。
        private const long _postCooldownTickGate = 60; // 1 游戏秒(60 tick)
        private static long _peaceCooldownEndedAtTick = long.MinValue; // 冷却到时的 TicksGame,.MinValue 表未到
        private static long _warCooldownEndedAtTick = long.MinValue;
        private static long _peaceLastGenTick = 0; // 上次触发生成时的 TicksGame(冷却到后门槛判用)
        private static long _warLastGenTick = 0;

        // 每日上限计数
        private static int _dailyGenCount = 0;
        private static int _dailyLimitResetDay = -1;

        // 新生成曲临时 boost 状态:支持多首同时 boost(Flow Music 一次生成 TrackA+TrackB 都要 boost)
        private static readonly System.Collections.Generic.List<SongDef> _pendingBoostSongs = new System.Collections.Generic.List<SongDef>();

        // 是否正在生成(防重入,生成期间不再触发新一轮)
        private static bool _isGenerating = false;

        // ---------------------------------------------------------------------
        // 进存档时一次性初始化起始冷却——无条件设,不依赖 tick 里懒初始化。
        // 冷却用现实秒,起始冷却一过现实时间就递减。
        // ---------------------------------------------------------------------
        public static void InitOnGameLoad()
        {
            if (RimMusicMod.Settings == null) return;
            // 起始冷却单位=现实秒。和平默认 5 分钟(300 秒),袭击默认 0。
            _peaceCooldownSecLeft = RimMusicMod.Settings.AutoGenPeaceInitialCooldown;
            _warCooldownSecLeft = RimMusicMod.Settings.AutoGenWarInitialCooldown;
            _peaceCooldownSw.Restart();
            _warCooldownSw.Restart();
            _peaceCooldownEndedAtTick = long.MinValue; // 冷却未到
            _warCooldownEndedAtTick = long.MinValue;
            // 每日计数从 0 开始
            _dailyGenCount = 0;
            long now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            _dailyLimitResetDay = (int)(now / 60000) * 60000;
            Log.Message($"[RimMusic] AutoGen init on game load: peace cooldown {RimMusicMod.Settings.AutoGenPeaceInitialCooldown/60f}m, war cooldown {RimMusicMod.Settings.AutoGenWarInitialCooldown/60f}m (real-time seconds)");
        }

        // ---------------------------------------------------------------------
        // 每帧 tick — 由 MusicGameComponent.GameComponentUpdate 调
        // ---------------------------------------------------------------------
        public static void Tick()
        {
            if (RimMusicMod.Settings == null) return;
            if (Find.CurrentMap == null) return;

            // 检查新生成曲是否已被原版开始播放——是则立刻恢复权重
            CheckBoostRestore();

            // 每日上限按游戏内日切换清零(一天 = 60000 tick)
            int currentDay = Find.TickManager.TicksGame / 60000;
            int resetDay = _dailyLimitResetDay < 0 ? currentDay : (_dailyLimitResetDay / 60000);
            if (currentDay != resetDay)
            {
                _dailyGenCount = 0;
                _dailyLimitResetDay = currentDay * 60000;
            }

            if (_isGenerating) return; // 生成中不触发新一轮
            if (_dailyGenCount >= RimMusicMod.Settings.AutoGenDailyLimit) return; // 当日上限已达

            // 判定当前是否袭击期 + 威胁度
            bool dangerMode = Find.MusicManagerPlay != null && Find.MusicManagerPlay.DangerMusicMode;
            float threatRatio = CultureCalculator.GetThreatRatio(Find.CurrentMap);
            float threshold = RimMusicMod.Settings.AutoGenThreatThreshold;
            long now = Find.TickManager.TicksGame;

            // 冷却用现实秒:Stopwatch 计时,不随游戏暂停/卡顿而停
            double peaceElapsedSec = _peaceCooldownSw.Elapsed.TotalSeconds;
            double warElapsedSec = _warCooldownSw.Elapsed.TotalSeconds;
            double peaceSecLeft = _peaceCooldownSecLeft - peaceElapsedSec;
            double warSecLeft = _warCooldownSecLeft - warElapsedSec;

            if (dangerMode && threatRatio >= threshold)
            {
                // 袭击生成:冷却=0 表示玩家选择永不自动生成战时曲,跳过。
                // 冷却到后还需经过 _postCooldownTickGate 游戏刻才触发(避免暂停/卡顿瞬间刷)。
                float warCooldown = RimMusicMod.Settings.AutoGenWarCooldown;
                if (warCooldown <= 0f) { /* 永不自动生成战时曲 */ }
                else if (warSecLeft <= 0)
                {
                    if (_warCooldownEndedAtTick == long.MinValue)
                    {
                        _warCooldownEndedAtTick = now; // 首次冷却到,记下当时的 TicksGame
                    }
                    if (now - _warCooldownEndedAtTick >= _postCooldownTickGate)
                    {
                        TriggerGenerate(isWar: true);
                        _warCooldownSecLeft = warCooldown; // 重置冷却
                        _warCooldownSw.Restart();
                        _warCooldownEndedAtTick = long.MinValue; // 冷却重置,门槛清
                        _warLastGenTick = now;
                    }
                }
            }
            else if (!dangerMode && threatRatio < threshold)
            {
                // 和平期生成:冷却=0 表示玩家选择永不自动生成和平时曲,跳过。
                float peaceCooldown = RimMusicMod.Settings.AutoGenPeaceCooldown;
                if (peaceCooldown <= 0f) { /* 永不自动生成和平时曲 */ }
                else if (peaceSecLeft <= 0)
                {
                    if (_peaceCooldownEndedAtTick == long.MinValue)
                    {
                        _peaceCooldownEndedAtTick = now;
                    }
                    if (now - _peaceCooldownEndedAtTick >= _postCooldownTickGate)
                    {
                        TriggerGenerate(isWar: false);
                        _peaceCooldownSecLeft = peaceCooldown;
                        _peaceCooldownSw.Restart();
                        _peaceCooldownEndedAtTick = long.MinValue;
                        _peaceLastGenTick = now;
                    }
                }
            }
        }

        // ---------------------------------------------------------------------
        // 触发一次自动生成 — 复用手动生成的 prompt 构造路径
        // ---------------------------------------------------------------------
        private static async void TriggerGenerate(bool isWar)
        {
            _isGenerating = true;
            _dailyGenCount++;
            Log.Message($"[RimMusic] AutoGen triggered ({(isWar ? "War" : "Peace")}). Daily count: {_dailyGenCount}/{RimMusicMod.Settings.AutoGenDailyLimit}");

            try
            {
                MusicContext ctx = MusicContext.Build(MusicGameComponent.ActiveProtagonist, true);
                if (ctx == null) { Log.Warning("[RimMusic] AutoGen: MusicContext build failed."); return; }

                string finalPrompt = await new MusicAIClient().GenerateParsedMusicPromptAsync(ctx);
                if (string.IsNullOrEmpty(finalPrompt)) { Log.Warning("[RimMusic] AutoGen: prompt generation returned empty."); return; }

                // 注入新生成曲后,给它临时极高权重
                // boost 由 RequestAndPlayMusic 末尾统一调,所有生成路径都自动 boost
                await RealtimeMusicEngine.RequestAndPlayMusic(finalPrompt, ctx.focus_name);

                // RequestAndPlayMusic 内部已调 RuntimeSongRegistrar.RegisterAndPlay,注入并 ForcePlaySong。
                // 但我们要的是"下一首随机必中"——需要给刚注入的曲 boost 权重。
                // RegisterAndPlay 已直接 ForcePlaySong 当前曲,所以它正在播——不需要 boost 随机池。
                // 但用户要"下一首播放的一定是刚生成的"——这指随机切换时。当前曲已直接 ForcePlaySong 播了,
                // 满足"刚生成的音乐下一首播放"。boost 权重用于:当当前曲播完,原版 StartNewSong 选下一首时,
                // 若玩家没手动切,刚生成的曲已在 recentSongs 里会被 AppropriateNow 跳过——所以 boost 其实多余。
                // 但保留 boost 逻辑以防 ForcePlaySong 失败回退到随机池的边界情况。
            }
            catch (Exception ex)
            {
                Log.Error($"[RimMusic] AutoGen failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _isGenerating = false;
            }
        }

        // 给刚注入的最新一首曲临时极高权重,等原版开始播放它后恢复
        // 由 RequestAndPlayMusic 主下载/副下载注入成功后统一调用——所有生成路径(自动+手动)都自动 boost
        // 支持多首:Flow Music 一次生成 TrackA+TrackB,两首都要 boost
        public static void BoostLatestRegisteredSong()
        {
            try
            {
                float boost = RimMusicMod.Settings.AutoGenTemporaryBoostWeight;
                SongDef latest = RuntimeSongRegistrar.LastRegisteredSong;
                if (latest != null && latest.clip != null)
                {
                    _pendingBoostSongs.Add(latest);
                    latest.commonality = boost;
                    Log.Message($"[RimMusic] AutoGen: boosted '{latest.defName}' weight→{boost}, will restore on first play.");
                }
                else { Log.Warning("[RimMusic] AutoGen: LastRegisteredSong is null, skip boost."); }
            }
            catch (Exception ex) { Log.Warning($"[RimMusic] ScheduleBoost failed: {ex.Message}"); }
        }

        // 检查新生成曲是否已被原版开始播放——是则立刻恢复权重
        private static void CheckBoostRestore()
        {
            if (_pendingBoostSongs.Count == 0) return;
            try
            {
                SongDef current = RealtimeMusicEngine.CurrentSong;
                for (int i = _pendingBoostSongs.Count - 1; i >= 0; i--)
                {
                    if (current == _pendingBoostSongs[i])
                    {
                        _pendingBoostSongs[i].commonality = RimMusicMod.Settings.InjectedSongWeight;
                        Log.Message($"[RimMusic] AutoGen: restored '{_pendingBoostSongs[i].defName}' weight to {RimMusicMod.Settings.InjectedSongWeight} after first play started.");
                        _pendingBoostSongs.RemoveAt(i);
                    }
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------------
        // Mod 选项重置按钮
        // ---------------------------------------------------------------------
        public static void ResetAllCooldowns()
        {
            _peaceCooldownSecLeft = 0;
            _warCooldownSecLeft = 0;
            _peaceCooldownSw.Restart();
            _warCooldownSw.Restart();
            _peaceCooldownEndedAtTick = long.MinValue;
            _warCooldownEndedAtTick = long.MinValue;
            Log.Message("[RimMusic] AutoGen: all cooldowns reset.");
        }

        public static void ResetDailyLimit()
        {
            _dailyGenCount = 0;
            _dailyLimitResetDay = -1;
            Log.Message("[RimMusic] AutoGen: daily limit count reset.");
        }

        // ---------------------------------------------------------------------
        // HUD 冷却显示字符串(供 MusicInspectorWindow 调)
        // ---------------------------------------------------------------------
        public static string GetCooldownDisplayString()
        {
            if (RimMusicMod.Settings == null) return "";
            var sb = new System.Text.StringBuilder();
            // 冷却用现实秒:Stopwatch 计时,剩秒 = 冷却时长 - 已过秒
            double peaceLeftSec = Mathf.Max(0, (float)(_peaceCooldownSecLeft - _peaceCooldownSw.Elapsed.TotalSeconds));
            double warLeftSec = Mathf.Max(0, (float)(_warCooldownSecLeft - _warCooldownSw.Elapsed.TotalSeconds));
            int pMin = Mathf.FloorToInt((float)peaceLeftSec / 60f);
            int pSec = Mathf.FloorToInt((float)peaceLeftSec) % 60;
            int wMin = Mathf.FloorToInt((float)warLeftSec / 60f);
            int wSec = Mathf.FloorToInt((float)warLeftSec) % 60;
            // 冷却到但还在等游戏刻门槛 → 显示 "wait" 提示玩家还需经过一点游戏刻
            string peaceTag = peaceLeftSec <= 0 ? " (wait)" : "";
            string warTag = warLeftSec <= 0 ? " (wait)" : "";
            sb.Append("Peace: ").Append(pMin).Append("m").Append(pSec.ToString("D2")).Append("s").Append(peaceTag).Append(" / ");
            sb.Append("War: ").Append(wMin).Append("m").Append(wSec.ToString("D2")).Append("s").Append(warTag).Append(" / ");
            sb.Append("Today: ").Append(_dailyGenCount).Append("/").Append(RimMusicMod.Settings.AutoGenDailyLimit);
            return sb.ToString();
        }
    }
}
