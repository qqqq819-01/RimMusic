using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimMusic.Core.Backends;
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
        // Network resilience: Extended global timeout limit to bypass slow CDN node interruptions.
        private static readonly HttpClient _httpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
        private static ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();

        // ================================================================
        // 替补播放器:照搬 Music Manager 的反射模式,直接操作原版 Find.MusicManagerPlay
        // 播所有原版曲 + AI 曲(注入的运行时 SongDef),而非自己用 GameObject 自播。
        // ================================================================

        // 反射拿 MusicManagerPlay 的私有字段。 AccessTools.FieldRefAccess 返回委托,
        // 调它传 MusicManagerPlay 实例即得字段值——与 Music Manager 同做法。
        private static readonly AccessTools.FieldRef<MusicManagerPlay, AudioSource> _audioSourceRef
            = AccessTools.FieldRefAccess<AudioSource>(typeof(MusicManagerPlay), "audioSource");
        private static readonly AccessTools.FieldRef<MusicManagerPlay, SongDef> _currentSongRef
            = AccessTools.FieldRefAccess<SongDef>(typeof(MusicManagerPlay), "currentSong");
        private static readonly FieldInfo _gameObjectCreatedField
            = AccessTools.Field(typeof(MusicManagerPlay), "gameObjectCreated");

        // 替补播放器的暂停态(独立于原版自己的暂停逻辑,我们额外掐 audioSource)
        private static bool _fallbackPaused = false;

        public static bool IsPlaying
        {
            get
            {
                var mp = Find.MusicManagerPlay;
                if (mp == null) return false;
                var src = _audioSourceRef(mp);
                return src != null && src.isPlaying;
            }
        }

        public static float TrackProgress
        {
            get
            {
                var mp = Find.MusicManagerPlay;
                if (mp == null) return 0f;
                var src = _audioSourceRef(mp);
                if (src == null || src.clip == null || src.clip.length <= 0f) return 0f;
                return Mathf.Clamp01(src.time / src.clip.length);
            }
        }

        public static string CurrentTrackName
        {
            get
            {
                var mp = Find.MusicManagerPlay;
                if (mp == null) return "Awaiting directives...";
                var song = _currentSongRef(mp);
                return song != null ? (song.label.NullOrEmpty() ? song.defName : song.label) : "Awaiting directives...";
            }
        }

        // 当前正在播的 SongDef 引用(反射拿)——供 AutoGenCoordinator 精判 boost 恢复时机
        public static SongDef CurrentSong
        {
            get
            {
                var mp = Find.MusicManagerPlay;
                return mp == null ? null : _currentSongRef(mp);
            }
        }

        // 让原版自己挑下一首播(AppropriateNow 过滤 + RandomElementByWeight 加权随机)
        public static void NextTrack()
        {
            var mp = Find.MusicManagerPlay;
            if (mp == null) return;
            _fallbackPaused = false;
            mp.StartNewSong();
        }

        // 同 NextTrack——原版没有"上一首"概念,替补播放器也照搬,让原版重选
        public static void PrevTrack()
        {
            NextTrack();
        }

        public static void TogglePause()
        {
            var mp = Find.MusicManagerPlay;
            if (mp == null) return;
            var src = _audioSourceRef(mp);
            if (src == null || src.clip == null)
            {
                // 没在播,触发原版选一首
                mp.StartNewSong();
                _fallbackPaused = false;
                return;
            }
            if (_fallbackPaused || !src.isPlaying)
            {
                src.UnPause();
                _fallbackPaused = false;
            }
            else
            {
                src.Pause();
                _fallbackPaused = true;
            }
        }

        public static void StopAndDestroy()
        {
            // 替补播放器无自建 GameObject,无需销毁。保留方法名兼容 MusicMainMenuWatcher。
            _fallbackPaused = false;
        }

        // 兼容 HUD 的 InitializePlaylist 调用——替补模式下由原版接管,无事可做。
        public static void InitializePlaylist()
        {
            // 原版 MusicManagerPlay 自己管 playlist(AppropriateNow 过滤 DefDatabase<SongDef>.AllDefs),
            // 无需我们扫磁盘。 AI 曲已注入 DefDatabase,原版自然能选到。
        }

        public static void Update()
        {
            // 装了 Music Manager → 替补模式不运行,但仍要处理 _mainThreadActions
            // (下载完成的注入动作通过 _mainThreadActions 触发 RuntimeSongRegistrar)
            bool injectionMode = RuntimeSongRegistrar.IsMusicManagerInstalled;

            if (injectionMode)
            {
                // 轻量定时扫描:玩家手动拖入新 mp3 时自动注入(每 ~5 秒一次,仅比文件数)
                RuntimeSongRegistrar.LightweightScanIfNeeded();
            }
            else
            {
                // 替补模式:照搬 Music Manager,让原版 MusicManagerPlay 自己管播放(AppropriateNow 过滤+随机)。
                // AI 曲已注入 DefDatabase<SongDef>,原版自然能选到。无需我们扫磁盘或自播。
                // 原版的 MusicUpdate 由游戏自己的 GameComponentTick 调,不在这里重复调。
            }

            while (_mainThreadActions.TryDequeue(out Action action))
            {
                try { action?.Invoke(); } catch { }
            }
        }

        // ExecutePlayback 已废弃:替补播放器不再自播,改为原版 MusicManagerPlay 接管。
        // 保留空壳避免外部(若仍有)调用报错。
        private static void ExecutePlayback(AudioClip clip) { }

        // =====================================================================
        // [NEW] Robust Network Downloader with Anti-Decoy Validation
        // =====================================================================
        private static async Task<byte[]> DownloadAudioWithRetriesAsync(string url, int maxAttempts = 3)
        {
            // Flow Music backend grounds mp3 bytes to disk and returns a file:// URL.
            // HttpClient rejects non-http(s) schemes ("Only http or https scheme is
            // allowed"), so short-circuit: read the local file directly.
            if (url != null && url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string localPath = new Uri(url).LocalPath;
                    byte[] data = System.IO.File.ReadAllBytes(localPath);
                    _mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Loaded grounded audio ({data.Length} bytes) from {localPath}"));
                    return data;
                }
                catch (Exception ex)
                {
                    _mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Failed reading grounded audio at {localPath}: {ex.GetType().Name}: {ex.Message}"));
                    throw;
                }
            }

            for (int i = 1; i <= maxAttempts; i++)
            {
                try
                {
                    byte[] data = await _httpClient.GetByteArrayAsync(url);

                    // Payload Validation: MP3s should be several MBs. If it's under 10KB, it's a decoy/error text.
                    if (data.Length < 10000)
                    {
                        string decoyText = Encoding.UTF8.GetString(data);
                        _mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Decoy payload intercepted ({data.Length} bytes). Decoded content: {decoyText}"));
                        throw new Exception($"Payload verification failed. Received decoy format instead of audio stream.");
                    }

                    return data;
                }
                catch (Exception ex)
                {
                    if (i == maxAttempts)
                    {
                        _mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Critical connection failure after {maxAttempts} attempts: {ex.Message}"));
                        throw;
                    }

                    _mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Connection drop detected. Initiating auto-retry {i}/{maxAttempts} in 3 seconds..."));
                    await Task.Delay(3000);
                }
            }
            return null;
        }

        public static async Task RequestAndPlayMusic(string generatedPrompt, string focusName)
        {
            IAudioBackend backend = ResolveActiveBackend();
            if (backend == null)
            {
                _mainThreadActions.Enqueue(() => Log.Error("[RimMusic] No audio backend configured. Open Mod Options → 实时音乐引擎 and select a backend."));
                return;
            }

            if (!backend.IsConfigured)
            {
                _mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Backend \"{backend.Name}\" is not configured. Fill in its credentials in the Mod options."));
                return;
            }

            List<string> audioUrls = await backend.GenerateAudioUrlsAsync(generatedPrompt, focusName, _mainThreadActions);

            if (audioUrls == null || audioUrls.Count == 0)
            {
                _mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] {backend.Name} did not return any audio URL. See prior log lines for the cause."));
                return;
            }

            _mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] {backend.Name} returned {audioUrls.Count} audio URL(s). Initiating armored download protocol..."));

            string saveDir = RimMusicMod.Settings.GetActualSavePath();
            string safeFocusName = EscapeFilename(focusName);
            if (string.IsNullOrWhiteSpace(safeFocusName)) safeFocusName = "Unknown";
            string timeStamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

            // 威胁度二分判定(英文标签): threatRatio < ThreatStealThreshold → Peace; >= → War
            // 注:SongDef 注入时一律 tense=false,此标签仅写进文件名供玩家反查
            string threatTag = "Peace";
            try { threatTag = CultureCalculator.ThreatTagFor(Find.CurrentMap); } catch { }
            _mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] Threat verdict for this generation: {threatTag}"));

            // 1. Primary Download via Resilient Downloader
            try
            {
                byte[] firstAudioBytes = await DownloadAudioWithRetriesAsync(audioUrls[0], maxAttempts: 3);

                if (firstAudioBytes != null)
                {
                    string firstHash = Guid.NewGuid().ToString().Substring(0, 4);
                    string firstFileName = $"{timeStamp}_{safeFocusName}_{threatTag}_TrackA_{firstHash}.mp3";
                    string firstFullPath = Path.Combine(saveDir, firstFileName);
                    File.WriteAllBytes(firstFullPath, firstAudioBytes);
                    // 删 Flow Music 临时文件(已被读成 bytes 写成时间戳版,避免同一份音频存两份)
                    TryDeleteTempAudioFile(audioUrls[0]);

                    _mainThreadActions.Enqueue(() =>
                    {
                        Log.Message($"[RimMusic] Primary payload grounded and verified. Injecting into vanilla music system.");
                        RuntimeSongRegistrar.RegisterAndPlay(firstFullPath);
                        // 注入成功后立即给这首曲 boost 权重——保证下一首随机必中刚生成的。
                        // AutoGenCoordinator.CheckBoostRestore 每帧判原版开始播它一次后恢复。
                        AutoGenCoordinator.BoostLatestRegisteredSong();
                        // 玩家开了"生成完成后立刻切歌"→让原版 StartNewSong 选下一首(boost 权重会选中刚生成的)
                        if (RimMusicMod.Settings?.AutoGenSwitchOnComplete == true && Find.MusicManagerPlay != null)
                        {
                            Find.MusicManagerPlay.StartNewSong();
                        }
                    });
                }
            }
            catch (Exception primaryEx)
            {
                _mainThreadActions.Enqueue(() => Log.Error($"[RimMusic] Primary payload download aborted. Reason: {primaryEx.Message}"));
            }

            // 2. Secondary Stealth Downloads via Resilient Downloader
            if (audioUrls.Count > 1)
            {
                _mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] {audioUrls.Count - 1} secondary tracks detected. Rerouting to asynchronous queue..."));

                _ = Task.Run(async () =>
                {
                    for (int i = 1; i < audioUrls.Count; i++)
                    {
                        try
                        {
                            byte[] extraBytes = await DownloadAudioWithRetriesAsync(audioUrls[i], maxAttempts: 3);
                            if (extraBytes != null)
                            {
                                string trackLabel = $"Track{(char)('A' + i)}";
                                string h = Guid.NewGuid().ToString().Substring(0, 4);
                                string fName = $"{timeStamp}_{safeFocusName}_{threatTag}_{trackLabel}_{h}.mp3";
                                string fullP = Path.Combine(saveDir, fName);
                                File.WriteAllBytes(fullP, extraBytes);
                                TryDeleteTempAudioFile(audioUrls[i]); // 删 Flow Music 临时文件

                                // 副产物只注册不强制播放,让玩家后续可在 Music Manager 主动播
                                _mainThreadActions.Enqueue(() => {
                                    RuntimeSongRegistrar.Register(fullP);
                                    AutoGenCoordinator.BoostLatestRegisteredSong(); // 副下载也要 boost
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _mainThreadActions.Enqueue(() => Log.Warning($"[RimMusic] Secondary payload sequence {i} download obstructed (ignored): {ex.Message}"));
                        }
                    }
                    _mainThreadActions.Enqueue(() => Log.Message($"[RimMusic] All secondary payload attempts concluded. Radar sweep active."));
                });
            }
        }

        /// <summary>Selects the backend implementation based on the current settings. Returns null if the mode is out of range.</summary>
        private static IAudioBackend ResolveActiveBackend()
        {
            switch (RimMusicMod.Settings.AudioBackendMode)
            {
                case 0: return new Backend302Ai();
                case 1: return new BackendMiniMaxWeb();
                case 2: return new BackendFlowMusicWeb();
                default: return null;
            }
        }

        // [LEGACY] 302.ai path retained as Backend302Ai.cs. The engine now dispatches
        // through RequestAndPlayMusic → IAudioBackend. This stub is kept only as a
        // historical anchor; it is no longer called. See Backends/Backend302Ai.cs.
        private static async Task Request302SunoAudioAsync(string generatedPrompt, string focusName)
        {
            await Task.CompletedTask;
        }

        private static string EscapeJson(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return input.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t");
        }

        // 删 Flow Music 临时文件:后端把 mp3 落盘成 FlowMusic_<clipId>.mp3 后返回 file:// URL,
        // 我们读成 bytes 写成时间戳版(20260709_xxxx_TrackA.mp3)——同一份音频存两份,这里删临时原文件。
        private static void TryDeleteTempAudioFile(string audioUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(audioUrl)) return;
                // file:///D:/RimMusic/FlowMusic_xxxx.mp3 → 取本地路径
                if (!audioUrl.StartsWith("file://")) return;
                string localPath = audioUrl.Substring("file://".Length).Replace('/', '\\').TrimStart('\\');
                if (!File.Exists(localPath)) return;
                // 只删 Flow Music 的临时文件(以 FlowMusic_ 开头),避免误删他处
                if (Path.GetFileName(localPath).StartsWith("FlowMusic_", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(localPath);
                    Log.Message($"[RimMusic] Deleted temp audio file: {Path.GetFileName(localPath)}");
                }
            }
            catch (Exception ex) { Log.Warning($"[RimMusic] Delete temp audio failed (ignored): {ex.Message}"); }
        }

        private static string EscapeFilename(string input)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) input = input.Replace(c.ToString(), "_");
            return input;
        }
    }
}