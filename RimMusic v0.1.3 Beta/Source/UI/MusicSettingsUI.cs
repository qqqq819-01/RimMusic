using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using RimMusic.Data;
using RimMusic.Core;

namespace RimMusic.UI
{
    public static class MusicSettingsUI
    {
        private enum CategoryTab { SystemPrompts, FocusSystem, CultureSystem, Integrations, UIAndDebug, RealtimeEngine }
        private enum PromptTab { System, UserContext, Mandatory, Presets }

        private static Vector2 _presetScroll;
        private static string _newPresetName = "My Epic Preset";

        private static CategoryTab _currentTab = CategoryTab.SystemPrompts;
        private static PromptTab _currentPromptTab = PromptTab.System;

        private static Vector2 _sysScroll, _userScroll, _manScroll, _varScroll, _focusScroll;

        public static PromptEntry CurrentEditingEntry;

        private static GUIStyle _wrapTextAreaStyle;
        private static GUIStyle WrapTextAreaStyle
        {
            get
            {
                if (_wrapTextAreaStyle == null)
                {
                    _wrapTextAreaStyle = new GUIStyle(Text.CurTextAreaStyle);
                    _wrapTextAreaStyle.wordWrap = true;
                }
                return _wrapTextAreaStyle;
            }
        }

        public static void Draw(Rect inRect, PromptPreset preset, RimMusicSettings settings)
        {
            float leftWidth = 180f;
            Rect leftNavRect = new Rect(inRect.x, inRect.y, leftWidth, inRect.height);
            Rect contentRect = new Rect(inRect.x + leftWidth + 15f, inRect.y, inRect.width - leftWidth - 15f, inRect.height);

            Widgets.DrawBoxSolid(leftNavRect, new Color(0.12f, 0.12f, 0.12f, 0.8f));

            Listing_Standard navListing = new Listing_Standard();
            navListing.Begin(leftNavRect.ContractedBy(10f));

            navListing.Label("RimMusic_TerminalTitle".Translate());
            navListing.GapLine();
            navListing.Gap(10f);

            if (DrawNavButton(navListing, "RimMusic_Tab_Instructions".Translate(), _currentTab == CategoryTab.SystemPrompts)) _currentTab = CategoryTab.SystemPrompts;
            if (DrawNavButton(navListing, "RimMusic_Tab_Focus".Translate(), _currentTab == CategoryTab.FocusSystem)) _currentTab = CategoryTab.FocusSystem;
            if (DrawNavButton(navListing, "RimMusic_Tab_Culture".Translate(), _currentTab == CategoryTab.CultureSystem)) _currentTab = CategoryTab.CultureSystem;
            if (DrawNavButton(navListing, "RimMusic_Tab_Integrations".Translate(), _currentTab == CategoryTab.Integrations)) _currentTab = CategoryTab.Integrations;

            GUI.color = settings.EnableRealtimeMusic ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.5f);
            if (DrawNavButton(navListing, "RimMusic_Tab_Engine".Translate(), _currentTab == CategoryTab.RealtimeEngine)) _currentTab = CategoryTab.RealtimeEngine;
            GUI.color = Color.white;

            if (DrawNavButton(navListing, "RimMusic_Tab_UI".Translate(), _currentTab == CategoryTab.UIAndDebug)) _currentTab = CategoryTab.UIAndDebug;

            navListing.End();

            Rect bottomNavRect = new Rect(leftNavRect.x + 10f, leftNavRect.yMax - 90f, leftNavRect.width - 20f, 80f);

            bool isChineseEnv = LanguageDatabase.activeLanguage != null && LanguageDatabase.activeLanguage.folderName.StartsWith("Chinese");
            if (isChineseEnv)
            {
                Rect checkRect = new Rect(bottomNavRect.x, bottomNavRect.y, bottomNavRect.width, 24f);
                Widgets.CheckboxLabeled(checkRect, "RimMusic_ForceChinese".Translate(), ref settings.ForceChineseOutput);
                TooltipHandler.TipRegion(checkRect, "RimMusic_ForceChinese_Tip".Translate());
            }

            if (Widgets.ButtonText(new Rect(bottomNavRect.x, bottomNavRect.yMax - 30f, bottomNavRect.width, 30f), "RimMusic_ResetToDefault".Translate()))
            {
                ResetToDefaults(preset, settings);
            }

            switch (_currentTab)
            {
                case CategoryTab.SystemPrompts: DrawSystemPrompts(contentRect, preset, settings); break;
                case CategoryTab.FocusSystem: DrawFocusSystem(contentRect, settings); break;
                case CategoryTab.CultureSystem: DrawCultureSystem(contentRect, settings); break;
                case CategoryTab.Integrations: DrawIntegrations(contentRect, settings); break;
                case CategoryTab.RealtimeEngine: DrawRealtimeEngine(contentRect, settings); break;
                case CategoryTab.UIAndDebug: DrawUIAndDebugSettings(contentRect, settings); break;
            }

            CheckFocusRelease(inRect);
        }

        private static void DrawRealtimeEngine(Rect rect, RimMusicSettings settings)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(rect);

            l.Label("RimMusic_Engine_Title".Translate());
            l.GapLine();
            l.Label("RimMusic_Engine_Hint1".Translate());
            l.Label("RimMusic_Engine_Hint2".Translate());
            l.Gap(15f);

            bool isEnabled = settings.EnableRealtimeMusic;
            Rect toggleRect = l.GetRect(30f);
            Widgets.DrawHighlightIfMouseover(toggleRect);
            Widgets.CheckboxLabeled(toggleRect, "RimMusic_Engine_Toggle".Translate(), ref isEnabled);
            settings.EnableRealtimeMusic = isEnabled;

            l.Gap(20f);

            if (settings.EnableRealtimeMusic)
            {
                // -------- Backend selector --------
                l.Label("RimMusic_Engine_BackendTitle".Translate());
                l.GapLine();

                Rect backendRect = l.GetRect(28f);
                Widgets.Label(backendRect.LeftPart(0.25f), "RimMusic_Engine_Backend".Translate());
               	Rect ddRect = backendRect.RightPart(0.75f);
                string[] backendLabels = new string[]
                {
                    "RimMusic_Engine_Backend_302".Translate(),
                    "RimMusic_Engine_Backend_MiniMax".Translate(),
                    "RimMusic_Engine_Backend_FlowMusic".Translate()
                };
                if (Widgets.ButtonText(ddRect, backendLabels[System.Math.Min(settings.AudioBackendMode, backendLabels.Length - 1)]))
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    for (int bi = 0; bi < backendLabels.Length; bi++)
                    {
                        int captured = bi;
                        opts.Add(new FloatMenuOption(backendLabels[bi], () => settings.AudioBackendMode = captured));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                l.Gap(5f);

                if (settings.AudioBackendMode == 0)
                {
                    // -------- 302.ai config --------
                    l.Label("RimMusic_Engine_AuthTitle".Translate());
                    l.GapLine();

                    Rect urlRect = l.GetRect(24f);
                    Widgets.Label(urlRect.LeftPart(0.2f), "API URL:");
                    settings.CustomAudioApiUrl = Widgets.TextField(urlRect.RightPart(0.8f), settings.CustomAudioApiUrl);
                    l.Gap(5f);

                    Rect keyRect = l.GetRect(24f);
                    Widgets.Label(keyRect.LeftPart(0.2f), "Bearer Token:");
                    settings.SunoApiKey = Widgets.TextField(keyRect.RightPart(0.8f), settings.SunoApiKey);
                    TooltipHandler.TipRegion(keyRect, "RimMusic_Engine_Token_Tip".Translate());
                    l.Gap(5f);

                    Rect audioModelRect = l.GetRect(24f);
                    Widgets.Label(audioModelRect.LeftPart(0.2f), "Suno Model:");
                    settings.SunoModelVersion = Widgets.TextField(audioModelRect.RightPart(0.8f), settings.SunoModelVersion);
                    TooltipHandler.TipRegion(audioModelRect, "RimMusic_Engine_Model_Tip".Translate());
                    l.Gap(5f);
                }
                else if (settings.AudioBackendMode == 1)
                {
                    // -------- MiniMax Web config --------
                    l.Label("RimMusic_Engine_MiniMaxTitle".Translate());
                    l.GapLine();

                    l.Label("RimMusic_Engine_MiniMax_Hint1".Translate());
                    l.Label("RimMusic_Engine_MiniMax_Hint2".Translate());
                    l.Gap(5f);

                    Rect tokRect = l.GetRect(24f);
                    Widgets.Label(tokRect.LeftPart(0.25f), "MiniMax _token:");
                    settings.MiniMaxWebToken = Widgets.TextField(tokRect.RightPart(0.75f), settings.MiniMaxWebToken);
                    TooltipHandler.TipRegion(tokRect, "RimMusic_Engine_MiniMax_Token_Tip".Translate());
                    l.Gap(5f);

                    Rect cookieRect = l.GetRect(24f);
                    Widgets.Label(cookieRect.LeftPart(0.25f), "Cookie (可选):");
                    settings.MiniMaxWebCookie = Widgets.TextField(cookieRect.RightPart(0.75f), settings.MiniMaxWebCookie);
                    TooltipHandler.TipRegion(cookieRect, "RimMusic_Engine_MiniMax_Cookie_Tip".Translate());
                    l.Gap(5f);

                    Rect varRect = l.GetRect(24f);
                    Widgets.Label(varRect.LeftPart(0.25f), "RimMusic_Engine_MiniMax_Variant".Translate());
                    string[] variantLabels = new string[]
                    {
                        "0 - " + "RimMusic_Engine_MiniMax_Variant_Default".Translate(),
                        "1 - Empty body",
                        "2 - Path only",
                        "3 - Double hash"
                    };
                    if (Widgets.ButtonText(varRect.RightPart(0.75f), variantLabels[System.Math.Max(0, System.Math.Min(3, settings.MiniMaxYyVariant))]))
                    {
                        List<FloatMenuOption> vopts = new List<FloatMenuOption>();
                        for (int vi = 0; vi < variantLabels.Length; vi++)
                        {
                            int captured = vi;
                            vopts.Add(new FloatMenuOption(variantLabels[vi], () => settings.MiniMaxYyVariant = captured));
                        }
                        Find.WindowStack.Add(new FloatMenu(vopts));
                    }
                    TooltipHandler.TipRegion(varRect, "RimMusic_Engine_MiniMax_Variant_Tip".Translate());
                    l.Gap(5f);
                }
                else
                {
                    // -------- Flow Music Web config --------
                    l.Label("RimMusic_Engine_FlowMusicTitle".Translate());
                    l.GapLine();

                    l.Label("RimMusic_Engine_FlowMusic_Hint1".Translate());
                    l.Label("RimMusic_Engine_FlowMusic_Hint2".Translate());
                    l.Gap(5f);

                    Rect rtRect = l.GetRect(24f);
                    Widgets.Label(rtRect.LeftPart(0.25f), "refresh_token:");
                    settings.FlowMusicRefreshToken = Widgets.TextField(rtRect.RightPart(0.75f), settings.FlowMusicRefreshToken);
                    TooltipHandler.TipRegion(rtRect, "RimMusic_Engine_FlowMusic_Token_Tip".Translate());
                    l.Gap(5f);
                }

                Rect toggleInstRect = l.GetRect(24f);
                Widgets.CheckboxLabeled(toggleInstRect, "RimMusic_Engine_Instrumental".Translate(), ref settings.SunoMakeInstrumental);
                TooltipHandler.TipRegion(toggleInstRect, "RimMusic_Engine_Instrumental_Tip".Translate());
                l.Gap(20f);

                l.Label("RimMusic_Armory_Title".Translate());
                l.GapLine();
                Rect pathRect = l.GetRect(24f);
                Widgets.Label(pathRect.LeftPart(0.2f), "RimMusic_Armory_Path".Translate());
                settings.MusicSavePath = Widgets.TextField(pathRect.RightPart(0.8f), settings.MusicSavePath);

                l.Gap(10f);
                Rect btnRect = l.GetRect(30f);
                if (Widgets.ButtonText(btnRect.LeftHalf().ContractedBy(2f), "RimMusic_Armory_OpenFolder".Translate()))
                {
                    Application.OpenURL(settings.GetActualSavePath());
                }
            }
            l.End();
        }

        private static void DrawSystemPrompts(Rect rect, PromptPreset preset, RimMusicSettings settings)
        {
            if (settings.Preset == null) settings.Preset = PromptPreset.CreateDefault();
            preset = settings.Preset;
            if (preset.Entries == null) preset.Entries = new List<PromptEntry>();

            if (preset.Entries.Find(e => e.Role == PromptRole.System) == null) preset.Entries.Add(new PromptEntry { Role = PromptRole.System, Name = "System" });
            if (preset.Entries.Find(e => e.Role == PromptRole.User) == null) preset.Entries.Add(new PromptEntry { Role = PromptRole.User, Name = "User" });
            if (preset.Entries.Find(e => e.Role == PromptRole.Mandatory) == null) preset.Entries.Add(new PromptEntry { Role = PromptRole.Mandatory, Name = "Mandatory" });

            Rect tabRect = rect.TopPartPixels(30f);
            float tabWidth = rect.width / 4f;

            if (DrawSubTabButton(new Rect(tabRect.x, tabRect.y, tabWidth - 2f, 30f), "RimMusic_PTab_System".Translate(), _currentPromptTab == PromptTab.System)) _currentPromptTab = PromptTab.System;
            if (DrawSubTabButton(new Rect(tabRect.x + tabWidth, tabRect.y, tabWidth - 2f, 30f), "RimMusic_PTab_User".Translate(), _currentPromptTab == PromptTab.UserContext)) _currentPromptTab = PromptTab.UserContext;
            if (DrawSubTabButton(new Rect(tabRect.x + tabWidth * 2f, tabRect.y, tabWidth - 2f, 30f), "RimMusic_PTab_Mandatory".Translate(), _currentPromptTab == PromptTab.Mandatory)) _currentPromptTab = PromptTab.Mandatory;
            if (DrawSubTabButton(new Rect(tabRect.x + tabWidth * 3f, tabRect.y, tabWidth - 2f, 30f), "RimMusic_PTab_Presets".Translate(), _currentPromptTab == PromptTab.Presets)) _currentPromptTab = PromptTab.Presets;

            Rect contentRect = new Rect(rect.x, rect.y + 40f, rect.width, rect.height - 40f);

            switch (_currentPromptTab)
            {
                case PromptTab.System: DrawEditor(contentRect, preset, PromptRole.System, ref _sysScroll, "RimMusic_PTab_System_Header".Translate()); break;
                case PromptTab.UserContext:
                    float editorWidth = contentRect.width * 0.6f;
                    Rect editorRect = new Rect(contentRect.x, contentRect.y, editorWidth - 10f, contentRect.height);
                    Rect varRect = new Rect(contentRect.x + editorWidth, contentRect.y, contentRect.width - editorWidth, contentRect.height);
                    DrawEditor(editorRect, preset, PromptRole.User, ref _userScroll, "RimMusic_PTab_User_Header".Translate());
                    DrawVarList(varRect, ref _varScroll);
                    break;
                case PromptTab.Mandatory: DrawEditor(contentRect, preset, PromptRole.Mandatory, ref _manScroll, "RimMusic_PTab_Mandatory_Header".Translate()); break;
                case PromptTab.Presets: DrawPresetsTab(contentRect, settings); break;
            }
        }

        private static void DrawPresetsTab(Rect rect, RimMusicSettings settings)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
            Rect body = rect.ContractedBy(10f);
            Listing_Standard l = new Listing_Standard();
            l.Begin(body);

            l.Label($"<b>{"RimMusic_Preset_SaveTitle".Translate()}</b>");
            Rect saveRect = l.GetRect(30f);
            _newPresetName = Widgets.TextField(saveRect.LeftPart(0.5f), _newPresetName);

            if (Widgets.ButtonText(new Rect(saveRect.x + saveRect.width * 0.55f, saveRect.y, 150f, 30f), "RimMusic_Preset_SaveBtn".Translate()))
            {
                PromptPreset newPreset = settings.Preset.Clone();
                newPreset.PresetName = _newPresetName;
                settings.SavedPresets.Add(newPreset);
                _newPresetName = "Custom Preset " + (settings.SavedPresets.Count + 1);
                Messages.Message("RimMusic_Preset_SavedMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }

            l.Gap(20f);
            l.Label($"<b>{"RimMusic_Preset_LibraryTitle".Translate()}</b>");
            l.GapLine();

            if (settings.SavedPresets.Count == 0)
            {
                l.Label($"<color=#888888>{"RimMusic_Preset_Empty".Translate()}</color>");
            }
            else
            {
                float innerHeight = settings.SavedPresets.Count * 40f;
                Rect viewRect = new Rect(0, 0, body.width - 20f, innerHeight);
                Widgets.BeginScrollView(l.GetRect(350f), ref _presetScroll, viewRect);

                for (int i = 0; i < settings.SavedPresets.Count; i++)
                {
                    var p = settings.SavedPresets[i];
                    Rect row = new Rect(0, i * 40f, viewRect.width, 35f);
                    Widgets.DrawBoxSolid(row, new Color(0.2f, 0.2f, 0.25f, 0.8f));
                    if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                    Widgets.Label(new Rect(row.x + 10f, row.y + 8f, 300f, 24f), $"<color=#FFD700>{p.PresetName}</color>");

                    if (Widgets.ButtonText(new Rect(row.xMax - 180f, row.y + 2f, 80f, 30f), "RimMusic_Preset_LoadBtn".Translate()))
                    {
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RimMusic_Preset_LoadConfirm".Translate(p.PresetName), () => {
                            settings.Preset = p.Clone();
                            Messages.Message("RimMusic_Preset_LoadedMsg".Translate(), MessageTypeDefOf.PositiveEvent, false);
                        }));
                    }

                    if (Widgets.ButtonText(new Rect(row.xMax - 90f, row.y + 2f, 80f, 30f), "RimMusic_Preset_DeleteBtn".Translate()))
                    {
                        settings.SavedPresets.RemoveAt(i);
                        break;
                    }
                }
                Widgets.EndScrollView();
            }
            l.End();
        }

        private static void DrawFocusSystem(Rect rect, RimMusicSettings settings)
        {
            Widgets.BeginScrollView(rect, ref _focusScroll, new Rect(0, 0, rect.width - 16f, 950f));
            Listing_Standard l = new Listing_Standard();
            l.Begin(new Rect(0, 0, rect.width - 16f, 950f));

            l.Label("RimMusic_Focus_Title".Translate());
            l.GapLine();
            settings.HoverRadius = Widgets.HorizontalSlider(l.GetRect(24f), settings.HoverRadius, 0.5f, 10f, true, "RimMusic_Focus_Radius".Translate(settings.HoverRadius.ToString("F1")), "0.5", "10");
            settings.FocusCacheDuration = Widgets.HorizontalSlider(l.GetRect(24f), settings.FocusCacheDuration, 0f, 60f, true, "RimMusic_Focus_Latency".Translate(settings.FocusCacheDuration.ToString("F0")), "0", "60");
            l.Gap(15f);

            l.Label("RimMusic_Macro_Title".Translate());
            l.GapLine();
            settings.SwitchDelay = Widgets.HorizontalSlider(l.GetRect(24f), settings.SwitchDelay, 0f, 60f, true, "RimMusic_Macro_Delay".Translate(settings.SwitchDelay.ToString("F1")), "0", "60");
            settings.MacroZoomThreshold = Widgets.HorizontalSlider(l.GetRect(24f), settings.MacroZoomThreshold, 10f, 60f, true, "RimMusic_Macro_Threshold".Translate(settings.MacroZoomThreshold.ToString("F0")), "10", "60");
            settings.MacroExitDelay = Widgets.HorizontalSlider(l.GetRect(24f), settings.MacroExitDelay, 0f, 10f, true, "RimMusic_Macro_Exit".Translate(settings.MacroExitDelay.ToString("F1")), "0", "10");
            settings.EventMemoryDuration = Widgets.HorizontalSlider(l.GetRect(24f), settings.EventMemoryDuration, 10f, 180f, true, "RimMusic_Macro_Memory".Translate(settings.EventMemoryDuration.ToString("F0")), "10", "180");
            l.Gap(15f);

            l.Label("RimMusic_Radar_Title".Translate());
            l.GapLine();
            l.CheckboxLabeled("RimMusic_Radar_Toggle".Translate(), ref settings.EnableEnvironmentalRadar);

            if (settings.EnableEnvironmentalRadar)
            {
                settings.RadarBaseRadius = (int)Widgets.HorizontalSlider(l.GetRect(24f), settings.RadarBaseRadius, 3f, 9f, true, "RimMusic_Radar_Radius".Translate(settings.RadarBaseRadius), "3", "9");
                settings.RadarMaxRadius = (int)Widgets.HorizontalSlider(l.GetRect(24f), settings.RadarMaxRadius, 7f, 25f, true, "RimMusic_Radar_MaxRadius".Translate(settings.RadarMaxRadius), "7", "25");
                settings.RadarExpandTime = Widgets.HorizontalSlider(l.GetRect(24f), settings.RadarExpandTime, 1.0f, 10.0f, true, "RimMusic_Radar_Expand".Translate(settings.RadarExpandTime.ToString("F1")), "1.0", "10.0");
                settings.RadarFastMoveThreshold = Widgets.HorizontalSlider(l.GetRect(24f), settings.RadarFastMoveThreshold, 5f, 50f, true, "RimMusic_Radar_FastThreshold".Translate(settings.RadarFastMoveThreshold.ToString("F1")), "5", "50");
                settings.RadarIdleThreshold = Widgets.HorizontalSlider(l.GetRect(24f), settings.RadarIdleThreshold, 0.1f, 2.0f, true, "RimMusic_Radar_IdleThreshold".Translate(settings.RadarIdleThreshold.ToString("F2")), "0.1", "2.0");
                settings.RadarTetherDistance = Widgets.HorizontalSlider(l.GetRect(24f), settings.RadarTetherDistance, 10f, 50f, true, "RimMusic_Radar_Tether".Translate(settings.RadarTetherDistance.ToString("F0")), "10", "50");
                l.Gap(10f);
                l.CheckboxLabeled("RimMusic_Radar_Draw".Translate(), ref settings.DrawRadarRadius);
            }

            l.End();
            Widgets.EndScrollView();
        }

        private static void DrawCultureSystem(Rect rect, RimMusicSettings settings)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(rect);

            l.Label("RimMusic_Culture_EngineTitle".Translate());
            l.GapLine();

            settings.TargetInstrumentCount = (int)Widgets.HorizontalSlider(l.GetRect(24f), settings.TargetInstrumentCount, 1f, 20f, true, "RimMusic_Culture_InstCount".Translate(settings.TargetInstrumentCount), "1", "20");
            settings.CultureVibeWordLimit = (int)Widgets.HorizontalSlider(l.GetRect(24f), settings.CultureVibeWordLimit, 10f, 100f, true, "Culture Vibe Word Limit: " + settings.CultureVibeWordLimit, "10", "100");

            l.Gap(5f);
            l.CheckboxLabeled("RimMusic_Culture_Realistic".Translate(), ref settings.UseRealisticInstruments);
            l.Gap(10f);

            Rect btnRect = l.GetRect(30f);
            if (Widgets.ButtonText(btnRect.LeftHalf().ContractedBy(2f), "RimMusic_Culture_OpenMixer".Translate()))
            {
                if (Current.Game == null) Messages.Message("RimMusic_Error_NoActiveSave".Translate(), MessageTypeDefOf.RejectInput, false);
                else Find.WindowStack.Add(new CultureMixerWindow());
            }

            l.Gap(20f);
            l.Label("RimMusic_Fusion_Title".Translate());
            l.GapLine();
            l.CheckboxLabeled("RimMusic_Fusion_Toggle".Translate(), ref settings.EnableCulturalFusion);
            l.Gap(10f);

            if (settings.EnableCulturalFusion)
            {
                settings.FusionMinorityThreshold = Widgets.HorizontalSlider(l.GetRect(24f), settings.FusionMinorityThreshold, 0f, 1f, true, "RimMusic_Fusion_Minority".Translate(settings.FusionMinorityThreshold.ToString("P0")), "0%", "100%");
                settings.ThreatStealThreshold = Widgets.HorizontalSlider(l.GetRect(24f), settings.ThreatStealThreshold, 0f, 1f, true, "RimMusic_Fusion_Steal".Translate(settings.ThreatStealThreshold.ToString("P0")), "0%", "100%");
                settings.ThreatHighTempoThreshold = Widgets.HorizontalSlider(l.GetRect(24f), settings.ThreatHighTempoThreshold, 0f, 1f, true, "RimMusic_Fusion_HighTempo".Translate(settings.ThreatHighTempoThreshold.ToString("P0")), "0%", "100%");
                settings.ThreatDominateThreshold = Widgets.HorizontalSlider(l.GetRect(24f), settings.ThreatDominateThreshold, 0f, 1f, true, "RimMusic_Fusion_Dominate".Translate(settings.ThreatDominateThreshold.ToString("P0")), "0%", "100%");
            }

            l.End();
        }

        private static void DrawIntegrations(Rect rect, RimMusicSettings settings)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(rect);

            l.Label("RimMusic_LLM_Title".Translate());
            l.GapLine();
            l.Label("RimMusic_LLM_Hint".Translate());
            l.Gap(15f);

            Rect toggleRimTalk = l.GetRect(30f);
            Widgets.DrawHighlightIfMouseover(toggleRimTalk);
            Widgets.CheckboxLabeled(toggleRimTalk, "RimMusic_LLM_UseRimTalk".Translate(), ref settings.UseRimTalkTextApi);

            if (!settings.UseRimTalkTextApi)
            {
                l.Gap(10f);
                l.Label("RimMusic_LLM_CustomTitle".Translate());
                l.GapLine();

                Rect urlRect = l.GetRect(24f);
                Widgets.Label(urlRect.LeftPart(0.2f), "API URL:");
                settings.CustomTextApiUrl = Widgets.TextField(urlRect.RightPart(0.8f), settings.CustomTextApiUrl);
                TooltipHandler.TipRegion(urlRect, "RimMusic_LLM_Url_Tip".Translate());
                l.Gap(5f);

                Rect keyRect = l.GetRect(24f);
                Widgets.Label(keyRect.LeftPart(0.2f), "Bearer Token:");
                settings.CustomTextApiKey = Widgets.TextField(keyRect.RightPart(0.8f), settings.CustomTextApiKey);
                l.Gap(5f);

                Rect modelRect = l.GetRect(24f);
                Widgets.Label(modelRect.LeftPart(0.2f), "Model Name:");
                settings.CustomTextModelName = Widgets.TextField(modelRect.RightPart(0.8f), settings.CustomTextModelName);
                TooltipHandler.TipRegion(modelRect, "RimMusic_LLM_Model_Tip".Translate());
            }
            else
            {
                l.Gap(10f);
                GUI.color = Color.gray;
                l.Label("RimMusic_LLM_IntegrationActive".Translate());
                GUI.color = Color.white;
            }

            l.Gap(25f);
            l.Label("RimMusic_Extraction_Title".Translate());
            l.GapLine();
            settings.DialogueLineLimit = (int)Widgets.HorizontalSlider(l.GetRect(24f), settings.DialogueLineLimit, 0f, 10f, true, "RimMusic_Extraction_Lines".Translate(settings.DialogueLineLimit), "0", "10");

            l.Gap(20f);
            l.Label("RimMusic_API_OverrideTitle".Translate());
            l.GapLine();
            l.CheckboxLabeled("RimMusic_API_RacePriority".Translate(), ref settings.EnableRaceOverride);
            if (settings.EnableRaceOverride)
            {
                l.CheckboxLabeled("RimMusic_API_RaceFusion".Translate(), ref settings.AllowRaceFusion);
            }

            l.End();
        }

        private static Vector2 _uiDebugScroll = Vector2.zero;

        private static void DrawUIAndDebugSettings(Rect rect, RimMusicSettings settings)
        {
            // 内容(快捷键+透明度+权重+自动生成区+AI截断+调试)总高度易超 rect,套 ScrollView 防裁切
            Widgets.BeginScrollView(rect, ref _uiDebugScroll, new Rect(0, 0, rect.width - 16f, 1100f));
            Listing_Standard l = new Listing_Standard();
            l.Begin(new Rect(0, 0, rect.width - 16f, 1100f));

            l.Label("RimMusic_HUD_SettingsTitle".Translate());
            l.GapLine();

            // HUD 呼出快捷键自定义区 — 由 Mod 选项独立管理，不依赖游戏全局按键绑定配置(后者被清理会失效)
            GUI.color = new Color(0.6f, 0.8f, 1f);
            l.Label("RimMusic_HUD_HotkeyTitle".Translate());
            GUI.color = Color.white;
            l.Label("RimMusic_HUD_HotkeyHint_Dynamic".Translate(BuildHotkeyDisplayString(settings)));
            l.Gap(6f);

            // 主键下拉选择(常用字母/数字/方向键子集)
            DrawHotkeyKeyPicker(l, settings);

            // 修饰键复选框
            l.CheckboxLabeled("RimMusic_HUD_Hotkey_RequireCtrl".Translate(), ref settings.HUDHotkeyRequireCtrl);
            l.CheckboxLabeled("RimMusic_HUD_Hotkey_RequireShift".Translate(), ref settings.HUDHotkeyRequireShift);
            l.CheckboxLabeled("RimMusic_HUD_Hotkey_RequireAlt".Translate(), ref settings.HUDHotkeyRequireAlt);
            l.Gap(10f);

            if (l.ButtonText("RimMusic_HUD_OpenManual".Translate()))
            {
                if (Current.Game == null)
                {
                    Messages.Message("RimMusic_Error_NoActiveSave".Translate(), MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    if (settings.DebugMode) Find.WindowStack.Add(new MusicInspectorWindow());
                    else Find.WindowStack.Add(new MusicHUDWindow());
                }
            }
            l.Gap(15f);

            settings.HUDTransparency = Widgets.HorizontalSlider(l.GetRect(24f), settings.HUDTransparency, 0f, 1f, true, "RimMusic_HUD_Alpha".Translate(settings.HUDTransparency.ToString("P0")));
            l.Gap(15f);

            // 注入 AI 曲的随机池权重——0 = 永不被随机选中, 值越高越优先于原版曲
            settings.InjectedSongWeight = Widgets.HorizontalSlider(l.GetRect(24f), settings.InjectedSongWeight, 0f, 50f, true, "RimMusic_HUD_InjectedWeight".Translate(settings.InjectedSongWeight.ToString("F1")), "0", "50");
            l.Gap(15f);

            // === 自动生成 (Auto Gen) 参数区 ===
            GUI.color = new Color(0.6f, 0.8f, 1f);
            l.Label("RimMusic_AutoGen_Title".Translate());
            GUI.color = Color.white;
            l.Gap(4f);

            settings.AutoGenThreatThreshold = Widgets.HorizontalSlider(l.GetRect(24f), settings.AutoGenThreatThreshold, 0f, 1f, true, "RimMusic_AutoGen_Threshold".Translate(settings.AutoGenThreatThreshold.ToString("F2")), "0", "1");
            l.Gap(6f);
            settings.AutoGenPeaceCooldown = Widgets.HorizontalSlider(l.GetRect(24f), settings.AutoGenPeaceCooldown, 0f, 120f * 60f, true, "RimMusic_AutoGen_PeaceCD".Translate((settings.AutoGenPeaceCooldown / 60f).ToString("F0")), "0m", "120m");
            l.Gap(6f);
            settings.AutoGenWarCooldown = Widgets.HorizontalSlider(l.GetRect(24f), settings.AutoGenWarCooldown, 0f, 60f * 60f, true, "RimMusic_AutoGen_WarCD".Translate((settings.AutoGenWarCooldown / 60f).ToString("F0")), "0m", "60m");
            l.Gap(6f);
            settings.AutoGenDailyLimit = (int)Widgets.HorizontalSlider(l.GetRect(24f), settings.AutoGenDailyLimit, 1f, 20f, true, "RimMusic_AutoGen_DailyLimit".Translate(settings.AutoGenDailyLimit), "1", "20");
            l.Gap(6f);
            l.CheckboxLabeled("RimMusic_AutoGen_SwitchOnComplete".Translate(), ref settings.AutoGenSwitchOnComplete);
            l.Gap(10f);

            if (l.ButtonText("RimMusic_AutoGen_ResetCooldowns".Translate()))
            {
                AutoGenCoordinator.ResetAllCooldowns();
                Messages.Message("RimMusic_AutoGen_ResetCooldowns_Done".Translate(), MessageTypeDefOf.PositiveEvent, false);
            }
            if (l.ButtonText("RimMusic_AutoGen_ResetDailyLimit".Translate()))
            {
                AutoGenCoordinator.ResetDailyLimit();
                Messages.Message("RimMusic_AutoGen_ResetDailyLimit_Done".Translate(), MessageTypeDefOf.PositiveEvent, false);
            }
            l.Gap(15f);

            l.Label("RimMusic_AI_TruncateTitle".Translate());
            l.GapLine();
            settings.MaxOutputWords = (int)Widgets.HorizontalSlider(l.GetRect(24f), settings.MaxOutputWords, 20f, 1000f, true, "RimMusic_AI_MaxWords".Translate(settings.MaxOutputWords), "20", "1000");
            if (settings.MaxOutputWords > 60)
            {
                GUI.color = new Color(1f, 0.7f, 0.3f);
                l.Label("RimMusic_AI_MaxWordsWarning".Translate());
                GUI.color = Color.white;
            }
            l.Gap(15f);

            l.Label("RimMusic_Dev_Title".Translate());
            l.GapLine();
            l.CheckboxLabeled("RimMusic_Dev_DebugMode".Translate(), ref settings.DebugMode);

            l.End();
            Widgets.EndScrollView();
        }

        // 构造当前快捷键的可读描述串，例如 "Ctrl + Shift + M" 或 "M"(无修饰键)
        private static string BuildHotkeyDisplayString(RimMusicSettings s)
        {
            var parts = new List<string>();
            if (s.HUDHotkeyRequireCtrl) parts.Add("Ctrl");
            if (s.HUDHotkeyRequireShift) parts.Add("Shift");
            if (s.HUDHotkeyRequireAlt) parts.Add("Alt");
            parts.Add(KeyToLabel(s.HUDHotkeyKeyCode));
            return string.Join(" + ", parts);
        }

        // 把 KeyCode 整数值翻译成玩家可读的标签
        private static string KeyToLabel(int code)
        {
            if (code <= 0) code = 39; // 回退到 M
            try
            {
                KeyCode kc = (KeyCode)code;
                // 字母/数字键用枚举名去掉前缀(Alpha/Keypad)即可
                string name = kc.ToString();
                if (name.StartsWith("Alpha")) return name.Substring(5);
                if (name.StartsWith("Keypad")) return "Num" + name.Substring(6);
                return name;
            }
            catch { return "M"; }
        }

        // 主键设置按钮状态：点击后进入"等待按键"模式，下次任意非修饰键按下即捕获为主键
        private static bool _awaitingHotkey = false;

        private static void DrawHotkeyKeyPicker(Listing_Standard l, RimMusicSettings s)
        {
            int current = Mathf.Clamp(s.HUDHotkeyKeyCode, 1, 500);
            string currentLabel = KeyToLabel(current);
            Rect row = l.GetRect(30f);

            Widgets.Label(new Rect(row.x, row.y, row.width * 0.4f, row.height), "RimMusic_HUD_Hotkey_MainKey".Translate());

            Rect btnRect = new Rect(row.x + row.width * 0.42f, row.y + 2f, 120f, row.height - 4f);
            if (_awaitingHotkey)
            {
                // 等待按键状态：按钮显示提示，并在本次 OnGUI 里捕获任意非修饰键
                Widgets.DrawBoxSolid(btnRect, new Color(0.3f, 0.5f, 0.8f, 0.5f));
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(btnRect, "RimMusic_HUD_Hotkey_Awaiting".Translate());
                Text.Anchor = TextAnchor.UpperLeft;

                // 扫描所有非修饰键，第一个按下的即捕获
                for (int code = 0; code < 500; code++)
                {
                    // 跳过 Ctrl/Shift/Alt 本身(它们是修饰键，由复选框控制)
                    if (code == (int)KeyCode.LeftControl || code == (int)KeyCode.RightControl
                        || code == (int)KeyCode.LeftShift || code == (int)KeyCode.RightShift
                        || code == (int)KeyCode.LeftAlt || code == (int)KeyCode.RightAlt
                        || code == (int)KeyCode.LeftCommand || code == (int)KeyCode.RightCommand)
                        continue;

                    if (Input.GetKeyDown((KeyCode)code))
                    {
                        s.HUDHotkeyKeyCode = code;
                        _awaitingHotkey = false;
                        break;
                    }
                }

                // Esc 取消设置，保留原值
                if (Input.GetKeyDown(KeyCode.Escape)) _awaitingHotkey = false;
            }
            else
            {
                // 正常状态：显示当前主键 + "设置"按钮
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(btnRect.x, btnRect.y, 40f, btnRect.height), $"<b>{currentLabel}</b>");
                Text.Anchor = TextAnchor.UpperLeft;

                Rect setBtn = new Rect(btnRect.x + 46f, btnRect.y, btnRect.width - 50f, btnRect.height);
                if (Widgets.ButtonText(setBtn, "RimMusic_HUD_Hotkey_SetBtn".Translate()))
                {
                    _awaitingHotkey = true;
                }
            }
            l.Gap(4f);
        }

        private static bool DrawNavButton(Listing_Standard l, string text, bool active)
        {
            Rect btnRect = l.GetRect(35f);
            if (active) Widgets.DrawBoxSolid(btnRect, new Color(0.2f, 0.4f, 0.6f, 0.5f));
            else if (Mouse.IsOver(btnRect)) Widgets.DrawBoxSolid(btnRect, new Color(0.3f, 0.3f, 0.3f, 0.3f));

            Widgets.Label(new Rect(btnRect.x + 10f, btnRect.y + 7f, btnRect.width - 20f, 24f), active ? $"<b>{text}</b>" : text);
            return Widgets.ButtonInvisible(btnRect);
        }

        private static bool DrawSubTabButton(Rect rect, string text, bool active)
        {
            if (active) Widgets.DrawBoxSolid(rect, new Color(0.3f, 0.3f, 0.3f, 1f));
            else Widgets.DrawBoxSolid(rect, new Color(0.15f, 0.15f, 0.15f, 1f));

            if (Mouse.IsOver(rect) && !active) Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.1f));

            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(rect, active ? $"<b><color=#FFDD44>{text}</color></b>" : text);
            Text.Anchor = TextAnchor.UpperLeft;
            return Widgets.ButtonInvisible(rect);
        }

        private static void DrawEditor(Rect r, PromptPreset p, PromptRole role, ref Vector2 s, string t)
        {
            Widgets.DrawBoxSolid(r, new Color(0.1f, 0.1f, 0.1f, 0.5f));
            Widgets.Label(r.TopPartPixels(25f), $"<b> {t}</b>");

            Rect body = r.ContractedBy(5f); body.yMin += 25f;
            var entry = p.Entries.Find(e => e.Role == role);
            if (entry == null) return;

            float cw = body.width - 16f;
            float ch = Mathf.Max(WrapTextAreaStyle.CalcHeight(new GUIContent(entry.Content), cw) + 40f, body.height);

            Widgets.BeginScrollView(body, ref s, new Rect(0, 0, cw, ch));
            string old = entry.Content;
            entry.Content = GUI.TextArea(new Rect(0, 0, cw, ch), entry.Content, WrapTextAreaStyle);
            if (Mouse.IsOver(body) && (Input.GetMouseButtonDown(0) || old != entry.Content)) CurrentEditingEntry = entry;
            Widgets.EndScrollView();
        }

        private static void DrawVarList(Rect r, ref Vector2 s)
        {
            Widgets.DrawBoxSolid(r, new Color(0.15f, 0.15f, 0.15f));
            Widgets.Label(new Rect(r.x + 5f, r.y + 5f, r.width, 25f), "RimMusic_Var_Title".Translate());
            Rect body = r.ContractedBy(5f); body.yMin += 25f;
            Widgets.BeginScrollView(body, ref s, new Rect(0, 0, body.width - 16f, 800f));
            Listing_Standard l = new Listing_Standard(); l.Begin(new Rect(0, 0, body.width - 16f, 800f));

            void H(string t) { l.Gap(10f); l.Label($"<b><color=#88AAFF>{t}</color></b>"); l.GapLine(); }
            void V(string c, string n, string d, string h) { Rect x = l.GetRect(28f); if (Widgets.ButtonText(x.LeftPart(0.45f), c)) InsertVar(c); Widgets.Label(x.RightPart(0.55f), $"<color={h}>{n}</color>"); TooltipHandler.TipRegion(x, d); l.Gap(2f); }

            H("RimMusic_Var_Cat_Macro".Translate());
            V("{{rimtalk.full_context}}", "RimMusic_Var_Full_Name".Translate(), "RimMusic_Var_Full_Tip".Translate(), "#FF4444");
            V("{{music.event_log}}", "RimMusic_Var_Event_Name".Translate(), "RimMusic_Var_Event_Tip".Translate(), "#FF4444");
            V("{{music.radar_log}}", "RimMusic_Var_Radar_Name".Translate(), "RimMusic_Var_Radar_Tip".Translate(), "#00FFFF");

            H("RimMusic_Var_Cat_Culture".Translate());
            // [V0.1.1 Beta] Introduced Culture Vibe Variable
            V("{{music.culture_vibe}}", "RimMusic_Var_Vibe_Name".Translate(), "RimMusic_Var_Vibe_Tip".Translate(), "#FF88FF");
            V("{{music.culture_instruments}}", "RimMusic_Var_Inst_Name".Translate(), "RimMusic_Var_Inst_Tip".Translate(), "#FF88FF");

            H("RimMusic_Var_Cat_Env".Translate());
            V("{{music.state}}", "RimMusic_Var_State_Name".Translate(), "RimMusic_Var_State_Tip".Translate(), "#88FF88");
            V("{{music.time_speed}}", "RimMusic_Var_Speed_Name".Translate(), "RimMusic_Var_Speed_Tip".Translate(), "#88FF88");
            V("{{music.season}}", "RimMusic_Var_Season_Name".Translate(), "RimMusic_Var_Season_Tip".Translate(), "#88FF88");
            V("{{music.weather}}", "RimMusic_Var_Weather_Name".Translate(), "RimMusic_Var_Weather_Tip".Translate(), "#88FF88");
            V("{{music.danger}}", "RimMusic_Var_Danger_Name".Translate(), "RimMusic_Var_Danger_Tip".Translate(), "#88FF88");

            H("RimMusic_Var_Cat_Micro".Translate());
            V("{{music.focus_name}}", "RimMusic_Var_Focus_Name".Translate(), "RimMusic_Var_Focus_Tip".Translate(), "#FFDD44");
            V("{{music.activity}}", "RimMusic_Var_Act_Name".Translate(), "RimMusic_Var_Act_Tip".Translate(), "#FFDD44");
            V("{{music.mood_level}}", "RimMusic_Var_Mood_Name".Translate(), "RimMusic_Var_Mood_Tip".Translate(), "#FFDD44");
            V("{{music.thoughts}}", "RimMusic_Var_Thought_Name".Translate(), "RimMusic_Var_Thought_Tip".Translate(), "#FFDD44");

            l.End(); Widgets.EndScrollView();
        }

        private static void ResetToDefaults(PromptPreset preset, RimMusicSettings settings)
        {
            preset.Entries.Clear(); preset.Entries.AddRange(PromptPreset.CreateDefault().Entries);

            bool isChineseEnv = LanguageDatabase.activeLanguage != null && LanguageDatabase.activeLanguage.folderName.StartsWith("Chinese");
            settings.MaxOutputWords = isChineseEnv ? 120 : 60;

            settings.EnableRaceOverride = false;
            settings.AllowRaceFusion = false;
            settings.EnableEnvironmentalRadar = false;
            settings.UseRimTalkTextApi = true;
            settings.CultureVibeWordLimit = 30;

            settings.CustomTextApiUrl = "https://api.siliconflow.cn";
            settings.CustomTextModelName = "deepseek-ai/DeepSeek-V3";

            settings.HoverRadius = 3f; settings.FocusCacheDuration = 5f;
            settings.SwitchDelay = 3f; settings.MacroZoomThreshold = 25f;
            settings.MacroExitDelay = 1f; settings.EventMemoryDuration = 60f;
            settings.DialogueLineLimit = 3;

            settings.EnableRealtimeMusic = false;
            settings.CustomAudioApiUrl = "https://api.302.ai";
            settings.SunoModelVersion = "chirp-bluejay";
            settings.SunoMakeInstrumental = true;

            Messages.Message("RimMusic_Msg_ResetDone".Translate(), MessageTypeDefOf.TaskCompletion, false);
        }

        private static void InsertVar(string s) { if (CurrentEditingEntry != null) CurrentEditingEntry.Content = CurrentEditingEntry.Content.Insert(CurrentEditingEntry.Content.Length, s); }
        public static void CheckFocusRelease(Rect r) { if (Event.current.type == EventType.MouseDown && !r.Contains(Event.current.mousePosition)) { GUI.FocusControl(null); GUIUtility.keyboardControl = 0; } }
    }

    public class MusicSettingsFloatingWindow : Window
    {
        public override Vector2 InitialSize => new Vector2(950f, 750f);

        public MusicSettingsFloatingWindow()
        {
            this.draggable = true;
            this.resizeable = false;
            this.doCloseX = true;
            this.closeOnAccept = false;
            this.closeOnCancel = true;
            this.forcePause = false;
            this.layer = WindowLayer.Dialog;

            this.preventCameraMotion = false;
            this.absorbInputAroundWindow = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            MusicSettingsUI.Draw(inRect, RimMusicMod.Settings.Preset, RimMusicMod.Settings);
        }

        public override void PostClose()
        {
            base.PostClose();
            RimMusicMod.Settings.Write();
        }
    }
}