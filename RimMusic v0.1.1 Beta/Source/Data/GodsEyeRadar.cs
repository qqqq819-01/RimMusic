using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimMusic.Data
{
    /// <summary>
    /// God's Eye - Environmental Intent Perception and Dynamic Grid Radar.
    /// Tracks mouse velocity and hover duration to dynamically scale the scanning radius,
    /// extracting environmental context and threat levels within the player's focal zone.
    /// </summary>
    public static class GodsEyeRadar
    {
        private static Vector3 _lastMousePos;
        private static float _idleTimer = 0f;
        private static float _currentRadius = 5f;
        private static float _lastRealTime;

        // Performance optimization: Static cache pools to prevent high-frequency GC allocation.
        private static readonly List<string> HighThreatCache = new List<string>(20);
        private static readonly List<string> MidContextCache = new List<string>(20);
        private static readonly List<string> LowBackgroundCache = new List<string>(20);
        private static readonly StringBuilder ReportBuilder = new StringBuilder(256);

        /// <summary>
        /// Radar update motor for continuous external invocation.
        /// Calculates mouse velocity and dynamically adjusts the scan radius.
        /// </summary>
        public static void UpdateRadarMotor()
        {
            if (RimMusic.Core.MusicGameComponent.IsMacroMode) return;
            if (RimMusicMod.Settings == null || !RimMusicMod.Settings.EnableEnvironmentalRadar) return;
            if (Find.CurrentMap == null) return;

            float dt = Time.realtimeSinceStartup - _lastRealTime;
            _lastRealTime = Time.realtimeSinceStartup;
            if (dt > 0.5f) dt = 0.016f;

            Vector3 curMousePos = Verse.UI.MouseMapPosition();
            float moveSpeed = (curMousePos - _lastMousePos).magnitude / dt;

            // Logic: Retract radar on fast movement, expand on hover.
            if (moveSpeed > RimMusicMod.Settings.RadarFastMoveThreshold)
            {
                _idleTimer = 0f;
                _currentRadius = Mathf.Lerp(_currentRadius, 0f, dt * 10f);
            }
            else if (moveSpeed > RimMusicMod.Settings.RadarIdleThreshold)
            {
                _idleTimer = 0f;
                _currentRadius = Mathf.Lerp(_currentRadius, RimMusicMod.Settings.RadarBaseRadius, dt * 5f);
            }
            else
            {
                _idleTimer += dt;
                if (_idleTimer >= 3.0f)
                {
                    float expandProgress = Mathf.Clamp01((_idleTimer - 3.0f) / RimMusicMod.Settings.RadarExpandTime);
                    float targetRadius = Mathf.Lerp(RimMusicMod.Settings.RadarBaseRadius, RimMusicMod.Settings.RadarMaxRadius, expandProgress);
                    _currentRadius = Mathf.Lerp(_currentRadius, targetRadius, dt * 2f);
                }
            }

            _lastMousePos = curMousePos;
        }

        /// <summary>
        /// Visual debugging: Renders the radar radius on the ground.
        /// </summary>
        public static void DrawDebugRadius()
        {
            if (RimMusic.Core.MusicGameComponent.IsMacroMode) return;
            if (RimMusicMod.Settings == null || !RimMusicMod.Settings.EnableEnvironmentalRadar || !RimMusicMod.Settings.DrawRadarRadius) return;
            if (Find.CurrentMap == null || _currentRadius < 1.0f) return;

            IntVec3 centerCell = Verse.UI.MouseCell();
            if (centerCell.InBounds(Find.CurrentMap))
            {
                GenDraw.DrawRadiusRing(centerCell, _currentRadius);
            }
        }

        /// <summary>
        /// Core scanning probe: Generates an entity report within the current radar footprint.
        /// </summary>
        /// <param name="focalPawn">The current protagonist pawn.</param>
        /// <param name="map">The active map instance.</param>
        /// <returns>Formatted environmental telemetry report.</returns>
        public static string GenerateRadarReport(Pawn focalPawn, Map map)
        {
            if (RimMusicMod.Settings == null || !RimMusicMod.Settings.EnableEnvironmentalRadar) return string.Empty;
            if (_currentRadius < 1.0f) return string.Empty;

            IntVec3 centerCell = Verse.UI.MouseCell();
            if (!centerCell.InBounds(map)) return string.Empty;

            bool isTetherBroken = false;
            if (focalPawn != null && focalPawn.Spawned)
            {
                float distance = centerCell.DistanceTo(focalPawn.Position);
                if (distance > RimMusicMod.Settings.RadarTetherDistance) isTetherBroken = true;
            }

            // Flush static cache pools
            HighThreatCache.Clear();
            MidContextCache.Clear();
            LowBackgroundCache.Clear();
            ReportBuilder.Clear();

            // Scan entities within radius
            var things = GenRadial.RadialDistinctThingsAround(centerCell, map, _currentRadius, true);

            foreach (Thing t in things)
            {
                if (t.Fogged()) continue;

                if (t is Corpse c)
                {
                    HighThreatCache.Add($"Corpse ({c.InnerPawn?.NameShortColored.Resolve() ?? "Unknown"})");
                }
                else if (t is Fire)
                {
                    HighThreatCache.Add("Spreading Fire");
                }
                else if (t is Pawn p)
                {
                    if (p == focalPawn) continue;

                    if (p.HostileTo(Faction.OfPlayer))
                        HighThreatCache.Add($"Hostile {p.def.label}");
                    else if (p.IsPrisoner)
                        MidContextCache.Add($"Prisoner ({p.NameShortColored.Resolve()})");
                    else
                        MidContextCache.Add($"Ally ({p.NameShortColored.Resolve()})");
                }
                else if (t.def.category == ThingCategory.Building)
                {
                    if (t.GetStatValue(StatDefOf.MarketValue) > 1000f)
                        MidContextCache.Add($"Valuable {t.def.label}");
                    else
                        LowBackgroundCache.Add(t.def.label);
                }
                else if (t.def.category == ThingCategory.Item)
                {
                    if (t.def.defName == "Silver" || t.def.defName == "Gold")
                        MidContextCache.Add("Precious Wealth");
                    else if (t.def.IsWeapon)
                        MidContextCache.Add("Dropped Weapons");
                }
            }

            // Logic interrupt: Abort report generation if the camera tether is broken and no high-threat targets are detected.
            if (isTetherBroken && HighThreatCache.Count == 0) return string.Empty;

            ReportBuilder.AppendLine($"[God's Eye Radar] Camera Focus Area (Radius: {_currentRadius:F1}):");

            if (HighThreatCache.Count > 0)
            {
                var groupedThreat = HighThreatCache.GroupBy(x => x).Select(g => $"{g.Key} x{g.Count()}");
                ReportBuilder.AppendLine($"[CRITICAL THREAT]: {string.Join(", ", groupedThreat.Take(5))}");
            }

            if (MidContextCache.Count > 0)
            {
                var groupedMid = MidContextCache.GroupBy(x => x).Select(g => $"{g.Key} x{g.Count()}");
                ReportBuilder.AppendLine($"[SCENE CONTEXT]: {string.Join(", ", groupedMid.Take(5))}");
            }

            if (!isTetherBroken && LowBackgroundCache.Count > 0)
            {
                var groupedLow = LowBackgroundCache.GroupBy(x => x).Select(g => g.Key);
                ReportBuilder.AppendLine($"[BACKGROUND VIBE]: {string.Join(", ", groupedLow.Take(5))}");
            }

            TerrainDef terrain = map.terrainGrid.TerrainAt(centerCell);
            if (terrain != null)
            {
                ReportBuilder.AppendLine($"[TERRAIN]: {terrain.label}");
            }

            return ReportBuilder.ToString().TrimEnd();
        }
    }
}