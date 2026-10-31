using System;
using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Authoritative director scores plus a conservative projection of finite
/// objective income which has already been committed. Director writes for one
/// logical tick may be split across frames, and different teams may tick at
/// different phases; therefore writes are coalesced briefly and each team is
/// tracked independently.
/// 权威实时比分，以及已经由有限时长据点承诺、但尚未跳完的保守投影。客户端可能把同一跳
/// 拆成多帧写入，不同阵营也可能错峰跳分，因此先合并短批次，再逐阵营跟踪周期流。
/// </summary>
internal sealed record LiveScoreProjection(int Maelstrom, int Adders, int Flames,
    int? MaelstromProjected, int? AddersProjected, int? FlamesProjected,
    string EvidenceScope)
{
    public int Current(byte team) => team switch { 0 => Maelstrom, 1 => Adders, _ => Flames };
    public int? Projected(byte team) => team switch
    {
        0 => MaelstromProjected, 1 => AddersProjected, _ => FlamesProjected
    };
}

internal sealed class LiveScoreProjectionTracker
{
    private const long CoalesceMs = 180;

    private sealed record Rule(int CombatPoints, int TickCount, int WinScore,
        int[] ObjectiveTickRates, int MaximumObjectives, bool PreserveDormantTransfers = false);

    private sealed class TeamStream
    {
        public int Rate;
        public int RemainingTicks;
        public long LastTickMs;
        public bool Confirmed;

        public TeamStream(int rate, int remainingTicks, long lastTickMs, bool confirmed)
        {
            Rate = rate;
            RemainingTicks = remainingTicks;
            LastTickMs = lastTickMs;
            Confirmed = confirmed;
        }
    }

    private readonly List<TeamStream>[] streams = [[], [], []];
    private readonly int[] pendingDelta = new int[3];
    private uint dutyId;
    private int[]? scores;
    private long pendingLastMs = -1;

    public void Reset()
    {
        dutyId = 0;
        scores = null;
        pendingLastMs = -1;
        Array.Clear(pendingDelta);
        foreach (var list in streams)
            list.Clear();
    }

    public void Observe(uint observedDutyId, long elapsedMs, int maelstrom, int adders, int flames)
    {
        if (observedDutyId != dutyId)
        {
            Reset();
            dutyId = observedDutyId;
        }

        var next = new[] { maelstrom, adders, flames };
        if (scores == null)
        {
            scores = next;
            return;
        }

        // Flush a completed logical director write even when the current frame
        // contains no score change. This lets +5 and +10 writes 30-70 ms apart
        // become one +15 objective tick without delaying the UI by a full tick.
        if (pendingLastMs >= 0 && elapsedMs - pendingLastMs > CoalesceMs)
            FlushPending();

        var prior = scores;
        scores = next;
        var delta = new[] { next[0] - prior[0], next[1] - prior[1], next[2] - prior[2] };
        if (delta.All(value => value == 0))
            return;

        if (pendingLastMs >= 0 && elapsedMs - pendingLastMs > CoalesceMs)
            FlushPending();
        for (var team = 0; team < 3; team++)
            pendingDelta[team] += delta[team];
        pendingLastMs = elapsedMs;
    }

    public LiveScoreProjection Snapshot(long elapsedMs)
    {
        if (pendingLastMs >= 0 && elapsedMs - pendingLastMs > CoalesceMs)
            FlushPending();

        var current = scores ?? [0, 0, 0];
        var rule = RuleFor(dutyId);
        int? Projected(int team)
        {
            if (rule == null)
                return null;
            var pending = streams[team]
                .Where(stream => stream.Confirmed && stream.RemainingTicks > 0 &&
                                 elapsedMs - stream.LastTickMs <= 4_500)
                .Sum(stream => stream.Rate * stream.RemainingTicks);
            return pending > 0 ? Math.Min(rule.WinScore, current[team] + pending) : null;
        }

        var projected = new[] { Projected(0), Projected(1), Projected(2) };
        return new LiveScoreProjection(current[0], current[1], current[2],
            projected[0], projected[1], projected[2],
            projected.Any(value => value.HasValue)
                ? "per_team_periodic_objective_ticks_rule_projected_v3"
                : "director_score_only");
    }

    private void FlushPending()
    {
        var rule = RuleFor(dutyId);
        var atMs = pendingLastMs;
        pendingLastMs = -1;
        if (rule == null)
        {
            Array.Clear(pendingDelta);
            return;
        }

        var rates = SolveObjectiveRates(pendingDelta, rule);
        Array.Clear(pendingDelta);
        if (rates == null)
            return;
        for (var team = 0; team < 3; team++)
            if (rates[team] > 0)
                ObserveTeamTick(team, rates[team], atMs, rule);
    }

    private void ObserveTeamTick(int team, int rate, long elapsedMs, Rule rule)
    {
        foreach (var list in streams)
            list.RemoveAll(stream => !stream.Confirmed && elapsedMs - stream.LastTickMs > 3_600);

        var own = streams[team]
            .Where(stream => stream.Rate == rate && IsNextTick(stream.LastTickMs, elapsedMs))
            .OrderByDescending(stream => stream.Confirmed)
            .ThenBy(stream => Math.Abs((elapsedMs - stream.LastTickMs) - 3_000))
            .FirstOrDefault();
        if (own != null)
        {
            if (!own.Confirmed)
            {
                own.Confirmed = true;
                own.RemainingTicks = Math.Max(0, rule.TickCount - 2);
            }
            else
                own.RemainingTicks = Math.Max(0, own.RemainingTicks - 1);
            own.LastTickMs = elapsedMs;
            return;
        }

        // A captured Seal Rock node keeps its finite remaining lifetime. If the
        // same periodic stream moves to another team at the expected next tick,
        // transfer that balance instead of restarting a full objective timer.
        for (var otherTeam = 0; otherTeam < 3; otherTeam++)
        {
            if (otherTeam == team)
                continue;
            var other = streams[otherTeam]
                .Where(stream => stream.Confirmed && stream.Rate == rate &&
                                 IsNextTick(stream.LastTickMs, elapsedMs))
                .OrderBy(stream => Math.Abs((elapsedMs - stream.LastTickMs) - 3_000))
                .FirstOrDefault();
            if (other == null)
                continue;
            streams[otherTeam].Remove(other);
            other.RemainingTicks = Math.Max(0, other.RemainingTicks - 1);
            other.LastTickMs = elapsedMs;
            streams[team].Add(other);
            return;
        }

        // Seal Rock ownership changes normally contain a neutral/capture gap,
        // so the first tick for the new owner is often not adjacent to the old
        // owner's last tick.  The tomelith does not regain spent capacity while
        // neutral.  Reuse a dormant balance of the same rank and, when several
        // indistinguishable nodes exist, choose the smallest remaining balance
        // so that the parenthesised score cannot be inflated back to 100%.
        if (rule.PreserveDormantTransfers)
        {
            var dormant = Enumerable.Range(0, 3)
                .Where(otherTeam => otherTeam != team)
                .SelectMany(otherTeam => streams[otherTeam]
                    .Where(stream => stream.Confirmed && stream.Rate == rate &&
                                     stream.RemainingTicks > 0 &&
                                     elapsedMs - stream.LastTickMs > 3_600)
                    .Select(stream => (Team: otherTeam, Stream: stream)))
                .OrderBy(candidate => candidate.Stream.RemainingTicks)
                .ThenByDescending(candidate => candidate.Stream.LastTickMs)
                .FirstOrDefault();
            if (dormant.Stream != null)
            {
                streams[dormant.Team].Remove(dormant.Stream);
                dormant.Stream.RemainingTicks = Math.Max(0, dormant.Stream.RemainingTicks - 1);
                dormant.Stream.LastTickMs = elapsedMs;
                streams[team].Add(dormant.Stream);
                return;
            }
        }

        // A changed or stale rate must earn two fresh periodic observations.
        // This rejects isolated +8 combat gains and other one-off score writes.
        streams[team].Add(new TeamStream(rate, 0, elapsedMs, false));
        if (streams[team].Count > rule.MaximumObjectives * 2)
            streams[team].RemoveAt(0);
    }

    private static int[]? SolveObjectiveRates(IReadOnlyList<int> delta, Rule rule)
    {
        var total = delta.Sum();
        if (total <= 0)
            return null;
        var maximumRate = rule.MaximumObjectives * rule.ObjectiveTickRates.Max();
        var possible = RepresentableSums(rule.ObjectiveTickRates, rule.MaximumObjectives, maximumRate);
        int[]? best = null;
        var bestCost = int.MaxValue;
        foreach (var left in possible)
        foreach (var middle in possible)
        {
            var right = total - left - middle;
            if (!possible.Contains(right))
                continue;
            var values = new[] { left, middle, right };
            if (Enumerable.Range(0, 3).Any(i => (delta[i] - values[i]) % rule.CombatPoints != 0))
                continue;
            // Prefer the least objective income required to explain each team's
            // observed delta; periodic repetition, not this tie break, confirms it.
            var cost = Enumerable.Range(0, 3).Sum(i => Math.Abs(values[i] - Math.Max(0, delta[i])));
            if (cost >= bestCost)
                continue;
            bestCost = cost;
            best = values;
        }
        return best;
    }

    private static HashSet<int> RepresentableSums(IReadOnlyList<int> rates, int maximumObjects,
        int maximumRate)
    {
        var values = new HashSet<int> { 0 };
        for (var count = 0; count < maximumObjects; count++)
            foreach (var value in values.ToArray())
            foreach (var rate in rates)
                if (value + rate <= maximumRate)
                    values.Add(value + rate);
        return values;
    }

    private static bool IsNextTick(long priorMs, long nextMs)
    {
        var delta = nextMs - priorMs;
        return delta is >= 2_400 and <= 3_600;
    }

    private static Rule? RuleFor(uint duty) => duty switch
    {
        701 => new Rule(8, 10, 1400, [5, 10, 20], 6),
        1080 => new Rule(8, 10, 1400, [5, 10, 20], 12),
        130 => new Rule(5, 40, 700, [2, 3, 4], 10, PreserveDormantTransfers: true),
        _ => null
    };
}
