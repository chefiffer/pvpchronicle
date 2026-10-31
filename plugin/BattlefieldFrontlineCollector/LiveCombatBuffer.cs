using System;
using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// A small in-memory view of already captured action-effect packets.  It never
/// rereads the JSONL file and is deliberately bounded, so opening the live UI
/// does not add disk I/O to the game thread.
/// 已采集动作包的有界内存视图；实时窗口不回读日志，也不会无限增长。
/// </summary>
internal sealed class LiveCombatBuffer
{
    // Version-reviewed advance support actions whose tactical effect precedes
    // the payload window. Retain one lightweight row even when the packet has
    // no damage/heal amount, otherwise a Bard/Dancer-style fourth member can
    // never enter the live coordination candidate pool.
    private static readonly HashSet<uint> AdvanceSupportActions = [29401, 43250];
    private const int MaximumRows = 8192;
    private const long RetentionMs = 65_000;
    internal const long EncounterGapMs = 8_000;
    private readonly object gate = new();
    private readonly Queue<LiveCombatRow> rows = new();
    private readonly Queue<LiveCoordinationRow> coordination = new();
    // Match-long tendency evidence is compressed into five-second actor buckets.
    // The detailed combat table remains a short rolling buffer; this separate
    // store makes live tendencies cumulative without retaining every packet.
    // 本局倾向证据按五秒和人物压缩；详细表仍保持短缓冲，避免整局事件无限增长。
    private readonly Dictionary<TendencyRowKey, LiveCombatRow> matchTendencyRows = new();
    private readonly List<LiveCoordinationRow> matchCoordination = new();
    private readonly Dictionary<uint, MutableObservedKda> observedKda = new();
    private readonly Queue<DamageCredit> recentDamage = new();
    private readonly Dictionary<uint, long> lastDeathAt = new();
    private long encounterEndMs = -1;
    private long matchEvidenceEndMs = -1;

    private readonly record struct TendencyRowKey(long Bucket, uint SourceEntityId,
        uint TargetEntityId, uint ActionId, string Kind);
    private readonly record struct DamageCredit(long ElapsedMs, uint SourceEntityId,
        uint TargetEntityId);
    private sealed class MutableObservedKda(uint entityId, string name, ushort worldId, byte jobId)
    {
        public uint EntityId { get; } = entityId;
        public string Name { get; set; } = name;
        public ushort WorldId { get; set; } = worldId;
        public byte JobId { get; set; } = jobId;
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Assists { get; set; }
        public LiveObservedKda Snapshot() => new(EntityId, Name, WorldId, JobId, Kills, Deaths, Assists);
    }

    public void Add(ActionEffectEvent value, LiveTargetState? localTargetState)
    {
        // Actor snapshots may contain a transient duplicate while an object is
        // replaced in the table.  Last observation wins instead of allowing a
        // UI-only live view to throw on ToDictionary.
        var actors = new Dictionary<uint, ActionActorIdentity>();
        foreach (var actor in value.Actors ?? []) actors[actor.EntityId] = actor;
        actors.TryGetValue(value.SourceEntityId, out var source);
        var sourceName = source?.OwnerName is { Length: > 0 } owner ? owner : source?.Name ?? string.Empty;
        var sourceWorld = source?.OwnerWorldId is > 0 ? source.OwnerWorldId : source?.WorldId ?? 0;
        var sourceJob = source?.OwnerClassJobId is > 0 ? source.OwnerClassJobId : source?.ClassJobId ?? 0;
        var sourceBattalion = source?.OwnerEntityId is > 0
            ? source.OwnerBattalion : source?.Battalion ?? 255;
        var selectableSource = source?.OwnerEntityId is > 0 ? source.OwnerEntityId : value.SourceEntityId;
        var appended = new List<LiveCombatRow>(value.Effects.Count);
        if (AdvanceSupportActions.Contains(value.ActionId) && !string.IsNullOrWhiteSpace(sourceName))
            appended.Add(new LiveCombatRow(value.ElapsedMs, value.ActionId, value.ActionType,
                selectableSource, sourceName, sourceWorld, (byte)Math.Min(byte.MaxValue, sourceJob),
                0, string.Empty, 0, 0, 1, "advance_support", 0, 0, 0,
                null, 255, 255, 255, 255, sourceBattalion, 255));
        foreach (var effect in value.Effects)
        {
            var kind = effect.Type switch
            {
                4 => "heal",
                3 or 5 or 6 or 7 or 74 => "damage",
                _ => string.Empty
            };
            if (kind.Length == 0 || effect.Amount == 0) continue;
            actors.TryGetValue(effect.TargetEntityId, out var target);
            appended.Add(new LiveCombatRow(value.ElapsedMs, value.ActionId, value.ActionType,
                selectableSource, sourceName, sourceWorld, (byte)Math.Min(byte.MaxValue, sourceJob),
                effect.TargetEntityId, target?.Name ?? string.Empty, target?.WorldId ?? 0,
                (byte)Math.Min(byte.MaxValue, target?.ClassJobId ?? 0), effect.Amount, kind,
                effect.TargetCurrentHp, effect.TargetMaxHp, effect.TargetShieldPercent,
                localTargetState?.EntityId == effect.TargetEntityId ? localTargetState.StatusIds : null,
                255, 255, 255, 255, sourceBattalion, target?.Battalion ?? 255));
        }
        if (appended.Count == 0) return;
        lock (gate)
        {
            // A quiet gap starts a new encounter.  Until then the completed
            // encounter remains available as a stable post-fight buffer.
            if (encounterEndMs >= 0 && value.ElapsedMs - encounterEndMs > EncounterGapMs)
                rows.Clear();
            foreach (var row in appended) rows.Enqueue(row);
            foreach (var row in appended)
            {
                AddMatchTendencyRow(row);
                RegisterActor(row.SourceEntityId, row.SourceName, row.SourceWorldId, row.SourceJobId);
                RegisterActor(row.TargetEntityId, row.TargetName, row.TargetWorldId, row.TargetJobId);
                if (row.Kind == "damage" && row.SourceEntityId != 0 && row.TargetEntityId != 0 &&
                    row.SourceEntityId != row.TargetEntityId)
                    recentDamage.Enqueue(new DamageCredit(row.ElapsedMs, row.SourceEntityId,
                        row.TargetEntityId));
            }
            encounterEndMs = Math.Max(encounterEndMs, value.ElapsedMs);
            matchEvidenceEndMs = Math.Max(matchEvidenceEndMs, value.ElapsedMs);
            var cutoff = encounterEndMs - RetentionMs;
            while (rows.Count > 0 && (rows.Count > MaximumRows || rows.Peek().ElapsedMs < cutoff))
                rows.Dequeue();
            while (recentDamage.Count > 0 && recentDamage.Peek().ElapsedMs < encounterEndMs - 20_000)
                recentDamage.Dequeue();
        }
    }

    public void AddDeath(ExactDeathEvent value)
    {
        lock (gate)
        {
            // ActorControl can repeat during a transition. Count at most one
            // observed down per actor within two seconds.
            if (lastDeathAt.TryGetValue(value.TargetEntityId, out var prior) &&
                value.ElapsedMs - prior < 2_000) return;
            lastDeathAt[value.TargetEntityId] = value.ElapsedMs;
            RegisterActor(value.TargetEntityId, string.Empty, 0, 0);
            observedKda[value.TargetEntityId].Deaths++;

            var contributors = recentDamage
                .Where(row => row.TargetEntityId == value.TargetEntityId &&
                              row.ElapsedMs >= value.ElapsedMs - 15_000 &&
                              row.ElapsedMs <= value.ElapsedMs)
                .GroupBy(row => row.SourceEntityId)
                .Select(group => new { EntityId = group.Key, LastHit = group.Max(row => row.ElapsedMs) })
                .OrderByDescending(row => row.LastHit)
                .ToArray();
            if (contributors.Length == 0) return;
            var killer = contributors[0].EntityId;
            if (observedKda.TryGetValue(killer, out var killerRow)) killerRow.Kills++;
            foreach (var contributor in contributors.Skip(1))
                if (observedKda.TryGetValue(contributor.EntityId, out var assistRow)) assistRow.Assists++;
        }
    }

    public LiveCombatSnapshot Snapshot(long currentElapsedMs)
    {
        lock (gate)
        {
            var active = encounterEndMs >= 0 && currentElapsedMs >= encounterEndMs &&
                         currentElapsedMs - encounterEndMs <= EncounterGapMs;
            return new LiveCombatSnapshot(rows.ToArray(), coordination.ToArray(),
                matchTendencyRows.Values.OrderBy(x => x.ElapsedMs).ToArray(),
                matchCoordination.ToArray(), observedKda.Values.Select(x => x.Snapshot())
                    .OrderByDescending(x => x.Kills).ThenBy(x => x.Deaths)
                    .ThenByDescending(x => x.Assists).ThenBy(x => x.Name).ToArray(),
                encounterEndMs, matchEvidenceEndMs, active);
        }
    }

    public void AddCoordination(long elapsedMs, string speaker, string digest,
        IReadOnlyList<string> categories)
    {
        if (string.IsNullOrWhiteSpace(speaker) || categories.Count == 0) return;
        lock (gate)
        {
            coordination.Enqueue(new LiveCoordinationRow(elapsedMs, speaker, digest, categories.ToArray()));
            matchCoordination.Add(new LiveCoordinationRow(elapsedMs, speaker, digest, categories.ToArray()));
            var cutoff = elapsedMs - RetentionMs;
            while (coordination.Count > 512 || coordination.Count > 0 && coordination.Peek().ElapsedMs < cutoff)
                coordination.Dequeue();
        }
    }

    /// <summary>
    /// Backfills authoritative faction/squad identities after the result packet.
    /// The live window deliberately does not infer these values from combat edges.
    /// 结算后回填权威阵营/分队；赛中不会用伤害关系猜测阵营。
    /// </summary>
    public void ApplyResult(IReadOnlyList<PlayerResult> players)
    {
        var identities = players
            .GroupBy(player => (player.Name, player.WorldId))
            .ToDictionary(group => group.Key, group => group.First());
        lock (gate)
        {
            if (rows.Count == 0) return;
            var updated = new Queue<LiveCombatRow>(rows.Count);
            foreach (var row in rows)
            {
                identities.TryGetValue((row.SourceName, row.SourceWorldId), out var source);
                identities.TryGetValue((row.TargetName, row.TargetWorldId), out var target);
                updated.Enqueue(row with
                {
                    SourceTeam = source?.Team ?? row.SourceTeam,
                    SourceAlliance = source?.Alliance ?? row.SourceAlliance,
                    TargetTeam = target?.Team ?? row.TargetTeam,
                    TargetAlliance = target?.Alliance ?? row.TargetAlliance
                });
            }
            rows.Clear();
            foreach (var row in updated) rows.Enqueue(row);

            if (matchTendencyRows.Count > 0)
            {
                var cumulative = matchTendencyRows.Values.ToArray();
                matchTendencyRows.Clear();
                foreach (var row in cumulative)
                {
                    identities.TryGetValue((row.SourceName, row.SourceWorldId), out var source);
                    identities.TryGetValue((row.TargetName, row.TargetWorldId), out var target);
                    AddMatchTendencyRow(row with
                    {
                        SourceTeam = source?.Team ?? row.SourceTeam,
                        SourceAlliance = source?.Alliance ?? row.SourceAlliance,
                        TargetTeam = target?.Team ?? row.TargetTeam,
                        TargetAlliance = target?.Alliance ?? row.TargetAlliance
                    });
                }
            }
        }
    }

    private void AddMatchTendencyRow(LiveCombatRow row)
    {
        if (string.IsNullOrWhiteSpace(row.SourceName)) return;
        var kind = row.Kind;
        var actionId = row.ActionId;
        var targetId = 0u;
        // Damage/healing only needs one aggregate per actor and five-second
        // bucket. Reviewed controls retain target/action so the disruptor
        // evidence count and distinct-target count remain auditable.
        if (LiveTendencyAnalyzer.IsReviewedControlAction(row.ActionId))
        {
            var controlKey = new TendencyRowKey(row.ElapsedMs / 5_000,
                row.SourceEntityId, row.TargetEntityId, row.ActionId, "control_evidence");
            matchTendencyRows.TryAdd(controlKey, row with { Kind = "control_evidence", Amount = 0 });
        }
        if (kind is not ("damage" or "heal" or "advance_support")) return;
        if (kind == "advance_support") targetId = row.TargetEntityId;
        else actionId = 0;
        var key = new TendencyRowKey(row.ElapsedMs / 5_000, row.SourceEntityId,
            targetId, actionId, kind);
        if (!matchTendencyRows.TryGetValue(key, out var existing))
        {
            matchTendencyRows[key] = row with { ActionId = actionId, TargetEntityId = targetId };
            return;
        }
        var amount = Math.Min(uint.MaxValue, (ulong)existing.Amount + row.Amount);
        matchTendencyRows[key] = existing with
        {
            ElapsedMs = Math.Max(existing.ElapsedMs, row.ElapsedMs),
            Amount = (uint)amount,
            TargetCurrentHp = row.TargetCurrentHp,
            TargetMaxHp = row.TargetMaxHp,
            TargetShieldPercent = row.TargetShieldPercent
        };
    }

    private void RegisterActor(uint entityId, string name, ushort worldId, byte jobId)
    {
        if (entityId == 0) return;
        if (!observedKda.TryGetValue(entityId, out var actor))
        {
            observedKda[entityId] = new MutableObservedKda(entityId, name, worldId, jobId);
            return;
        }
        if (!string.IsNullOrWhiteSpace(name)) actor.Name = name;
        if (worldId != 0) actor.WorldId = worldId;
        if (jobId != 0) actor.JobId = jobId;
    }

    public void Reset()
    {
        lock (gate)
        {
            rows.Clear(); coordination.Clear(); matchTendencyRows.Clear(); matchCoordination.Clear();
            observedKda.Clear(); recentDamage.Clear(); lastDeathAt.Clear();
            encounterEndMs = -1; matchEvidenceEndMs = -1;
        }
    }
}
