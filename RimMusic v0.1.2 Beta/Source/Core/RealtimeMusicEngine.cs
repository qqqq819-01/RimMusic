using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Verse;
using RimWorld;

namespace RimMusic.Core
{
    public class MusicMainMenuWatcher : MonoBehaviour
    {
        void Update()
        {
            if (Current.Game == null || Current.ProgramState == ProgramState.Entry)
            {
                RealtimeMusicEngine.StopAndDestroy();
            }
        }
    }

    public static class RealtimeMusicEngine
    {
        // [Core Fix] Network resilience: Extended timeout limit from 2 minutes to 10 minutes to bypass slow CDN node interruptions.
        private static readonly HttpClient _httpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
        private static ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();

        private static UnityWebRequest _activeAudioReq;
        private static GameObject _audioPlayerObject;
        private static AudioSource _customAudioSource;

        // ==========================================
        // Tactical Jukebox (Walkman) Core Variables
        // ==========================================
        public static List<string> Playlist = new List<string>();
        public static int CurrentTrackIndex = -1;
        public static string CurrentTrackName = "Awaiting directives...";
        public static bool IsPaused = false;

        // Armory Radar: File directory monitor
        private static int _lastFileCount = -1;
        private static int _scanTicks = 0;

        public static bool IsPlaying => _customAudioSource != null && _customAudioSource.isPlaying;
        public static float TrackProgress => _customAudioSource != null && _customAudioSource.clip != null ? _customAudioSource.time / _customAudioSource.clip.length : 0f;

        public static void InitializePlaylist()
        {
            string path = RimMusicMod.Settings.GetActualSavePath();
            if (Directory.Exists(path))
            {
                var files = Directory.GetFiles(path, "*.mp3").ToList();
                Playlist = files;
                _lastFileCount = Playlist.Count; // Update radar signature

                if (Playlist.Count > 0 && CurrentTrackIndex == -1)
                {
                    CurrentTrackName = "RimMusic_ArmoryLoaded".Translate(Playlist.Count).ToString();
                }
            }
        }

        public static void PlayTrack(int index)
        {
            if (Playlist.Count == 0 || index < 0 || index >= Playlist.Count) return;
            CurrentTrackIndex = index;
            string path = Playlist[index];
            CurrentTrackName = Path.GetFileNameWithoutExtension(path);
            IsPaused = false;

            string uri = "file:///" + path.Replace("\\", "/");
            if (_activeAudioReq != null) _activeAudioReq.Dispose();
            _activeAudioReq = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG);
            _activeAudioReq.SendWebRequest();
        }

        public static void NextTrack()
        {
            if (Playlist.Count == 0) return;
            PlayTrack((CurrentTrackIndex + 1) % Playlist.Count);
        }

        public static void PrevTrack()
        {
            if (Playlist.Count == 0) return;
            int newIndex = CurrentTrackIndex - 1;
            if (newIndex < 0) newIndex = Playlist.Count - 1;
            PlayTrack(newIndex);
        }

        public static void TogglePause()
        {
            if (_customAudioSource == null || _customAudioSource.clip == null)
            {
                if (Playlist.Count > 0) PlayTrack(CurrentTrackIndex == -1 ? 0 : CurrentTrackIndex);
                return;
            }

            if (IsPlaying)
            {
                _customAudioSource.Pause();
                IsPaused = true;
            }
            else
            {
                _customAudioSource.UnPause();
                IsPaused = false;
            }
        }

        public static void StopAndDestroy()
        {
            if (_audioPlayerObject != null)
            {
                UnityEngine.Object.Destroy(_audioPlayerObject);
                _audioPlayerObject = null;
            }
            _customAudioSource = null;
            IsPaused = false;
            CurrentTrackIndex = -1;
            CurrentTrackName = "Awaiting directives...";
        }

        public static void Update()
        {
            // Radar sweep: Scans local directory every 120 ticks (~2s)
            _scanTicks++;
            if (_scanTicks > 120)
            {
                _scanTicks = 0;
                string path = RimMusicMod.Settings.GetActualSavePath();
                if (Directory.Exists(path))
                {
                    int currentCount = Directory.GetFiles(path, "*.mp3").Length;
                    if (currentCount != _lastFileCount)
                    {
                        InitializePlaylist(); // Silent refresh upon detecting new files
                    }
                }
            }

            while (_mainThreadActions.TryDequeue(out Action action))
            {
                try { action?.Invoke(); } catch { }
            }

            if (_activeAudioReq != null && _activeAudioReq.isDone)
            {
                try
                {
                    if (_activeAudioReq.result == UnityWebRequest.Result.Success)
                    {
                        AudioClip clip = DownloadHandlerAudioClip.GetContent(_activeAudioReq);
                        if (clip != null) ExecutePlayback(clip);
                    }
                }
                finally
                {
                    _activeAudioReq.Dispose();
                    _activeAudioReq = null;
                }
            }

            if (_customAudioSource != null)
            {
                if (_customAudioSource.isPlaying)
                {
                    _customAudioSource.volume = Prefs.VolumeMusic;
                    if (Find.MusicManagerPlay != null) Find.MusicManagerPlay.ForceSilenceFor(0.5f);
                }
                else if (!IsPaused && _customAudioSource.clip != null && _customAudioSource.time >= _customAudioSource.clip.length - 0.1f)
                {
                    if (RimMusicMod.Settings.AutoPlayNextTrack) NextTrack();
                    else _customAudioSource.clip = null;
                }
            }
        }

        private static void ExecutePlayback(AudioClip clip)
        {
            if (_audioPlayerObject == null)
            {
                _audioPlayerObject = new GameObject("RimMusic_JukeboxPlayer");
                UnityEngine.Object.DontDestroyOnLoad(_audioPlayerObject);

                _audioPlayerObject.AddComponent<MusicMainMenuWatcher>();

                _customAudioSource = _audioPlayerObject.AddComponent<AudioSource>();
                _customAudioSource.bypassEffects = true;
                _customAudioSource.spatialBlend = 0f;
            }
            _customAudioSource.clip = clip;
            _customAudioSource.volume = Prefs.VolumeMusic;
            _customAudioSource.Play();
            IsPaused = false;
            Messages.Message("RimMusic_TrackPlaying".Translate(CurrentTrackName).ToString(), MessageTypeDefOf.PositiveEvent, false);
        }

        public static async Task RequestAndPlayMusic(string generatedPrompt, string focusName)
        {
            await Request302SunoAudioAsync(generatedPrompt, focusName);
        }

        private static async Task Request302SunoAudioAsync(string generatedPrompt, string focusName)
        {
            string apiUrl = RimMusicMod.Settings.CustomAudioApiUrl.TrimEnd('/');
            string apiKey = RimMusicMod.Settings.SunoApiKey.Trim();
            string modelVer = RimMusicMod.Settings.SunoModelVersion.Trim();

            string isInstStr = RimMusicMod.Settings.SunoMakeInstrumental ? "true" : "false";

            // [Dynamic Payload Configuration]
            string requestJson = $@"{{ ""gpt_description_prompt"": ""{EscapeJson(generatedPrompt)}"", ""mv"": ""{modelVer}"", ""make_instrumental"": {isInstStr} }}";

            try
            {
                _mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Dispatching audio generation task to proxy node. Awaiting confirmation..."));

                var request = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/suno/submit/music");
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                request.Headers.Add("Accept", "application/json");
                request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                string responseStr = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Proxy node rejected request: {response.StatusCode}\n{responseStr}"));
                    return;
                }

                var matchId = System.Text.RegularExpressions.Regex.Match(responseStr, "\"data\"\\s*:\\s*\"([^\"]+)\"");
                if (!matchId.Success) matchId = System.Text.RegularExpressions.Regex.Match(responseStr, "\"id\"\\s*:\\s*\"([^\"]+)\"");

                if (!matchId.Success)
                {
                    _mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Failed to extract Task ID from response payload.\n{responseStr}"));
                    return;
                }
                string taskId = matchId.Groups[1].Value;

                _mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Task authorized. ID: {taskId}. Entering background polling sequence..."));

                List<string> audioUrls = new List<string>();
                int maxRetries = 60;
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

                        // [Core Fix] Absolute Deduplication Radar: utilizing HashSet to eliminate identical CDN mirrors
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
                        _mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Audio generation task failed.\nPrompt: {generatedPrompt}\nModel: {modelVer}\nRaw API Blackbox: {checkStr}"));
                        return;
                    }
                }

                if (audioUrls.Count == 0)
                {
                    _mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Polling sequence timed out. API failed to deliver audio payloads."));
                    return;
                }

                _mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Payload retrieved. {audioUrls.Count} unique links secured post-deduplication. Initiating primary download..."));

                string saveDir = RimMusicMod.Settings.GetActualSavePath();
                string safeFocusName = EscapeFilename(focusName);
                string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // 1. Prioritize downloading the first track. Play immediately upon securement.
                byte[] firstAudioBytes = await _httpClient.GetByteArrayAsync(audioUrls[0]);
                string firstHash = Guid.NewGuid().ToString().Substring(0, 4);
                string firstFileName = $"{timeStamp}_{safeFocusName}_TrackA_{firstHash}.mp3";
                string firstFullPath = Path.Combine(saveDir, firstFileName);
                File.WriteAllBytes(firstFullPath, firstAudioBytes);

                _mainThreadActions.Enqueue(() =>
                {
                    Log.Message($"[RimMusic] Primary payload grounded. Executing playback sequence.");
                    InitializePlaylist();

                    int newIndex = Playlist.IndexOf(firstFullPath);
                    if (newIndex != -1) PlayTrack(newIndex);
                });

                // 2. Reroute remaining payloads to background stealth download. Prevents main thread blocking.
                if (audioUrls.Count > 1)
                {
                    _mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] {audioUrls.Count - 1} secondary tracks detected. Rerouting to asynchronous background queue..."));

                    _ = Task.Run(async () =>
                    {
                        for (int i = 1; i < audioUrls.Count; i++)
                        {
                            try
                            {
                                byte[] extraBytes = await _httpClient.GetByteArrayAsync(audioUrls[i]);
                                string trackLabel = $"Track{(char)('A' + i)}";
                                string h = Guid.NewGuid().ToString().Substring(0, 4);
                                string fName = $"{timeStamp}_{safeFocusName}_{trackLabel}_{h}.mp3";
                                File.WriteAllBytes(Path.Combine(saveDir, fName), extraBytes);
                            }
                            catch (Exception ex)
                            {
                                _mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Secondary payload sequence {i} download obstructed (ignored): {ex.Message}"));
                            }
                        }
                        _mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] All secondary payloads successfully archived. Radar sweep active."));
                    });
                }
            }
            catch (Exception ex)
            {
                _mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Polling engine critical exception: {ex}"));
            }
        }

        private static string EscapeJson(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return input.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t");
        }

        private static string EscapeFilename(string input)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) input = input.Replace(c.ToString(), "_");
            return input;
        }
    }
}