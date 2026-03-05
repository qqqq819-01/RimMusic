using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using RimMusic.Data;
using RimMusic.Core;

namespace RimMusic.UI
{
    [StaticConstructorOnStartup]
    public class CultureMixerWindow : Window
    {
        private CulturalMusicComponent _musicComp;
        private KeyValuePair<string, CulturalRoster> _selectedRoster;
        private Vector2 _leftScrollPos;
        private Vector2 _rightScrollPos;

        private List<KeyValuePair<string, CulturalRoster>> _cachedSortedRosters = new List<KeyValuePair<string, CulturalRoster>>();
        private List<string> _cachedDisplayNames = new List<string>();
        private List<bool> _cachedIsPlayer = new List<bool>();

        private Dictionary<string, int> _autoRerollRetries = new Dictionary<string, int>();

        private enum LeftTab { Culture, RaceFaction, Character }
        private LeftTab _currentLeftTab = LeftTab.Culture;

        public override Vector2 InitialSize => new Vector2(920f, 680f);

        public CultureMixerWindow()
        {
            this.draggable = true;
            this.resizeable = false;
            this.doCloseX = true;
            this.closeOnAccept = false;
            this.closeOnCancel = true;
            this.forcePause = false;
            this.absorbInputAroundWindow = false;
            this.layer = WindowLayer.Dialog;
            this.preventCameraMotion = false;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            if (Current.Game != null)
            {
                _musicComp = Current.Game.GetComponent<CulturalMusicComponent>();
                BuildAndSortRosterList();
            }
        }

        private void BuildAndSortRosterList()
        {
            _cachedSortedRosters.Clear();
            _cachedDisplayNames.Clear();
            _cachedIsPlayer.Clear();

            if (_musicComp == null) return;

            var rostersDict = _musicComp.GetAllRosters();
            if (rostersDict == null || rostersDict.Count == 0) return;

            HashSet<string> playerIds = new HashSet<string>();

            if (Faction.OfPlayer != null)
            {
                playerIds.Add("Faction_" + Faction.OfPlayer.def.defName);
            }

            foreach (Pawn p in PawnsFinder.AllMaps_FreeColonists)
            {
                if (ModsConfig.IdeologyActive && p.ideo?.Ideo != null)
                {
                    playerIds.Add("Ideo_" + p.ideo.Ideo.id.ToString());
                }
            }

            var rawList = rostersDict.ToList();
            _cachedSortedRosters = rawList.OrderByDescending(kvp => playerIds.Contains(kvp.Key) ? 1 : 0)
                                           .ThenBy(kvp => kvp.Key).ToList();

            for (int i = 0; i < _cachedSortedRosters.Count; i++)
            {
                var kvp = _cachedSortedRosters[i];
                string rawId = kvp.Key;
                bool isPlayer = playerIds.Contains(rawId);
                string displayName = rawId;
                string prefix = "";

                if (rawId.StartsWith("Ideo_"))
                {
                    displayName = "RimMusic_Resolving".Translate();
                    if (ModsConfig.IdeologyActive && Find.IdeoManager != null)
                    {
                        if (int.TryParse(rawId.Substring(5), out int ideoId))
                        {
                            var ideo = Find.IdeoManager.IdeosListForReading.FirstOrDefault(idx => idx.id == ideoId);
                            if (ideo != null) displayName = ideo.name;
                        }
                    }
                    prefix = isPlayer ? "RimMusic_Prefix_Player".Translate().ToString() : "RimMusic_Prefix_Culture".Translate().ToString();
                }
                else if (rawId.StartsWith("Faction_"))
                {
                    displayName = "RimMusic_Resolving".Translate();
                    if (Find.FactionManager != null)
                    {
                        var faction = Find.FactionManager.AllFactions.FirstOrDefault(f => f.def.defName == rawId.Substring(8));
                        if (faction != null) displayName = faction.Name;
                    }
                    prefix = isPlayer ? "RimMusic_Prefix_Player".Translate().ToString() : "RimMusic_Prefix_Faction".Translate().ToString();
                }
                else if (rawId.StartsWith("Pawn_"))
                {
                    string thingId = rawId.Substring(5);
                    var pawn = PawnsFinder.AllMapsWorldAndTemporary_Alive.FirstOrDefault(p => p.ThingID == thingId);

                    displayName = pawn != null ? pawn.Name.ToStringShort : "RimMusic_UnknownPawn".Translate().ToString();
                    prefix = "RimMusic_Prefix_Protagonist".Translate().ToString();
                }
                else if (rawId.StartsWith("OC_"))
                {
                    displayName = rawId.Substring(3);
                    prefix = "RimMusic_Prefix_Chosen".Translate().ToString();
                }

                _cachedIsPlayer.Add(isPlayer);
                _cachedDisplayNames.Add(prefix + displayName);
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (_musicComp == null)
            {
                Widgets.Label(inRect, "RimMusic_Error_NoSaveLoaded".Translate());
                return;
            }

            Rect headerRect = inRect.TopPartPixels(40f);
            Text.Font = GameFont.Medium;
            Widgets.Label(headerRect, "RimMusic_Mixer_Title".Translate());
            Text.Font = GameFont.Small;
            Widgets.DrawLineHorizontal(inRect.x, headerRect.yMax, inRect.width);

            Rect workArea = new Rect(inRect.x, headerRect.yMax + 10f, inRect.width, inRect.height - 50f - 40f);
            float leftWidth = 280f;
            Rect leftRect = new Rect(workArea.x, workArea.y, leftWidth, workArea.height);
            Rect rightRect = new Rect(workArea.x + leftWidth + 15f, workArea.y, workArea.width - leftWidth - 15f, workArea.height);

            DrawLeftPanel(leftRect);
            DrawRightPanel(rightRect);

            Rect bottomRect = new Rect(inRect.x, inRect.yMax - 30f, inRect.width, 30f);
            Widgets.DrawLineHorizontal(inRect.x, bottomRect.y - 10f, inRect.width);

            if (Widgets.ButtonText(new Rect(bottomRect.x, bottomRect.y, 160f, 30f), "RimMusic_Mixer_PurgeMemory".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RimMusic_Mixer_PurgeConfirm".Translate(), () =>
                {
                    _musicComp.ResetAllRosters();
                    _autoRerollRetries.Clear();
                    _selectedRoster = default;
                    BuildAndSortRosterList();
                    Messages.Message("RimMusic_Mixer_PurgeDone".Translate(), MessageTypeDefOf.TaskCompletion, false);
                }));
            }

            if (Widgets.ButtonText(new Rect(bottomRect.x + 170f, bottomRect.y, 160f, 30f), "RimMusic_Mixer_Rescan".Translate()))
            {
                _musicComp.ScanGlobalFactions();
                _selectedRoster = default;
                BuildAndSortRosterList();
                Messages.Message("RimMusic_Mixer_RescanDone".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }

            Rect autoRect = new Rect(bottomRect.x + 340f, bottomRect.y, 220f, 30f);
            if (MusicAIClient.IsCircuitTripped)
            {
                GUI.color = new Color(1f, 0.4f, 0.4f);
                if (Widgets.ButtonText(autoRect, "RimMusic_Mixer_ResetCircuit".Translate())) MusicAIClient.ResetCircuit();
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = new Color(1f, 0.9f, 0.4f);
                if (Widgets.ButtonText(autoRect, "RimMusic_Mixer_BatchOptimize".Translate())) TriggerAutoReroll();
                GUI.color = Color.white;
            }
        }

        private void TriggerAutoReroll()
        {
            int threshold = RimMusicMod.Settings?.MinMatchScore ?? 2;
            int purgedCount = 0;
            int stubbornCount = 0;

            var rostersDict = _musicComp.GetAllRosters();
            if (rostersDict == null) return;

            List<CulturalRoster> toReroll = new List<CulturalRoster>();

            foreach (var kvp in _cachedSortedRosters)
            {
                var roster = kvp.Value;
                int score = roster.lastMatchScore;

                string hashKey = CultureCalculator.GetHashForSourceId(roster.sourceId);
                var cacheData = CultureOracleCache.GetData(hashKey);
                bool isOverridden = cacheData != null && cacheData.OverrideProfile != null && cacheData.OverrideProfile.IsActive;

                if (isOverridden || score == 9999) continue;

                if (score >= 0 && score < threshold && score != 999 && !roster.isPendingOracle)
                {
                    if (!_autoRerollRetries.ContainsKey(roster.sourceId)) _autoRerollRetries[roster.sourceId] = 0;
                    if (_autoRerollRetries[roster.sourceId] >= 3) stubbornCount++;
                    else
                    {
                        _autoRerollRetries[roster.sourceId]++;
                        toReroll.Add(roster);
                    }
                }
            }

            if (toReroll.Count == 0)
            {
                if (stubbornCount > 0) Messages.Message("RimMusic_Mixer_OptimizeStubborn".Translate(stubbornCount), MessageTypeDefOf.NeutralEvent, false);
                else Messages.Message("RimMusic_Mixer_OptimizeNoTarget".Translate(), MessageTypeDefOf.PositiveEvent, false);
                return;
            }

            foreach (var roster in toReroll)
            {
                string hashKey = CultureCalculator.GetHashForSourceId(roster.sourceId);
                CultureOracleCache.EvictData(hashKey);
                _musicComp.RemoveRoster(roster.sourceId);
                purgedCount++;
            }

            _musicComp.ScanGlobalFactions();
            _selectedRoster = default;
            BuildAndSortRosterList();
            Messages.Message("RimMusic_Mixer_OptimizeProcessing".Translate(purgedCount), MessageTypeDefOf.PositiveEvent, false);
        }

        private bool DrawCustomTabButton(Rect rect, string label, bool isSelected, bool isDisabled = false)
        {
            if (isSelected) Widgets.DrawBoxSolid(rect, new Color(0.2f, 0.4f, 0.6f, 0.8f));
            else if (!isDisabled && Mouse.IsOver(rect)) Widgets.DrawBoxSolid(rect, new Color(0.3f, 0.3f, 0.3f, 0.5f));
            else Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f, 0.8f));

            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = isDisabled ? Color.gray : (isSelected ? Color.white : new Color(0.8f, 0.8f, 0.8f));
            Widgets.Label(rect, isSelected ? $"<b>{label}</b>" : label);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            return !isDisabled && Widgets.ButtonInvisible(rect);
        }

        private void DrawLeftPanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.15f, 0.15f, 0.15f));

            Rect tabRect = new Rect(rect.x, rect.y, rect.width, 30f);
            float tabWidth = rect.width / 3f;

            if (DrawCustomTabButton(new Rect(tabRect.x, tabRect.y, tabWidth, 30f), "RimMusic_Tab_Culture".Translate(), _currentLeftTab == LeftTab.Culture))
            {
                _currentLeftTab = LeftTab.Culture;
                _selectedRoster = default;
            }
            if (DrawCustomTabButton(new Rect(tabRect.x + tabWidth, tabRect.y, tabWidth, 30f), "RimMusic_Tab_RaceFaction".Translate(), _currentLeftTab == LeftTab.RaceFaction))
            {
                _currentLeftTab = LeftTab.RaceFaction;
                _selectedRoster = default;
            }
            if (DrawCustomTabButton(new Rect(tabRect.x + tabWidth * 2f, tabRect.y, tabWidth, 30f), "RimMusic_Tab_Protagonist".Translate(), _currentLeftTab == LeftTab.Character))
            {
                _currentLeftTab = LeftTab.Character;
                _selectedRoster = default;
            }

            Rect listRect = new Rect(rect.x, tabRect.yMax + 10f, rect.width, rect.height - 40f);

            if (_currentLeftTab == LeftTab.Character)
            {
                if (Widgets.ButtonText(new Rect(listRect.x + 5f, listRect.y, listRect.width - 10f, 30f), "RimMusic_Mixer_ScanColonists".Translate()))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (Pawn p in PawnsFinder.AllMaps_FreeColonists)
                    {
                        Pawn localPawn = p;
                        options.Add(new FloatMenuOption(localPawn.Name.ToStringShort, () => AddPawnToRoster(localPawn)));
                    }
                    if (options.Count == 0) options.Add(new FloatMenuOption("RimMusic_Mixer_NoColonistFound".Translate(), null));
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                listRect.yMin += 35f;
            }

            int visibleCount = 0;
            foreach (var kvp in _cachedSortedRosters)
            {
                if (_currentLeftTab == LeftTab.Culture && !kvp.Key.StartsWith("Ideo_")) continue;
                if (_currentLeftTab == LeftTab.RaceFaction && !kvp.Key.StartsWith("Faction_")) continue;
                if (_currentLeftTab == LeftTab.Character && !kvp.Key.StartsWith("Pawn_") && !kvp.Key.StartsWith("OC_")) continue;
                visibleCount++;
            }

            if (visibleCount == 0)
            {
                Widgets.Label(listRect.ContractedBy(10f), "RimMusic_Mixer_NoData".Translate());
                return;
            }

            Rect viewRect = new Rect(0, 0, listRect.width - 16f, visibleCount * 35f);
            Widgets.BeginScrollView(listRect, ref _leftScrollPos, viewRect);

            float y = 0;
            int threshold = RimMusicMod.Settings?.MinMatchScore ?? 2;
            int targetInstrumentCount = RimMusicMod.Settings?.TargetInstrumentCount ?? 5;

            for (int i = 0; i < _cachedSortedRosters.Count; i++)
            {
                var kvp = _cachedSortedRosters[i];
                CulturalRoster roster = kvp.Value;
                string rawId = kvp.Key;

                if (_currentLeftTab == LeftTab.Culture && !rawId.StartsWith("Ideo_")) continue;
                if (_currentLeftTab == LeftTab.RaceFaction && !rawId.StartsWith("Faction_")) continue;
                if (_currentLeftTab == LeftTab.Character && !rawId.StartsWith("Pawn_") && !rawId.StartsWith("OC_")) continue;

                string baseName = _cachedDisplayNames[i];
                bool isPlayer = _cachedIsPlayer[i];

                Rect rowRect = new Rect(0, y, viewRect.width, 30f);
                bool isSelected = (_selectedRoster.Key == kvp.Key);

                if (isSelected) Widgets.DrawBoxSolid(rowRect, new Color(0.2f, 0.4f, 0.6f, 0.5f));
                else if (Mouse.IsOver(rowRect)) Widgets.DrawBoxSolid(rowRect, new Color(0.3f, 0.3f, 0.3f, 0.3f));

                if (Widgets.ButtonInvisible(rowRect))
                {
                    _selectedRoster = kvp;
                    Verse.UI.UnfocusCurrentControl();
                }

                string hashKey = CultureCalculator.GetHashForSourceId(roster.sourceId);
                var cacheData = CultureOracleCache.GetData(hashKey);
                bool isOverridden = cacheData != null && cacheData.OverrideProfile != null && cacheData.OverrideProfile.IsActive;

                int score = roster.lastMatchScore;
                string colorHex = "#FFFFFF";
                string scoreLabel = "";

                if (rawId.StartsWith("Pawn_") || rawId.StartsWith("OC_"))
                { colorHex = "#FFD700"; scoreLabel = "RimMusic_Score_Privilege".Translate(); }
                else if (isOverridden) { colorHex = "#44CCFF"; scoreLabel = "RimMusic_Score_Custom".Translate(); }
                else if (score == 9999) { colorHex = "#BA55D3"; scoreLabel = "RimMusic_Score_Exclusive".Translate(); }
                else if (roster.isPendingOracle || score == -1) { colorHex = "#AAAAAA"; scoreLabel = "RimMusic_Score_Polling".Translate(); }
                else if (score == 999) { colorHex = "#88FF88"; scoreLabel = "RimMusic_Score_Locked".Translate(); }
                else if (score > threshold) { colorHex = "#88FF88"; scoreLabel = "RimMusic_Score_Value".Translate(score); }
                else if (score == threshold) { colorHex = "#FFCC00"; scoreLabel = "RimMusic_Score_Value".Translate(score); }
                else { colorHex = "#FF8888"; scoreLabel = "RimMusic_Score_Value".Translate(score); }

                if (isPlayer && !isOverridden && score != 9999 && !rawId.StartsWith("Pawn_") && !rawId.StartsWith("OC_")) colorHex = "#FFD700";

                int currentCount = roster.activeInstruments.Count;
                string shortFallStr = (currentCount > 0 && currentCount < targetInstrumentCount && !roster.isPendingOracle && score != 9999 && !rawId.StartsWith("Pawn_") && !rawId.StartsWith("OC_"))
                                      ? $"<color=#FF8800>{"RimMusic_Score_Shortfall".Translate(currentCount, targetInstrumentCount)}</color> "
                                      : "";

                string finalLabel = $"<color={colorHex}>{scoreLabel}</color>{shortFallStr}<color={colorHex}>{baseName}</color>";
                Widgets.Label(new Rect(rowRect.x + 5f, rowRect.y + 4f, rowRect.width - 10f, 24f), finalLabel);

                y += 35f;
            }
            Widgets.EndScrollView();
        }

        private void DrawRightPanel(Rect rect)
        {
            if (_selectedRoster.Value == null)
            {
                Widgets.Label(rect, "RimMusic_Mixer_SelectPrompt".Translate());
                return;
            }

            CulturalRoster roster = _selectedRoster.Value;

            if (roster.sourceId.StartsWith("Pawn_") || roster.sourceId.StartsWith("OC_"))
            {
                DrawProtagonistRightPanel(rect, roster);
                return;
            }

            string hashKey = CultureCalculator.GetHashForSourceId(roster.sourceId);
            var cacheData = CultureOracleCache.GetOrInitData(hashKey);
            if (cacheData.OverrideProfile == null) cacheData.OverrideProfile = new PlayerOverrideProfile();
            PlayerOverrideProfile profile = cacheData.OverrideProfile;
            profile.EnsureCapacity(roster.activeInstruments.Count);

            Listing_Standard l = new Listing_Standard();
            l.Begin(rect);

            if (roster.isPendingOracle)
            {
                l.Label($"<size=16><b><color=#FF8888>{"RimMusic_Mixer_Status_Polling".Translate()}</color></b></size>");
                l.Gap(24f);
            }
            else
            {
                l.Label($"<size=16><b>{"RimMusic_Mixer_StyleBase".Translate()}:</b> <color=#FFDD44>{roster.styleDefName}</color></size>");
                if (profile.IsActive) l.Label($"<size=12><color=#44CCFF>{"RimMusic_Mixer_State_Manual".Translate()}</color></size>");
                else if (roster.lastMatchScore == 9999) l.Label($"<size=12><color=#BA55D3>{"RimMusic_Mixer_State_Privilege".Translate()}</color></size>");
                else l.Label($"<size=12><color=#AAAAAA>{"RimMusic_Mixer_State_AI".Translate(roster.lastMatchScore)}</color></size>");
            }

            int targetCount = RimMusicMod.Settings?.TargetInstrumentCount ?? 5;
            int currentCount = roster.activeInstruments.Count;
            if (currentCount > 0 && currentCount < targetCount && !roster.isPendingOracle && roster.lastMatchScore != 9999)
            {
                GUI.color = new Color(1f, 0.8f, 0.2f);
                l.Label("RimMusic_Mixer_CountWarning".Translate(currentCount));
                GUI.color = Color.white;
                l.Gap(5f);
            }

            string sourceInfo = roster.sourceId.StartsWith("Ideo_") ? "RimMusic_Source_Ideo".Translate() : "RimMusic_Source_Race".Translate();
            string techInfo = "RimMusic_Unknown".Translate();

            if (roster.sourceId.StartsWith("Ideo_"))
            {
                var style = DefDatabase<MusicStyleDef>.GetNamedSilentFail(roster.styleDefName);
                if (style != null && style.minTechLevel != TechLevel.Undefined) techInfo = $"{style.minTechLevel} - {style.maxTechLevel}";
                else techInfo = "RimMusic_UniversalEra".Translate();

                if (ModsConfig.IdeologyActive && Find.IdeoManager != null && Find.FactionManager != null)
                {
                    if (int.TryParse(roster.sourceId.Substring(5), out int ideoId))
                    {
                        var ideo = Find.IdeoManager.IdeosListForReading.FirstOrDefault(idx => idx.id == ideoId);
                        if (ideo != null)
                        {
                            var linkedFactions = Find.FactionManager.AllFactions.Where(f => f.ideos?.PrimaryIdeo == ideo).ToList();
                            if (linkedFactions.Count == 1) sourceInfo = "RimMusic_Source_Exclusive".Translate(linkedFactions.First().Name);
                            else if (linkedFactions.Count >= 2) sourceInfo = "RimMusic_Source_Shared".Translate();
                            else sourceInfo = "RimMusic_Source_Colony".Translate();
                        }
                    }
                }
            }
            else if (roster.sourceId.StartsWith("Faction_"))
            {
                string defName = roster.sourceId.Substring(8);
                var fac = Find.FactionManager.AllFactions.FirstOrDefault(f => f.def.defName == defName);
                if (fac != null) { sourceInfo = "RimMusic_Source_Faction".Translate(fac.Name); techInfo = fac.def.techLevel.ToString(); }
            }

            l.Label($"<size=12><color=#AAAAAA>{"RimMusic_Mixer_Class".Translate(sourceInfo)} | {"RimMusic_Mixer_Era".Translate(techInfo)}</color></size>");
            l.GapLine(6f);
            l.Gap(5f);

            l.Label("RimMusic_Mixer_UsageHint".Translate());
            l.Gap(10f);

            float innerHeight = (roster.activeInstruments.Count * 45f) + (profile.ExtraInstruments.Count * 45f) + 60f;
            Rect scrollOutRect = l.GetRect(240f);
            Rect scrollInRect = new Rect(0, 0, scrollOutRect.width - 16f, innerHeight);

            Widgets.BeginScrollView(scrollOutRect, ref _rightScrollPos, scrollInRect);
            Listing_Standard innerL = new Listing_Standard();
            innerL.Begin(scrollInRect);

            for (int i = 0; i < roster.activeInstruments.Count; i++)
            {
                Rect row = innerL.GetRect(35f);
                Widgets.DrawBoxSolid(row, new Color(0.1f, 0.1f, 0.1f, 0.5f));

                bool isMuted = profile.MutedSlots[i];
                Rect muteBtn = new Rect(row.x + 5f, row.y + 2f, 55f, 30f);
                if (Widgets.ButtonText(muteBtn, isMuted ? "RimMusic_Mute".Translate() : "RimMusic_Enable".Translate()))
                {
                    profile.MutedSlots[i] = !isMuted;
                    CultureOracleCache.ForceSave();
                }

                string roleKey = $"RimMusic_Role_{i}";
                string roleLabel = roleKey.CanTranslate() ? roleKey.Translate().ToString() : "RimMusic_Role_Generic".Translate(i + 1).ToString();

                GUI.color = isMuted ? Color.gray : Color.white;
                Widgets.Label(new Rect(row.x + 65f, row.y + 6f, 75f, 24f), $"<b>{roleLabel}</b>");

                string rawInst = roster.activeInstruments[i];
                bool isNativeEmpty = string.IsNullOrWhiteSpace(rawInst);
                string customName = profile.CustomSlots[i];
                bool hasCustom = !string.IsNullOrEmpty(customName);

                string textToEdit = hasCustom ? customName : (isNativeEmpty ? "" : rawInst);
                if (isNativeEmpty && !hasCustom) GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);

                Rect textRect = new Rect(row.x + 145f, row.y + 2f, row.width - 215f, 30f);
                string newText = Widgets.TextField(textRect, textToEdit);
                if (isNativeEmpty && !hasCustom && string.IsNullOrEmpty(newText))
                {
                    Widgets.Label(new Rect(textRect.x + 5f, textRect.y + 5f, textRect.width, 24f), "RimMusic_EmptyInst".Translate());
                }
                GUI.color = Color.white;

                string compareBase = isNativeEmpty ? "" : rawInst;
                if (newText != textToEdit && newText != compareBase)
                {
                    profile.CustomSlots[i] = newText;
                    CultureOracleCache.ForceSave();
                }

                if (hasCustom)
                {
                    if (Widgets.ButtonText(new Rect(row.xMax - 60f, row.y + 2f, 55f, 30f), "RimMusic_Restore".Translate()))
                    {
                        profile.CustomSlots[i] = "";
                        CultureOracleCache.ForceSave();
                    }
                }
                innerL.Gap(10f);
            }

            innerL.GapLine();
            innerL.Gap(5f);

            innerL.Label("RimMusic_Mixer_ExtraRack".Translate());
            for (int j = 0; j < profile.ExtraInstruments.Count; j++)
            {
                Rect exRow = innerL.GetRect(35f);
                Widgets.DrawBoxSolid(exRow, new Color(0.15f, 0.2f, 0.15f, 0.5f));
                Widgets.Label(new Rect(exRow.x + 55f, exRow.y + 6f, 75f, 24f), "RimMusic_ExtraIndex".Translate(j + 1));

                string exText = profile.ExtraInstruments[j];
                string newExText = Widgets.TextField(new Rect(exRow.x + 135f, exRow.y + 2f, exRow.width - 200f, 30f), exText);

                if (newExText != exText)
                {
                    profile.ExtraInstruments[j] = newExText;
                    CultureOracleCache.ForceSave();
                }

                if (Widgets.ButtonText(new Rect(exRow.xMax - 60f, exRow.y + 2f, 55f, 30f), "RimMusic_Remove".Translate()))
                {
                    profile.ExtraInstruments.RemoveAt(j);
                    CultureOracleCache.ForceSave();
                    break;
                }
                innerL.Gap(10f);
            }

            if (Widgets.ButtonText(new Rect(135f, innerL.CurHeight, 180f, 30f), "RimMusic_AddExtra".Translate()))
            {
                profile.ExtraInstruments.Add("New Instrument");
                CultureOracleCache.ForceSave();
            }
            innerL.Gap(35f);

            innerL.End();
            Widgets.EndScrollView();

            l.Gap(10f);

            if (roster.lastMatchScore == 9999)
            {
                if (Widgets.ButtonText(l.GetRect(35f), "RimMusic_Mixer_RestoreHardcoded".Translate()))
                {
                    cacheData.OverrideProfile = new PlayerOverrideProfile();
                    CultureOracleCache.ForceSave();
                    Messages.Message("RimMusic_Mixer_RestoreDone".Translate(), MessageTypeDefOf.NeutralEvent, false);
                }
            }
            else
            {
                if (Widgets.ButtonText(l.GetRect(35f), "RimMusic_Mixer_RejectOracle".Translate())) RejectOracleAndReroll(roster);
            }
            l.Gap(5f);

            if (profile.IsActive)
            {
                GUI.color = new Color(0.4f, 0.8f, 1f);
                if (Widgets.ButtonText(l.GetRect(35f), "RimMusic_Mixer_ClearMask".Translate()))
                {
                    cacheData.OverrideProfile = new PlayerOverrideProfile();
                    CultureOracleCache.ForceSave();
                    Messages.Message("RimMusic_Mixer_MaskCleared".Translate(), MessageTypeDefOf.NeutralEvent, false);
                }
                GUI.color = Color.white;
                l.Gap(5f);
            }

            if (Widgets.ButtonText(l.GetRect(35f), "RimMusic_Mixer_Forget".Translate())) DeleteRoster(roster);

            l.End();
        }

        private void DrawProtagonistRightPanel(Rect rect, CulturalRoster roster)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(rect);

            l.Label($"<size=18><b>{"RimMusic_Protagonist_Title".Translate()}</b></size>");

            string sourceInfo = roster.sourceId.StartsWith("OC_") ? "RimMusic_Protagonist_Chosen".Translate() : "RimMusic_Protagonist_Manual".Translate();
            l.Label($"<size=12><color=#AAAAAA>{"RimMusic_Protagonist_Source".Translate(sourceInfo)}</color></size>");
            l.GapLine(6f);
            l.Gap(10f);

            l.Label("RimMusic_Protagonist_Hint1".Translate());
            l.Label("RimMusic_Protagonist_Hint2".Translate());
            l.Gap(15f);

            float innerHeight = (roster.activeInstruments.Count * 45f) + 60f;
            Rect scrollOutRect = l.GetRect(300f);
            Rect scrollInRect = new Rect(0, 0, scrollOutRect.width - 16f, innerHeight);

            Widgets.BeginScrollView(scrollOutRect, ref _rightScrollPos, scrollInRect);
            Listing_Standard innerL = new Listing_Standard();
            innerL.Begin(scrollInRect);

            if (roster.activeInstruments.Count == 0)
            {
                innerL.Label("RimMusic_Protagonist_Empty".Translate());
            }

            for (int i = 0; i < roster.activeInstruments.Count; i++)
            {
                Rect row = innerL.GetRect(35f);
                Widgets.DrawBoxSolid(row, new Color(0.2f, 0.35f, 0.45f, 0.3f));
                Widgets.Label(new Rect(row.x + 10f, row.y + 6f, 60f, 24f), $"<color=#FFD700>{"RimMusic_Slot".Translate(i + 1)}</color>");

                string inst = roster.activeInstruments[i];
                string newInst = Widgets.TextField(new Rect(row.x + 80f, row.y + 2f, row.width - 160f, 30f), inst);

                if (newInst != inst) roster.activeInstruments[i] = newInst;

                if (Widgets.ButtonText(new Rect(row.xMax - 70f, row.y + 2f, 60f, 30f), "RimMusic_Remove".Translate()))
                {
                    roster.activeInstruments.RemoveAt(i);
                    break;
                }
                innerL.Gap(5f);
            }

            if (Widgets.ButtonText(new Rect(80f, innerL.CurHeight + 5f, 150f, 30f), "RimMusic_AddSlot".Translate()))
            {
                roster.activeInstruments.Add("Custom Instrument");
            }

            innerL.End();
            Widgets.EndScrollView();

            l.Gap(20f);
            if (Widgets.ButtonText(l.GetRect(35f), "RimMusic_Protagonist_Revoke".Translate())) DeleteRoster(roster);

            l.End();
        }

        private void AddPawnToRoster(Pawn p)
        {
            string staticID = null;
            if (p?.kindDef != null)
            {
                Type registryType = GenTypes.AllTypes.FirstOrDefault(t => t.Name == "SpecialPawnRegistry");
                if (registryType != null)
                {
                    var method = registryType.GetMethod("GetStaticID", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (method != null) staticID = method.Invoke(null, new object[] { p.kindDef }) as string;
                }
            }

            string sourceId = !string.IsNullOrEmpty(staticID) ? "OC_" + staticID : "Pawn_" + p.ThingID;
            var rostersDict = _musicComp.GetAllRosters();

            if (!rostersDict.ContainsKey(sourceId))
            {
                rostersDict[sourceId] = new CulturalRoster
                {
                    sourceId = sourceId,
                    styleDefName = "Protagonist_Absolute",
                    lastMatchScore = 9999,
                    activeInstruments = new List<string>()
                };
            }

            BuildAndSortRosterList();
            _selectedRoster = _cachedSortedRosters.FirstOrDefault(kvp => kvp.Key == sourceId);
        }

        private void RejectOracleAndReroll(CulturalRoster roster)
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RimMusic_Mixer_RejectConfirm".Translate(), () =>
            {
                string hashKey = CultureCalculator.GetHashForSourceId(roster.sourceId);
                CultureOracleCache.EvictData(hashKey);

                _musicComp.RemoveRoster(roster.sourceId);
                _musicComp.ScanGlobalFactions();
                _selectedRoster = default;
                BuildAndSortRosterList();

                Verse.UI.UnfocusCurrentControl();
                Messages.Message("RimMusic_Mixer_RejectDone".Translate(), MessageTypeDefOf.PositiveEvent, false);
            }));
        }

        private void DeleteRoster(CulturalRoster roster)
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RimMusic_Mixer_DeleteConfirm".Translate(), () =>
            {
                _musicComp.RemoveRoster(roster.sourceId);
                _selectedRoster = default;
                BuildAndSortRosterList();

                Verse.UI.UnfocusCurrentControl();
                Messages.Message("RimMusic_Mixer_DeleteDone".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }));
        }
    }
}