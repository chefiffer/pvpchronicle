using System;
using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Evidence adapter for the Archeia Harmonias Crystalline Conflict arena.
///
/// The normal checkpoint gauge and the map-specific aetherometer are separate
/// mechanisms. Checkpoint percentages come directly from the CC director. The
/// older capture schema has no independent aether-index percentage, so route
/// activation is derived only from the tactical crystal crossing the fixed
/// aetherometer towards the checkpoint. No percentage is fabricated.
/// </summary>
internal static class ArcheiaHarmoniasMechanics
{
    public const uint PrimaryTerritoryId = 1357;
    public const uint AlternateTerritoryId = 1358;
    public const uint HealingGlyphBaseId = 8062;
    public const uint HealingPulseActionId = 49768;
    public const int CurrentHealingPulse = 10_000;
    public const long ReplayEffectWindowMs = 1_500;

    public const string AstraGaugeLabel = "cc_astra_checkpoint";
    public const string UmbraGaugeLabel = "cc_umbra_checkpoint";
    public const string HealingGlyphLabel = "cc_harmonias_healing_glyph";

    private const double ArenaCenterZ = 100d;
    private const double AetherometerOffset = 30d;
    private const double MeterTolerance = 1.25d;
    private const double CrossingMargin = 1.25d;

    /// <summary>
    /// Versioned replay geometry in captured FFXIV world X/Z coordinates.
    /// The bridge joins the map-specific aetherometer to the normal blue
    /// tactical checkpoint. Jump coordinates describe an observed replay
    /// route, not a collision mesh or activation radius.
    /// </summary>
    internal readonly record struct RouteGeometry(
        byte Team,
        double AetherometerX,
        double AetherometerZ,
        double CheckpointX,
        double CheckpointZ,
        double JumpGlyphX,
        double JumpGlyphZ,
        double JumpAirborneX,
        double JumpAirborneZ,
        double JumpLandingX,
        double JumpLandingZ);

    private static readonly RouteGeometry AstraGeometry = new(
        0, 100, 70, 100, 40, 32, 97, 48, 52, 88, 25);

    private static readonly RouteGeometry UmbraGeometry = new(
        1, 100, 130, 100, 160, 168, 103, 152, 148, 112, 175);

    internal enum AetherometerState
    {
        Unobserved,
        Idle,
        AttemptObserved,
        Reset,
        Unlocked
    }

    internal enum CheckpointState
    {
        Unobserved,
        Idle,
        Clearing,
        Reset,
        Cleared
    }

    internal readonly record struct AetherometerSnapshot(
        AetherometerState State,
        bool BridgeUnlocked,
        bool JumpGlyphUnlocked,
        double? X,
        double? Z,
        long EvidenceElapsedMs)
    {
        public bool PositionObserved => X.HasValue && Z.HasValue;
    }

    internal readonly record struct CheckpointSnapshot(
        string GaugeLabel,
        CheckpointState State,
        double ProgressPercent,
        long EvidenceElapsedMs);

    public static bool IsTerritory(uint territoryId) =>
        territoryId is PrimaryTerritoryId or AlternateTerritoryId;

    public static bool IsHealingGlyph(uint territoryId, uint baseId) =>
        IsTerritory(territoryId) && baseId == HealingGlyphBaseId;

    /// <summary>Returns fixed geometry only for verified Harmonias territories.</summary>
    public static bool TryGetRouteGeometry(uint territoryId, byte team,
        out RouteGeometry geometry)
    {
        geometry = team switch
        {
            0 => AstraGeometry,
            1 => UmbraGeometry,
            _ => default
        };
        return IsTerritory(territoryId) && team <= 1;
    }

    public static float EvidenceAlpha(long eventElapsedMs, long selectedElapsedMs)
    {
        var age = Math.Abs(selectedElapsedMs - eventElapsedMs);
        return age > ReplayEffectWindowMs
            ? 0f
            : 1f - age / (float)ReplayEffectWindowMs;
    }

    /// <summary>
    /// Derives the old-cache aetherometer state from the observed tactical
    /// crystal route. Crossing beyond the meter proves the bridge existed;
    /// merely clearing the later checkpoint never unlocks this state.
    /// </summary>
    public static AetherometerSnapshot ResolveAetherometer(
        IEnumerable<ObjectiveStateEvent>? objectiveRows,
        uint territoryId,
        byte team,
        long selectedElapsedMs)
    {
        if (!TryGetRouteGeometry(territoryId, team, out _))
            return new(AetherometerState.Unobserved, false, false, null, null, 0);

        var rows = (objectiveRows ?? Array.Empty<ObjectiveStateEvent>())
            .Where(row => row.ElapsedMs <= selectedElapsedMs &&
                          row.Label == "cc_tactical_crystal" &&
                          row.EvidenceScope == "cc_loaded_tactical_crystal_position_v1")
            .OrderBy(row => row.ElapsedMs)
            .ToArray();
        if (rows.Length == 0)
            return new(AetherometerState.Unobserved, false, false, null, null, 0);

        static double OutwardDistance(ObjectiveStateEvent row, byte side) =>
            side == 0 ? ArenaCenterZ - row.Z : row.Z - ArenaCenterZ;

        var crossed = rows.FirstOrDefault(row =>
            OutwardDistance(row, team) > AetherometerOffset + CrossingMargin);
        if (crossed != null)
            return new(AetherometerState.Unlocked, true, true, crossed.X, crossed.Z,
                crossed.ElapsedMs);

        var near = rows.Where(row =>
                Math.Abs(OutwardDistance(row, team) - AetherometerOffset) <= MeterTolerance)
            .ToArray();
        if (near.Length == 0)
        {
            var latestIdle = rows[^1];
            return new(AetherometerState.Idle, false, false, latestIdle.X, latestIdle.Z,
                latestIdle.ElapsedMs);
        }

        var lastNear = near[^1];
        var latest = rows[^1];
        var returnedInward = latest.ElapsedMs > lastNear.ElapsedMs &&
                             OutwardDistance(latest, team) <
                             AetherometerOffset - MeterTolerance;
        return returnedInward
            ? new(AetherometerState.Reset, false, false, latest.X, latest.Z,
                latest.ElapsedMs)
            : new(AetherometerState.AttemptObserved, false, false, lastNear.X, lastNear.Z,
                lastNear.ElapsedMs);
    }

    /// <summary>
    /// Resolves the ordinary tactical-checkpoint progress directly from the CC
    /// director. This percentage must never be presented as an aether index.
    /// </summary>
    public static CheckpointSnapshot ResolveCheckpoint(
        IEnumerable<ObjectiveStateEvent>? objectiveRows,
        string gaugeLabel,
        long selectedElapsedMs)
    {
        if (gaugeLabel is not (AstraGaugeLabel or UmbraGaugeLabel))
            return new(gaugeLabel, CheckpointState.Unobserved, 0, 0);

        var rows = (objectiveRows ?? Array.Empty<ObjectiveStateEvent>())
            .Where(row => row.ElapsedMs <= selectedElapsedMs &&
                          row.Label == gaugeLabel && row.ProgressPercent.HasValue)
            .OrderBy(row => row.ElapsedMs)
            .ToArray();
        if (rows.Length == 0)
            return new(gaugeLabel, CheckpointState.Unobserved, 0, 0);

        var latest = rows[^1];
        var progress = Math.Clamp(latest.ProgressPercent!.Value, 0, 100);
        var completed = rows.FirstOrDefault(row => row.ProgressPercent >= 100d);
        var lastPositive = rows.LastOrDefault(row => row.ProgressPercent > 0);
        var reset = completed == null && progress <= 0 && lastPositive != null &&
                    latest.ElapsedMs > lastPositive.ElapsedMs;
        var state = completed != null ? CheckpointState.Cleared
            : progress > 0 ? CheckpointState.Clearing
            : reset ? CheckpointState.Reset
            : CheckpointState.Idle;
        return new(gaugeLabel, state, completed != null ? 100 : progress,
            (completed ?? latest).ElapsedMs);
    }
}
