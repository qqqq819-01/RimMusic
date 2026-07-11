using System;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using UnityEngine.Networking;
using Verse;
using RimWorld;
using RimMusic.Data;

namespace RimMusic.Core
{
    public class MusicAIClient
    {
        public static bool IsCircuitTripped { get; private set; } = false;
        private static int _consecutiveFailures = 0;
        private const int MaxFailuresBeforeTrip = 3;

        public static void ResetCircuit()
        {
            IsCircuitTripped = false;
            _consecutiveFailures = 0;
            Log.Message("[RimMusic] API circuit breaker manually reset. Network comms re-initialized.");
        }

        public static string ReplaceVars(string content, MusicContext x)
        {
            if (string.IsNullOrEmpty(content) || x == null) return "";
            return content.Replace("{{rimtalk.full_context}}", x.rt_full_context)
                          .Replace("{{music.radar_log}}", x.radar_log)
                          .Replace("{{music.event_log}}", x.event_log)
                          .Replace("{{music.time_speed}}", x.time_speed)
                          .Replace("{{music.nearby}}", x.music_nearby)
                          .Replace("{{music.location_details}}", x.music_location)
                          .Replace("{{music.wealth_level}}", x.music_wealth)
                          .Replace("{{music.state}}", x.state)
                          .Replace("{{rimtalk.persona}}", x.rt_persona)
                          .Replace("{{rimtalk.relations}}", x.rt_relations)
                          .Replace("{{rimtalk.dialogue}}", x.rt_dialogue)
                          .Replace("{{rimtalk.backstory}}", x.rt_backstory)
                          .Replace("{{music.season}}", x.season)
                          .Replace("{{music.weather}}", x.weather)
                          .Replace("{{music.danger}}", x.danger)
                          .Replace("{{music.colony_avg_mood}}", x.colony_avg_mood)
                          .Replace("{{music.focus_name}}", x.focus_name)
                          .Replace("{{music.activity}}", x.activity)
                          .Replace("{{music.mood_level}}", x.mood_level)
                          .Replace("{{music.thoughts}}", x.thoughts)
                          .Replace("{{music.culture_vibe}}", x.culture_vibe)
                          .Replace("{{music.culture_instruments}}", x.culture_instruments);
        }

        public static string BuildFullPromptString(PromptPreset preset, MusicContext ctx, int maxWords, out string sysPart, out string userPart, out string manPart)
        {
            sysPart = ""; userPart = ""; manPart = "";
            if (preset == null) return "";

            var sysEntry = preset.Entries.Find(e => e.Role == PromptRole.System);
            if (sysEntry != null && sysEntry.Enabled) sysPart = sysEntry.Content;

            var userEntry = preset.Entries.Find(e => e.Role == PromptRole.User);
            if (userEntry != null && userEntry.Enabled) userPart = ReplaceVars(userEntry.Content, ctx);

            var manEntry = preset.Entries.Find(e => e.Role == PromptRole.Mandatory);
            if (manEntry != null && manEntry.Enabled) manPart = ReplaceVars(manEntry.Content.Replace("{{MaxOutputWords}}", maxWords.ToString()), ctx);

            bool isChineseEnv = LanguageDatabase.activeLanguage != null && LanguageDatabase.activeLanguage.folderName.StartsWith("Chinese");
            if (isChineseEnv && RimMusicMod.Settings.ForceChineseOutput)
            {
                manPart += "\n\n[System Mandatory]\nOutput Language: Simplified Chinese (简体中文). Translate ALL content into Chinese. DO NOT output any English words.";
            }

            return $"{sysPart}\n\n{userPart}\n\n{manPart}".Trim();
        }

        public async Task<string> SendRequestViaRimTalkConfig(string fullPrompt, int maxWords)
        {
            string sysPrompt = "You are a top-tier cinematic music composer and audio engineer.";
            return await SendSmartChatCompletionAsync(sysPrompt, fullPrompt);
        }

        public async Task<string> GenerateParsedMusicPromptAsync(MusicContext ctx)
        {
            if (IsCircuitTripped) return "RimMusic_Error_CircuitTripped".Translate();

            var preset = RimMusicMod.Settings.Preset;
            int maxWords = RimMusicMod.Settings.MaxOutputWords;
            string sysPart, userPart, manPart;
            string fullPrompt = BuildFullPromptString(preset, ctx, maxWords, out sysPart, out userPart, out manPart);

            string jsonResponse = await SendSmartChatCompletionAsync(sysPart, $"{userPart}\n{manPart}");

            if (IsCircuitTripped) return "RimMusic_Error_CircuitTripped".Translate();

            var m = Regex.Match(jsonResponse, "\"content\"\\s*:\\s*\"(.*?)\"", RegexOptions.Singleline);
            if (m.Success)
            {
                return Regex.Unescape(m.Result("$1"));
            }
            return jsonResponse;
        }

        private async Task<string> SendSmartChatCompletionAsync(string sysPrompt, string userPrompt)
        {
            string finalUrl = "";
            string finalKey = "";
            string finalModel = "";

            bool isPlayer2 = false;

            if (RimMusicMod.Settings.UseRimTalkTextApi)
            {
                var rtSettings = RimTalk.Settings.Get();
                if (rtSettings == null || rtSettings.GetActiveConfig() == null)
                {
                    Log.Error("[RimMusic] RimTalk integration enabled, but configuration file is missing.");
                    return "Error: RimTalk missing or unconfigured";
                }
                var config = rtSettings.GetActiveConfig();
                finalUrl = config.BaseUrl;
                finalKey = config.ApiKey;
                finalModel = config.SelectedModel ?? "gpt-3.5-turbo";

                string providerStr = config.Provider.ToString();
                if (providerStr.Contains("Player2") || (finalUrl != null && finalUrl.Contains("player2")) || (finalKey != null && finalKey.StartsWith("p2_")))
                {
                    isPlayer2 = true;
                }
            }
            else
            {
                finalUrl = RimMusicMod.Settings.CustomTextApiUrl;
                finalKey = RimMusicMod.Settings.CustomTextApiKey;
                finalModel = RimMusicMod.Settings.CustomTextModelName;

                if (string.IsNullOrWhiteSpace(finalUrl) || string.IsNullOrWhiteSpace(finalKey))
                {
                    Log.Error("[RimMusic] Standalone LLM engine misconfigured. Missing URL or API Key.");
                    return "Error: Custom API missing";
                }
            }

            if (isPlayer2)
            {
                if (!string.IsNullOrEmpty(finalUrl) && !finalUrl.ToLower().Contains("player2"))
                {
                    Log.Warning($"[RimMusic] Stale endpoint detected in RimTalk config ({finalUrl}). Forcefully overriding with official Player2 coordinates.");
                }
                finalUrl = "https://api.player2.game/v1/chat/completions";
            }

            if (!string.IsNullOrEmpty(finalUrl) && !finalUrl.EndsWith("/chat/completions") && !finalUrl.EndsWith("/completions"))
            {
                finalUrl = finalUrl.TrimEnd('/') + "/v1/chat/completions";
            }

            try
            {
                if (string.IsNullOrEmpty(sysPrompt)) sysPrompt = "You are a top-tier cinematic music composer and audio engineer.";

                string newJsonBody = $"{{\"model\":\"{finalModel}\",\"messages\":[{{\"role\":\"system\",\"content\":\"{EscapeJson(sysPrompt)}\"}},{{\"role\":\"user\",\"content\":\"{EscapeJson(userPrompt)}\"}}],\"temperature\":0.7}}";

                using (UnityWebRequest webRequest = new UnityWebRequest(finalUrl, "POST"))
                {
                    byte[] bodyRaw = Encoding.UTF8.GetBytes(newJsonBody);
                    webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    webRequest.downloadHandler = new DownloadHandlerBuffer();
                    webRequest.SetRequestHeader("Content-Type", "application/json");
                    webRequest.SetRequestHeader("Authorization", "Bearer " + finalKey);

                    var op = webRequest.SendWebRequest();
                    while (!op.isDone) await Task.Delay(20);

                    string responseText = webRequest.downloadHandler.text;
                    long code = webRequest.responseCode;

                    bool isHttpError = webRequest.result != UnityWebRequest.Result.Success;
                    bool isJsonError = !string.IsNullOrEmpty(responseText) && responseText.Contains("\"error\"") && responseText.Contains("\"message\"");

                    if (isHttpError || isJsonError)
                    {
                        _consecutiveFailures++;
                        if (_consecutiveFailures >= MaxFailuresBeforeTrip)
                        {
                            IsCircuitTripped = true;
                            Log.Error($"[RimMusic] LLM API critical failure (HTTP {code}).\nEndpoint: {finalUrl}\nResponse: {responseText}");
                        }
                        return $"Error: {code}\n{responseText}";
                    }

                    if (_consecutiveFailures > 0)
                    {
                        Log.Message("[RimMusic] LLM API connection recovered. Fault counter reset.");
                        _consecutiveFailures = 0;
                    }
                    return responseText;
                }
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= MaxFailuresBeforeTrip) IsCircuitTripped = true;
                return $"Error: {ex.Message}";
            }
        }

        private string EscapeJson(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t");
    }
}