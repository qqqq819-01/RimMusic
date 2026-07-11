using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace RimMusic.Core.Backends
{
    /// <summary>
    /// Google Flow Music (flowmusic.app) web backend. Drives the internal REST API at
    /// https://www.flowmusic.app/__api/* using the player's own Supabase refresh_token.
    ///
    /// Flow Music is a Google Labs product (formerly Producer AI / Riffusion) backed by
    /// the Lyria 3 Pro model. Authentication is delegated to Supabase PKCE; the only
    /// long-lived credential the player needs is the refresh_token copied from the
    /// browser's Network tab (sb.flowmusic.app/auth/v1/token → token?grant_type=pkce →
    /// Response → refresh_token). The backend refreshes it into a 1-hour access_token
    /// (a Supabase JWT) and uses that as a Bearer header against /__api/*.
    ///
    /// Verified request chain (captured 2026-07-04):
    ///   1. POST /__api/conversation                  → {"job_id":"<op_id>"}
    ///   2. GET  /__api/audio-create-song-status/<op_id>  (poll)  → {"status":"complete","clip_id":"<clip_id>"}
    ///   3. POST /__api/clips  body={"clip_ids":["<clip_id>"]}   → {"clips":{<clip_id>:{...,"audio_url":"https://storage.googleapis.com/...","wav_url":"..."}}}
    ///
    /// Audio URLs are public GCS links (m4a + wav). No signature, no WebSocket, no
    /// Cloudflare challenge on the API path with a valid Bearer token.
    /// This consumes the player's own Flow Music free credit balance — NOT a paid API.
    /// </summary>
    public class BackendFlowMusicWeb : IAudioBackend
    {
        public string Name => "Flow Music Web (free quota)";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(RimMusicMod.Settings.FlowMusicRefreshToken);

        // .NET Framework 4.8 default SecurityProtocol may not include TLS 1.2/1.3.
        // flowmusic.app is fronted by Cloudflare and rejects legacy TLS.
        private static int _tlsPatched;
        private static void EnsureTlsVersions()
        {
            if (System.Threading.Interlocked.Exchange(ref _tlsPatched, 1) != 0) return;
            try
            {
                var allFlags = SecurityProtocolType.Tls12;
                var tls13 = (SecurityProtocolType)0x00003000; // SecurityProtocolType.Tls13 numeric
                if (Enum.IsDefined(typeof(SecurityProtocolType), tls13)) allFlags |= tls13;
                ServicePointManager.SecurityProtocol = allFlags;
                ServicePointManager.CheckCertificateRevocationList = false;
            }
            catch { /* non-fatal */ }
        }

        // Single shared HttpClient. Timeouts are generous: Lyria renders can take 2–3 min.
        private static readonly HttpClient _httpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };

        // Supabase project ref is encoded inside the access_token's `iss` claim, but we
        // can also just derive it once at refresh time and cache it. To stay robust
        // against project migration we parse it from the JWT iss on each refresh.
        private const string FlowOrigin = "https://www.flowmusic.app";
        private const string FlowApiBase = "https://www.flowmusic.app/__api";

        public async Task<List<string>> GenerateAudioUrlsAsync(string generatedPrompt, string focusName, ConcurrentQueue<Action> mainThreadActions)
        {
            string refreshToken = (RimMusicMod.Settings.FlowMusicRefreshToken ?? "").Trim();
            if (string.IsNullOrEmpty(refreshToken))
            {
                mainThreadActions.Enqueue(() => Log.Error("[RimMusic] Flow Music: refresh_token is empty. Sign into flowmusic.app → F12 → Network → filter `sb.flowmusic.app/auth/v1/token` → log in with Google → open the `token?grant_type=pkce` request → Response → copy refresh_token into Mod settings."));
                return new List<string>();
            }

            EnsureTlsVersions();

            // ---- Step 0: refresh the Supabase access_token ----
            mainThreadActions.Enqueue(() => Log.Message("[RimMusic] Flow Music: refreshing Supabase access_token..."));

            string accessToken;
            try
            {
                accessToken = await RefreshAccessTokenAsync(refreshToken, mainThreadActions);
                if (string.IsNullOrEmpty(accessToken))
                {
                    mainThreadActions.Enqueue(() => Log.Error("[RimMusic] Flow Music: token refresh returned no access_token. The refresh_token may have been revoked (signing out of flowmusic.app in the browser revokes it). Re-capture a fresh refresh_token."));
                    return new List<string>();
                }
            }
            catch (Exception ex)
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: Supabase token refresh failed: {ex.GetType().Name}: {ex.Message}"));
                return new List<string>();
            }

            mainThreadActions.Enqueue(() => Log.Message("[RimMusic] Flow Music: access_token refreshed. Dispatching prompt..."));

            // ---- Step 1: POST /__api/conversation ----
            string jobId;
            try
            {
                jobId = await PostConversationAsync(accessToken, generatedPrompt, mainThreadActions);
                if (string.IsNullOrEmpty(jobId))
                {
                    mainThreadActions.Enqueue(() => Log.Error("[RimMusic] Flow Music: conversation endpoint returned no job_id."));
                    return new List<string>();
                }
            }
            catch (Exception ex)
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: conversation POST failed: {ex.GetType().Name}: {ex.Message}"));
                return new List<string>();
            }

            mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: job accepted (job_id={jobId}). Opening SSE stream for operation_ids..."));

            // ---- Step 1.5: stream /__api/messages/<job_id>/stream ----
            // Captured 2026-07-05: an A/B pair arrives as TWO independent v5 op_ids in
            // the same SSE stream. Each op_id has its own status endpoint yielding one
            // clip_id. Collect all v5 UUIDs here, then poll each.
            List<string> operationIds;
            try
            {
                operationIds = await StreamForOperationIdsAsync(accessToken, jobId, mainThreadActions);
                if (operationIds.Count == 0)
                {
                    mainThreadActions.Enqueue(() => Log.Error("[RimMusic] Flow Music: SSE stream yielded no v5 operation_id."));
                    return new List<string>();
                }
            }
            catch (Exception ex)
            {
                mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: SSE stream read failed: {ex.GetType().Name}: {ex.Message}. Falling back to job_id (will likely 404)."));
                operationIds = new List<string> { jobId };
            }

            mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: resolved {operationIds.Count} operation_id(s). Polling render status..."));

            // ---- Step 2: poll /__api/audio-create-song-status/<op_id> for each op_id ----
            // Each op_id yields exactly one clip_id (confirmed via capture). Poll them
            // in parallel to minimize latency for the A/B pair.
            var allClipIds = new List<string>();
            var pollTasks = new List<Task<List<string>>>();
            foreach (string opId in operationIds)
            {
                string opIdLocal = opId;
                pollTasks.Add(PollSongStatusAsync(accessToken, opIdLocal, mainThreadActions));
            }
            try
            {
                var results = await Task.WhenAll(pollTasks);
                foreach (var clips in results)
                {
                    if (clips != null) allClipIds.AddRange(clips);
                }
                if (allClipIds.Count == 0)
                {
                    mainThreadActions.Enqueue(() => Log.Error("[RimMusic] Flow Music: render did not produce any clip_id (timed out or failed)."));
                    return new List<string>();
                }
            }
            catch (Exception ex)
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: status polling failed: {ex.GetType().Name}: {ex.Message}"));
                return new List<string>();
            }

            mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: render complete ({allClipIds.Count} clip(s)). Downloading mp3..."));

            // ---- Step 3: for each clip, GET /__api/download/audio/<clip_id>?format=mp3 ----
            // mp3 endpoint needs a Bearer header (NOT a public GCS link), so the backend
            // downloads the bytes itself, writes them to the player's music folder, and
            // returns file:// URLs. The engine's headerless downloader then "downloads"
            // the local files (HttpClient supports file:// on desktop .NET + Unity Mono)
            // — zero engine changes, native mp3 the whole way. Fallback per clip if mp3
            // fails: return the public GCS wav_url (no auth, Unity plays wav natively).
            List<string> audioUrls;
            try
            {
                audioUrls = await DownloadAndReturnAudioAsync(accessToken, allClipIds, mainThreadActions);
            }
            catch (Exception ex)
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: audio download failed: {ex.GetType().Name}: {ex.Message}"));
                return new List<string>();
            }

            if (audioUrls.Count > 0)
            {
                mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: secured {audioUrls.Count} audio URL(s)."));
            }
            else
            {
                mainThreadActions.Enqueue(() => Log.Error("[RimMusic] Flow Music: all mp3 + wav fallback attempts failed."));
            }
            return audioUrls;
        }

        // --------------------------------------------------------------------------------
        // Step 0 — Supabase token refresh.
        //
        // The Supabase project ref is embedded in the access_token's `iss` claim as
        // `https://<ref>.supabase.co/auth/v1`. We don't know the ref ahead of time
        // (the player only pastes a refresh_token), so we parse it from the refreshed
        // JWT. But to GET the JWT we need the ref... solve by trying the well-known
        // project URL captured from the live trace: ednjccqcmbxeaxbidinr.supabase.co.
        // If Flow Music ever migrates Supabase projects the player will see a 404 and
        // we can add a settings field for the ref; for now hard-coding is simplest.
        // --------------------------------------------------------------------------------
        private const string SupabaseRef = "ednjccqcmbxeaxbidinr";
        private const string SupabaseAuthBase = "https://ednjccqcmbxeaxbidinr.supabase.co/auth/v1";

        // Public anon key for Flow Music's Supabase project. Supabase requires an
        // `apikey` header on all auth requests; this is the public anon key embedded
        // in the browser bundle (not a secret — it's safe to ship, it only identifies
        // the project, and Row-Level Security enforces actual authorization).
        // Extracted from flowmusic.app's _app-*.js bundle: payload decodes to
        // {"iss":"supabase","ref":"ednjccqcmbxeaxbidinr","role":"anon",...}.
        private const string SupabaseAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImVkbmpjY3FjbWJ4ZWF4YmlkaW5yIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzE1NjEwNjQsImV4cCI6MjA4NzEzNzA2NH0.XCXSuL7Th1xHecfRrP0vAOFmKwJxwBqVFLu06SxtVzg";

        private async Task<string> RefreshAccessTokenAsync(string refreshToken, ConcurrentQueue<Action> mainThreadActions)
        {
            // grant_type=refresh_token is the standard Supabase PKCE refresh flow.
            string url = $"{SupabaseAuthBase}/token?grant_type=refresh_token";
            string body = "{\"refresh_token\":\"" + JsonEscape(refreshToken) + "\"}";

            // Per-request timeout (15s) + transient-failure retry (3 attempts).
            // Token refresh is the first call on every generate; a hang here freezes
            // the whole engine before any user-visible error can surface.
            string respStr = null;
            HttpStatusCode respCode = HttpStatusCode.OK;
            bool reqOk = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Add("apikey", SupabaseAnonKey);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    using (var resp = await _httpClient.SendAsync(req, cts.Token))
                    {
                        respCode = resp.StatusCode;
                        respStr = await resp.Content.ReadAsStringAsync();
                        reqOk = resp.IsSuccessStatusCode;
                    }
                    break;
                }
                catch (OperationCanceledException)
                {
                    mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: Supabase refresh timed out (attempt {attempt + 1}/3)."));
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
                {
                    mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: Supabase refresh network error (attempt {attempt + 1}/3): {ex.GetType().Name}: {ex.Message}"));
                }
                if (attempt < 2) await Task.Delay(2000 * (attempt + 1));
            }

            if (respStr == null)
            {
                mainThreadActions.Enqueue(() => Log.Error("[RimMusic] Flow Music: Supabase refresh failed after 3 attempts (no response)."));
                return null;
            }

            if (!reqOk)
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: Supabase refresh returned {(int)respCode} {respCode}: {respStr}"));
                return null;
            }

            // Extract access_token. Prefer regex over a JSON parser (no dependency in
            // .NET 4.8 without System.Text.Json, and the field is a flat top-level key).
            var m = Regex.Match(respStr, "\"access_token\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success)
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: refresh response had no access_token: {respStr}"));
                return null;
            }

            // Also persist the rotated refresh_token if Supabase returned a new one
            // (Supabase rotates refresh tokens on each refresh by default). This keeps
            // the Mod working across sessions without the player re-capturing.
            var rtMatch = Regex.Match(respStr, "\"refresh_token\"\\s*:\\s*\"([^\"]+)\"");
            if (rtMatch.Success)
            {
                string newRt = rtMatch.Groups[1].Value;
                if (!string.IsNullOrEmpty(newRt) && newRt != refreshToken)
                {
                    RimMusicMod.Settings.FlowMusicRefreshToken = newRt;
                    // Persist asynchronously — WriteSettings is main-thread only in
                    // RimWorld; defer to a queued main-thread action.
                    mainThreadActions.Enqueue(() =>
                    {
                        try { RimMusicMod.Settings.Write(); Log.Message("[RimMusic] Flow Music: refresh_token rotated and persisted."); }
                        catch (Exception ex) { Log.Warning($"[RimMusic] Flow Music: failed to persist rotated refresh_token: {ex.Message}"); }
                    });
                }
            }

            return m.Groups[1].Value;
        }

        // --------------------------------------------------------------------------------
        // Step 1 — POST /__api/conversation
        //
        // Payload shape (captured):
        //   {
        //     "parts":[{"content":"<prompt>","part_kind":"user-prompt"}],
        //     "client_context":{"song_queue":[],"selected_model":null,
        //                        "lyrics_id_map":{},"ghostwriter_version":"standard"},
        //     "model_name":"producer:standard",
        //     "mode":"standard"
        //   }
        //
        // The first generation in a session omits current_song_id; subsequent ones
        // include it. We always send the "fresh session" shape — Flow Music accepts
        // it and starts a new song. instrumental is conveyed by prefixing the prompt
        // with [Instrumental] (matching useapi.net's documented convention) rather
        // than a separate field, since the captured conversation payload has no such
        // field.
        // --------------------------------------------------------------------------------
        private async Task<string> PostConversationAsync(string accessToken, string prompt, ConcurrentQueue<Action> mainThreadActions)
        {
            string idea = prompt;
            if (RimMusicMod.Settings.SunoMakeInstrumental)
            {
                // Prepend the [Instrumental] tag to force an instrumental render.
                if (!idea.StartsWith("[Instrumental]", StringComparison.OrdinalIgnoreCase))
                    idea = "[Instrumental] " + idea;
            }

            string body = "{\"parts\":[{\"content\":\"" + JsonEscape(idea) + "\",\"part_kind\":\"user-prompt\"}],"
                + "\"client_context\":{\"song_queue\":[],\"selected_model\":null,\"lyrics_id_map\":{},\"ghostwriter_version\":\"standard\"},"
                + "\"model_name\":\"producer:standard\",\"mode\":\"standard\"}";

            // DIAGNOSTIC: log the exact body bytes so it can be diffed against a fresh
            // browser capture. The handover.md body (~196 B) is from an earlier
            // protocol version; the 2026-07-04 capture is 227 B. A body-shape mismatch
            // makes the server return a placeholder job_id that 404s on status poll.
            mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: /conversation body ({body.Length} B): {body}"));

            // Per-request timeout (20s) + transient-failure retry (3 attempts).
            // Without this, a dropped connection on /conversation hangs the whole
            // generate pipeline indefinitely ("stuck on generating").
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, $"{FlowApiBase}/conversation");
                    req.Headers.Add("Authorization", "Bearer " + accessToken);
                    req.Headers.Add("Origin", FlowOrigin);
                    req.Headers.Add("Referer", FlowOrigin + "/");
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    using (var resp = await _httpClient.SendAsync(req, cts.Token))
                    {
                        string respStr = await resp.Content.ReadAsStringAsync();
                        // DIAGNOSTIC: log full conversation response — if the job_id here
                        // 404s on status poll, comparing this response shape with a browser
                        // capture pinpoints the protocol drift.
                        mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: /conversation resp ({(int)resp.StatusCode}): {respStr}"));
                        if (!resp.IsSuccessStatusCode)
                        {
                            mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: /conversation returned {(int)resp.StatusCode} {resp.StatusCode}: {respStr}"));
                            return null;
                        }
                        var m = Regex.Match(respStr, "\"job_id\"\\s*:\\s*\"([^\"]+)\"");
                        return m.Success ? m.Groups[1].Value : null;
                    }
                }
                catch (OperationCanceledException)
                {
                    mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: /conversation timed out (attempt {attempt + 1}/3)."));
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
                {
                    mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: /conversation network error (attempt {attempt + 1}/3): {ex.GetType().Name}: {ex.Message}"));
                }
                if (attempt < 2) await Task.Delay(2000 * (attempt + 1));
            }
            mainThreadActions.Enqueue(() => Log.Error("[RimMusic] Flow Music: /conversation failed after 3 attempts."));
            return null;
        }

        // --------------------------------------------------------------------------------
        // Step 1.5 — Stream GET /__api/messages/<job_id>/stream?last_id=0  (SSE)
        //
        // Captured 2026-07-05: /conversation returns a v4 job_id, but the status
        // endpoint path param is a UUID v5 (version nibble '5' at start of 3rd
        // group, e.g. 232d1c5d-4c97-5468-bb52-21ce1bcf6449). That v5 op_id is
        // emitted inside the SSE event stream on /__api/messages/<job_id>/stream.
        //
        // We read events (≤ 30 s) and return the first UUID whose version nibble
        // is '5'. We also log the first few raw events so the field layout can be
        // confirmed against a live capture if the heuristic ever drifts.
        // --------------------------------------------------------------------------------
        // Captured 2026-07-05: an A/B pair arrives as TWO independent v5 operation_ids
        // in the SAME SSE stream (e.g. 2e429285-...-b4da-... and b3799de0-...-7a9a-...).
        // Each op_id has its own /audio-create-song-status/<op_id> endpoint yielding
        // one clip_id. So we must collect ALL v5 UUIDs from the stream, not just the
        // first. We keep reading until the stream closes, the deadline (40s) hits, or
        // we've seen 2 op_ids (hard cap — Flow Music free tier never returns >2).
        private async Task<List<string>> StreamForOperationIdsAsync(string accessToken, string jobId, ConcurrentQueue<Action> mainThreadActions)
        {
            string url = $"{FlowApiBase}/messages/{Uri.EscapeDataString(jobId)}/stream?last_id=0";

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", "Bearer " + accessToken);
            req.Headers.Add("Origin", FlowOrigin);
            req.Headers.Add("Referer", FlowOrigin + "/");
            req.Headers.Add("Accept", "text/event-stream");
            req.Headers.Add("Cache-Control", "no-cache");

            var opIds = new List<string>();
            var seen = new HashSet<string>();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
            int loggedEvents = 0;

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                using (var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string errStr = await resp.Content.ReadAsStringAsync();
                        mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: SSE stream returned {(int)resp.StatusCode} {resp.StatusCode}: {errStr}"));
                        return opIds;
                    }

                    using (var stream = await resp.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var dataBuf = new StringBuilder();
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null && DateTime.UtcNow < deadline && opIds.Count < 2)
                        {
                            if (line.Length == 0)
                            {
                                if (dataBuf.Length > 0)
                                {
                                    string data = dataBuf.ToString();

                                    if (loggedEvents < 3)
                                    {
                                        string preview = data.Length > 400 ? data.Substring(0, 400) + "..." : data;
                                        mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: SSE event #{loggedEvents + 1}: {preview}"));
                                        loggedEvents++;
                                    }

                                    // Collect every UUID whose 3rd group starts with '5' (UUID v5).
                                    foreach (Match m in Regex.Matches(data, "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-5[0-9a-fA-F]{3}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})"))
                                    {
                                        string id = m.Groups[1].Value;
                                        if (seen.Add(id))
                                        {
                                            opIds.Add(id);
                                            mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: operation_id #{opIds.Count} captured ({id})."));
                                        }
                                        if (opIds.Count >= 2) break;
                                    }
                                    dataBuf.Clear();
                                }
                                continue;
                            }

                            if (line.StartsWith("data:"))
                                dataBuf.AppendLine(line.Substring(5).TrimStart());
                    }
                }
            }
            }
            catch (OperationCanceledException)
            {
                mainThreadActions.Enqueue(() => Log.Warning("[RimMusic] Flow Music: SSE stream timed out (40s)."));
            }
            catch (Exception ex)
            {
                mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: SSE stream read failed: {ex.GetType().Name}: {ex.Message}"));
            }

            if (opIds.Count == 0)
                mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: SSE stream ended without any v5 op_id for job {jobId}."));
            return opIds;
        }

        // --------------------------------------------------------------------------------
        // Step 2 — Poll GET /__api/audio-create-song-status/<operation_id>
        //
        // Returns: {operation_id, status, progress, clip_id, error_type, error_message}
        // status values observed: "pending"/"running" (inferred) and "complete".
        // On complete, clip_id is a rendered clip. error_type != null means failure.
        //
        // One conversation may yield 1 or 2 clips (A/B pair). After the first "complete"
        // we keep polling a few more times to collect any second clip_id the server
        // emits under the same operation_id. Dedupe by clip_id.
        //
        // Poll cadence: 4 seconds. Timeout: 6 minutes total. After first complete, poll
        // up to 3 more times (≈12s) hunting a second clip — if none appears, settle.
        // --------------------------------------------------------------------------------
        private async Task<List<string>> PollSongStatusAsync(string accessToken, string operationId, ConcurrentQueue<Action> mainThreadActions)
        {
            string url = $"{FlowApiBase}/audio-create-song-status/{Uri.EscapeDataString(operationId)}";

            List<string> clipIds = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            bool gotComplete = false;
            int extraPollsAfterComplete = 0;
            int maxPolls = 90;   // 90 × 4s = 6 min

            for (int i = 0; i < maxPolls; i++)
            {
                // Per-request timeout (15s) + transient-failure retry (3 attempts).
                // Network drops no longer hang the engine indefinitely: a stuck poll
                // aborts after 15 s, retries twice, then surfaces the error.
                string respStr = null;
                HttpStatusCode respCode = HttpStatusCode.OK;
                bool reqOk = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.Add("Authorization", "Bearer " + accessToken);
                        req.Headers.Add("Origin", FlowOrigin);
                        req.Headers.Add("Referer", FlowOrigin + "/");

                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        using (var resp = await _httpClient.SendAsync(req, cts.Token))
                        {
                            respCode = resp.StatusCode;
                            respStr = await resp.Content.ReadAsStringAsync();
                            reqOk = resp.IsSuccessStatusCode;
                        }
                        break; // got a response (even if non-2xx) — stop retrying.
                    }
                    catch (OperationCanceledException)
                    {
                        mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: status poll timed out (attempt {attempt + 1}/3)."));
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
                    {
                        mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: status poll network error (attempt {attempt + 1}/3): {ex.GetType().Name}: {ex.Message}"));
                    }
                    if (attempt < 2) await Task.Delay(2000 * (attempt + 1)); // 2s, 4s backoff
                }

                if (respStr == null)
                {
                    // All 3 attempts failed (timeout/network) — wait one poll interval and continue.
                    // Don't abort the whole render: a transient outage shouldn't kill an in-flight job.
                    await Task.Delay(4000);
                    continue;
                }

                if (!reqOk)
                {
                    mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: status poll returned {(int)respCode} {respCode}: {respStr}"));
                    // 404 "Operation not found" is terminal — the job_id is wrong or the op
                    // expired server-side. Retry won't help; abort to unblock the engine.
                    if (respCode == HttpStatusCode.NotFound) return null;
                    await Task.Delay(4000);
                    continue;
                }

                string status = ExtractStringField(respStr, "status");
                string errorType = ExtractStringField(respStr, "error_type");

                if (errorType != null && errorType != "null")
                {
                    string errMsg = ExtractStringField(respStr, "error_message");
                    mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: render failed. error_type={errorType}, message={errMsg}. Full: {respStr}"));
                    return null;
                }

                if (status == "complete")
                {
                    gotComplete = true;
                    // DIAGNOSTIC: log full complete response once, so multi-clip layout
                    // (array vs repeated field) can be confirmed. A/B pairs sometimes
                    // arrive in one response, sometimes across consecutive polls.
                    if (clipIds.Count == 0)
                    {
                        string preview = respStr.Length > 600 ? respStr.Substring(0, 600) + "..." : respStr;
                        mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: first complete response: {preview}"));
                    }
                    // Extract ALL clip_id occurrences in this response (A/B pair may
                    // arrive together in one payload as an array or repeated field).
                    foreach (Match m in Regex.Matches(respStr, "\"clip_id\"\\s*:\\s*\"([^\"]+)\""))
                    {
                        string cid = m.Groups[1].Value;
                        if (seen.Add(cid))
                        {
                            clipIds.Add(cid);
                            mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: clip {clipIds.Count} ready (clip_id={cid})."));
                        }
                    }
                    // Fallback: if regex found none but ExtractStringField does, use it.
                    if (clipIds.Count == 0)
                    {
                        string clipId = ExtractStringField(respStr, "clip_id");
                        if (!string.IsNullOrEmpty(clipId) && seen.Add(clipId))
                        {
                            clipIds.Add(clipId);
                            mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: clip {clipIds.Count} ready (clip_id={clipId})."));
                        }
                    }

                    // After first complete, poll a few more times hunting a second clip.
                    // If we've already done 3 extra polls, settle and return.
                    if (clipIds.Count >= 2 || extraPollsAfterComplete >= 3)
                    {
                        return clipIds;
                    }
                    extraPollsAfterComplete++;
                    await Task.Delay(4000);
                    continue;
                }

                if (gotComplete)
                {
                    // status flipped back from complete (rare) — keep hunting.
                    extraPollsAfterComplete++;
                    if (extraPollsAfterComplete >= 3) return clipIds;
                }

                await Task.Delay(4000);
            }

            if (clipIds.Count > 0) return clipIds;
            mainThreadActions.Enqueue(() => Log.Error("[RimMusic] Flow Music: render timed out after 6 minutes of polling."));
            return null;
        }

        // --------------------------------------------------------------------------------
        // Step 3 — For each clip: download mp3 with Bearer auth, write to disk, return
        // file:// URL. Fallback per clip: public GCS wav_url via POST /__api/clips.
        //
        // Primary: GET /__api/download/audio/<clip_id>?format=mp3
        //   - Requires Authorization: Bearer <access_token>
        //   - Returns audio/mpeg byte stream (Content-Disposition: attachment)
        //   - We read bytes, write to Settings.GetActualSavePath()/FlowMusic_<clip_id>.mp3
        //   - Return file:// URL so the engine's headerless downloader can re-read it
        //
        // Fallback (only if mp3 download fails for that clip): POST /__api/clips with
        // that single clip_id, extract its wav_url, return it. wav_url is an
        // unauthenticated public https URL and Unity natively plays wav, so the engine's
        // existing pipeline handles it transparently.
        // --------------------------------------------------------------------------------
        private async Task<List<string>> DownloadAndReturnAudioAsync(string accessToken, List<string> clipIds, ConcurrentQueue<Action> mainThreadActions)
        {
            string saveDir;
            try { saveDir = RimMusicMod.Settings.GetActualSavePath(); }
            catch (Exception ex)
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: cannot resolve save path: {ex.Message}"));
                return new List<string>();
            }

            List<string> resultUrls = new List<string>();

            foreach (string clipId in clipIds)
            {
                // ---- Primary: mp3 download (60s timeout — audio bytes can be large) ----
                string mp3Url = $"{FlowApiBase}/download/audio/{Uri.EscapeDataString(clipId)}?format=mp3";
                bool mp3Ok = false;
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, mp3Url);
                    req.Headers.Add("Authorization", "Bearer " + accessToken);
                    req.Headers.Add("Origin", FlowOrigin);
                    req.Headers.Add("Referer", FlowOrigin + "/");

                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                    using (var resp = await _httpClient.SendAsync(req, cts.Token))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                            if (bytes != null && bytes.Length >= 10000)
                            {
                                string fileName = $"FlowMusic_{clipId}.mp3";
                                string fullPath = Path.Combine(saveDir, fileName);
                                File.WriteAllBytes(fullPath, bytes);

                                mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Flow Music: mp3 grounded ({bytes.Length} bytes) at {fullPath}"));

                                // file:// URL for the engine's headerless downloader.
                                string fileUrl = "file:///" + fullPath.Replace('\\', '/').TrimStart('/');
                                resultUrls.Add(fileUrl);
                                mp3Ok = true;
                            }
                            else
                            {
                                mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: mp3 response too small ({bytes?.Length ?? 0} bytes) for clip {clipId}, trying wav fallback."));
                            }
                        }
                        else
                        {
                            string errBody = await resp.Content.ReadAsStringAsync();
                            mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: mp3 download for clip {clipId} returned {(int)resp.StatusCode} {resp.StatusCode}: {errBody}. Trying wav fallback."));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: mp3 download for clip {clipId} timed out (60s). Trying wav fallback."));
                }
                catch (Exception ex)
                {
                    mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Flow Music: mp3 download for clip {clipId} threw {ex.GetType().Name}: {ex.Message}. Trying wav fallback."));
                }

                if (mp3Ok) continue;

                // ---- Fallback: public GCS wav_url via /__api/clips (20s timeout) ----
                try
                {
                    string body = "{\"clip_ids\":[\"" + JsonEscape(clipId) + "\"]}";
                    var req = new HttpRequestMessage(HttpMethod.Post, $"{FlowApiBase}/clips");
                    req.Headers.Add("Authorization", "Bearer " + accessToken);
                    req.Headers.Add("Origin", FlowOrigin);
                    req.Headers.Add("Referer", FlowOrigin + "/");
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    using (var resp = await _httpClient.SendAsync(req, cts.Token))
                    {
                        string respStr = await resp.Content.ReadAsStringAsync();

                        if (!resp.IsSuccessStatusCode)
                        {
                            mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: /clips fallback for clip {clipId} returned {(int)resp.StatusCode} {resp.StatusCode}: {respStr}"));
                            continue;
                        }

                        var wavMatch = Regex.Match(respStr, "\"wav_url\"\\s*:\\s*\"([^\"]+)\"");
                        var m4aMatch = Regex.Match(respStr, "\"audio_url\"\\s*:\\s*\"([^\"]+)\"");

                        if (wavMatch.Success && !string.IsNullOrEmpty(wavMatch.Groups[1].Value))
                            resultUrls.Add(wavMatch.Groups[1].Value);
                        else if (m4aMatch.Success && !string.IsNullOrEmpty(m4aMatch.Groups[1].Value))
                            resultUrls.Add(m4aMatch.Groups[1].Value);
                    }
                }
                catch (OperationCanceledException)
                {
                    mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: /clips fallback for clip {clipId} timed out (20s)."));
                }
                catch (Exception ex)
                {
                    mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Flow Music: /clips fallback for clip {clipId} threw {ex.GetType().Name}: {ex.Message}"));
                }
            }

            return resultUrls;
        }

        private static string ExtractStringField(string json, string field)
        {
            var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t");
        }
    }
}
