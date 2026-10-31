using System;
using System.Collections.Generic;

namespace BattlefieldFrontlineCollector;

internal sealed record ScoreEvent(string Schema, string Event, string MatchUid, DateTime ObservedAtUtc,
    long ElapsedMs, short Maelstrom, short Adders, short Flames, byte PlayerBattleHigh,
    uint DutyId, uint TerritoryId, string LayoutVersion);

internal sealed record ActorStatus(uint StatusId, uint SourceId, float RemainingTime);
internal sealed record ActorVitals(uint EntityId, string Name, ushort WorldId, uint ClassJobId,
    float X, float Y, float Z, uint CurrentHp, uint MaxHp, uint CurrentMp, uint MaxMp,
    IReadOnlyList<ActorStatus>? Statuses = null, byte ShieldPercent = 0);

internal sealed record VitalsEvent(string Schema, string Event, string MatchUid, DateTime ObservedAtUtc,
    long ElapsedMs, uint DutyId, uint TerritoryId, IReadOnlyList<ActorVitals> Actors);

/// <summary>
/// Discovery evidence for map-specific objective adapters.  A row only says
/// that a non-player candidate was loaded for this observer; it is not yet a
/// confirmed objective or an ownership transition.
/// 地图机制目录的发现证据：只表示观察者加载到了非玩家候选，不等于已经确认据点或归属变化。
/// </summary>
internal sealed record ObjectiveObjectObservationEvent(string Schema, string Event,
    string MatchUid, DateTime ObservedAtUtc, long ElapsedMs, uint DutyId,
    uint TerritoryId, uint ObjectId, uint BaseId, uint DataId, string ObjectKind,
    byte SubKind, string Name, float X, float Y, float Z, bool Loaded,
    bool IsTargetable, uint CurrentHp, uint MaxHp, byte Battalion,
    string Change, string EvidenceScope, bool RealtimeHpEligible = false);

internal sealed record ObjectiveMapMarkerObservationEvent(string Schema, string Event,
    string MatchUid, DateTime ObservedAtUtc, long ElapsedMs, uint DutyId,
    uint TerritoryId, uint MarkerId, uint IconId, ushort DataId, byte MarkerType,
    sbyte EventState, string Label, float X, float Y, float Z, float Radius,
    bool Active, string Change, string EvidenceScope, int EndTimestamp = 0,
    int? RemainingSeconds = null, string DisplayText = "", double? ProgressPercent = null);

internal sealed record IndexedUiValue(int Index, int Value);
internal sealed record ObjectiveUiNumberArrayObservationEvent(string Schema, string Event,
    string MatchUid, DateTime ObservedAtUtc, long ElapsedMs, uint DutyId,
    uint TerritoryId, string ArrayName, int ArrayIndex, int ArraySize,
    IReadOnlyList<IndexedUiValue> Changes, string EvidenceScope);

internal sealed record ObjectiveStateCaptureEvent(string Schema, string Event,
    string MatchUid, DateTime ObservedAtUtc, long ElapsedMs, uint ObjectId,
    string Label, string State, byte? OwnerTeam, double X, double Y, double Z,
    uint CurrentHp, uint MaxHp, string EvidenceScope,
    uint ActorEntityId = 0, string ActorName = "", ushort ActorWorldId = 0,
    byte ActorJobId = 0, long? InteractionDurationMs = null,
    string InteractionResult = "", uint InterrupterEntityId = 0,
    string InterrupterName = "", ushort InterrupterWorldId = 0,
    byte InterrupterJobId = 0, int? RemainingSeconds = null,
    double? ProgressPercent = null, string DisplayText = "");

internal sealed record RawActionEffect(uint TargetEntityId, byte Slot, byte Type, uint Amount,
    byte Param0, byte Param1, byte Param2, byte Param3, byte Param4,
    uint TargetCurrentHp, uint TargetMaxHp, byte TargetShieldPercent,
    float? TargetX = null, float? TargetY = null, float? TargetZ = null,
    byte TargetIndex = 0);
internal sealed record ActionActorIdentity(uint EntityId, string Name, ushort WorldId, uint ClassJobId,
    uint OwnerEntityId, string OwnerName, ushort OwnerWorldId, uint OwnerClassJobId,
    byte Battalion = 255, byte OwnerBattalion = 255);
internal readonly record struct ActionCaptureMetadata(short? NominalRange, byte? EffectRange,
    byte ActionCategoryId, ushort Cast100Ms, ushort Recast100Ms, byte CooldownGroup,
    byte MaxCharges, bool AffectsPosition);
internal sealed record ActionEffectEvent(string Schema, string Event, string MatchUid, DateTime ObservedAtUtc,
    long ElapsedMs, uint ActionId, byte ActionType, uint SourceEntityId,
    IReadOnlyList<RawActionEffect> Effects, IReadOnlyList<ActionActorIdentity>? Actors = null,
    float? SourceX = null, float? SourceY = null, float? SourceZ = null,
    short? NominalRange = null, byte? EffectRange = null, byte ActionCategoryId = 0,
    ushort Cast100Ms = 0, ushort Recast100Ms = 0, byte CooldownGroup = 0,
    byte MaxCharges = 0, bool AffectsPosition = false,
    string ActionMetadataVersion = "",
    uint GlobalSequence = 0, ushort SourceSequence = 0, float AnimationLockSeconds = 0);
internal sealed record CastStartObservationEvent(string Schema, string Event, string MatchUid,
    DateTime ObservedAtUtc, long ElapsedMs, uint SourceEntityId, string SourceName,
    ushort SourceWorldId, uint SourceClassJobId, uint ActionId, uint TargetEntityId,
    float ObservedCurrentCastSeconds, float ObservedTotalCastSeconds,
    ushort AdvertisedCast100Ms, int SamplingIntervalMs, bool EligibleOnset);
/// <summary>
/// Read-only observation of a local UseAction call. The detour never changes
/// arguments, ActionManager state, or the return value. Before/after queue
/// state is retained so ordinary action queuing cannot be mislabeled as an
/// animation-lock bypass.
/// </summary>
internal sealed record LocalActionRequestEvent(string Schema, string Event, string MatchUid,
    DateTime ObservedAtUtc, long ElapsedMs, long RequestOrdinal,
    uint SourceEntityId, string SourceName, ushort SourceWorldId, byte SourceClassJobId,
    uint ActionType, uint ActionId, ulong TargetId, uint UseMode, bool Accepted,
    float AnimationLockBefore, float AnimationLockAfter,
    bool ActionQueuedBefore, uint QueuedActionIdBefore,
    bool ActionQueuedAfter, uint QueuedActionIdAfter,
    float SourceX, float SourceY, float SourceZ, string EvidenceScope);

/// <summary>
/// A compact 20 Hz local-only state stream. It exists to prove cast continuity,
/// queue state, animation-lock countdown and first observable movement without
/// polling every visible player or writing on every rendered frame.
/// </summary>
internal sealed record LocalActionStateEvent(string Schema, string Event, string MatchUid,
    DateTime ObservedAtUtc, long ElapsedMs, uint SourceEntityId,
    string SourceName, ushort SourceWorldId, byte SourceClassJobId,
    float X, float Y, float Z, bool IsCasting, uint CastActionId,
    float CurrentCastSeconds, float TotalCastSeconds,
    float AnimationLockSeconds, bool ActionQueued, uint QueuedActionId,
    string Continuity, string EvidenceScope);
internal sealed record StatusChangeEvent(string Schema, string Event, string MatchUid, DateTime ObservedAtUtc,
    long ElapsedMs, uint TargetEntityId, uint SourceEntityId, uint StatusId, uint StackCount,
    float DurationSeconds, string Change);
internal sealed record ExactDeathEvent(string Schema, string Event, string MatchUid, DateTime ObservedAtUtc,
    long ElapsedMs, uint TargetEntityId);
internal sealed record PeriodicEffectEvent(string Schema, string Event, string MatchUid, DateTime ObservedAtUtc,
    long ElapsedMs, uint TargetEntityId, uint StatusId, uint Amount, string Kind);

/// <summary>
/// Privacy-preserving evidence for the final web-demo commander/macro scorer.
/// The original chat text is never written: MessageDigest preserves distinct-message
/// counting, while CommandCategories preserves only the tactical meaning needed by
/// the model (follow/attack/retreat/countdown).
/// </summary>
internal sealed record CoordinationChatEvent(string Schema, string Event, string MatchUid,
    DateTime ObservedAtUtc, long ElapsedMs, string Channel, string Speaker,
    string MessageDigest, IReadOnlyList<string> CommandCategories, bool ExplicitCommand);
internal sealed record TargetMarkerProtocolEvent(string Schema, string Event, string MatchUid,
    DateTime ObservedAtUtc, long ElapsedMs, uint ContextEntityId, uint RawSignId,
    uint CommandActorEntityId, uint Param3, uint Param4, ulong TargetId,
    string EvidenceScope = "network_target_marker_actor_target_v1");
internal sealed record TargetMarkerEvent(string Schema, string Event, string MatchUid,
    DateTime ObservedAtUtc, long ElapsedMs, int MarkerIndex, string MarkerKey,
    uint TargetEntityId, string TargetName, ushort TargetWorldId, uint TargetClassJobId,
    byte TargetBattalion, string Change, string OperatorEvidence,
    string TargetObjectKind = "", uint TargetBaseId = 0, uint TargetDataId = 0,
    byte TargetSubKind = 0, uint TargetOwnerId = 0,
    float? TargetX = null, float? TargetY = null, float? TargetZ = null,
    string TargetNameEvidence = "",
    uint OperatorEntityId = 0, string OperatorName = "", ushort OperatorWorldId = 0,
    uint OperatorClassJobId = 0, byte OperatorBattalion = 255);
internal sealed record FieldMarkerEvent(string Schema, string Event, string MatchUid,
    DateTime ObservedAtUtc, long ElapsedMs, int MarkerIndex, string MarkerKey,
    float X, float Y, float Z, string Change,
    uint NearbyActorEntityId, string NearbyActorName, ushort NearbyActorWorldId,
    uint NearbyActorClassJobId, byte NearbyActorBattalion, float? NearbyActorDistance,
    string PlacementRelation, string OperatorEvidence,
    uint OperatorEntityId = 0, string OperatorName = "", ushort OperatorWorldId = 0,
    uint OperatorClassJobId = 0, byte OperatorBattalion = 255);
internal sealed record MovementSpeedSample(long ElapsedMs, double Speed, double Distance, double Dt,
    IReadOnlyList<int> SpeedStatusIds,
    float? FromX = null, float? FromY = null, float? FromZ = null,
    float? ToX = null, float? ToY = null, float? ToZ = null,
    IReadOnlyList<uint>? MovementActionIds = null, string? MovementAttribution = null,
    double? AppliedEnvelope = null);
internal sealed record MovementAnomalyObservationEvent(string Schema, string Event, string MatchUid,
    DateTime ObservedAtUtc, long ElapsedMs, uint EntityId, string PlayerName, ushort WorldId,
    byte ClassJobId, double HardEnvelope, IReadOnlyList<MovementSpeedSample> Samples,
    string EvidenceScope);
internal sealed record MovementSpeedDistributionEvent(string Schema, string Event, string MatchUid,
    DateTime ObservedAtUtc, long ElapsedMs, uint EntityId, string PlayerName, ushort WorldId,
    byte ClassJobId, double HardEnvelope, IReadOnlyList<double> UpperBounds,
    IReadOnlyList<int> Counts, IReadOnlyList<int> SpeedStatusCounts,
    IReadOnlyList<int> DisplacementCounts, IReadOnlyList<int> UnexplainedCounts,
    IReadOnlyList<int> MountedCounts, IReadOnlyList<int> BuffNormalizedCounts,
    int ContinuousIntervals, int MountedIntervals, int BuffNormalizedIntervals,
    string EvidenceScope);
internal sealed record ForcedMovementPositionSample(long ElapsedMs, float X, float Y, float Z);
internal sealed record ForcedMovementObservationEvent(string Schema, string Event, string MatchUid,
    DateTime ObservedAtUtc, long ElapsedMs, uint ActionId, uint SourceEntityId,
    string SourceName, ushort SourceWorldId, byte SourceClassJobId,
    uint TargetEntityId, string TargetName, ushort TargetWorldId, byte TargetClassJobId,
    IReadOnlyList<ForcedMovementPositionSample> Samples, bool ContinuousVisible,
    string RejectionReason, string EvidenceScope);

internal sealed record TeamResult(string Team, ushort Placement, ushort OccupationPoints,
    ushort EnemyKillPoints, ushort DronePoints, ushort IcePoints, ushort TotalPoints, ushort KoPointLosses);

internal sealed record PlayerResult(string Name, ushort WorldId, byte ClassJobId, byte Team, byte Alliance,
    byte Kills, byte Deaths, byte Assists, uint DamageDealt, uint DamageToOther, uint DamageTaken,
    uint HpRestored, uint Occupations);

internal sealed record ResultEvent(string Schema, string Event, string MatchUid, DateTime ObservedAtUtc,
    ushort MatchLengthSeconds, ushort PlayerCount, uint DutyId, uint TerritoryId, string LayoutVersion,
    IReadOnlyList<TeamResult> Teams, IReadOnlyList<PlayerResult> Players);
