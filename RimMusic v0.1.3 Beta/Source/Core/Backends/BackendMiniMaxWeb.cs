using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace RimMusic.Core.Backends
{
    /// <summary>
    /// MiniMax music web backend. Drives the internal WebSocket at
    /// wss://www.minimaxi.com/v1/api/music/ws using the player's _token (JWT copied
    /// from their browser LocalStorage). Replicates the verified yy signature and
    /// the MusicGen / Heartbeat protocol observed in the live capture.
    ///
    /// This consumes the player's own MiniMax web free quota (~hundreds/day). It is
    /// NOT the official paid API; credentials belong to the player, and so does any
    /// ToS/account risk.
    /// </summary>
    public class BackendMiniMaxWeb : IAudioBackend
    {
        public string Name => "MiniMax Web (free quota)";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(RimMusicMod.Settings.MiniMaxWebToken);

        // .NET Framework 4.8's ClientWebSocket defaults to TLS 1.2 only. minimaxi.com
        // is fronted by a CDN that aggressively uses TLS 1.3 + http2/zstd; without
        // explicitly allowing both versions the TLS handshake fails and surfaces as
        // the opaque "Unable to connect to the remote server".
        private static int _tlsPatched;
        private static void EnsureTlsVersions()
        {
            if (System.Threading.Interlocked.Exchange(ref _tlsPatched, 1) != 0) return;
            try
            {
                // Tls13 may be undefined on very old 4.8 installs; guard with reflection.
                var allFlags = SecurityProtocolType.Tls12;
                var tls13 = (SecurityProtocolType)0x00003000; // SecurityProtocolType.Tls13 numeric
                if (Enum.IsDefined(typeof(SecurityProtocolType), tls13)) allFlags |= tls13;
                ServicePointManager.SecurityProtocol = allFlags;
            }
            catch { /* non-fatal: best-effort TLS upgrade */ }
        }

        // Persistent pseudo-device fingerprint. Regenerated only if missing; stored
        // in settings so the same player looks like the same browser across sessions.
        private static string EnsureDeviceId()
        {
            string id = RimMusicMod.Settings.MiniMaxDeviceId;
            if (string.IsNullOrWhiteSpace(id))
            {
                // 18-digit numeric string, mimicking the captured device_id shape.
                var rng = new System.Random();
                var sb = new StringBuilder(18);
                sb.Append((char)('1' + rng.Next(1, 9)));
                for (int i = 1; i < 18; i++) sb.Append((char)('0' + rng.Next(0, 10)));
                id = sb.ToString();
                RimMusicMod.Settings.MiniMaxDeviceId = id;
            }
            return id;
        }

        public async Task<List<string>> GenerateAudioUrlsAsync(string generatedPrompt, string focusName, ConcurrentQueue<Action> mainThreadActions)
        {
            string token = RimMusicMod.Settings.MiniMaxWebToken.Trim();
            if (!IsConfigured)
            {
                mainThreadActions.Enqueue(() => Log.Error("[RimMusic] MiniMax Web token is empty. Open the MiniMax music page, F12 → Application → Local Storage → copy the _token value, and paste it into the Mod settings."));
                return new List<string>();
            }

            string deviceId = EnsureDeviceId();
            string uuid = Guid.NewGuid().ToString();
            long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Base query parameters in the EXACT order observed in the capture.
            // unix is part of the signed path; yy/token/op_ticket are appended AFTER signing.
            string basePath = "/v1/api/music/ws";
            var baseParams = new StringBuilder();
            baseParams.Append("device_platform=web");
            baseParams.Append("&app_id=3001");
            baseParams.Append("&version_code=22201");
            baseParams.Append("&biz_id=1");
            baseParams.Append("&uuid=").Append(uuid);
            baseParams.Append("&lang=zh-Hans");
            baseParams.Append("&device_id=").Append(deviceId);
            baseParams.Append("&os_name=Windows");
            baseParams.Append("&browser_name=chrome");
            baseParams.Append("&device_memory=8");
            baseParams.Append("&cpu_core_num=8");
            baseParams.Append("&browser_language=zh-CN");
            baseParams.Append("&browser_platform=Win32");
            baseParams.Append("&screen_width=1920");
            baseParams.Append("&screen_height=1080");
            baseParams.Append("&unix=").Append(unixMs);

            string fullSearchPath = basePath + "?" + baseParams;
            int variant = RimMusicMod.Settings.MiniMaxYyVariant;
            string yy = MiniMaxYySigner.Compute(variant, fullSearchPath, unixMs);

            string wsUrl = "wss://www.minimaxi.com" + fullSearchPath
                + "&yy=" + yy
                + "&token=" + token
                + "&op_ticket=";

            mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] MiniMax Web: opening raw WebSocket (variant={variant}, unix={unixMs})..."));

            EnsureTlsVersions();

            // Inject a Cookie header. Default = just _token; advanced users can paste
            // the full F12 Cookie for anti-bot compliance.
            string fullCookie = (RimMusicMod.Settings.MiniMaxWebCookie ?? "").Trim();
            if (string.IsNullOrEmpty(fullCookie)) fullCookie = "_token=" + token;

            CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            List<string> audioUrls = new List<string>();
            HashSet<string> seenIds = new HashSet<string>();

            MiniMaxRawWebSocket ws = null;
            try
            {
                // Hand-rolled WebSocket over TcpClient+SslStream — bypasses the broken
                // Mono ClientWebSocket that fails wss/TLS handshakes inside RimWorld.
                ws = new MiniMaxRawWebSocket("www.minimaxi.com", 443, fullSearchPath + "&yy=" + yy + "&token=" + token + "&op_ticket=", "https://www.minimaxi.com", fullCookie);
                await ws.ConnectAsync(cts.Token);

                // Send MusicGen request.
                string msgId = Guid.NewGuid().ToString();
                bool instrumental = RimMusicMod.Settings.SunoMakeInstrumental;
                string ideaEsc = JsonEscape(generatedPrompt);
                string genPayload = "{\"method\":\"MusicGen\",\"music_payLoad\":{"
                    + "\"model\":\"music-2.6\",\"generation_type\":1,"
                    + "\"idea\":\"" + ideaEsc + "\",\"lyrics\":\"\",\"title\":\"\","
                    + "\"n\":2,\"rewrite_idea_switch\":false,"
                    + "\"instrumental\":" + (instrumental ? "true" : "false") + ",\"stream\":true"
                    + "},\"msg_id\":\"" + msgId + "\"}";

                await ws.SendTextAsync(genPayload, cts.Token);

                mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] MiniMax Web: MusicGen dispatched (idea=\"{generatedPrompt}\", instrumental={instrumental}). Awaiting stream..."));

                bool ended = false;
                DateTime lastMsgAt = DateTime.UtcNow;

                while (!ended && !cts.Token.IsCancellationRequested)
                {
                    if ((DateTime.UtcNow - lastMsgAt).TotalMinutes > 5)
                    {
                        mainThreadActions.Enqueue(() => Log.Error("[RimMusic] MiniMax Web: 5-minute silence on the WebSocket — aborting."));
                        break;
                    }

                    string fullMsg;
                    try
                    {
                        fullMsg = await ws.ReceiveMessageAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        mainThreadActions.Enqueue(() => Log.Error("[RimMusic] MiniMax Web: overall 6-minute timeout reached — aborting."));
                        break;
                    }

                    // null = server sent Close frame.
                    if (fullMsg == null)
                    {
                        mainThreadActions.Enqueue(() => Log.Warning("[RimMusic] MiniMax Web: server closed the socket."));
                        break;
                    }

                    lastMsgAt = DateTime.UtcNow;

                    HandleWsMessage(fullMsg, mainThreadActions, audioUrls, seenIds, ref ended);

                    // Optional heartbeat echo — keeps the socket warm during long renders.
                    if (!ended && ExtractStringField(fullMsg, "method") == "Heartbeat")
                    {
                        string hbId = ExtractStringField(fullMsg, "msg_id");
                        if (!string.IsNullOrEmpty(hbId))
                        {
                            long hbTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            string hb = "{\"method\":\"Heartbeat\",\"msg_id\":\"" + hbId + "\",\"timestamp\":" + hbTs + "}";
                            try { await ws.SendTextAsync(hb, cts.Token); } catch { /* heartbeat failure is non-fatal */ }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Two scenarios land here:
                //  (a) WS read broke with an exception we couldn't swallow inside
                //      ReceiveMessageAsync (rare — most are normalized to null).
                //  (b) The send side threw.
                // If we already collected at least one audio_url, the music WAS
                // generated on MiniMax's side and we have a URL to download — treat
                // that as success and don't scare the user. Only report an error when
                // we genuinely came back empty-handed.
                if (audioUrls.Count > 0)
                {
                    mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] MiniMax Web: stream ended abruptly ({ex.GetType().Name}: {ex.Message}), but {audioUrls.Count} audio URL(s) were already captured. Proceeding to download."));
                }
                else
                {
                    mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] MiniMax Web: raw WebSocket failure: {ex.GetType().Name}: {ex.Message}"));
                }
            }
            finally
            {
                if (ws != null && ws.IsConnected)
                {
                    // Fire-and-forget: this is best-effort cleanup in a finally block.
                    // We deliberately do not await; the socket is about to be disposed anyway.
                    try { _ = ws.CloseAsync(CancellationToken.None); } catch { }
                }
                ws?.Dispose();
            }

            if (audioUrls.Count > 0)
            {
                mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] MiniMax Web: secured {audioUrls.Count} audio URL(s)."));
            }
            return audioUrls;
        }

        private static void HandleWsMessage(string msg, ConcurrentQueue<Action> mainThreadActions, List<string> audioUrls, HashSet<string> seenIds, ref bool ended)
        {
            string method = ExtractStringField(msg, "method");

            if (msg.Contains("\"input_sensitive\":true"))
            {
                mainThreadActions.Enqueue(() => Log.Error("[RimMusic] MiniMax Web: prompt flagged as input-sensitive (sensitive word). Generation aborted by server."));
                ended = true;
                return;
            }

            if (msg.Contains("\"statusInfo\""))
            {
                mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] MiniMax Web: server returned statusInfo: {msg}"));
                ended = true;
                return;
            }

            if (method != "MusicGen" && method != "MusicFetch") return;

            // Extract every non-empty audio_url in the message.
            var urlMatches = System.Text.RegularExpressions.Regex.Matches(msg, "\"audio_url\"\\s*:\\s*\"([^\"]+)\"");
            foreach (System.Text.RegularExpressions.Match m in urlMatches)
            {
                string url = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(url)) continue;

                // Extract the matching music_id to dedupe by id+url (the stream emits
                // progressive updates; we only want the final resolved URL per track).
                string mid = ExtractStringField(msg, "music_id");
                string dedupeKey = mid + "|" + url;
                if (seenIds.Contains(dedupeKey)) continue;
                seenIds.Add(dedupeKey);
                audioUrls.Add(url);
            }

            if (msg.Contains("\"ended\":true"))
            {
                ended = true;
                mainThreadActions.Enqueue(() => Log.Message("[RimMusic] MiniMax Web: server signalled ended=true — stream complete."));
            }
        }

        private static string ExtractStringField(string json, string field)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t");
        }
    }
}
