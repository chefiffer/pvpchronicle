using System;
using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Reduces Seal Rock's global map-UI layers into stable strategic-point state.
///
/// Input: change-only <see cref="ObjectiveStateEvent"/> rows emitted from
/// <c>AgentMap.EventMarkers</c>. Output: one canonical point per map position,
/// with rank, owner and remaining-data percentage retained across temporary UI
/// layer replacement.
///
/// Nearby loaded objects, action targets and observer-range samples are never
/// accepted here. They may explain local participation elsewhere, but cannot
/// create, recolour or delete a global strategic point.
/// </summary>
internal static class SealRockGlobalObjectiveReducer
{
    public const string Version = "seal-rock-global-objective-reducer-v1";
    public const double TerminalRemainingPercent = 2d;
    public const int MissingCompleteSnapshotsBeforeRemoval = 3;
    private const double SpatialLayerJoinYalms = 1.5d;

    private readonly record struct SpatialKey(int X2, int Z2)
    {
        public static SpatialKey From(double x, double z) => new(
            (int)Math.Round(x * 2d, MidpointRounding.AwayFromZero),
            (int)Math.Round(z * 2d, MidpointRounding.AwayFromZero));

        public override string ToString() => $"{X2}:{Z2}";
    }

    private sealed class PointAccumulator
    {
        public required SpatialKey Key { get; init; }
        public readonly HashSet<uint> ActiveMarkerIds = [];
        public uint ObjectId;
        public long LastObservedMs;
        public string Code = string.Empty;
        public string LastLabel = string.Empty;
        public char Rank;
        public byte? OwnerTeam;
        public bool Neutral = true;
        public bool Stale;
        public bool Terminal;
        public int MissingCompleteSnapshots;
        public double X;
        public double Y;
        public double Z;
        public double? RemainingPercent;
    }

    /// <summary>
    /// Immutable replay state produced from direct global map-UI evidence.
    /// <see cref="Stale"/> means all currently known UI layers disappeared
    /// without terminal remaining-data evidence. The point is deliberately
    /// retained rather than falsely declared depleted.
    /// </summary>
    internal sealed record PointState(string PointKey, uint ObjectId, long LastObservedMs,
        string Code, char Rank, byte? OwnerTeam, bool Neutral, bool Stale,
        double X, double Y, double Z, double? RemainingPercent)
    {
        public ObjectiveStateEvent ToObjectiveStateEvent()
        {
            var iconId = SealRockObjectiveCatalog.IconForRankAndOwner(Rank,
                Neutral ? null : OwnerTeam);
            var statePrefix = Neutral ? "neutral" : "active";
            var state = iconId == 0
                ? $"{statePrefix}:reduced={Version}"
                : $"{statePrefix}:reduced={Version}:icon={iconId}";
            if (Stale) state += ":ui_gap=1";
            var label = !string.IsNullOrWhiteSpace(Code)
                ? Rank == '\0' ? Code : $"{Code}\nRank {Rank}"
                : Rank == '\0' ? PointKey : $"{PointKey}\nRank {Rank}";
            return new ObjectiveStateEvent(LastObservedMs, ObjectId, label, state,
                Neutral ? null : OwnerTeam, X, Y, Z,
                SealRockObjectiveCatalog.ReducedMapEvidenceScope,
                ProgressPercent: RemainingPercent);
        }
    }

    /// <summary>
    /// Replays direct map-UI changes up to <paramref name="elapsedMs"/>.
    /// An inactive layer removes the canonical point only after the client UI
    /// has directly reported a terminal remaining value (0-2%). Otherwise the
    /// last good state is retained as stale, preventing a one-frame UI swap or
    /// observer-range unload from erasing a live point.
    /// </summary>
    public static IReadOnlyList<PointState> ReduceAt(
        IEnumerable<ObjectiveStateEvent> timeline, long elapsedMs)
    {
        var points = new Dictionary<SpatialKey, PointAccumulator>();
        var markerBindings = new Dictionary<uint, SpatialKey>();
        var codeBindings = new Dictionary<string, SpatialKey>(StringComparer.OrdinalIgnoreCase);
        var rows = timeline
            .Where(row => row.ElapsedMs <= elapsedMs &&
                          (SealRockObjectiveCatalog.IsRawMapEvidence(row.EvidenceScope) ||
                           SealRockObjectiveCatalog.IsCompleteSnapshot(row)))
            .OrderBy(row => row.ElapsedMs)
            // Process retiring layers first, then their same-tick replacement.
            .ThenBy(RowOrder)
            // The archive writer preserves arrival order, but replay results
            // must not depend on JSON array order after a server rebuild.
            .ThenBy(row => MapIcon(row.State))
            .ThenBy(row => row.ObjectId)
            .ThenBy(row => row.Label, StringComparer.Ordinal)
            .ToArray();

        foreach (var row in rows)
        {
            if (SealRockObjectiveCatalog.IsCompleteSnapshot(row))
            {
                foreach (var missingPoint in points.Values.Where(point =>
                             point.Stale && point.ActiveMarkerIds.Count == 0 && !point.Terminal))
                {
                    missingPoint.MissingCompleteSnapshots++;
                    if (missingPoint.MissingCompleteSnapshots >= MissingCompleteSnapshotsBeforeRemoval)
                        missingPoint.Terminal = true;
                }
                continue;
            }

            if (IsInactive(row))
            {
                if (!markerBindings.TryGetValue(row.ObjectId, out var inactiveKey) ||
                    !points.TryGetValue(inactiveKey, out var inactivePoint)) continue;
                inactivePoint.ActiveMarkerIds.Remove(row.ObjectId);
                markerBindings.Remove(row.ObjectId);
                if (inactivePoint.ActiveMarkerIds.Count > 0) continue;
                if (inactivePoint.RemainingPercent is <= TerminalRemainingPercent)
                    inactivePoint.Terminal = true;
                else
                    inactivePoint.Stale = true;
                continue;
            }

            if (!SealRockObjectiveCatalog.TryGetMapIcon(row.State, out var iconId) ||
                !SealRockObjectiveCatalog.IsPointIcon(iconId)) continue;

            var code = SealRockObjectiveCatalog.ShortPointCode(row.Label);
            SpatialKey key;
            if (!string.IsNullOrWhiteSpace(code) && codeBindings.TryGetValue(code, out var codeKey))
                key = codeKey;
            else
            {
                var nearest = points.Values
                    .Select(existing => new
                    {
                        existing.Key,
                        DistanceSquared = Math.Pow(existing.X - row.X, 2) +
                                          Math.Pow(existing.Z - row.Z, 2)
                    })
                    .Where(existing => existing.DistanceSquared <=
                                       SpatialLayerJoinYalms * SpatialLayerJoinYalms)
                    .OrderBy(existing => existing.DistanceSquared)
                    .FirstOrDefault();
                key = nearest?.Key ?? SpatialKey.From(row.X, row.Z);
                if (!string.IsNullOrWhiteSpace(code)) codeBindings[code] = key;
            }
            if (!points.TryGetValue(key, out var point))
            {
                point = new PointAccumulator { Key = key };
                points[key] = point;
            }

            if (point.Terminal)
            {
                // A later generation may reuse the same fixed map position.
                // Never carry the depleted generation's percentage/ownership
                // into a newly spawned point before its own UI value arrives.
                point.RemainingPercent = null;
                point.OwnerTeam = null;
                point.Neutral = true;
                point.ActiveMarkerIds.Clear();
            }
            markerBindings[row.ObjectId] = key;
            point.ActiveMarkerIds.Add(row.ObjectId);
            point.ObjectId = row.ObjectId;
            point.LastObservedMs = row.ElapsedMs;
            point.X = row.X;
            point.Y = row.Y;
            point.Z = row.Z;
            point.Stale = false;
            point.Terminal = false;
            point.MissingCompleteSnapshots = 0;

            if (!string.IsNullOrWhiteSpace(code)) point.Code = code;
            if (!string.IsNullOrWhiteSpace(row.Label)) point.LastLabel = row.Label;
            var rank = SealRockObjectiveCatalog.RankFromIcon(iconId);
            if (rank != '\0') point.Rank = rank;
            var percent = row.ProgressPercent ??
                          SealRockObjectiveCatalog.ProgressPercentFromText(row.DisplayText) ??
                          SealRockObjectiveCatalog.ProgressPercentFromText(row.Label);
            if (percent.HasValue) point.RemainingPercent = percent;

            if (SealRockObjectiveCatalog.IsNeutralIcon(iconId))
            {
                point.Neutral = true;
                point.OwnerTeam = null;
            }
            else if (SealRockObjectiveCatalog.OwnerFromIcon(iconId) is byte owner)
            {
                point.Neutral = false;
                point.OwnerTeam = owner;
            }
        }

        return points.Values
            .Where(point => !point.Terminal)
            .OrderBy(point => point.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(point => point.Key.X2)
            .ThenBy(point => point.Key.Z2)
            .Select(point =>
            {
                var pointKey = !string.IsNullOrWhiteSpace(point.Code)
                    ? $"{SealRockObjectiveCatalog.DutyId}:{SealRockObjectiveCatalog.Version}:code:{point.Code}"
                    : $"{SealRockObjectiveCatalog.DutyId}:{SealRockObjectiveCatalog.Version}:position:{point.Key}";
                return new PointState(pointKey, StableReducedObjectId(pointKey),
                    point.LastObservedMs, point.Code, point.Rank,
                point.OwnerTeam, point.Neutral, point.Stale,
                    point.X, point.Y, point.Z, point.RemainingPercent);
            })
            .ToArray();
    }

    /// <summary>
    /// Collapses the current live AgentMap layers through the same reducer used
    /// by replay. Rank and faction overlays at one point therefore produce one
    /// polygon instead of two overlapping circles or labels.
    /// </summary>
    public static IReadOnlyList<PointState> ReduceLive(
        IEnumerable<LiveObjectiveMarker> markers)
    {
        var sequence = 0L;
        var rows = markers
            .Where(marker => SealRockObjectiveCatalog.IsRawMapEvidence(marker.EvidenceScope) &&
                             SealRockObjectiveCatalog.TryGetMapIcon(marker.State, out var iconId) &&
                             SealRockObjectiveCatalog.IsPointIcon(iconId))
            .Select(marker => new ObjectiveStateEvent(++sequence, marker.Id, marker.Label,
                marker.State, marker.OwnerTeam < 3 ? marker.OwnerTeam : null,
                marker.X, marker.Y, marker.Z, marker.EvidenceScope,
                marker.CurrentHp, marker.MaxHp, ProgressPercent: marker.ProgressPercent,
                DisplayText: marker.DisplayText))
            .ToArray();
        return ReduceAt(rows, long.MaxValue);
    }

    private static int RowOrder(ObjectiveStateEvent row) =>
        SealRockObjectiveCatalog.IsCompleteSnapshot(row) ? 3 : IsInactive(row) ? 0 :
        SealRockObjectiveCatalog.TryGetMapIcon(row.State, out var iconId) &&
        SealRockObjectiveCatalog.IsRankIcon(iconId) ? 1 : 2;

    private static uint MapIcon(string? state) =>
        SealRockObjectiveCatalog.TryGetMapIcon(state, out var iconId) ? iconId : 0;

    private static uint StableReducedObjectId(string pointKey)
    {
        uint hash = 2_166_136_261;
        foreach (var character in pointKey)
            hash = (hash ^ character) * 16_777_619;
        return 0xFFE0_0000u | (hash & 0x000F_FFFFu);
    }

    private static bool IsInactive(ObjectiveStateEvent row) =>
        string.Equals(row.State, "inactive", StringComparison.OrdinalIgnoreCase) ||
        (row.State ?? string.Empty).Contains("destroy", StringComparison.OrdinalIgnoreCase) ||
        (row.State ?? string.Empty).Contains("unload", StringComparison.OrdinalIgnoreCase);
}
