using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BattlefieldFrontlineCollector;

internal sealed record LocalPlayerAnalysis(
    string Name, ushort WorldId, byte ClassJobId, byte Team, byte Alliance,
    byte Kills, byte Deaths, byte Assists, uint DamageDealt, uint DamageTaken,
    uint HpRestored, uint Occupations, double LocalImpact);

internal sealed record RemoteScore(double? Overall, double? Damage, double? Healing,
    double? Kills, double? Assists, double? Survival, double? Objective);

internal sealed record BehaviorTendency(string Key, string LabelZh, double Score,
    string Confidence, string EvidenceScope);

internal sealed record ScorePoint(long ElapsedMs, short Maelstrom, short Adders, short Flames,
    byte? PlayerBattleHigh);
internal sealed record FactionMovement(byte Team, int Count, double X, double Y, double Z,
    double AverageHpPercent, double AverageMpPercent, long ObservedHpLoss, long ObservedHpGain);
internal sealed record ObservedActor(uint EntityId, string Name, ushort WorldId, byte ClassJobId,
    byte Team, byte Alliance, double X, double Y, double Z, uint CurrentHp, uint MaxHp,
    uint CurrentMp, uint MaxMp, long ObservedHpLoss = 0, long ObservedHpGain = 0,
    bool FellToZero = false, IReadOnlyList<StatusState>? Statuses = null, byte ShieldPercent = 0);
internal sealed record MovementPoint(long ElapsedMs, int VisibleActors,
    IReadOnlyList<FactionMovement> Factions, IReadOnlyList<ObservedActor>? Actors = null);
internal sealed record ActionSliceEvent(long ElapsedMs, uint ActionId, string ActionName,
    uint SourceEntityId, string SourceName, ushort SourceWorldId, byte SourceJobId, byte SourceTeam,
    uint TargetEntityId, string TargetName, ushort TargetWorldId, byte TargetJobId, byte TargetTeam,
    long Damage, long Healing, string EffectKind, uint TargetCurrentHp = 0,
    uint TargetMaxHp = 0, byte TargetShieldPercent = 0,
    byte SourceAlliance = 255, byte TargetAlliance = 255, byte ActionType = 1,
    uint SourceOwnerEntityId = 0, string SourceOwnerName = "", ushort SourceOwnerWorldId = 0,
    byte SourceOwnerJobId = 0, uint TargetOwnerEntityId = 0, string TargetOwnerName = "",
    ushort TargetOwnerWorldId = 0, byte TargetOwnerJobId = 0,
    double? SourceX = null, double? SourceY = null, double? SourceZ = null,
    double? TargetX = null, double? TargetY = null, double? TargetZ = null,
    double? NominalRange = null, double? EffectRange = null);
internal sealed record ObjectiveStateEvent(long ElapsedMs, uint ObjectId, string Label,
    string State, byte? OwnerTeam, double X, double Y, double Z, string EvidenceScope,
    uint CurrentHp = 0, uint MaxHp = 0,
    uint ActorEntityId = 0, string ActorName = "", ushort ActorWorldId = 0,
    byte ActorJobId = 0, long? InteractionDurationMs = null,
    string InteractionResult = "", uint InterrupterEntityId = 0,
    string InterrupterName = "", ushort InterrupterWorldId = 0,
    byte InterrupterJobId = 0, int? RemainingSeconds = null,
    double? ProgressPercent = null, string DisplayText = "");
internal sealed record ObjectiveObjectObservation(long ElapsedMs, uint DutyId, uint TerritoryId,
    uint ObjectId, uint BaseId, uint DataId, string ObjectKind, byte SubKind, string Name,
    double X, double Y, double Z, bool Loaded, bool IsTargetable, uint CurrentHp,
    uint MaxHp, byte Battalion, string Change, string EvidenceScope,
    // Global map markers from older server details used JSON null here because
    // they have no realtime HP contract. Keep this nullable so one marker does
    // not make the whole replay detail fail to deserialize.
    bool? RealtimeHpEligible = null);
internal sealed record TargetMarkerTimelineEvent(long ElapsedMs, int MarkerIndex, string MarkerKey,
    uint TargetEntityId, string TargetName, ushort TargetWorldId, byte TargetJobId,
    byte TargetTeam, byte TargetAlliance, string Change, string OperatorEvidence,
    string TargetObjectKind = "", uint TargetBaseId = 0, uint TargetDataId = 0,
    byte TargetSubKind = 0, uint TargetOwnerId = 0,
    double? TargetX = null, double? TargetY = null, double? TargetZ = null,
    string TargetNameEvidence = "",
    uint OperatorEntityId = 0, string OperatorName = "", ushort OperatorWorldId = 0,
    byte OperatorJobId = 0, byte OperatorTeam = 255, byte OperatorAlliance = 255);
internal sealed record FieldMarkerTimelineEvent(long ElapsedMs, int MarkerIndex, string MarkerKey,
    double X, double Y, double Z, string Change, uint NearbyActorEntityId,
    string NearbyActorName, ushort NearbyActorWorldId, byte NearbyActorJobId,
    byte NearbyActorTeam, byte NearbyActorAlliance, double? NearbyActorDistance,
    string PlacementRelation, string OperatorEvidence,
    uint OperatorEntityId = 0, string OperatorName = "", ushort OperatorWorldId = 0,
    byte OperatorJobId = 0, byte OperatorTeam = 255, byte OperatorAlliance = 255);
internal sealed record StatusState(long StatusId, string StatusName, uint SourceEntityId,
    double RemainingSeconds);
internal sealed record DeathMarker(long ElapsedMs, uint TargetEntityId);
internal sealed record DetailedStatusChange(long ElapsedMs, uint TargetEntityId, uint SourceEntityId,
    long StatusId, double DurationSeconds, string Change);
internal sealed record BehaviorGroupMember(string Name, ushort WorldId, byte ClassJobId,
    int Kills = 0, int Deaths = 0, int Assists = 0, long Damage = 0);
internal sealed record BehaviorGroupCandidate(byte Team, byte Alliance, double ModelScore,
    double Cohesion, double BurstSync, int Kills, int Deaths, int Assists, long Damage,
    IReadOnlyList<BehaviorGroupMember> Members, double AdvanceSupport = 0,
    int AdvanceSupportLinks = 0, string CandidateKind = "four_core");
internal sealed record DetailCoverage(int VitalsFrames, int ScoreChanges, string PositionScope,
    string HealthFlowScope);
internal sealed record AnomalySample(long ElapsedMs, double? Speed = null, double? Distance = null,
    double? Dt = null, double? NominalRange = null, double? Tolerance = null,
    long? LatencyMs = null, long? TriggerElapsedMs = null,
    IReadOnlyList<int>? SpeedStatusIds = null,
    double? FromX = null, double? FromY = null, double? FromZ = null,
    double? ToX = null, double? ToY = null, double? ToZ = null,
    int? WeaveCount = null, long? MinimumIntervalMs = null,
    long? WindowDurationMs = null, IReadOnlyList<uint>? ActionIds = null,
    int? LockViolationCount = null, long? MaximumLockDeficitMs = null,
    IReadOnlyList<long>? AnimationLockMs = null);
internal sealed record AnomalyCandidate(string Kind, string PlayerName, ushort WorldId,
    byte ClassJobId, int Occurrences, double RiskScore, string Confidence,
    string EvidenceScope, string Summary, IReadOnlyList<AnomalySample>? Samples = null,
    uint ActionId = 0);
internal sealed record AnomalyDistribution(string Kind, string PlayerName, ushort WorldId,
    byte ClassJobId, string Unit, IReadOnlyList<double> UpperBounds,
    IReadOnlyList<int> Counts, IReadOnlyList<int> HighlightCounts, int Observations,
    double? ReferenceValue = null, double? Tolerance = null, uint ActionId = 0,
    string EvidenceScope = "", IReadOnlyList<int>? SpeedStatusCounts = null,
    IReadOnlyList<int>? DisplacementCounts = null, IReadOnlyList<int>? UnexplainedCounts = null);
internal sealed record ForcedMovementPositionSampleDetail(long ElapsedMs, double X, double Y, double Z);
internal sealed record ForcedMovementObservation(long ElapsedMs, uint ActionId,
    uint SourceEntityId, string SourceName, ushort SourceWorldId, byte SourceClassJobId,
    uint TargetEntityId, string TargetName, ushort TargetWorldId, byte TargetClassJobId,
    IReadOnlyList<ForcedMovementPositionSampleDetail> Samples, bool ContinuousVisible,
    string RejectionReason, string EvidenceScope);
internal sealed record IntegrityCohortSeries(string Kind, uint ActionId, byte ClassJobId,
    string Unit, IReadOnlyList<double> UpperBounds, IReadOnlyList<int> Counts,
    IReadOnlyList<int> HighlightCounts, IReadOnlyList<int> SpeedStatusCounts,
    IReadOnlyList<int> DisplacementCounts, IReadOnlyList<int> UnexplainedCounts,
    int Observations, int MatchCount, int PlayerMatchCount, double? MeanBehaviorScore,
    double HighlightShare, string EvidenceStatus, double? ReferenceValue = null,
    double? Tolerance = null, string Model = "", string EvidenceScope = "",
    string CountWeighting = "", string DisplayPolicyVersion = "",
    IReadOnlyList<double>? DisplayUpperBounds = null,
    IReadOnlyList<int>? DisplayCounts = null, bool DisplayOpenTail = true);
internal sealed record IntegrityRealmCoverage(int Observations, int MatchCount, int PlayerMatchCount);
internal sealed record IntegrityCohortSnapshot(string Key, bool Loading,
    IntegrityCohortSeries? Series = null,
    IReadOnlyDictionary<string, IntegrityRealmCoverage>? RealmCoverage = null,
    string? Error = null);
internal sealed record IntegrityReferenceSnapshotState(bool Available, bool Loading,
    DateTimeOffset? GeneratedAtUtc = null, string? ModelVersion = null,
    int? RemainingDownloadsToday = null, int DailyDownloadLimit = 2,
    int SeriesCount = 0, string? Error = null);
internal sealed record AnomalyCoverage(int MovementIntervals, int CaptureGapRejected,
    int RangeUsesWithExactPositions, int RangeUsesMissingPositions, int ReactionOpportunities,
    int GcdWindows = 0, int TripleWeaveWindows = 0,
    int MovementSignalsRejectedLegacy = 0, int MovementCalibrationSuppressed = 0,
    int MovementEligibleIdentities = 0, int EffectOrderTripleWeaveWindows = 0,
    int LocalRequestVerifiedWindows = 0, int LbEffectOrderWindows = 0,
    int LocalRequestVerifiedLbWindows = 0,
    int RecastMetadataUses = 0, int RecastOpportunities = 0,
    int RecastRejectedMissingMetadata = 0, int RecastAmbiguousChargeWindows = 0,
    int StrictRecastOpportunities = 0, int StrictRecastEarlyEffects = 0,
    int StrictRecastRejectedLegacyMetadata = 0, int StrictRecastRejectedMultiCharge = 0,
    int CastEffectUses = 0, int CastAdvertisedEffectUses = 0,
    int CastStartEventsCaptured = 0,
    int CastEligibleStarts = 0, int CastRejectedLateObservation = 0,
    int CastTimingNotIdentifiable = 0,
    int ForcedControlOpportunities = 0, int ForcedControlMovementPairs = 0,
    int ForcedControlRejectedVisibility = 0,
    int ForcedControlShortWindowCaptured = 0,
    int ForcedControlShortWindowRejected = 0);
internal sealed record DetailedAnalysis(string Schema, IReadOnlyList<ScorePoint> ScoreTimeline,
    IReadOnlyList<MovementPoint> MovementTimeline, DetailCoverage Coverage, string EvidenceNotice,
    IReadOnlyList<ActionSliceEvent>? Actions = null,
    IReadOnlyList<ObjectiveStateEvent>? ObjectiveTimeline = null,
    IReadOnlyDictionary<string, IReadOnlyList<StatusState>>? StatusSnapshots = null,
    IReadOnlyList<DeathMarker>? DeathTimeline = null,
    IReadOnlyList<DetailedStatusChange>? StatusTimeline = null,
    IReadOnlyList<string>? CaptureFeatures = null,
    IReadOnlyList<BehaviorGroupCandidate>? BehaviorGroups = null,
    string? BehaviorModel = null, double FourSmallDisplayThreshold = 60,
    IReadOnlyList<AnomalyCandidate>? AnomalyCandidates = null,
    AnomalyCoverage? AnomalyCoverage = null, string? AnomalyModel = null,
    IReadOnlyList<TargetMarkerTimelineEvent>? TargetMarkers = null,
    IReadOnlyList<FieldMarkerTimelineEvent>? FieldMarkers = null,
    IReadOnlyList<AnomalyDistribution>? AnomalyDistributions = null,
    IReadOnlyList<ForcedMovementObservation>? ForcedMovementObservations = null,
    IReadOnlyList<ObjectiveObjectObservation>? ObjectiveDiscovery = null);

// This state is deliberately about capture availability, not player behaviour.
// It prevents a zero count in an old or incomplete capture from being rendered
// as evidence that no forced movement occurred.
internal static class EvidenceAvailability
{
    internal const string ForcedMovementFeature = "forced_movement_target_trajectory_250ms";

    internal static string ForcedMovementState(DetailedAnalysis detail, AnomalyCoverage coverage)
    {
        if (detail.CaptureFeatures?.Contains(ForcedMovementFeature) != true)
            return "legacy_capture";
        if (coverage.ForcedControlOpportunities == 0)
            return "active_no_opportunity";
        if (coverage.ForcedControlShortWindowCaptured > 0)
            return "active_observed";
        if (coverage.ForcedControlShortWindowRejected > 0)
            return "active_all_rejected";
        return "declared_missing_events";
    }
}

internal sealed record LocalMatchAnalysis(
    string Schema, string MatchUid, DateTime StartedAtUtc, DateTime EndedAtUtc,
    uint DutyId, uint TerritoryId, ushort MatchLengthSeconds, string Realm, string LocalPlayerName,
    string CapturePath, string UploadState, string? UploadError, string? RemoteAnalysisUrl,
    double? MatchLogs, IReadOnlyList<TeamResult> Teams,
    IReadOnlyList<LocalPlayerAnalysis> Players,
    IReadOnlyDictionary<string, RemoteScore>? RemoteScores,
    IReadOnlyDictionary<string, IReadOnlyList<BehaviorTendency>>? BehaviorTable,
    DetailedAnalysis? Detail, string? DetailError,
    string EvidenceNotice, string? RemoteMatchUid = null);

internal static class LocalMatchAnalyzer
{
    public const string Schema = "battlefield-frontline-analysis/v1";
    public const string CrystallineSchema = "battlefield-crystalline-analysis/v1";

    public static LocalMatchAnalysis Build(ResultEvent result, DateTime startedAtUtc,
        string realm, string localPlayerName, string capturePath)
    {
        var players = result.Players.Select(player => new LocalPlayerAnalysis(
            player.Name, player.WorldId, player.ClassJobId, player.Team, player.Alliance,
            player.Kills, player.Deaths, player.Assists, player.DamageDealt, player.DamageTaken,
            player.HpRestored, player.Occupations, LocalImpact(player))).ToArray();
        return new LocalMatchAnalysis(Schema, result.MatchUid, startedAtUtc, result.ObservedAtUtc,
            result.DutyId, result.TerritoryId, result.MatchLengthSeconds, realm, localPlayerName,
            capturePath, "local_only", null, null, null, result.Teams, players, null, null, null, null,
            "本地复盘只显示本局直接记录；Logs 百分位与跨局行为倾向需要上传后由服务器计算。"
        );
    }

    public static LocalMatchAnalysis BuildCrystalline(ResultEvent result, DateTime startedAtUtc,
        string realm, string localPlayerName, string capturePath)
    {
        var players = result.Players.Select(player => new LocalPlayerAnalysis(
            player.Name, player.WorldId, player.ClassJobId, player.Team, 0,
            player.Kills, player.Deaths, player.Assists, player.DamageDealt,
            player.DamageTaken, player.HpRestored, player.Occupations,
            LocalImpact(player))).ToArray();
        return new LocalMatchAnalysis(CrystallineSchema, result.MatchUid, startedAtUtc,
            result.ObservedAtUtc, result.DutyId, result.TerritoryId,
            result.MatchLengthSeconds, realm, localPlayerName, capturePath,
            "local_only", null, null, null, result.Teams, players, null, null,
            null, null,
            "本地水晶冲突复盘使用结算包与本局直接事件；跨局 Logs 需要上传后按水晶冲突独立总体计算，行为倾向在样本不足时保持空白。"
        );
    }

    private static double LocalImpact(PlayerResult p)
    {
        // A descriptive within-match index, never presented as a global Logs percentile.
        // 仅用于本局浏览，不得作为全库 Logs 百分位。
        var value = Math.Log10(1 + p.DamageDealt) * 9
                    + Math.Log10(1 + p.HpRestored) * 6
                    + p.Kills * 3 + p.Assists - p.Deaths * 2 + p.Occupations * 2;
        return Math.Round(Math.Clamp(value, 0, 100), 1);
    }
}

internal sealed class MatchRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string directory;
    private readonly string assetRoot;
    private readonly object gate = new();
    private readonly List<LocalMatchAnalysis> matches = new();
    private IReadOnlyList<LocalMatchAnalysis> snapshot = Array.Empty<LocalMatchAnalysis>();

    public MatchRepository(string directory, string assetRoot)
    {
        this.directory = directory;
        this.assetRoot = assetRoot;
        Directory.CreateDirectory(directory);
        Reload();
    }

    public IReadOnlyList<LocalMatchAnalysis> Snapshot()
    {
        lock (gate) return snapshot;
    }

    public (int Imported, int Skipped, int Failed) ImportCaptureFolder(string importDirectory,
        string fallbackRealm)
    {
        Directory.CreateDirectory(importDirectory);
        var imported = 0; var skipped = 0; var failed = 0;
        var capturePaths = Directory.EnumerateFiles(importDirectory, "*.jsonl",
                SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(importDirectory, "*.bfcap",
                SearchOption.TopDirectoryOnly));
        foreach (var path in capturePaths)
        {
            try
            {
                ResultEvent? result = null;
                var startedAt = DateTime.MinValue;
                var localPlayerName = string.Empty;
                var realm = fallbackRealm;
                foreach (var line in BfcapCodec.ReadJsonRows(path, assetRoot))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("event", out var eventProperty)) continue;
                    var eventName = eventProperty.GetString();
                    if (eventName == "match_start")
                    {
                        if (root.TryGetProperty("observedAtUtc", out var observed) &&
                            observed.TryGetDateTime(out var value)) startedAt = value;
                        if (root.TryGetProperty("localPlayerName", out var player))
                            localPlayerName = player.GetString() ?? string.Empty;
                        if (root.TryGetProperty("realm", out var region) &&
                            !string.IsNullOrWhiteSpace(region.GetString()))
                            realm = region.GetString()!.ToUpperInvariant();
                    }
                    else if (eventName == "result")
                        result = JsonSerializer.Deserialize<ResultEvent>(line, JsonOptions);
                }
                if (result == null || result.Players.Count is 0 or > 72 || result.Teams.Count is not (2 or 3))
                    throw new InvalidDataException("Capture has no valid PvP result packet.");
                if (Snapshot().Any(x => x.MatchUid == result.MatchUid ||
                    string.Equals(x.CapturePath, path, StringComparison.OrdinalIgnoreCase)))
                { skipped++; continue; }
                if (startedAt == DateTime.MinValue)
                    startedAt = result.ObservedAtUtc.AddSeconds(-result.MatchLengthSeconds);
                Save(result.Teams.Count == 2
                    ? LocalMatchAnalyzer.BuildCrystalline(result, startedAt, realm, localPlayerName, path)
                    : LocalMatchAnalyzer.Build(result, startedAt, realm, localPlayerName, path));
                imported++;
            }
            catch { failed++; }
        }
        return (imported, skipped, failed);
    }

    public void Save(LocalMatchAnalysis analysis)
    {
        Persist(analysis);
        lock (gate)
        {
            matches.RemoveAll(x => x.MatchUid == analysis.MatchUid);
            matches.Add(analysis);
            RefreshSnapshot();
        }
    }

    public void MergeRemote(string matchUid, CollectorUploadResponse response)
    {
        lock (gate)
        {
            var index = matches.FindIndex(x => x.MatchUid == matchUid);
            if (index < 0) return;
            var current = matches[index];
            var merged = current with
            {
                UploadState = "complete",
                UploadError = null,
                RemoteAnalysisUrl = response.AnalysisUrl,
                MatchLogs = response.MatchLogs,
                RemoteMatchUid = response.MatchUid,
                RemoteScores = response.PlayerScores,
                BehaviorTable = response.BehaviorTable,
                EvidenceNotice = response.EvidenceNotice ?? current.EvidenceNotice
            };
            matches[index] = merged;
            RefreshSnapshot();
            Persist(merged);
        }
    }

    public void SetUploadState(string matchUid, string state, string? error = null)
    {
        lock (gate)
        {
            var index = matches.FindIndex(x => x.MatchUid == matchUid);
            if (index < 0) return;
            var updated = matches[index] with { UploadState = state, UploadError = error };
            matches[index] = updated;
            RefreshSnapshot();
            Persist(updated);
        }
    }

    public void MergeDetail(string matchUid, DetailedAnalysis detail, byte[]? compactPayload = null)
    {
        lock (gate)
        {
            var index = matches.FindIndex(x => x.MatchUid == matchUid);
            if (index < 0) return;
            var updated = matches[index] with { Detail = detail, DetailError = null };
            matches[index] = updated;
            RefreshSnapshot();
            if (compactPayload != null)
            {
                var target = DetailPath(updated); var temporary = target + ".partial";
                File.WriteAllBytes(temporary, compactPayload); File.Move(temporary, target, true);
                // The in-memory object stays expanded for instant UI access;
                // the durable summary points to the compact sidecar implicitly.
                Persist(updated);
            }
            else Persist(updated);
        }
    }

    public void SetDetailError(string matchUid, string? error)
    {
        lock (gate)
        {
            var index = matches.FindIndex(x => x.MatchUid == matchUid);
            if (index < 0) return;
            var updated = matches[index] with { DetailError = error };
            matches[index] = updated;
            RefreshSnapshot();
            Persist(updated);
        }
    }

    private string RecordDirectory(LocalMatchAnalysis value)
    {
        // Keep legacy root files readable in place; all new summaries are partitioned by mode.
        var legacy = Path.Combine(directory, $"{value.MatchUid}.analysis.json");
        if (File.Exists(legacy)) return directory;
        var mode = value.Schema == LocalMatchAnalyzer.CrystallineSchema ? "crystalline-conflict" : "frontline";
        var target = Path.Combine(directory, mode);
        Directory.CreateDirectory(target);
        return target;
    }
    private string AnalysisPath(LocalMatchAnalysis value) =>
        Path.Combine(RecordDirectory(value), $"{value.MatchUid}.analysis.json");
    private string DetailPath(LocalMatchAnalysis value) =>
        Path.Combine(RecordDirectory(value), $"{value.MatchUid}.detail.bfdetail");

    private void Persist(LocalMatchAnalysis value)
    {
        var durable = File.Exists(DetailPath(value)) ? value with { Detail = null } : value;
        File.WriteAllText(AnalysisPath(value),JsonSerializer.Serialize(durable,JsonOptions));
    }

    private void Reload()
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.analysis.json", SearchOption.AllDirectories))
        {
            try
            {
                var value = JsonSerializer.Deserialize<LocalMatchAnalysis>(File.ReadAllText(path), JsonOptions);
                if (value != null)
                {
                    var compactPath = Path.Combine(Path.GetDirectoryName(path)!, $"{value.MatchUid}.detail.bfdetail");
                    if (value.Detail == null && File.Exists(compactPath))
                    {
                        using var compact = JsonDocument.Parse(BfdetailCodec.DecodeToJson(
                            File.ReadAllBytes(compactPath),assetRoot));
                        if (compact.RootElement.TryGetProperty("detail",out var detailElement))
                        {
                            var detail = detailElement.Deserialize<DetailedAnalysis>(JsonOptions);
                            if (detail != null) value = value with { Detail = detail };
                        }
                    }
                    matches.Add(value);
                }
            }
            catch { /* A damaged summary remains on disk for manual recovery. */ }
        }
        RefreshSnapshot();
    }

    private void RefreshSnapshot() => snapshot = matches.OrderByDescending(x => x.EndedAtUtc).ToArray();
}
