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
        public string PresetName = "Custom Preset";
        public List<PromptEntry> Entries = new List<PromptEntry>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref PresetName, "PresetName", "Custom Preset");
            Scribe_Collections.Look(ref Entries, "Entries", LookMode.Deep);
        }

        public PromptPreset Clone()
        {
            PromptPreset clone = new PromptPreset { PresetName = this.PresetName };
            foreach (var entry in this.Entries)
            {
                clone.Entries.Add(new PromptEntry
                {
                    Name = entry.Name,
                    Role = entry.Role,
                    Content = entry.Content,
                    Enabled = entry.Enabled
                });
            }
            return clone;
        }

        public static PromptPreset CreateDefault()
        {
            var p = new PromptPreset();

            p.Entries.Add(new PromptEntry
            {
                Name = "RimMusic_Prompt_SystemName".Translate(),
                Role = PromptRole.System,
                Content =
@"You are a top-tier versatile music producer, expert in cinematic game scores, modern EDM, heavy rock, and traditional/tribal acoustics.
CRITICAL: Do NOT output specific character names, game items, or literal dialogue. Translate the narrative into PURELY INSTRUMENTAL emotional and sonic themes.

[CAMERA FOCUS PROTOCOL]
- MACRO MODE (Target is Empty/Colony): You are scoring a grand strategy game. Ignore weather and minor details. Base the core style on 'Culture Vibe' and 'Danger Level'. Make it epic, energetic, or broadly ambient.
- MICRO MODE (Target is a Character Name): You are their personal bard. Synergize their 'Mood', 'RimTalk Context', and 'Weather' to create a deeply emotional, intimate character theme (e.g., Sad + Raining = Tragic & Melancholic). NEVER use their dialogue as lyrics.

[EVENT OVERRIDE PROTOCOL]
Deeply analyze the semantic meaning of 'Action'. It may be in any language.
1. CEREMONY/WEDDING: ABSOLUTE PRIORITY. Force style to be romantic, sacred, and joyful.
2. FUNERAL/MOURNING: ABSOLUTE PRIORITY. Force style to be extremely melancholic and slow.
3. PARTY/FESTIVAL: ABSOLUTE PRIORITY. Force style to be upbeat, dancing, and rhythmic.

[COMBAT PROTOCOL]
If Danger Level is High/Combat: Heavily lean into adrenaline-pumping, aggressive modern genres (Epic Orchestral, Cyberpunk EDM, Heavy Metal) fused with the featured cultural instruments. Avoid calm classical."
            });

            p.Entries.Add(new PromptEntry
            {
                Name = "RimMusic_Prompt_UserName".Translate(),
                Role = PromptRole.User,
                Content =
@"[Layer 1: State & Focus]
Target: {{music.focus_name}}
Action: {{music.activity}}
Mood: {{music.mood_level}}
Danger Level: {{music.danger}}

[Layer 2: Deep Context (Use ONLY in MICRO MODE)]
{{rimtalk.full_context}}

[Layer 3: Environment]
Weather: {{music.weather}}

[Layer 4: Cultural Identity]
Culture Vibe & Genre: {{music.culture_vibe}}
Featured Instruments: {{music.culture_instruments}}"
            });

            p.Entries.Add(new PromptEntry
            {
                Name = "RimMusic_Prompt_MandatoryName".Translate(),
                Role = PromptRole.Mandatory,
                Content =
@"[System Instruction]
Keep the total length under {{MaxOutputWords}} words. Provide rich, professional details without cutting off mid-sentence.
Format strictly as follows (DO NOT output anything else):

Genre & Mood: [Integrate the 'Culture Vibe' ({{music.culture_vibe}}) with the current Danger/Event. Specify 2-3 precise musical genres and the core emotion.]
Thematic Motif: [Abstract cinematic and emotional theme]
Instrumentation: [CRITICAL: You MUST prominently feature the EXACT instruments listed in 'Featured Instruments' ({{music.culture_instruments}}) as the core ensemble. Add supportive audio engineering terms. NO poetic descriptions.]
Rhythm & Tempo: [BPM and rhythmic feel]"
            });

            return p;
        }
    }
}