using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BattlefieldFrontlineCollector;

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct FrontlineContentDirector
{
    [FieldOffset(GameLayout.ScoreOffset)] public short MaelstromScore;
    [FieldOffset(GameLayout.ScoreOffset + GameLayout.TeamStride)] public short AddersScore;
    [FieldOffset(GameLayout.ScoreOffset + 2 * GameLayout.TeamStride)] public short FlamesScore;
    [FieldOffset(GameLayout.ScoreOffset + GameLayout.BattleHighRelativeOffset)] public byte PlayerBattleHigh;
}

/// <summary>
/// Read-only Crystalline Conflict director fields verified against the current
/// game build and the MIT-licensed PvpStats implementation. Progress values
/// are tenths of a percent; checkpoint values are whole percentages.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct CrystallineConflictContentDirector
{
    private const int Offset = 0x1F90;
    [FieldOffset(Offset + 0x1A0)] public float CrystalUnbindTimeRemaining;
    [FieldOffset(Offset + 0x1A8)] public int CrystalPosition;
    [FieldOffset(Offset + 0x1AC)] public int AstraProgress;
    [FieldOffset(Offset + 0x1B0)] public int UmbraProgress;
    [FieldOffset(Offset + 0x1B4)] public int AstraOnPoint;
    [FieldOffset(Offset + 0x1B8)] public int UmbraOnPoint;
    [FieldOffset(Offset + 0x1BC)] public int AstraCheckpointProgress;
    [FieldOffset(Offset + 0x1C0)] public int UmbraCheckpointProgress;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct FrontlineResultsPacket
{
    [FieldOffset(0x10)] public ushort MatchLength;
    [FieldOffset(0x24)] public ushort PlayerCount;
    [FieldOffset(0x28)] public TeamStat Maelstrom;
    [FieldOffset(0x36)] public TeamStat Adders;
    [FieldOffset(0x44)] public TeamStat Flames;
    [FieldOffset(0x58)] public fixed byte Players[0x60 * 72];
    public Span<PlayerStat> PlayerSpan => new(Unsafe.AsPointer(ref Players[0]), 72);

    [StructLayout(LayoutKind.Explicit, Size = 0x0E)]
    public struct TeamStat
    {
        [FieldOffset(0x00)] public ushort Placement;
        [FieldOffset(0x02)] public ushort OccupationPoints;
        [FieldOffset(0x04)] public ushort EnemyKillPoints;
        [FieldOffset(0x06)] public ushort DronePoints;
        [FieldOffset(0x08)] public ushort IcePoints;
        [FieldOffset(0x0A)] public ushort TotalPoints;
        [FieldOffset(0x0C)] public ushort KOPointLosses;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x60)]
    public unsafe struct PlayerStat
    {
        [FieldOffset(0x10)] public uint DamageDealt;
        [FieldOffset(0x14)] public uint DamageToOther;
        [FieldOffset(0x18)] public uint DamageTaken;
        [FieldOffset(0x1C)] public uint HpRestored;
        [FieldOffset(0x24)] public uint Occupations;
        [FieldOffset(0x2A)] public ushort WorldId;
        [FieldOffset(0x2C)] public byte ClassJobId;
        [FieldOffset(0x2D)] public byte Kills;
        [FieldOffset(0x2E)] public byte Deaths;
        [FieldOffset(0x2F)] public byte Team;
        [FieldOffset(0x30)] public byte Alliance;
        [FieldOffset(0x31)] public byte Assists;
        [FieldOffset(0x32)] public fixed byte PlayerName[32];
    }
}
