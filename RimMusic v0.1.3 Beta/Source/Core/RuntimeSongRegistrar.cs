using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RimMusic.Core;
using UnityEngine;
using UnityEngine.Networking;
using Verse;
using RimWorld;

namespace RimMusic.Core
{
    // =========================================================================
    // RuntimeSongRegistrar
    // 把磁盘上的 mp3 包装成运行时 SongDef,反射注入 DefDatabase<SongDef>,
    // 让原版 MusicManagerPlay / Music Manager 可见可播。
    //
    // 策略(半官方做法):
    //   1. UnityWebRequestMultimedia 加载本地 mp3 → AudioClip
    //   2. new SongDef() 反射设字段(defName/label/clip/tense/allowedSeasons/...)
    //   3. ShortHashGiver.GiveShortHash + DefDatabase<SongDef>.Add(def) 注入
    //   4. 持久化注册清单 Config/RimMusic_SongRegistry.xml,重启后重建
    //
    // 注入时一律 tense=false(和平时)。战时/和平时由玩家在 Music Manager 自改。
    // 不区分 RimMusic 生成 vs 玩家手动拖入——统一 tense=false。
    // =========================================================================
    public static class RuntimeSongRegistrar
    {
        private const string RegistryFilePath = "RimMusic_SongRegistry.xml";

        // defName 前缀,便于反查与去重
        private const string DefNamePrefix = "RimMusic_Runtime_";

        // 已注册的 defName → 完整磁盘路径(防止重复注入)
        private static readonly Dictionary<string, string> _registered = new Dictionary<string, string>(StringComparer.Ordinal);

        // 最近一次 Register 注入的 SongDef——供 AutoGenCoordinator boost 权重精确定位刚生成的那首
        public static SongDef LastRegisteredSong => _lastRegisteredSong;
        private static SongDef _lastRegisteredSong = null;

        // 反射缓存
        private static readonly Type SongDefType = typeof(SongDef);
        private static readonly MethodInfo _addMethod;        // DefDatabase<SongDef>.Add(SongDef) — static
        private static readonly MethodInfo _removeMethod;     // DefDatabase<SongDef>.Remove(SongDef) — static
        private static readonly MethodInfo _giveShortHashMethod;

        static RuntimeSongRegistrar()
        {
            try
            {
                // DefDatabase<SongDef>.Add(SongDef) / Remove(SongDef) — static public, 经探针确认存在
                var dbType = typeof(DefDatabase<>).MakeGenericType(SongDefType);
                _addMethod = dbType.GetMethod("Add", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { SongDefType }, null);
                _removeMethod = dbType.GetMethod("Remove", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { SongDefType }, null);

                // ShortHashGiver.GiveShortHash(Def, Type, HashSet<ushort>) — static
                _giveShortHashMethod = typeof(ShortHashGiver).GetMethod(
                    "GiveShortHash", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(Def), typeof(Type), typeof(HashSet<ushort>) }, null);

                if (_addMethod == null) Log.Error("[RimMusic] RuntimeSongRegistrar: DefDatabase<SongDef>.Add not resolved — injection will fail.");
                if (_removeMethod == null) Log.Warning("[RimMusic] RuntimeSongRegistrar: Remove not resolved, re-injection may leave stale defs.");
                if (_giveShortHashMethod == null) Log.Warning("[RimMusic] RuntimeSongRegistrar: GiveShortHash not resolved, will use random fallback.");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimMusic] RuntimeSongRegistrar static init failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------
        // 公开 API
        // ---------------------------------------------------------------------

        /// <summary>注册一首 mp3 为运行时 SongDef(只注入,不强制播放)。</summary>
        public static void RegisterAndPlay(string mp3FullPath)
        {
            SongDef def = Register(mp3FullPath);
            if (def == null) return;
            // 不再 ForcePlaySong:尊重玩家设置。刚生成的曲靠 boost 权重让原版下次随机选中,
            // 而非生成完立即切播(那是旧的自动连播逻辑残留)。
            // AutoGenCoordinator.ScheduleBoostForLatestSong 会给最新注入的曲 boost 权重。
            Log.Message($"[RimMusic] Injected song registered (will be picked by vanilla random on next switch): {def.defName}");
        }

        /// <summary>仅注册,不强制播放。返回注入的 SongDef,失败返回 null。</summary>
        public static SongDef Register(string mp3FullPath)
        {
            if (string.IsNullOrWhiteSpace(mp3FullPath) || !File.Exists(mp3FullPath))
            {
                Log.Warning($"[RimMusic] Register aborted: file not found {mp3FullPath}");
                return null;
            }

            // 拒绝 Flow Music 临时文件:后端先落盘 FlowMusic_<clipId>.mp3 供引擎读 bytes,
            // 读完成 RequestAndPlayMusic 会删它并写成时间戳版(正式曲)。临时文件绝不该注入 DefDatabase,
            // 否则扫盘竞态会把它注入成幽灵条目(文件删了 SongDef 还在)。
            if (Path.GetFileName(mp3FullPath).StartsWith("FlowMusic_", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // 去重:同一文件已注册且 DefDatabase 里真有对应 def 才跳过。
            // 缓存清单(_registered)只记历史,重启后 DefDatabase 会清空运行时 def——
            // 若只信清单会误判"已注册"而跳过注入,导致重启后不可见。
            string existingDef = _registered.FirstOrDefault(kv => kv.Value == mp3FullPath).Key;
            if (existingDef != null)
            {
                SongDef existing = DefDatabase<SongDef>.GetNamedSilentFail(existingDef);
                if (existing != null) return existing; // 真存在,跳过
                // 清单说有但 DefDatabase 里没有(重启后常见)——清掉陈旧映射,重新注入
                _registered.Remove(existingDef);
            }

            try
            {
                // 1. 加载 AudioClip(异步转同步阻塞主线程——本方法只在主线程被调用)
                AudioClip clip = LoadAudioClipBlocking(mp3FullPath);
                if (clip == null)
                {
                    Log.Warning($"[RimMusic] AudioClip load failed for {mp3FullPath}");
                    return null;
                }

                // 2. 构造稳定 defName(基于文件名,去掉扩展名与非法字符)
                string stableKey = BuildStableDefName(mp3FullPath);
                string defName = DefNamePrefix + stableKey;

                // 幂等二级防护:若 DefDatabase 已有同 defName 的 SongDef,判 clip.name 是否同一首曲。
                // 同一首曲(clip.name 相同) → 直接返回已有的,避免重复注入 Music Manager 列表出现两条。
                // 真正同名不同曲(clip 不同) → 加随机后缀绕过(保留原边界处理逻辑)。
                SongDef existingSameName = DefDatabase<SongDef>.GetNamedSilentFail(defName);
                if (existingSameName != null)
                {
                    bool sameClip = existingSameName.clip != null && clip != null
                        && string.Equals(existingSameName.clip.name, clip.name, StringComparison.OrdinalIgnoreCase);
                    if (sameClip)
                    {
                        Log.Message($"[RimMusic] Register skipped (duplicate): '{defName}' already injected with same clip.");
                        return existingSameName;
                    }
                    // 同名但不同曲,加后缀绕过
                    defName += "_" + Guid.NewGuid().ToString().Substring(0, 4);
                }

                // 3. new SongDef() 并填充字段
                SongDef song = (SongDef)Activator.CreateInstance(SongDefType);
                song.defName = defName;
                song.label = BuildHumanLabel(mp3FullPath);
                song.clip = clip;
                // clipPath 原版用于从 Resources 按 clipPath 加载 clip;但我们已直接赋 clip,
                // 不再需要引擎按 clipPath 重复加载。留空避免引擎误判路径无效。
                song.clipPath = "";
                // 按文件名标签判 tense:_War_ → true(进战时随机池),_Peace_ 或无标签 → false(进和平时池)
                // 玩家手动拖入的无标签音乐统一为和平时(tense=false),符合"无标签=和平时"约定
                song.tense = Path.GetFileNameWithoutExtension(mp3FullPath).IndexOf("_War_", StringComparison.OrdinalIgnoreCase) >= 0;
                song.allowedSeasons = null; // 全季节(null = AppropriateNow 放行所有)
                song.allowedTimeOfDay = TimeOfDay.Any; // 全时段
                // commonality 必须为正值: ChooseNextSong 末尾用 RandomElementByWeight(s => s.commonality) 选曲,
                // 权重 0 = 零概率被选中。值由玩家在 Mod 选项调(默认 2f),值越高越优先于原版曲被随机选中。
                song.commonality = RimMusicMod.Settings?.InjectedSongWeight ?? 2f;
                song.volume = 1f;
                song.playOnMap = true; // 关键:AppropriateNow 第一判 playOnMap,false 直接踢出随机池
                typeof(Def).GetField("generated", BindingFlags.Public | BindingFlags.Instance)?.SetValue(song, true);

                // 4. 给 shortHash + 注入 DefDatabase
                if (_giveShortHashMethod != null)
                {
                    _giveShortHashMethod.Invoke(null, new object[] { song, SongDefType, null });
                }
                else
                {
                    // fallback:手动给一个不冲突的 shortHash
                    song.shortHash = (ushort)(UnityEngine.Random.Range(1, ushort.MaxValue));
                }

                if (_addMethod != null)
                {
                    InjectViaReflection(song);
                }
                else
                {
                    Log.Error("[RimMusic] DefDatabase<SongDef>.Add method not found, cannot inject.");
                    return null;
                }

                _registered[defName] = mp3FullPath;
                _lastRegisteredSong = song; // 供 AutoGenCoordinator boost 精确定位刚注入的那首
                Log.Message($"[RimMusic] Injected runtime SongDef '{defName}' (label='{song.label}', tense=false) from {Path.GetFileName(mp3FullPath)}");

                return song;
            }
            catch (Exception ex)
            {
                Log.Error($"[RimMusic] Register failed for {mp3FullPath}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>扫描整个保存目录,把所有 mp3 都注册一遍。幂等:先清掉自己注入过的旧 def,再重注。
        /// mod 重载/重新启用时 DefDatabase 可能残留旧运行时 def,不清掉会导致状态错乱。</summary>
        public static void RegisterAllFromDisk()
        {
            string dir;
            try { dir = RimMusicMod.Settings.GetActualSavePath(); }
            catch (Exception ex) { Log.Error($"[RimMusic] RegisterAllFromDisk: cannot resolve save path: {ex.Message}"); return; }

            if (!Directory.Exists(dir))
            {
                Log.Message($"[RimMusic] RegisterAllFromDisk: scanning directory [{dir}] — not yet created, no songs to register. If your mp3 files are elsewhere, set MusicSavePath in Mod Options → Armory.");
                return;
            }

            // 先读持久化清单,确认 defName 与路径的稳定映射
            LoadRegistry();

            // 幂等:清掉自己注入过的旧 def(mod 重载时可能残留)
            UnregisterAll();

            Log.Message($"[RimMusic] RegisterAllFromDisk: scanning directory [{dir}] for *.mp3 ...");

            var files = Directory.GetFiles(dir, "*.mp3", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("FlowMusic_", StringComparison.OrdinalIgnoreCase)) // 跳过 Flow Music 临时文件
                .OrderBy(f => f)
                .ToList();

            int newCount = 0;
            foreach (string f in files)
            {
                if (Register(f) != null) newCount++;
            }

            // 清单中失效路径(文件已删)的条目清掉
            var stale = _registered.Where(kv => !File.Exists(kv.Value)).ToList();
            foreach (var kv in stale) _registered.Remove(kv.Key);

            _lastScanFileCount = files.Count;
            _lastScanDir = dir;
            Log.Message($"[RimMusic] RegisterAllFromDisk: scanned [{dir}] — found {files.Count} mp3 file(s), registered {newCount} new, total injected={_registered.Count}. If this is wrong folder, set MusicSavePath in Mod Options → Armory.");
            WriteRegistry();
        }

        // 轻量定时扫描:仅当文件数变化时才触发完整重注,性能成本极低(一次 Directory.GetFiles)。
        // 供 RealtimeMusicEngine.Update() 每 ~5 秒调一次,实现玩家手动拖入新 mp3 的实时注入。
        private static int _lastScanFileCount = -1;
        private static string _lastScanDir = null;
        private static float _lastScanTime = -1f;
        public static void LightweightScanIfNeeded()
        {
            if (Time.realtimeSinceStartup - _lastScanTime < 5f) return; // 5 秒一次
            _lastScanTime = Time.realtimeSinceStartup;

            string dir;
            try { dir = RimMusicMod.Settings.GetActualSavePath(); }
            catch { return; }
            if (!Directory.Exists(dir)) return;

            try
            {
                int currentCount = Directory.GetFiles(dir, "*.mp3", SearchOption.TopDirectoryOnly).Length;
                if (currentCount != _lastScanFileCount || dir != _lastScanDir)
                {
                    Log.Message($"[RimMusic] Detected mp3 count change ({_lastScanFileCount} → {currentCount}), re-injecting...");
                    RegisterAllFromDisk();
                }
            }
            catch { }
        }

        // 清掉自己注入过的所有旧 def,幂等重注的前置步骤
        private static void UnregisterAll()
        {
            int removed = 0;
            // 1. 清 _registered 字典里记录的(正常注入的)
            if (_removeMethod != null && _registered.Count > 0)
            {
                var snapshot = _registered.Keys.ToList();
                foreach (string defName in snapshot)
                {
                    SongDef existing = DefDatabase<SongDef>.GetNamedSilentFail(defName);
                    if (existing != null)
                    {
                        try { _removeMethod.Invoke(null, new object[] { existing }); removed++; } catch { }
                    }
                    _registered.Remove(defName);
                }
            }
            // 2. 清幽灵条目:扫盘竞态可能把 FlowMusic_ 临时文件注入成 RimMusic_Runtime_FlowMusic_ 开头的 def,
            //    文件删了但 def 还在 DefDatabase。按 defName 前缀扫一遍清掉。
            if (_removeMethod != null)
            {
                var ghosts = DefDatabase<SongDef>.AllDefsListForReading
                    .Where(s => s.defName != null && s.defName.StartsWith(DefNamePrefix + "FlowMusic_", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var ghost in ghosts)
                {
                    try { _removeMethod.Invoke(null, new object[] { ghost }); removed++; } catch { }
                }
            }
            if (removed > 0) Log.Message($"[RimMusic] UnregisterAll: purged {removed} stale runtime SongDef(s) before re-injection.");
        }

        // ---------------------------------------------------------------------
        // 内部实现
        // ---------------------------------------------------------------------

        // 同步阻塞加载 AudioClip。 UnityWebRequestMultimedia 本身是异步的,
        // 但在主线程里轮询 isDone 即可阻塞完成(RimMusic 现有代码已用此模式)。
        private static AudioClip LoadAudioClipBlocking(string mp3Path)
        {
            string uri = "file:///" + mp3Path.Replace('\\', '/').TrimStart('/');
            UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG);
            req.SendWebRequest();

            // 同步等待。设个软上限(15s),避免大文件卡死主线程太久。
            float deadline = Time.realtimeSinceStartup + 15f;
            while (!req.isDone)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Log.Warning($"[RimMusic] AudioClip load timed out (15s) for {mp3Path}");
                    req.Dispose();
                    return null;
                }
                // 不让主线程空转,但 Unity API 必须主线程——这里就让它继续
            }

            try
            {
                if (req.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                    if (clip != null) clip.name = Path.GetFileNameWithoutExtension(mp3Path);
                    return clip;
                }
                Log.Warning($"[RimMusic] AudioClip load error: {req.error}");
                return null;
            }
            finally
            {
                req.Dispose();
            }
        }

        // 经探针确认: DefDatabase<SongDef>.Add(SongDef) 是 static public,直接反射调用即可。
        private static void InjectViaReflection(SongDef song)
        {
            if (_addMethod == null)
                throw new InvalidOperationException("DefDatabase<SongDef>.Add method not resolved.");

            _addMethod.Invoke(null, new object[] { song });
        }

        // 稳定可重现的 defName 核心部分:用文件名(不含扩展名),去非法字符
        private static string BuildStableDefName(string mp3Path)
        {
            string fn = Path.GetFileNameWithoutExtension(mp3Path);
            if (string.IsNullOrWhiteSpace(fn)) fn = "track";
            // defName 只允许 [A-Za-z0-9_]
            var sb = new StringBuilder(fn.Length);
            foreach (char c in fn)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            // 截断超长名(defName 不宜过长)
            if (sb.Length > 80) sb.Length = 80;
            string result = sb.ToString();
            // 去掉连续下划线美化
            while (result.Contains("__")) result = result.Replace("__", "_");
            return result.Trim('_');
        }

        // 给 Music Manager 列表显示的人类可读 label:用文件名,_ 转空格
        private static string BuildHumanLabel(string mp3Path)
        {
            string fn = Path.GetFileNameWithoutExtension(mp3Path);
            if (string.IsNullOrWhiteSpace(fn)) return "RimMusic Track";
            // 替换下划线为空格,首字母大写
            string nice = fn.Replace('_', ' ').Trim();
            if (nice.Length > 0) nice = char.ToUpper(nice[0]) + (nice.Length > 1 ? nice.Substring(1) : "");
            return nice;
        }

        // ---------------------------------------------------------------------
        // 持久化注册清单 Config/RimMusic_SongRegistry.xml
        // 重启后 RimWorld 会清空运行时 def,需要这个清单重建。
        // ---------------------------------------------------------------------

        private static string RegistryPath => Path.Combine(GenFilePaths.ConfigFolderPath, RegistryFilePath);

        private static void LoadRegistry()
        {
            try
            {
                if (!File.Exists(RegistryPath)) return;
                Scribe.loader.InitLoading(RegistryPath);
                Scribe_Values.Look(ref _registryDummy, "dummy");
                // 实际加载用 working list
                var loaded = new List<RegistryEntry>();
                Scribe_Collections.Look(ref loaded, "entries", LookMode.Deep);
                _registered.Clear();
                foreach (var e in loaded)
                    if (!string.IsNullOrWhiteSpace(e.defName) && File.Exists(e.path))
                        _registered[e.defName] = e.path;
            }
            catch (Exception ex) { Log.Error($"[RimMusic] LoadRegistry failed: {ex.Message}"); }
            finally { Scribe.loader.FinalizeLoading(); }
        }

        private static string _registryDummy = "";
        private static List<RegistryEntry> _registryWorkingList = new List<RegistryEntry>();

        public static void WriteRegistry()
        {
            try
            {
                _registryWorkingList = _registered.Select(kv => new RegistryEntry { defName = kv.Key, path = kv.Value }).ToList();
                Scribe.saver.InitSaving(RegistryPath, "RimMusic_SongRegistry");
                Scribe_Values.Look(ref _registryDummy, "dummy");
                Scribe_Collections.Look(ref _registryWorkingList, "entries", LookMode.Deep);
            }
            catch (Exception ex) { Log.Error($"[RimMusic] WriteRegistry failed: {ex.Message}"); }
            finally { Scribe.saver.FinalizeSaving(); }
        }

        // 检测是否"启用"了 Music Manager。一次缓存即可——mod 启用/禁用都需重启游戏才生效,
        // 运行中不会变,无需每帧重算。GetActiveModWithIdentifier 只返回 Active=true 的 mod。
        private static bool? _musicManagerInstalledCache;
        public static bool IsMusicManagerInstalled
        {
            get
            {
                if (_musicManagerInstalledCache.HasValue) return _musicManagerInstalledCache.Value;
                try
                {
                    // ignorePostfix:true 容忍 Steam workshop 加的版本后缀(如 zal.musicmanager.steam)
                    if (ModLister.GetActiveModWithIdentifier("zal.musicmanager", ignorePostfix: true) != null)
                        _musicManagerInstalledCache = true;
                    else if (ModLister.GetActiveModWithIdentifier("fluffy.musicmanager", ignorePostfix: true) != null)
                        _musicManagerInstalledCache = true;
                    else
                        _musicManagerInstalledCache = false;
                }
                catch { _musicManagerInstalledCache = false; }
                return _musicManagerInstalledCache.Value;
            }
        }
    }

    // 注册清单的序列化条目
    public class RegistryEntry : IExposable
    {
        public string defName;
        public string path;
        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName", "");
            Scribe_Values.Look(ref path, "path", "");
        }
    }
}
