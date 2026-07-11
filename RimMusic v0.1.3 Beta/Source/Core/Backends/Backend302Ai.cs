using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace RimMusic.Core.Backends
{
    /// <summary>
    /// 302.ai Suno proxy backend. Extracted verbatim (behaviour preserved) from the
    /// original RealtimeMusicEngine.Request302SunoAudioAsync: submit, poll, fetch the
    /// SUCCESS payload, and return de-duplicated audio URLs. Downloading + playback
    /// stay in the engine.
    /// </summary>
    public class Backend302Ai : IAudioBackend
    {
        public string Name => "302.ai (Suno)";

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(RimMusicMod.Settings.CustomAudioApiUrl) &&
            !string.IsNullOrWhiteSpace(RimMusicMod.Settings.SunoApiKey);

        private static readonly HttpClient _httpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };

        private static string EscapeJson(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return input.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t");
        }

        public async Task<List<string>> GenerateAudioUrlsAsync(string generatedPrompt, string focusName, ConcurrentQueue<Action> mainThreadActions)
        {
            string apiUrl = RimMusicMod.Settings.CustomAudioApiUrl.TrimEnd('/');
            string apiKey = RimMusicMod.Settings.SunoApiKey.Trim();
            string modelVer = RimMusicMod.Settings.SunoModelVersion.Trim();

            string isInstStr = RimMusicMod.Settings.SunoMakeInstrumental ? "true" : "false";
            string requestJson = $@"{{ ""gpt_description_prompt"": ""{EscapeJson(generatedPrompt)}"", ""mv"": ""{modelVer}"", ""make_instrumental"": {isInstStr} }}";

            mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Dispatching audio generation task to 302.ai proxy node. Awaiting confirmation..."));

            var request = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/suno/submit/music");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Headers.Add("Accept", "application/json");
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            string responseStr = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] 302.ai rejected request: {response.StatusCode}\n{responseStr}"));
                return new List<string>();
            }

            var matchId = System.Text.RegularExpressions.Regex.Match(responseStr, "\"data\"\\s*:\\s*\"([^\"]+)\"");
            if (!matchId.Success) matchId = System.Text.RegularExpressions.Regex.Match(responseStr, "\"id\"\\s*:\\s*\"([^\"]+)\"");

            if (!matchId.Success)
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Failed to extract Task ID from 302.ai response.\n{responseStr}"));
                return new List<string>();
            }
            string taskId = matchId.Groups[1].Value;

            mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] 302.ai task authorized. ID: {taskId}. Entering background polling sequence..."));

            List<string> audioUrls = new List<string>();

            // Extended polling from 60 to 120 ticks (10 full minutes) to accommodate slow AI nodes
            int maxRetries = 120;
            int currentRetry = 0;

            while (currentRetry < maxRetries)
            {
                await Task.Delay(5000);
                currentRetry++;

                var checkReq = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}/suno/fetch/{taskId}");
                checkReq.Headers.Add("Authorization", $"Bearer {apiKey}");
                var checkRes = await _httpClient.SendAsync(checkReq);
                string checkStr = await checkRes.Content.ReadAsStringAsync();

                bool isComplete = checkStr.Contains("\"status\":\"SUCCESS\"") || checkStr.Contains("\"status\": \"SUCCESS\"") || checkStr.Contains("\"status\":\"completed\"");

                if (isComplete)
                {
                    var urlMatches = System.Text.RegularExpressions.Regex.Matches(checkStr, "\"audio_url\"\\s*:\\s*\"([^\"]+)\"");
                    HashSet<string> uniqueUrls = new HashSet<string>();
                    foreach (System.Text.RegularExpressions.Match m in urlMatches)
                    {
                        string matchedUrl = m.Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(matchedUrl)) uniqueUrls.Add(matchedUrl);
                    }

                    audioUrls = uniqueUrls.ToList();
                    if (audioUrls.Count > 0) break;
                }
                else if (checkStr.Contains("\"status\":\"FAILED\"") || checkStr.Contains("\"status\":\"error\""))
                {
                    mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] 302.ai audio generation task failed.\nPrompt: {generatedPrompt}\nModel: {modelVer}\nRaw API Blackbox: {checkStr}"));
                    return new List<string>();
                }
            }

            if (audioUrls.Count == 0)
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] 302.ai polling sequence timed out after 10 minutes. API failed to deliver audio payloads."));
            }

            return audioUrls;
        }
    }
}
