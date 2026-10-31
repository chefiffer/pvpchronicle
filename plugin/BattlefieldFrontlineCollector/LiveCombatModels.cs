using System.Collections.Generic;

namespace BattlefieldFrontlineCollector;

internal sealed record LiveTargetState(uint EntityId, IReadOnlyList<uint> StatusIds);
internal sealed record LiveObservedKda(uint EntityId, string Name, ushort WorldId, byte JobId,
    int Kills, int Deaths, int Assists);
internal sealed record LiveCombatSnapshot(IReadOnlyList<LiveCombatRow> Rows,
    IReadOnlyList<LiveCoordinationRow> Coordination,
    IReadOnlyList<LiveCombatRow> MatchTendencyRows,
    IReadOnlyList<LiveCoordinationRow> MatchCoordination,
    IReadOnlyList<LiveObservedKda> ObservedKda,
    long EndElapsedMs, long MatchEvidenceEndElapsedMs, bool IsActive);
internal sealed record LiveCoordinationRow(long ElapsedMs, string Speaker, string Digest,
    IReadOnlyList<string> Categories);
internal sealed record LiveCombatRow(long ElapsedMs, uint ActionId, byte ActionType,
    uint SourceEntityId, string SourceName, ushort SourceWorldId, byte SourceJobId,
    uint TargetEntityId, string TargetName, ushort TargetWorldId, byte TargetJobId,
    uint Amount, string Kind, uint TargetCurrentHp, uint TargetMaxHp, byte TargetShieldPercent,
    IReadOnlyList<uint>? TargetStatusIds = null,
    byte SourceTeam = 255, byte SourceAlliance = 255,
    byte TargetTeam = 255, byte TargetAlliance = 255,
    byte SourceBattalion = 255, byte TargetBattalion = 255);
