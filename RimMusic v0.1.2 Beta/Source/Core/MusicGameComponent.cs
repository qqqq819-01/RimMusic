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

            Log.Message("[RimMusic] Static memory purged. Context matrices reinitialized for new session.");
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            if (KeyBindingDefNamed.OpenRimMusicInspector.JustPressed)
            {
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) ToggleWindow();
            }
            UpdateGazeLogic();

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
    }

    [DefOf] public static class KeyBindingDefNamed { public static KeyBindingDef OpenRimMusicInspector; }
}