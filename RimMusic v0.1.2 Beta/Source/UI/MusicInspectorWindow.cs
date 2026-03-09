using UnityEngine;
using Verse;
using RimWorld;
using RimMusic.Data;
using RimMusic.Core;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RimMusic.UI
{
    // =========================================================================
    // 1. Music HUD & Walkman Control Panel
    // =========================================================================
    public class MusicHUDWindow : Window
    {
        private int _updateCounter = 0;
        private MusicContext _cachedCtx;
        private string _statusText = "";
        private string _lastGeneratedPrompt = "";

        private string _cachedTelemetryText = "";

        private bool _isGeneratingPrompt = false;
        private bool _isGeneratingRealMusic = false;

        private bool _isHoveringWindow = false;
        private WindowResizer _customResizer;

        public override Vector2 InitialSize => new Vector2(340f, 260f);
        protected override float Margin => 0f;

        public MusicHUDWindow()
        {
            this.draggable = true;
            this.resizeable = false;
            this.doCloseX = false;
            this.closeOnCancel = false;
            this.closeOnAccept = false;
            this.preventCameraMotion = false;
            this.doWindowBackground = false;
            this.shadowAlpha = 0f;
            this.layer = WindowLayer.GameUI;
            _customResizer = new WindowResizer();
            _customResizer.minWindowSize = new Vector2(240f, 240f);
            this._statusText = "RimMusic_Ready".Translate();
        }

        public override void PreOpen()
        {
            base.PreOpen();
            if (RimMusicMod.Settings.HUDRect == Rect.zero)
                windowRect = new Rect((Verse.UI.screenWidth - InitialSize.x) / 2f, (Verse.UI.screenHeight - InitialSize.y) / 2f, InitialSize.x, InitialSize.y);
            else
                windowRect = RimMusicMod.Settings.HUDRect;

            _cachedCtx = MusicContext.Build(MusicGameComponent.ActiveProtagonist);
            RefreshTelemetry();
            RealtimeMusicEngine.InitializePlaylist();
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();

            windowRect.x = Mathf.Clamp(windowRect.x, 0, Verse.UI.screenWidth - windowRect.width);
            windowRect.y = Mathf.Clamp(windowRect.y, 0, Verse.UI.screenHeight - windowRect.height);
            if (RimMusicMod.Settings.HUDRect != windowRect) RimMusicMod.Settings.HUDRect = windowRect;

            _isHoveringWindow = this.windowRect.Contains(Verse.UI.MousePositionOnUIInverted);

            if (!_isGeneratingPrompt && !_isGeneratingRealMusic)
            {
                _updateCounter++;
                if (_updateCounter > 30)
                {
                    _cachedCtx = MusicContext.Build(MusicGameComponent.ActiveProtagonist);
                    RefreshTelemetry();
                    _updateCounter = 0;
                }
            }

            if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.M))
            {
                if (RimMusicMod.Settings.EnableRealtimeMusic && !_isGeneratingRealMusic)
                {
                    GenerateAndPlayRealMusic();
                }
                else if (!RimMusicMod.Settings.EnableRealtimeMusic)
                {
                    Messages.Message("RimMusic_EngineDisabled".Translate(), MessageTypeDefOf.RejectInput, false);
                }
            }
        }

        private void RefreshTelemetry()
        {
            if (Find.CurrentMap == null || _cachedCtx == null) return;

            if (_cachedCtx.danger == "Safe")
            {
                string demo = CultureCalculator.GetCultureDemographics(Find.CurrentMap);
                _cachedTelemetryText = $"Culture: {demo}";
            }
            else
            {
                CultureCalculator.GetThreatTelemetry(Find.CurrentMap, out float ratio, out string topThreat);
                _cachedTelemetryText = $"Threat: {(ratio * 100f):F1}% | Top: {topThreat}";
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            RimMusicMod.Settings.HUDRect = this.windowRect;
            RimMusicMod.Settings.Write();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.DrawBoxSolid(inRect, new Color(0.1f, 0.1f, 0.1f, RimMusicMod.Settings.HUDTransparency));

            if (_isHoveringWindow)
            {
                Rect localRect = new Rect(0, 0, this.windowRect.width, this.windowRect.height);
                Rect resizedLocal = _customResizer.DoResizeControl(localRect);

                // =========================================================================
                // [FIXED] HUD Resizer Limit - Prevent push-back bug when dragging out of bounds
                // =========================================================================
                float maxWidth = Verse.UI.screenWidth - this.windowRect.x;
                float maxHeight = Verse.UI.screenHeight - this.windowRect.y;

                this.windowRect.width = Mathf.Min(resizedLocal.width, maxWidth);
                this.windowRect.height = Mathf.Min(resizedLocal.height, maxHeight);
            }

            if (_cachedCtx == null) return;

            bool showUI = _isHoveringWindow;
            Rect contentRect = inRect.ContractedBy(12f);

            if (showUI)
            {
                Rect closeRect = new Rect(contentRect.xMax - 18f, contentRect.y, 18f, 18f);
                if (Widgets.ButtonImage(closeRect, TexButton.CloseXSmall)) this.Close();

                Rect menuRect = new Rect(contentRect.xMax - 55f, contentRect.y - 3f, 35f, 24f);
                GUI.color = Mouse.IsOver(menuRect) ? Color.white : new Color(1f, 1f, 1f, 0.55f);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(menuRect, "RimMusic_MenuLabel".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;

                if (Widgets.ButtonInvisible(menuRect))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>
                    {
                        new FloatMenuOption("RimMusic_Option_Settings".Translate(), () => Find.WindowStack.Add(new MusicSettingsFloatingWindow())),
                        new FloatMenuOption("RimMusic_Option_Mixer".Translate(), () => Find.WindowStack.Add(new CultureMixerWindow()))
                    };
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }

            Listing_Standard l = new Listing_Standard();
            l.Begin(new Rect(contentRect.x, contentRect.y, contentRect.width, contentRect.height - 90f));

            Text.Font = GameFont.Medium;
            l.Label("RimMusic_HUD_Title".Translate());
            l.GapLine(2f);
            l.Gap(4f);

            Text.Font = GameFont.Small;
            l.Label("RimMusic_HUD_State".Translate(_cachedCtx.state));
            l.Label("RimMusic_HUD_Focus".Translate(_cachedCtx.focus_name));

            string act = _cachedCtx.activity;
            if (act.Length > 25) act = act.Substring(0, 25) + "...";
            l.Label("RimMusic_HUD_Activity".Translate(act));

            if (!string.IsNullOrEmpty(_cachedTelemetryText))
            {
                GUI.color = _cachedCtx.danger == "Safe" ? new Color(0.7f, 0.9f, 0.7f) : new Color(0.9f, 0.5f, 0.5f);
                l.Label(_cachedTelemetryText);
                GUI.color = Color.white;
            }

            if (MusicAIClient.IsCircuitTripped)
            {
                GUI.color = new Color(1f, 0.3f, 0.3f);
                Text.Font = GameFont.Tiny;
                l.Label("RimMusic_Warning_CircuitTripped".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            l.End();

            Rect generateArea = new Rect(contentRect.x, contentRect.yMax - 95f, contentRect.width, 50f);
            if (showUI)
            {
                Rect btnGenPrompt = new Rect(generateArea.x, generateArea.y, 75f, 24f);
                Rect btnCopy = new Rect(generateArea.x + 80f, generateArea.y, 65f, 24f);

                if (_isGeneratingPrompt || MusicAIClient.IsCircuitTripped) GUI.color = new Color(0.8f, 0.4f, 0.4f);
                if (Widgets.ButtonText(btnGenPrompt, "RimMusic_Btn_PredictOnly".Translate())) GeneratePromptSilent();
                GUI.color = Color.white;

                if (Widgets.ButtonText(btnCopy, "RimMusic_Btn_Copy".Translate()))
                {
                    if (!string.IsNullOrEmpty(_lastGeneratedPrompt)) { GUIUtility.systemCopyBuffer = _lastGeneratedPrompt; Messages.Message("RimMusic_Msg_Copied".Translate(), MessageTypeDefOf.TaskCompletion, false); }
                }

                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(generateArea.x + 150f, generateArea.y + 4f, 100f, 24f), _statusText);
                GUI.color = Color.white; Text.Font = GameFont.Small;

                Rect btnReal = new Rect(generateArea.x, generateArea.y + 28f, generateArea.width, 26f);
                if (!RimMusicMod.Settings.EnableRealtimeMusic)
                {
                    GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                    Widgets.DrawBoxSolid(btnReal, new Color(0, 0, 0, 0.3f));
                    Text.Anchor = TextAnchor.MiddleCenter; Widgets.Label(btnReal, "RimMusic_Status_Disabled".Translate()); Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                }
                else
                {
                    if (_isGeneratingRealMusic) { GUI.color = new Color(0.3f, 0.6f, 0.8f); Widgets.ButtonText(btnReal, "RimMusic_Status_AirdropPending".Translate()); GUI.color = Color.white; }
                    else { GUI.color = new Color(0.9f, 0.6f, 0.2f); if (Widgets.ButtonText(btnReal, "RimMusic_Btn_CallAudio".Translate())) GenerateAndPlayRealMusic(); GUI.color = Color.white; }
                }
            }
            else
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                string bgStatus = _isGeneratingRealMusic ? "RimMusic_BGStatus_Syncing".Translate().ToString() : "RimMusic_BGStatus_Idle".Translate(_statusText).ToString();
                Widgets.Label(new Rect(generateArea.x, generateArea.yMax - 20f, contentRect.width, 24f), bgStatus);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            Rect playerArea = new Rect(contentRect.x, contentRect.yMax - 35f, contentRect.width, 35f);
            Widgets.DrawBoxSolid(playerArea, new Color(0.15f, 0.15f, 0.15f, 0.8f));

            Rect progBg = new Rect(playerArea.x, playerArea.y, playerArea.width, 3f);
            Widgets.DrawBoxSolid(progBg, new Color(0.1f, 0.1f, 0.1f));
            Rect progFg = new Rect(playerArea.x, playerArea.y, playerArea.width * RealtimeMusicEngine.TrackProgress, 3f);
            Widgets.DrawBoxSolid(progFg, new Color(0.3f, 0.8f, 0.4f));

            if (showUI)
            {
                Rect btnPrev = new Rect(playerArea.x + 5f, playerArea.y + 6f, 30f, 24f);
                Rect btnPlay = new Rect(playerArea.x + 40f, playerArea.y + 6f, 40f, 24f);
                Rect btnNext = new Rect(playerArea.x + 85f, playerArea.y + 6f, 30f, 24f);

                if (Widgets.ButtonText(btnPrev, "|<")) RealtimeMusicEngine.PrevTrack();
                string playIcon = RealtimeMusicEngine.IsPlaying ? "||" : ">";
                if (Widgets.ButtonText(btnPlay, playIcon)) RealtimeMusicEngine.TogglePause();
                if (Widgets.ButtonText(btnNext, ">|")) RealtimeMusicEngine.NextTrack();
            }

            float titleX = showUI ? playerArea.x + 125f : playerArea.x + 10f;
            float titleW = showUI ? playerArea.width - 130f : playerArea.width - 20f;
            Rect titleRect = new Rect(titleX, playerArea.y + 8f, titleW, 24f);

            string trackName = "RimMusic_TrackLabel".Translate(RealtimeMusicEngine.CurrentTrackName);

            Text.Font = GameFont.Tiny;
            float textWidth = Text.CalcSize(trackName).x;

            GUI.BeginGroup(titleRect);
            if (textWidth > titleRect.width)
            {
                float offset = (Time.realtimeSinceStartup * 25f) % (textWidth + titleRect.width);
                Widgets.Label(new Rect(titleRect.width - offset, 0, textWidth, 24f), trackName);
            }
            else
            {
                Widgets.Label(new Rect(0, 0, titleRect.width, 24f), trackName);
            }
            GUI.EndGroup();
            Text.Font = GameFont.Small;
        }

        private async void GeneratePromptSilent()
        {
            if (_isGeneratingPrompt || MusicAIClient.IsCircuitTripped) return;
            _isGeneratingPrompt = true; _statusText = "RimMusic_Status_Predicting".Translate();
            MusicContext ctx = MusicContext.Build(MusicGameComponent.ActiveProtagonist, true);
            try { _lastGeneratedPrompt = await new MusicAIClient().GenerateParsedMusicPromptAsync(ctx); _statusText = "RimMusic_Status_Generated".Translate(); }
            catch { _statusText = "RimMusic_Status_Failed".Translate(); }
            finally { _isGeneratingPrompt = false; }
        }

        private async void GenerateAndPlayRealMusic()
        {
            if (_isGeneratingRealMusic) return;
            if (string.IsNullOrWhiteSpace(RimMusicMod.Settings.SunoApiKey))
            {
                Messages.Message("RimMusic_Error_ApiKeyEmpty".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            _isGeneratingRealMusic = true;
            Messages.Message("RimMusic_Msg_Requesting".Translate(), MessageTypeDefOf.NeutralEvent, false);

            try
            {
                MusicContext ctx = MusicContext.Build(MusicGameComponent.ActiveProtagonist, true);
                string finalPrompt = await new MusicAIClient().GenerateParsedMusicPromptAsync(ctx);
                _lastGeneratedPrompt = finalPrompt;
                _statusText = "RimMusic_Status_Sent".Translate();

                await RealtimeMusicEngine.RequestAndPlayMusic(finalPrompt, ctx.focus_name);
            }
            catch (System.Exception ex)
            {
                Log.Error($"[RimMusic] Tactical coordinate exception: {ex.Message}");
                Messages.Message("RimMusic_Error_NodeLost".Translate(), MessageTypeDefOf.RejectInput, false);
            }
            finally
            {
                _isGeneratingRealMusic = false;
            }
        }
    }

    // =========================================================================
    // 2. Music Inspector Window (Debug)
    // =========================================================================
    public class MusicInspectorWindow : Window
    {
        private Vector2 _previewScroll, _resultScroll;
        private string _previewContent = "";
        private string _aiResultContent = "";
        private bool _autoUpdate = true;
        private int _updateCounter = 0;
        private bool _isGenerating = false;

        public override Vector2 InitialSize => new Vector2(500f, 850f);

        public MusicInspectorWindow()
        {
            this.draggable = true;
            this.resizeable = true;
            this.doCloseX = true;
            this.preventCameraMotion = false;
            this.layer = WindowLayer.GameUI;
            this._previewContent = "RimMusic_AwaitingData".Translate();
            this._aiResultContent = "RimMusic_ResultPrompt".Translate();
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();

            if (_autoUpdate && !_isGenerating)
            {
                _updateCounter++;
                if (_updateCounter > 30)
                {
                    RefreshPreview();
                    _updateCounter = 0;
                }
            }
        }

        // =========================================================================
        // [FIXED] Inspector Native Resizer Limit - Intercept RimWorld's base resizer 
        // immediately after it runs to prevent leftward UI push-back bugs.
        // =========================================================================
        public override void ExtraOnGUI()
        {
            base.ExtraOnGUI();

            float maxWidth = Verse.UI.screenWidth - this.windowRect.x;
            float maxHeight = Verse.UI.screenHeight - this.windowRect.y;

            if (this.windowRect.width > maxWidth) this.windowRect.width = maxWidth;
            if (this.windowRect.height > maxHeight) this.windowRect.height = maxHeight;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (Event.current.type == EventType.MouseDown && !inRect.Contains(Event.current.mousePosition))
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
            }
            DrawUI(inRect);
        }

        private void DrawUI(Rect inRect)
        {
            Rect toolbarRect = inRect.TopPartPixels(30f);
            Listing_Standard l = new Listing_Standard();
            l.Begin(toolbarRect);
            l.ColumnWidth = toolbarRect.width / 2.5f;

            l.CheckboxLabeled("RimMusic_Inspector_AutoRefresh".Translate(), ref _autoUpdate);
            l.NewColumn();

            if (MusicAIClient.IsCircuitTripped) GUI.color = new Color(0.8f, 0.4f, 0.4f);

            string btnLabel = _isGenerating ? "RimMusic_Inspector_Generating".Translate().ToString() : "RimMusic_Inspector_TestGen".Translate().ToString();
            if (l.ButtonText(btnLabel))
            {
                if (!MusicAIClient.IsCircuitTripped) GenerateNow();
            }
            GUI.color = Color.white;
            l.End();

            float remainingH = inRect.height - 40f;
            float previewH = remainingH * 0.6f;
            float resultH = remainingH - previewH - 10f;

            Rect previewRect = new Rect(0, 35f, inRect.width, previewH);
            Widgets.DrawBoxSolid(previewRect, new Color(0.1f, 0.1f, 0.1f));
            Widgets.Label(previewRect.TopPartPixels(20f), "  " + "RimMusic_Inspector_PreviewHeader".Translate());

            Rect pBody = previewRect.ContractedBy(2f); pBody.yMin += 20f;
            DrawScrollableArea(pBody, ref _previewContent, ref _previewScroll, true);

            Rect resultRect = new Rect(0, previewRect.yMax + 10f, inRect.width, resultH);
            Widgets.DrawBoxSolid(resultRect, new Color(0.15f, 0.15f, 0.15f));
            Widgets.Label(resultRect.TopPartPixels(20f), "  " + "RimMusic_Inspector_ResultHeader".Translate());

            Rect rBody = resultRect.ContractedBy(2f); rBody.yMin += 20f;
            DrawScrollableArea(rBody, ref _aiResultContent, ref _resultScroll, false);
        }

        private void DrawScrollableArea(Rect rect, ref string text, ref Vector2 scrollPos, bool readOnly)
        {
            if (text == null) text = "";
            float width = rect.width - 16f;
            float height = Text.CalcHeight(text, width) + 50f;
            if (height < rect.height) height = rect.height;
            Rect viewRect = new Rect(0, 0, width, height);

            Widgets.BeginScrollView(rect, ref scrollPos, viewRect);
            if (readOnly) Widgets.TextArea(viewRect, text, true);
            else text = Widgets.TextArea(viewRect, text, false);
            Widgets.EndScrollView();
        }

        private void RefreshPreview()
        {
            MusicContext ctx = MusicContext.Build(MusicGameComponent.ActiveProtagonist);
            var preset = RimMusicMod.Settings.Preset;
            int maxWords = RimMusicMod.Settings.MaxOutputWords;

            string fullStr = MusicAIClient.BuildFullPromptString(preset, ctx, maxWords, out string sys, out string user, out string man);

            string telemetryBlock = "";
            Map map = Find.CurrentMap;

            if (map != null && ctx.danger != "Safe")
            {
                float colonyPower = CultureCalculator.GetColonyDefensePower(map);
                float totalThreat = 0f;
                Dictionary<string, float> threatFactions = new Dictionary<string, float>();

                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p.HostileTo(Faction.OfPlayer) && !p.Downed && !p.Dead)
                    {
                        float pPower = p.kindDef.combatPower > 0 ? p.kindDef.combatPower : 50f;
                        string facName = p.Faction != null ? p.Faction.Name : "Unknown Entity";

                        if (!threatFactions.ContainsKey(facName)) threatFactions[facName] = 0f;
                        threatFactions[facName] += pPower;
                        totalThreat += pPower;
                    }
                }

                if (totalThreat > 0f)
                {
                    float ratio = totalThreat / (totalThreat + colonyPower + 0.001f);

                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("=========== [ BATTLEFIELD TELEMETRY ] ===========");
                    sb.AppendLine($"[Colony Defense Power]: {colonyPower:F0} CP (Gear + Infrastructure)");
                    sb.AppendLine($"[Total Threat Power]: {totalThreat:F0} CP");
                    sb.AppendLine($"[Threat Ratio (Dominance)]: {(ratio * 100f):F1}%\n");

                    sb.AppendLine("--- Top 3 Active Threat Forces ---");
                    var sortedThreats = threatFactions.OrderByDescending(kvp => kvp.Value).Take(3).ToList();

                    for (int i = 0; i < sortedThreats.Count; i++)
                    {
                        float pct = (sortedThreats[i].Value / totalThreat) * 100f;
                        sb.AppendLine($"{i + 1}. {sortedThreats[i].Key} -> {sortedThreats[i].Value:F0} CP ({pct:F1}%)");
                    }
                    sb.AppendLine("=================================================\n\n");

                    telemetryBlock = sb.ToString();
                }
            }

            string baseFormat = "RimMusic_Inspector_PreviewFormat".Translate(ctx.state, ctx.focus_name, sys, user, man);
            _previewContent = telemetryBlock + baseFormat;
        }

        private async void GenerateNow()
        {
            _isGenerating = true;
            _aiResultContent = "RimMusic_Inspector_Requesting".Translate();
            RefreshPreview();

            try
            {
                MusicContext ctx = MusicContext.Build(MusicGameComponent.ActiveProtagonist, true);
                _aiResultContent = await new MusicAIClient().GenerateParsedMusicPromptAsync(ctx);
            }
            catch (System.Exception ex)
            {
                _aiResultContent = "Error: " + ex.Message;
            }
            finally
            {
                _isGenerating = false;
            }
        }
    }
}