using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// High-cadence, allocation-bounded movement screening.  Only a completed
/// repeated outlier is written; ordinary 250 ms samples never touch disk.
/// 高频、有界的移动筛查；普通采样不落盘，只写出达到重复门槛的证据包。
/// </summary>
internal sealed class MovementAnomalyTracker
{
    internal const string MovementCatalogVersion = MovementIntegrityRules.CatalogVersion;
    internal const string DisplacementCatalogVersion = MovementIntegrityRules.DisplacementCatalogVersion;
    private readonly bool catalogCompatible;
    private readonly string gameVersion;

    public MovementAnomalyTracker(string gameVersion)
    {
        this.gameVersion = gameVersion.Trim();
        catalogCompatible = MovementIntegrityRules.SupportsGameVersion(this.gameVersion);
    }
    // Ordinary movement is screened much more tightly than status-enhanced
    // movement. Both thresholds still require repetition and reject known
    // displacement actions.  普通移动与带移速状态分开设限，避免“有任意状态就无限放行”。
    private const double BaseRunSpeed = 6.0;
    // The old fixed +1.5 y/s margin let sustained ordinary movement reach
    // 7.5 y/s before screening. Keep only a small fixed margin plus a bounded
    // per-sample coordinate jitter allowance. The latter is divided by dt, so
    // short samples are not punished while longer samples converge tightly on
    // the documented 6 y/s base speed.
    // 旧版固定 +1.5 的余量过宽；现在使用小幅固定余量与有上限的坐标抖动距离。
    private const double FixedSamplingSlack = .25;
    private const double PositionJitterAllowance = .10;
    private const double HardEnvelope = BaseRunSpeed + FixedSamplingSlack + PositionJitterAllowance / .10;
    // Publishing is intentionally much stricter than merely crossing the
    // engineering envelope. Only a far-tail sample above
    // E + max(4.5 y/s, 60% of E) contributes to an integrity-review candidate.
    // 发布候选要求显著脱离正常范围，而不是刚刚擦过工程包络。
    private const double SevereExcessFloor = 4.5;
    private const double SevereExcessRatio = .60;
    private const int RequiredIntervals = 3;
    private static readonly double[] DistributionUpperBounds =
        [2, 4, 6, 6.5, 7, 7.5, 8, 10, 12, 14, 16, 18, 22, 30, 45];
    // Versioned PvP displacement catalog. These actions move their caster by
    // design and therefore explain a short speed spike; effect type 32 covers
    // observed pull/knockback displacement applied by another actor.
    // 版本化的合法位移目录；不能把突进、后跳、缩地或被拉/击退误报为改速。
    private static readonly HashSet<uint> CasterDisplacementActions =
        [29065, 29079, 29092, 29123, 29229, 29261, 29430, 29479, 29494,
         29510, 29513, 29522, 29532, 29550, 29660, 29689, 29692, 29699,
         29700, 29707, 29708, 39148, 39153, 39184, 41488, 41581, 41646,
         41654, 41942, 42039];
    private sealed record DisplacementAttribution(long ElapsedMs, uint ActionId, string Kind,
        long? EndElapsedMs = null);
    private sealed class PendingForcedMovement
    {
        public long EffectElapsedMs;
        public uint ActionId;
        public uint SourceEntityId;
        public string SourceName = string.Empty;
        public ushort SourceWorldId;
        public byte SourceClassJobId;
        public uint TargetEntityId;
        public string TargetName = string.Empty;
        public ushort TargetWorldId;
        public byte TargetClassJobId;
        public bool Started;
        public long LastObservedMs;
        public readonly List<ForcedMovementPositionSample> Samples = new(6);
    }
    private sealed class Track
    {
        public long ElapsedMs; public float X; public float Y; public float Z; public int Tick;
        public string Name = string.Empty; public ushort WorldId; public byte ClassJobId;
        public bool Mounted;
        public int[] SpeedStatusIds = [];
        public int ContinuousIntervals;
        public readonly int[] Distribution = new int[DistributionUpperBounds.Length + 1];
        public readonly int[] SpeedStatusDistribution = new int[DistributionUpperBounds.Length + 1];
        public readonly int[] DisplacementDistribution = new int[DistributionUpperBounds.Length + 1];
        public readonly int[] UnexplainedDistribution = new int[DistributionUpperBounds.Length + 1];
        public readonly int[] MountedDistribution = new int[DistributionUpperBounds.Length + 1];
        public readonly int[] BuffNormalizedDistribution = new int[DistributionUpperBounds.Length + 1];
        public int MountedIntervals;
        public int BuffNormalizedIntervals;
        public long LastPublishedMs = -60_000;
        public readonly List<MovementSpeedSample> Streak = new(RequiredIntervals + 2);
    }
    private readonly Dictionary<uint, Track> tracks = new();
    private readonly Dictionary<uint, List<DisplacementAttribution>> displacement = new();
    private readonly object displacementGate = new();
    private readonly Dictionary<(uint Source, uint Sequence, uint Action, uint Target),
        PendingForcedMovement> pendingForcedMovement = new();
    private readonly object forcedMovementGate = new();
    private int tick;

    public void ObserveAction(ActionEffectEvent value)
    {
        lock (displacementGate)
        {
            if (value.AffectsPosition || CasterDisplacementActions.Contains(value.ActionId))
                AddAttribution(value.SourceEntityId,
                    new DisplacementAttribution(value.ElapsedMs, value.ActionId, "caster_displacement_action"));
            if (value.Effects.Any(effect => MovementIntegrityRules.IsForcedTargetEffect(
                    value.ActionId, effect.TargetIndex, effect.Type)))
                foreach (var target in value.Effects
                             .Where(effect => MovementIntegrityRules.IsForcedTargetEffect(
                                 value.ActionId, effect.TargetIndex, effect.Type))
                             .Select(effect => effect.TargetEntityId).Where(target => target != 0).Distinct())
                {
                    AddAttribution(target,
                        new DisplacementAttribution(value.ElapsedMs, value.ActionId, "forced_displacement_effect"));
                    var sourceIdentity = value.Actors?.FirstOrDefault(actor =>
                        actor.EntityId == value.SourceEntityId);
                    var targetIdentity = value.Actors?.FirstOrDefault(actor => actor.EntityId == target);
                    var sequence = value.GlobalSequence != 0
                        ? value.GlobalSequence
                        : unchecked((uint)value.ElapsedMs);
                    lock (forcedMovementGate)
                        pendingForcedMovement.TryAdd((value.SourceEntityId, sequence, value.ActionId, target),
                            new PendingForcedMovement
                            {
                                EffectElapsedMs = value.ElapsedMs,
                                ActionId = value.ActionId,
                                SourceEntityId = value.SourceEntityId,
                                SourceName = sourceIdentity?.Name ?? string.Empty,
                                SourceWorldId = sourceIdentity?.WorldId ?? 0,
                                SourceClassJobId = (byte)(sourceIdentity?.ClassJobId ?? 0),
                                TargetEntityId = target,
                                TargetName = targetIdentity?.Name ?? string.Empty,
                                TargetWorldId = targetIdentity?.WorldId ?? 0,
                                TargetClassJobId = (byte)(targetIdentity?.ClassJobId ?? 0)
                            });
                }
        }
    }

    /// <summary>
    /// Capture a one-second target trajectory around an observed forced-movement effect.
    /// The previous framework sample must precede the effect and every following sample
    /// must remain loaded at 100-500 ms cadence. Rejected windows are retained as
    /// coverage evidence, but only continuous windows enter reference distributions.
    /// 仅在效果前后连续加载时保留一秒短窗；断帧只记拒绝理由。
    /// </summary>
    public IReadOnlyList<ForcedMovementObservationEvent> ObserveForcedMovement(string schema,
        string matchUid, long elapsedMs, IEnumerable<IBattleChara> actors)
    {
        // Forced displacement is sparse. Avoid enumerating ObjectTable and
        // allocating a lookup on ordinary 250 ms ticks when no short window is
        // active. A network event racing this check is simply handled on the
        // next framework tick, still inside the 500 ms boundary gate.
        lock (forcedMovementGate)
            if (pendingForcedMovement.Count == 0) return [];
        // ObjectTable can transiently expose duplicate wrappers for one entity
        // during churn.  Keep one entry instead of letting telemetry crash the
        // framework update with a duplicate-key exception.
        var visible = actors.GroupBy(actor => actor.EntityId)
            .ToDictionary(group => group.Key, group => group.Last());
        var completed = new List<ForcedMovementObservationEvent>();
        lock (forcedMovementGate)
        {
            foreach (var pair in pendingForcedMovement.ToArray())
            {
                var pending = pair.Value;
                if (!pending.Started)
                {
                    if (elapsedMs < pending.EffectElapsedMs) continue;
                    var hasPrevious = tracks.TryGetValue(pending.TargetEntityId, out var previous) &&
                        previous.Tick == tick && previous.ElapsedMs <= pending.EffectElapsedMs &&
                        pending.EffectElapsedMs - previous.ElapsedMs <= 500;
                    if (!hasPrevious || !visible.TryGetValue(pending.TargetEntityId, out var current) ||
                        elapsedMs - pending.EffectElapsedMs > 500)
                    {
                        if (elapsedMs - pending.EffectElapsedMs <= 500 && hasPrevious) continue;
                        completed.Add(BuildForcedMovementEvent(schema, matchUid, pending, false,
                            "visibility_missing_at_effect_boundary"));
                        pendingForcedMovement.Remove(pair.Key);
                        continue;
                    }
                    pending.Samples.Add(new ForcedMovementPositionSample(previous!.ElapsedMs,
                        previous.X, previous.Y, previous.Z));
                    pending.Samples.Add(new ForcedMovementPositionSample(elapsedMs,
                        current.Position.X, current.Position.Y, current.Position.Z));
                    pending.Started = true;
                    pending.LastObservedMs = elapsedMs;
                }
                else
                {
                    if (!visible.TryGetValue(pending.TargetEntityId, out var current) ||
                        elapsedMs - pending.LastObservedMs is < 100 or > 500)
                    {
                        completed.Add(BuildForcedMovementEvent(schema, matchUid, pending, false,
                            "visibility_gap_inside_short_window"));
                        pendingForcedMovement.Remove(pair.Key);
                        continue;
                    }
                    pending.Samples.Add(new ForcedMovementPositionSample(elapsedMs,
                        current.Position.X, current.Position.Y, current.Position.Z));
                    pending.LastObservedMs = elapsedMs;
                }

                if (pending.Started && elapsedMs - pending.EffectElapsedMs >= 1_000)
                {
                    var eligible = pending.Samples.Count >= 4;
                    completed.Add(BuildForcedMovementEvent(schema, matchUid, pending, eligible,
                        eligible ? string.Empty : "insufficient_short_window_samples"));
                    pendingForcedMovement.Remove(pair.Key);
                }
            }
        }
        // Once a continuous trajectory is complete, preserve its actual span.
        // A displacement may keep moving the target well beyond the packet's
        // ±250 ms boundary; every overlapping movement interval must be
        // excluded from the speed comparison.
        lock (displacementGate)
            foreach (var observation in completed.Where(row => row.ContinuousVisible && row.Samples.Count >= 2))
                AddAttribution(observation.TargetEntityId,
                    new DisplacementAttribution(observation.Samples[0].ElapsedMs,
                        observation.ActionId, "forced_displacement_trajectory",
                        observation.Samples[^1].ElapsedMs));
        return completed;
    }

    private static ForcedMovementObservationEvent BuildForcedMovementEvent(string schema,
        string matchUid, PendingForcedMovement pending, bool continuous, string rejection) =>
        new(schema, "forced_movement_observation", matchUid, DateTime.UtcNow,
            pending.EffectElapsedMs, pending.ActionId, pending.SourceEntityId,
            pending.SourceName, pending.SourceWorldId, pending.SourceClassJobId,
            pending.TargetEntityId, pending.TargetName, pending.TargetWorldId,
            pending.TargetClassJobId, pending.Samples.ToArray(), continuous, rejection,
            "effect_type_32_target_trajectory_250ms_1s_v1");

    private void AddAttribution(uint entityId, DisplacementAttribution value)
    {
        if (entityId == 0) return;
        if (!displacement.TryGetValue(entityId, out var rows))
            displacement[entityId] = rows = new List<DisplacementAttribution>(4);
        rows.Add(value);
        rows.RemoveAll(row => value.ElapsedMs - row.ElapsedMs > 3_000);
    }

    private DisplacementAttribution[] RecentAttributions(uint entityId, long fromMs, long toMs)
    {
        lock (displacementGate)
        {
            if (!displacement.TryGetValue(entityId, out var rows)) return [];
            rows.RemoveAll(row => toMs - row.ElapsedMs > 3_000);
            return rows.Where(row => MovementIntegrityRules.ExplainsInterval(
                    row.Kind, row.ElapsedMs, fromMs, toMs, row.EndElapsedMs))
                .ToArray();
        }
    }

    public IReadOnlyList<MovementAnomalyObservationEvent> Observe(string schema, string matchUid,
        long elapsedMs, IEnumerable<IBattleChara> actors)
    {
        tick++; var result = new List<MovementAnomalyObservationEvent>();
        foreach (var actor in actors)
        {
            var entity = actor.EntityId;
            if (!tracks.TryGetValue(entity, out var track))
            {
                tracks[entity] = new Track { ElapsedMs = elapsedMs, X = actor.Position.X,
                    Y = actor.Position.Y, Z = actor.Position.Z, Tick = tick,
                    Name = actor.Name.TextValue,
                    WorldId = (ushort)((actor as IPlayerCharacter)?.HomeWorld.RowId ?? 0),
                    ClassJobId = (byte)actor.ClassJob.RowId,
                    Mounted = actor.CurrentMount is not null,
                    SpeedStatusIds = actor.StatusList.Select(x => x.StatusId).Where(IsKnownSpeedStatus)
                        .Select(x => (int)x).ToArray() };
                continue;
            }
            var dt = (elapsedMs - track.ElapsedMs) / 1000.0;
            var currentMounted = actor.CurrentMount is not null;
            var aliveContinuous = track.Tick == tick - 1 && dt is >= .10 and <= .50 &&
                                  actor.CurrentHp > 0 && actor.MaxHp > 0;
            var continuous = aliveContinuous && !track.Mounted && !currentMounted;
            var mountedContinuous = aliveContinuous && track.Mounted && currentMounted;
            var fromX = track.X; var fromY = track.Y; var fromZ = track.Z;
            var distance = Math.Sqrt(Math.Pow(actor.Position.X - fromX, 2) +
                                     Math.Pow(actor.Position.Z - fromZ, 2));
            var speed = aliveContinuous ? distance / dt : 0;
            // A status may expire between the two endpoints.  Attribute the
            // whole interval using the union of start/end states so expiry on
            // the current frame cannot turn legitimate movement into an alert.
            var currentSpeedStatuses = actor.StatusList.Select(x => x.StatusId).Where(IsKnownSpeedStatus)
                .Select(x => (int)x).ToArray();
            var speedStatuses = track.SpeedStatusIds.Concat(currentSpeedStatuses).Distinct().ToArray();
            var attributions = aliveContinuous
                ? RecentAttributions(entity, track.ElapsedMs, elapsedMs) : [];
            var activeMovement = aliveContinuous &&
                MovementIntegrityRules.IsActiveMovement(distance, dt);
            var normalizedSpeed = continuous
                ? speed / MovementMultiplier(speedStatuses, (byte)actor.ClassJob.RowId)
                : 0;
            if (continuous && activeMovement)
            {
                var bin = Array.FindIndex(DistributionUpperBounds, upper => speed <= upper);
                bin = bin < 0 ? track.Distribution.Length - 1 : bin;
                if (attributions.Length > 0) track.DisplacementDistribution[bin]++;
                else
                {
                    track.ContinuousIntervals++;
                    track.Distribution[bin]++;
                    if (speedStatuses.Length > 0) track.SpeedStatusDistribution[bin]++;
                    else track.UnexplainedDistribution[bin]++;

                    // Keep a second view in base-speed-equivalent units.  The
                    // raw histogram intentionally retains Sprint/Expedience
                    // peaks; this normalized histogram divides only by the
                    // version-reviewed visible status multiplier. Unknown
                    // statuses stay unadjusted instead of being guessed.
                    // 原始分布保留合法增速形成的峰；归一化分布只抵扣版本目录中已审核且
                    // 在区间端点可见的增速状态，未知状态绝不补猜。
                    var normalizedBin = Array.FindIndex(DistributionUpperBounds,
                        upper => normalizedSpeed <= upper);
                    normalizedBin = normalizedBin < 0
                        ? track.BuffNormalizedDistribution.Length - 1 : normalizedBin;
                    track.BuffNormalizedDistribution[normalizedBin]++;
                    track.BuffNormalizedIntervals++;
                }
            }
            else if (mountedContinuous && activeMovement && attributions.Length == 0)
            {
                // Mount transitions are excluded because the interval mixes
                // two movement regimes. Only two mounted endpoints enter the
                // descriptive mount histogram.
                var mountedNormalizedSpeed = speed / MovementMultiplier(speedStatuses, 0);
                var bin = Array.FindIndex(DistributionUpperBounds,
                    upper => mountedNormalizedSpeed <= upper);
                bin = bin < 0 ? track.MountedDistribution.Length - 1 : bin;
                track.MountedDistribution[bin]++;
                track.MountedIntervals++;
            }
            // Candidate publication uses the status-normalized speed and the
            // unmodified base envelope. Visible positive statuses and the
            // reviewed passive job modifier have already been divided out.
            // 候选使用抵扣状态后的等效速度；已审核 Buff 与职业常驻增速不再重复放宽包络。
            var appliedEnvelope = MovementEnvelope([], 0, dt);
            var reviewEnvelope = SevereReviewEnvelope(appliedEnvelope);
            if (catalogCompatible && continuous && activeMovement &&
                normalizedSpeed > reviewEnvelope && attributions.Length == 0)
            {
                track.Streak.Add(new MovementSpeedSample(elapsedMs, Math.Round(normalizedSpeed, 2),
                    Math.Round(distance, 2), Math.Round(dt, 3),
                    speedStatuses, fromX, fromY, fromZ, actor.Position.X, actor.Position.Y,
                    actor.Position.Z, [], speedStatuses.Length > 0
                        ? "speed_status_present_and_far_tail_exceeded"
                        : "unexplained_continuous_far_tail_movement", appliedEnvelope));
                if (track.Streak.Count == RequiredIntervals && elapsedMs - track.LastPublishedMs >= 10_000)
                {
                    result.Add(new MovementAnomalyObservationEvent(schema, "movement_anomaly_observation",
                        matchUid, DateTime.UtcNow, elapsedMs, entity, actor.Name.TextValue,
                        (ushort)((actor as IPlayerCharacter)?.HomeWorld.RowId ?? 0),
                        (byte)actor.ClassJob.RowId, HardEnvelope,
                        track.Streak.ToArray(),
                        $"continuous_loaded_active_unmounted_100_500ms_far_tail_envelope_v8:{MovementCatalogVersion}:{DisplacementCatalogVersion}:{gameVersion}"));
                    track.LastPublishedMs = elapsedMs;
                }
                if (track.Streak.Count > RequiredIntervals + 2) track.Streak.RemoveAt(0);
            }
            else track.Streak.Clear();
            track.ElapsedMs = elapsedMs; track.X = actor.Position.X; track.Y = actor.Position.Y;
            track.Z = actor.Position.Z; track.Tick = tick; track.Name = actor.Name.TextValue;
            track.WorldId = (ushort)((actor as IPlayerCharacter)?.HomeWorld.RowId ?? 0);
            track.ClassJobId = (byte)actor.ClassJob.RowId;
            track.Mounted = currentMounted;
            track.SpeedStatusIds = currentSpeedStatuses;
        }
        // Missing from one ObjectTable sample breaks continuity.  Keeping the
        // last coordinate is safe because Tick prevents it being joined later.
        foreach (var track in tracks.Values.Where(x => x.Tick < tick - 1)) track.Streak.Clear();
        return result;
    }

    public void Reset()
    {
        tracks.Clear(); tick = 0;
        lock (displacementGate) displacement.Clear();
        lock (forcedMovementGate) pendingForcedMovement.Clear();
    }
    public IReadOnlyList<MovementSpeedDistributionEvent> ExportProfiles(string schema,
        string matchUid, long elapsedMs)
    {
        if (!catalogCompatible) return [];
        var scope = $"continuous_loaded_active_100_500ms_histogram_v6:{MovementCatalogVersion}:{DisplacementCatalogVersion}:{gameVersion}";
        return tracks
            .Where(x => x.Value.ContinuousIntervals > 0 && !string.IsNullOrWhiteSpace(x.Value.Name))
            .Select(x => new MovementSpeedDistributionEvent(schema, "movement_speed_distribution",
                matchUid, DateTime.UtcNow, elapsedMs, x.Key, x.Value.Name, x.Value.WorldId,
                x.Value.ClassJobId, HardEnvelope, DistributionUpperBounds,
                x.Value.Distribution, x.Value.SpeedStatusDistribution,
                x.Value.DisplacementDistribution, x.Value.UnexplainedDistribution,
                x.Value.MountedDistribution, x.Value.BuffNormalizedDistribution,
                x.Value.ContinuousIntervals, x.Value.MountedIntervals,
                x.Value.BuffNormalizedIntervals, scope))
            .ToArray();
    }
    private static bool IsKnownSpeedStatus(uint id) =>
        MovementIntegrityRules.IsKnownPositiveSpeedStatus(id);

    // Add every simultaneously visible increment from the structured,
    // version-reviewed catalog, then retain a fixed sampling margin and a
    // dt-aware coordinate-jitter allowance. Closed Position alone is not a
    // speed state. The catalog contains the full audited current PvP set, not
    // merely Sprint/Huton/Expedience/Swift.
    // 只累加结构化、逐版本审核目录中同时可见的正向移速状态；闭式舞姿本身
    // 不是移速状态。目录覆盖当前已审核 PvP 全集，而非只列少数常见技能。
    private static double MovementEnvelope(IReadOnlyCollection<int> statuses, byte classJobId, double dt)
    {
        // Do not impose an unverified global cap. Summing every simultaneously
        // visible, version-reviewed bonus is conservative for integrity review:
        // a wider legal envelope may miss a lead, but will not accuse a legal
        // multi-buff combination merely because the catalog cap was guessed.
        var multiplier = MovementMultiplier(statuses, classJobId);
        var jitterPerSecond = PositionJitterAllowance / Math.Max(dt, .10);
        return Math.Round(BaseRunSpeed * multiplier + FixedSamplingSlack + jitterPerSecond, 2);
    }

    private static double MovementMultiplier(IReadOnlyCollection<int> statuses, byte classJobId) =>
        MovementIntegrityRules.MovementMultiplier(statuses, classJobId);

    private static double SevereReviewEnvelope(double appliedEnvelope) =>
        Math.Round(appliedEnvelope + Math.Max(SevereExcessFloor,
            appliedEnvelope * SevereExcessRatio), 2);

}
