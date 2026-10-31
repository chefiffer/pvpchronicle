using System;
using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Lightweight observer-bounded candidates for the live window. These scores
/// are navigation hints only; authoritative post-match behavior scores remain
/// server-side and use the complete versioned evidence contract.
/// 实时窗口的轻量候选器；只用于导航，不替代赛后版本化行为模型。
/// </summary>
internal static class LiveTendencyAnalyzer
{
    // Verified control actions from coordination_function_semantics_v1.1.
    // Purify/Guard and unreviewed semantic guesses are deliberately excluded.
    private static readonly HashSet<uint> ControlActions =
    [4283, 29084, 29244, 29248, 29394, 29407, 29432, 29496, 41430, 41433, 41492, 41493, 43257];
    private static readonly IReadOnlyDictionary<uint, long> AdvanceSupportWindows =
        new Dictionary<uint, long> { [29401] = 30_000, [43250] = 15_000 };

    internal static bool IsReviewedControlAction(uint actionId) => ControlActions.Contains(actionId);

    public static LiveTendencySnapshot Analyze(IReadOnlyList<LiveCombatRow> rows,
        IReadOnlyList<LiveCoordinationRow> commands, byte localBattalion, long endMs)
    {
        return new LiveTendencySnapshot(
            BuildCoordination(rows, localBattalion),
            BuildCommanders(rows, commands, localBattalion),
            BuildDisruptors(rows, localBattalion));
    }

    private static IReadOnlyList<LiveGroupCandidate> BuildCoordination(
        IReadOnlyList<LiveCombatRow> rows, byte localBattalion)
    {
        var allActors = rows.Where(x => IsOwn(x.SourceBattalion, localBattalion) &&
                                     (x.Kind == "damage" || x.Kind == "advance_support") &&
                                     !string.IsNullOrWhiteSpace(x.SourceName))
            .GroupBy(x => Actor(x.SourceEntityId, x.SourceName, x.SourceWorldId, x.SourceJobId))
            .Select(g => new ActorBuckets(g.Key, EffectiveBuckets(g),
                g.Any(x => x.Kind == "advance_support")))
            .Where(x => x.Buckets.Count >= 2)
            .ToArray();
        // Keep the search bounded, but do not let match-long damage totals push
        // a verified early support member out of the candidate pool.
        var actors = allActors.Where(x => x.HasAdvanceSupport)
            .Concat(allActors.OrderByDescending(x => x.Buckets.Values.Sum()).Take(8))
            .DistinctBy(x => x.Actor.EntityId).Take(12).ToArray();
        if (actors.Length < 3) return [];

        var candidates = new List<LiveGroupCandidate>();
        foreach (var size in new[] { 4, 3 })
        foreach (var group in Combinations(actors, size))
        {
            var pairScores = new List<double>();
            for (var i = 0; i < group.Length; i++)
            for (var j = i + 1; j < group.Length; j++)
            {
                var left = group[i].Buckets.Keys.ToHashSet();
                var right = group[j].Buckets.Keys.ToHashSet();
                var union = left.Union(right).Count();
                pairScores.Add(union == 0 ? 0 : left.Intersect(right).Count() / (double)union);
            }
            var shared = group.Select(x => x.Buckets.Keys.AsEnumerable())
                .Aggregate((a, b) => a.Intersect(b)).Count();
            var score = Math.Clamp(100 * (.72 * pairScores.Average() + .20 * Math.Min(1, shared / 2d) +
                                          (size == 4 ? .08 : .05)), 0, 100);
            if (score >= 42) candidates.Add(new LiveGroupCandidate(group.Select(x => x.Actor).ToArray(), score,
                shared, "visible_coactivity_only"));
        }
        // Cardinality is structural. A tighter three-person subset normally
        // has a higher pairwise average than the four-person core containing
        // it, so select qualifying four-person candidates first. Three-person
        // rows are a rare visibility fallback and use a materially higher bar.
        var selected = new List<LiveGroupCandidate>();
        foreach (var candidate in candidates.Where(x => x.Members.Count == 4 && x.Score >= 42 &&
                     !candidates.Any(three => three.Members.Count == 3 && three.Score > x.Score + 12 &&
                         three.Members.Select(member => member.EntityId)
                             .All(id => x.Members.Any(member => member.EntityId == id))))
                     .OrderByDescending(x => x.Score))
        {
            if (selected.Any(existing => existing.Members.Select(x => x.EntityId)
                    .Intersect(candidate.Members.Select(x => x.EntityId)).Count() >= 3)) continue;
            selected.Add(candidate);
            if (selected.Count == 3) break;
        }
        foreach (var candidate in candidates.Where(x => x.Members.Count == 3 &&
                     x.Score >= (actors.Length == 3 ? 55 : 68)).OrderByDescending(x => x.Score))
        {
            if (selected.Any(existing => existing.Members.Select(x => x.EntityId)
                    .Intersect(candidate.Members.Select(x => x.EntityId)).Any())) continue;
            selected.Add(candidate);
            if (selected.Count == 3) break;
        }
        return selected;
    }

    private static IReadOnlyDictionary<long, long> EffectiveBuckets(IEnumerable<LiveCombatRow> rows)
    {
        var result = new Dictionary<long, long>();
        foreach (var row in rows)
        {
            if (row.Kind == "damage")
            {
                var bucket = row.ElapsedMs / 5_000;
                result[bucket] = result.GetValueOrDefault(bucket) + row.Amount;
                continue;
            }
            if (!AdvanceSupportWindows.TryGetValue(row.ActionId, out var window)) continue;
            var first = row.ElapsedMs / 5_000;
            var last = (row.ElapsedMs + window) / 5_000;
            for (var bucket = first; bucket <= last; bucket++)
                result[bucket] = Math.Max(1, result.GetValueOrDefault(bucket));
        }
        return result;
    }

    private static IReadOnlyList<LiveActorCandidate> BuildCommanders(IReadOnlyList<LiveCombatRow> rows,
        IReadOnlyList<LiveCoordinationRow> commands, byte localBattalion)
    {
        var identities = rows.SelectMany(x => new[]
            {
                Actor(x.SourceEntityId, x.SourceName, x.SourceWorldId, x.SourceJobId),
                Actor(x.TargetEntityId, x.TargetName, x.TargetWorldId, x.TargetJobId)
            }).Where(x => x.Name.Length > 0).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<LiveActorCandidate>();
        foreach (var group in commands.GroupBy(x => x.Speaker, StringComparer.OrdinalIgnoreCase))
        {
            var distinctMessages = group.Select(x => x.Digest).Distinct().Count();
            var categories = group.SelectMany(x => x.Categories).Distinct().Count();
            var responseWindows = group.Count(command => rows.Any(row =>
                IsOwn(row.SourceBattalion, localBattalion) && row.ElapsedMs >= command.ElapsedMs + 500 &&
                row.ElapsedMs <= command.ElapsedMs + 8_000 &&
                !row.SourceName.Equals(group.Key, StringComparison.OrdinalIgnoreCase)));
            if (distinctMessages < 2 || categories < 2 || responseWindows < 2) continue;
            identities.TryGetValue(group.Key, out var actor);
            actor ??= new LiveActorRef(0, group.Key, 0, 0);
            var score = Math.Clamp(20 + distinctMessages * 10 + categories * 12 + responseWindows * 8, 0, 100);
            result.Add(new LiveActorCandidate(actor, score, distinctMessages,
                "direct_allied_commands_and_observed_response"));
        }
        return result.OrderByDescending(x => x.Score).Take(5).ToArray();
    }

    private static IReadOnlyList<LiveActorCandidate> BuildDisruptors(
        IReadOnlyList<LiveCombatRow> rows, byte localBattalion) => rows
        .Where(x => IsOwn(x.SourceBattalion, localBattalion) &&
                    (x.Kind == "control_evidence" || ControlActions.Contains(x.ActionId)) &&
                    !string.IsNullOrWhiteSpace(x.SourceName))
        .GroupBy(x => Actor(x.SourceEntityId, x.SourceName, x.SourceWorldId, x.SourceJobId))
        .Select(g => new LiveActorCandidate(g.Key,
            Math.Clamp(18 + g.Count() * 12 + g.Select(x => x.TargetEntityId).Distinct().Count() * 8, 0, 100),
            g.Count(), "verified_control_actions_only"))
        .Where(x => x.EvidenceCount >= 2)
        .OrderByDescending(x => x.Score).Take(5).ToArray();

    private static bool IsOwn(byte battalion, byte localBattalion) =>
        localBattalion <= 2 && battalion == localBattalion;
    private static LiveActorRef Actor(uint id, string name, ushort world, byte job) =>
        new(id, name, world, job);

    private static IEnumerable<T[]> Combinations<T>(IReadOnlyList<T> values, int count)
    {
        var chosen = new T[count];
        IEnumerable<T[]> Walk(int start, int depth)
        {
            if (depth == count) { yield return chosen.ToArray(); yield break; }
            for (var i = start; i <= values.Count - (count - depth); i++)
            {
                chosen[depth] = values[i];
                foreach (var result in Walk(i + 1, depth + 1)) yield return result;
            }
        }
        return Walk(0, 0);
    }

    private sealed record ActorBuckets(LiveActorRef Actor, IReadOnlyDictionary<long, long> Buckets,
        bool HasAdvanceSupport);
}

internal sealed record LiveActorRef(uint EntityId, string Name, ushort WorldId, byte JobId);
internal sealed record LiveActorCandidate(LiveActorRef Actor, double Score, int EvidenceCount, string EvidenceScope);
internal sealed record LiveGroupCandidate(IReadOnlyList<LiveActorRef> Members, double Score,
    int SharedBurstBuckets, string EvidenceScope);
internal sealed record LiveTendencySnapshot(IReadOnlyList<LiveGroupCandidate> Coordination,
    IReadOnlyList<LiveActorCandidate> Commanders, IReadOnlyList<LiveActorCandidate> Disruptors);
