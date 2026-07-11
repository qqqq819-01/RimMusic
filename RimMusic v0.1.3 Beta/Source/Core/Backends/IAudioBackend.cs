using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RimMusic.Core.Backends
{
    /// <summary>
    /// Abstraction over an audio-generation provider. Each implementation is responsible
    /// for turning a music prompt into one or more playable audio URLs. Downloading,
    /// saving, and triggering playback remain the responsibility of the engine so that
    /// every backend reuses the existing resilient downloader and playlist pipeline.
    /// </summary>
    public interface IAudioBackend
    {
        /// <summary>Human-readable identifier shown in logs.</summary>
        string Name { get; }

        /// <summary>True when the backend has the minimum credentials required to attempt a request.</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Submit the prompt and return a list of public http(s) audio URLs (mp3).
        /// Implementations should enqueue log/status messages into mainThreadActions
        /// because Verse.Log must be touched from the main thread.
        /// </summary>
        Task<List<string>> GenerateAudioUrlsAsync(string generatedPrompt, string focusName, ConcurrentQueue<Action> mainThreadActions);
    }
}
