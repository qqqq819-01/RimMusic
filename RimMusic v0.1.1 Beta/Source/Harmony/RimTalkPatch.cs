using HarmonyLib;
using RimTalk.API;
using RimTalk.Data;
using RimTalk.Service;
using System.Collections.Generic;
using Verse;

namespace RimMusic.HarmonyPatches
{
    /// <summary>
    /// Telemetry Patch: Intercepts outgoing prompt data from RimTalk.
    /// Captures dialogue segments and speaker identification for musical context synthesis.
    /// </summary>
    [HarmonyPatch(typeof(PromptService), "DecoratePrompt")]
    public static class Patch_DecoratePrompt
    {
        // Buffers for captured telemetry
        public static string LastDialogueSegment = "";
        public static Pawn LastSpeaker = null;
        public static int LastSpeechTick = -99999;

        /// <summary>
        /// Postfix extraction: Records the full prompt, timestamp, and primary speaker.
        /// </summary>
        public static void Postfix(TalkRequest talkRequest, List<Pawn> pawns)
        {
            if (talkRequest != null && !string.IsNullOrEmpty(talkRequest.Prompt))
            {
                LastDialogueSegment = talkRequest.Prompt;
                LastSpeechTick = Find.TickManager.TicksGame;

                // Primary speaker identification (Originating pawn usually occupies index 0)
                if (pawns != null && pawns.Count > 0)
                {
                    LastSpeaker = pawns[0];
                }
            }
        }
    }
}