using System;
using UnityEngine;
using Verse;
using RimWorld;
using RimMusic.Data;
using RimMusic.UI;
using System.Collections.Generic;
using RimTalk.Data;
using System.Linq;
using System.Reflection;

namespace RimMusic.Core
{
    public class RecentEvent
    {
        public string Label;
        public string Text;
        public int Timestamp;
    }

    public class MusicGameComponent : GameComponent
    {
        private Window _activeWindow;

        public static Pawn ActiveProtagonist = null;
        public static Pawn AnchorPawn = null;
        public static bool IsMacroMode = false;

        public static List<RecentEvent> EventMemory = new List<RecentEvent>();

        private static Pawn _cachedPawn = null;
        private static float _lastFocusTime = 0f;
        private static Pawn _pendingCandidate = null;
        private static float _pendingStartTime = 0f;
        private static float _macroEntryTime = 0f;
        private static float _microEntryTime = 0f;

        private static Dictionary<int, int> _pawnLastSpeechTick = new Dictionary<int, int>();
        private static Dictionary<int, int> _pawnHistoryCount = new Dictionary<int, int>();
        private int _slowUpdateTicker = 0;

        private static readonly FieldInfo _choiceLetterTextField = typeof(ChoiceLetter).GetField("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public MusicGameComponent(Game game) { }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            TriggerGenesisCeremony(true);
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            TriggerGenesisCeremony(false);
        }

        private void TriggerGenesisCeremony(bool isNewGame)
        {
            if (Current.Game == null) return;
            var musicComp = Current.Game.GetComponent<CulturalMusicComponent>();
            if (musicComp != null)
            {
                musicComp.ScanGlobalFactions();
                if (isNewGame)
                {
                    Find.WindowStack.Add(new CultureMixerWindow());
                    Messages.Message("RimMusic_GenesisComplete".Translate().ToString(), MessageTypeDefOf.PositiveEvent, false);
                }
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            ActiveProtagonist = null;
            AnchorPawn = null;
            _cachedPawn = null;
            _pendingCandidate = null;
            IsMacroMode = false;
            EventMemory.Clear();

            MusicContext.ResetStaticCache();

            // 重注推迟到 LongEventHandler 完成:此时 DefDatabase/音频系统都已就绪,
            // UnityWebRequestMultimedia 能正常加载 AudioClip。在 FinalizeInit 阶段调
            // 可能因音频系统未就绪而注入失败(重启后不可见的根因之一)。
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try { RuntimeSongRegistrar.RegisterAllFromDisk(); }
                catch (Exception ex) { Log.Warning($"[RimMusic] Runtime song re-injection skipped: {ex.Message}"); }
                // 进存档后一次性初始化自动生成起始冷却(此时 Settings 已加载、TicksGame 已就绪)
                try { AutoGenCoordinator.InitOnGameLoad(); }
                catch (Exception ex) { Log.Warning($"[RimMusic] AutoGen init skipped: {ex.Message}"); }
            });

            Log.Message("[RimMusic] Static memory purged. Context matrices reinitialized for new session.");
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();

            // HUD 悬浮窗呼出快捷键 — 完全由 Mod 选项自定义，不依赖游戏全局按键绑定配置(后者被清理会失效)。
            // 直接读取 RimMusicSettings 中持久化的主键 + 修饰键组合，每帧判定一次按下。
            if (RimMusicMod.Settings != null && HUDHotkeyJustPressed())
            {
                ToggleWindow();
            }
            UpdateGazeLogic();

            // 自动生成协调器 tick — 每帧判定冷却/威胁度/每日上限,满足条件触发生成
            try { AutoGenCoordinator.Tick(); }
            catch (Exception ex) { Log.Warning($"[RimMusic] AutoGen tick skipped: {ex.Message}"); }

            _slowUpdateTicker++;
            if (_slowUpdateTicker > 30)
            {
                UpdateSpeechTimers();
                UpdateEventMemory();
                _slowUpdateTicker = 0;
            }
        }

        private void UpdateEventMemory()
        {
            if (Find.LetterStack == null) return;

            int currentTick = Find.TickManager.TicksGame;
            var letters = Find.LetterStack.LettersListForReading;

            foreach (var l in letters)
            {
                if (!EventMemory.Any(e => e.Label == l.Label))
                {
                    string body = "";
                    if (l is ChoiceLetter cl && _choiceLetterTextField != null)
                    {
                        var val = _choiceLetterTextField.GetValue(cl);
                        if (val != null) body = val.ToString();
                    }

                    EventMemory.Add(new RecentEvent
                    {
                        Label = l.Label,
                        Text = body,
                        Timestamp = currentTick
                    });
                }
            }

            int expiryTicks = (int)(RimMusicMod.Settings.EventMemoryDuration * 60f);
            EventMemory.RemoveAll(e => currentTick - e.Timestamp > expiryTicks);
        }

        public static int GetLastSpeechTick(Pawn p)
        {
            if (p == null) return -99999;
            return _pawnLastSpeechTick.TryGetValue(p.thingIDNumber, out int tick) ? tick : -99999;
        }

        private void UpdateSpeechTimers()
        {
            if (Find.CurrentMap == null) return;
            foreach (var p in Find.CurrentMap.mapPawns.FreeColonists)
            {
                var history = TalkHistory.GetMessageHistory(p, true);
                if (history != null)
                {
                    int currentCount = history.Count;
                    _pawnHistoryCount.TryGetValue(p.thingIDNumber, out int lastCount);
                    if (currentCount > lastCount)
                    {
                        _pawnLastSpeechTick[p.thingIDNumber] = Find.TickManager.TicksGame;
                        _pawnHistoryCount[p.thingIDNumber] = currentCount;
                    }
                }
            }
        }

        private bool IsValidTarget(Pawn p, bool isHover)
        {
            if (p == null || p.Dead || p.Map != Find.CurrentMap) return false;
            if (!p.RaceProps.Humanlike && !p.RaceProps.IsMechanoid) return false;

            bool isColonist = p.IsColonist && !p.IsSlave;
            bool isSlave = p.IsSlave;
            bool isPrisoner = p.IsPrisoner;
            bool isEnemy = p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer);
            bool isNeutral = !isColonist && !isSlave && !isPrisoner && !isEnemy;

            if (isColonist) return isHover ? RimMusicMod.Settings.HoverColonist : RimMusicMod.Settings.SelectColonist;
            if (isSlave) return isHover ? RimMusicMod.Settings.HoverSlave : RimMusicMod.Settings.SelectSlave;
            if (isPrisoner) return isHover ? RimMusicMod.Settings.HoverPrisoner : RimMusicMod.Settings.SelectPrisoner;
            if (isEnemy) return isHover ? RimMusicMod.Settings.HoverEnemy : RimMusicMod.Settings.SelectEnemy;
            if (isNeutral) return isHover ? RimMusicMod.Settings.HoverNeutral : RimMusicMod.Settings.SelectNeutral;

            return false;
        }

        private void UpdateGazeLogic()
        {
            if (Find.CurrentMap == null) return;

            if (Find.Selector.SingleSelectedThing is Pawn selectedPawn && IsValidTarget(selectedPawn, false))
            {
                ActiveProtagonist = selectedPawn;
                AnchorPawn = selectedPawn;
                _cachedPawn = selectedPawn;
                _lastFocusTime = Time.time;
                _pendingCandidate = null;
                IsMacroMode = false;
                _macroEntryTime = 0f;
                return;
            }

            float currentZoom = Find.CameraDriver.CellSizePixels;

            if (currentZoom < RimMusicMod.Settings.MacroZoomThreshold)
            {
                _microEntryTime = 0f;
                if (!IsMacroMode)
                {
                    if (_macroEntryTime <= 0f) _macroEntryTime = Time.time;
                    if (Time.time - _macroEntryTime > RimMusicMod.Settings.SwitchDelay)
                    {
                        IsMacroMode = true;
                        ActiveProtagonist = null;
                        AnchorPawn = null;
                        _cachedPawn = null;
                        _pendingCandidate = null;
                    }
                }
            }
            else
            {
                _macroEntryTime = 0f;
                if (IsMacroMode)
                {
                    if (_microEntryTime <= 0f) _microEntryTime = Time.time;
                    if (Time.time - _microEntryTime > RimMusicMod.Settings.MacroExitDelay)
                    {
                        IsMacroMode = false;
                    }
                }
            }

            if (IsMacroMode) return;

            Pawn defaultFocus = null;
            if (AnchorPawn != null && !AnchorPawn.Dead && AnchorPawn.Map == Find.CurrentMap)
            {
                Vector3 screenPos = AnchorPawn.DrawPos.MapToUIPosition();
                if (screenPos.x > 0 && screenPos.x < Verse.UI.screenWidth &&
                    screenPos.y > 0 && screenPos.y < Verse.UI.screenHeight)
                {
                    defaultFocus = AnchorPawn;
                }
                else
                {
                    AnchorPawn = null;
                    defaultFocus = null;
                }
            }
            else
            {
                AnchorPawn = null;
            }

            IntVec3 mouseCell = Verse.UI.MouseCell();
            float radius = RimMusicMod.Settings.HoverRadius;

            Pawn hoverCandidate = Find.CurrentMap.mapPawns.AllPawnsSpawned
                .Where(x => IsValidTarget(x, true))
                .FirstOrDefault(x => x.Position.InHorDistOf(mouseCell, radius));

            if (hoverCandidate != null)
            {
                if (hoverCandidate == _cachedPawn)
                {
                    ActiveProtagonist = hoverCandidate;
                    _lastFocusTime = Time.time;
                    _pendingCandidate = null;
                }
                else
                {
                    if (_pendingCandidate != hoverCandidate)
                    {
                        _pendingCandidate = hoverCandidate;
                        _pendingStartTime = Time.time;
                    }
                    else
                    {
                        if (Time.time - _pendingStartTime > RimMusicMod.Settings.SwitchDelay)
                        {
                            ActiveProtagonist = hoverCandidate;
                            _cachedPawn = hoverCandidate;
                            _lastFocusTime = Time.time;
                            _pendingCandidate = null;
                        }
                    }
                }
            }
            else
            {
                _pendingCandidate = null;
                if (ActiveProtagonist == _cachedPawn && ActiveProtagonist != null)
                {
                    if (Time.time - _lastFocusTime > RimMusicMod.Settings.FocusCacheDuration)
                    {
                        ActiveProtagonist = defaultFocus;
                        _cachedPawn = defaultFocus;
                    }
                }
                else
                {
                    ActiveProtagonist = defaultFocus;
                }
            }
        }

        private void ToggleWindow()
        {
            if (_activeWindow != null && _activeWindow.IsOpen)
            {
                _activeWindow.Close();
                _activeWindow = null;
            }
            else
            {
                if (RimMusicMod.Settings.DebugMode)
                {
                    _activeWindow = new MusicInspectorWindow();
                }
                else
                {
                    _activeWindow = new MusicHUDWindow();
                }
                Find.WindowStack.Add(_activeWindow);
            }
        }

        // 判定玩家是否刚刚按下 HUD 呼出快捷键(主键 + 必需的修饰键)。
        // 主键用 Input.GetKeyDown(每帧一次瞬时触发，避免按住时反复开关)；修饰键用 Input.GetKey(持续按下即视为按住)。
        private bool HUDHotkeyJustPressed()
        {
            var s = RimMusicMod.Settings;
            if (s == null) return false;

            // 主键越界保护(配置被清理读到非法值时回退到 M)
            int code = s.HUDHotkeyKeyCode;
            if (code <= 0) code = 39; // KeyCode.M

            if (!Input.GetKeyDown((KeyCode)code)) return false;

            if (s.HUDHotkeyRequireCtrl &&
                !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return false;
            if (s.HUDHotkeyRequireShift &&
                !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return false;
            if (s.HUDHotkeyRequireAlt &&
                !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))) return false;

            return true;
        }
    }
}