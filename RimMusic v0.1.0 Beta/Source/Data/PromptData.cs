using System.Collections.Generic;
using Verse;

namespace RimMusic.Data
{
    public enum PromptRole { System, User, Mandatory }

    public class PromptEntry : IExposable
    {
        public string Name = "Directive";
        public PromptRole Role = PromptRole.User;
        public string Content = "";
        public bool Enabled = true;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Name, "Name");
            Scribe_Values.Look(ref Role, "Role");
            Scribe_Values.Look(ref Content, "Content");
            Scribe_Values.Look(ref Enabled, "Enabled", true);
        }
    }

    public class PromptPreset : IExposable
    {
        public List<PromptEntry> Entries = new List<PromptEntry>();

        public void ExposeData()
        {
            Scribe_Collections.Look(ref Entries, "Entries", LookMode.Deep);
        }

        public static PromptPreset CreateDefault()
        {
            var p = new PromptPreset();

            // [V9.0 Protocol] AI Persona Configuration
            p.Entries.Add(new PromptEntry
            {
                Name = "RimMusic_Prompt_SystemName".Translate(),
                Role = PromptRole.System,
                Content =
@"You are a top-tier cinematic music composer and audio engineer.
CRITICAL: This is a prompt for an Audio Generation AI. Do NOT output specific character names, in-game items, or literal game events. Translate the narrative into ABSTRACT cinematic, emotional, and sonic themes.
Prioritize [Character Depth] and [Narrative] over [Environment].
Adapt to [Time Speed]. If 'Paused', focus on static atmosphere or tactical suspense. If 2x/3x fast-forward, prefer a rhythmic, driving flow.

[Context & Threat Protocol]
- [CRITICAL THREAT]: ABSOLUTE PRIORITY. If combat or severe threats are present, immediately shift the motif to tense, oppressive, tragic, or action-oriented, overriding calm activities.
- [SCENE CONTEXT]: Use available local environment data (if any) to color the stylistic vibe (e.g., luxurious, industrial, grim).
- [BACKGROUND VIBE]: Ambient baseline. Keep it calm and textural unless overridden by higher threats or dramatic narrative events."
            });

            // [V9.0 Protocol] Full Telemetry Context
            p.Entries.Add(new PromptEntry
            {
                Name = "RimMusic_Prompt_UserName".Translate(),
                Role = PromptRole.User,
                Content =
@"[Layer 1: Focus State]
Target: {{music.focus_name}}
Action: {{music.activity}}
Mood: {{music.mood_level}}

[Layer 2: Local Environment]
{{music.radar_log}}

[Layer 3: Deep RimTalk Context]
{{rimtalk.full_context}}

[Ongoing events]
{{music.event_log}}

[Layer 4: Environmental Override]
Time Speed: {{music.time_speed}}
Season: {{music.season}}
Weather: {{music.weather}}
Danger Level: {{music.danger}}

[Layer 5: Cultural Ensemble Palette]
Featured Instruments: {{music.culture_instruments}}"
            });

            // [V9.0 Protocol] Output Constraints & Instrument Lock
            p.Entries.Add(new PromptEntry
            {
                Name = "RimMusic_Prompt_MandatoryName".Translate(),
                Role = PromptRole.Mandatory,
                Content =
@"[System Instruction]
Keep the total length under {{MaxOutputWords}} words. Provide rich, professional details without cutting off mid-sentence.
Format strictly as follows (DO NOT output anything else):

Core Atmosphere: [Keywords]
Thematic Motif: [Abstract cinematic and emotional theme. NO character names.]
Instrumentation: [CRITICAL: You MUST prominently feature the EXACT instruments listed in 'Featured Instruments' ({{music.culture_instruments}}) as the core ensemble. Add supportive audio engineering and MIDI terminology (e.g., Pizzicato, High dampening, Short reverb). NO poetic descriptions for instruments.]
Rhythm & Tempo: [BPM and rhythmic feel]"
            });

            return p;
        }
    }
}