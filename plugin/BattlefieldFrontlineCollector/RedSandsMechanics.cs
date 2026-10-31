using System;
using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Evidence-bounded Red Sands replay rules.
///
/// Red Sands field objects do not expose a dependable unload event in the
/// captures we have.  Their object rows therefore prove that a mechanic was
/// observed, but not that it remains active.  Map telegraphs are instead
/// derived from the short, directly observed action window.  The persistent
/// medicine kit keeps using its normal load/unload lifecycle.
/// </summary>
internal static class RedSandsMechanics
{
    public const string RangeCatalogVersion = "redsands-action-range-2026-10-v1";
    public const double WideMechanismRadiusYalms = 22d;
    public const double GeyserRadiusYalms = 2d;
    private const double MaxReasonableCapturedRadiusYalms = 50d;
    public const int WorldCircleSegments = 32;

    public const uint HeatWaveStatusId = 3754;
    public const uint GeyserPresentationBaseId = 8062;
    public const uint AntlionBaseId = 16563;
    public const uint SabotenderBaseId = 16040;
    public const uint AcutenderBaseId = 16564;
    public const uint MedicineKitBaseId = 2012832;

    public const uint NeedlesActionId = 35752;
    public const uint TherapeuticNeedlesActionId = 35753;
    public const uint LandslipActionId = 35754;
    public const uint SandpearlShroudActionId = 35755;
    public const uint GeyserActionId = 35756;

    public static bool IsTerritory(uint territoryId) => territoryId is 1138 or 1139;

    public static bool IsTransientBaseId(uint territoryId, uint baseId) =>
        IsTerritory(territoryId) && baseId is GeyserPresentationBaseId or AntlionBaseId or
            SabotenderBaseId or AcutenderBaseId;

    public static bool IsTransientLabel(uint territoryId, string? label) =>
        IsTerritory(territoryId) && label is "cc_redsands_geyser" or "cc_redsands_antlion" or
            "cc_redsands_sabotender" or "cc_redsands_acutender" or
            // Catalogue versions before the territory split mislabeled Red
            // Sands BaseId 8062 as a Clockwork trick floor.
            "cc_trick_floor";

    public static bool IsMechanismAction(uint actionId) => actionId is NeedlesActionId or
        TherapeuticNeedlesActionId or LandslipActionId or SandpearlShroudActionId or GeyserActionId;

    /// <summary>
    /// Resolves the world-space effect radius for a directly observed Red Sands
    /// action. Sandpearl Shroud and Geyser are locked to the reviewed current
    /// catalogue radius of 2 yalms: an unexpected capture value must not
    /// silently enlarge the small white recovery/hazard circles. For the three
    /// wide monster actions, a sane positive capture value is accepted and
    /// older captures fall back to the reviewed 22-yalm radius.
    /// </summary>
    public static double? ResolveEffectRadius(uint actionId, double? capturedEffectRange)
    {
        if (!IsMechanismAction(actionId)) return null;
        if (actionId is SandpearlShroudActionId or GeyserActionId)
            return GeyserRadiusYalms;
        if (capturedEffectRange is > 0 and <= MaxReasonableCapturedRadiusYalms)
            return capturedEffectRange.Value;
        return actionId switch
        {
            NeedlesActionId or TherapeuticNeedlesActionId or LandslipActionId =>
                WideMechanismRadiusYalms,
            _ => null
        };
    }

    internal readonly record struct WorldPoint(double X, double Z);

    /// <summary>
    /// Returns a 32-point world-space circle.  The renderer projects every
    /// point through the same map transform as players and objectives, so the
    /// range scales with the viewport instead of becoming a fixed-pixel icon.
    /// </summary>
    public static WorldPoint[] BuildWorldCircle(double centerX, double centerZ,
        double radiusYalms)
    {
        if (!double.IsFinite(centerX) || !double.IsFinite(centerZ) ||
            !double.IsFinite(radiusYalms) || radiusYalms <= 0)
            return [];
        var points = new WorldPoint[WorldCircleSegments];
        for (var index = 0; index < points.Length; index++)
        {
            var angle = Math.Tau * index / points.Length;
            points[index] = new WorldPoint(centerX + radiusYalms * Math.Cos(angle),
                centerZ + radiusYalms * Math.Sin(angle));
        }
        return points;
    }

    /// <summary>
    /// Returns true only while the observed cast/impact can be shown.  Times
    /// are milliseconds on the match clock.  This method never turns a loaded
    /// object into an indefinitely active mechanic.
    /// </summary>
    public static bool IsActionVisible(uint actionId, long effectElapsedMs, long replayElapsedMs)
    {
        var preEffectMs = actionId switch
        {
            NeedlesActionId or TherapeuticNeedlesActionId or LandslipActionId => 5_000,
            SandpearlShroudActionId => 3_000,
            GeyserActionId => 500,
            _ => -1
        };
        return preEffectMs >= 0 && replayElapsedMs >= effectElapsedMs - preEffectMs &&
               replayElapsedMs <= effectElapsedMs + 1_200;
    }

    internal readonly record struct HeatWaveReplayState(bool Active, int? CountdownSeconds,
        long? NextObservedStartMs);

    /// <summary>
    /// Resolves Heat Wave from actor-vitals status snapshots.  The 15 second
    /// countdown is retrospective replay annotation to the next observed status
    /// onset; it is not presented as a separately captured UI timer.
    /// </summary>
    public static HeatWaveReplayState ResolveHeatWave(IReadOnlyList<MovementPoint>? timeline,
        long replayElapsedMs)
    {
        var frames = (timeline ?? [])
            .Where(frame => frame.Actors is { Count: > 0 })
            .OrderBy(frame => frame.ElapsedMs)
            .Select(frame => (frame.ElapsedMs, Active: frame.Actors!.Any(actor =>
                (actor.Statuses ?? []).Any(status => status.StatusId == HeatWaveStatusId))))
            .ToArray();
        if (frames.Length == 0) return new(false, null, null);

        var current = frames.LastOrDefault(frame => frame.ElapsedMs <= replayElapsedMs);
        if (current != default && current.Active && replayElapsedMs - current.ElapsedMs <= 3_500)
            return new(true, null, null);

        var starts = frames.Where((frame, index) => frame.Active &&
                (index == 0 || !frames[index - 1].Active))
            .Select(frame => frame.ElapsedMs).ToArray();
        var next = starts.FirstOrDefault(start => start > replayElapsedMs);
        if (next <= 0 || next - replayElapsedMs > 15_000) return new(false, null, null);
        return new(false, (int)Math.Ceiling((next - replayElapsedMs) / 1000d), next);
    }
}
