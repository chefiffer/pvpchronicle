using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Optional, result-only Crystalline Conflict collector.  Signature/layout are
/// isolated here so a patch mismatch disables only this feature.
/// 可选的水晶冲突结算采集；版本失配只会停用本功能，不影响前线记录。
/// </summary>
internal sealed unsafe class CrystallineResultCapture : IDisposable
{
    // Current upstream result handler pattern; kept local and failure-safe.
    private const string ResultSignature =
        "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 0F B6 42";
    private delegate void ResultDelegate(IntPtr director, IntPtr packet, IntPtr value, uint unknown);
    private readonly Hook<ResultDelegate>? hook;
    private readonly Func<(string Name, ushort WorldId, byte ClassJobId, uint DutyId, uint TerritoryId)> context;
    private readonly Action<CrystallineMatchRecord> sink;
    private readonly Action<CrystallineRankObservation> rankSink;
    private readonly IPluginLog log;

    [StructLayout(LayoutKind.Explicit)]
    private struct ResultPacket
    {
        [FieldOffset(0x10)] public ushort MatchLength;
        [FieldOffset(0x22)] public byte TierBefore;
        [FieldOffset(0x23)] public byte TierAfter;
        [FieldOffset(0x24)] public byte RiserBefore;
        [FieldOffset(0x25)] public byte RiserAfter;
        [FieldOffset(0x26)] public byte StarsBefore;
        [FieldOffset(0x27)] public byte StarsAfter;
        [FieldOffset(0x28)] public ushort CreditBefore;
        [FieldOffset(0x2A)] public ushort CreditAfter;
        [FieldOffset(0x3C)] public byte Result;
        [FieldOffset(0x40)] public uint AstraProgress;
        [FieldOffset(0x44)] public uint UmbraProgress;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x50)]
    private struct PlayerPacket
    {
        [FieldOffset(0x10)] public int DamageDealt;
        [FieldOffset(0x14)] public int DamageTaken;
        [FieldOffset(0x18)] public int HpRestored;
        [FieldOffset(0x1C)] public ushort WorldId;
        [FieldOffset(0x1E)] public byte ClassJobId;
        [FieldOffset(0x1F)] public byte Kills;
        [FieldOffset(0x20)] public byte Deaths;
        [FieldOffset(0x21)] public byte Assists;
        [FieldOffset(0x22)] public ushort TimeOnCrystal;
        [FieldOffset(0x24)] public byte RankId;
        [FieldOffset(0x25)] public byte Team;
        [FieldOffset(0x26)] public fixed byte PlayerName[42];
    }

    public CrystallineResultCapture(IGameInteropProvider interop,
        Func<(string Name, ushort WorldId, byte ClassJobId, uint DutyId, uint TerritoryId)> context,
        Action<CrystallineMatchRecord> sink, Action<CrystallineRankObservation> rankSink,
        IPluginLog log)
    {
        this.context = context;
        this.sink = sink;
        this.rankSink = rankSink;
        this.log = log;
        try
        {
            hook = interop.HookFromSignature<ResultDelegate>(ResultSignature, Detour);
            hook.Enable();
        }
        catch (Exception exception)
        {
            hook?.Dispose();
            log.Warning(exception,
                "Crystalline Conflict result capture is unavailable for this game version; Frontline capture remains active.");
        }
    }

    private void Detour(IntPtr director, IntPtr packetAddress, IntPtr value, uint unknown)
    {
        try
        {
            if (packetAddress == IntPtr.Zero) return;
            var packet = (ResultPacket*)packetAddress;
            if (packet->MatchLength is 0 or > 3600) return;
            var identity = context();
            var mapKey = MapKey(identity.TerritoryId);
            var matchType = MatchType(identity.DutyId);
            if (identity.WorldId == 0 || mapKey.Length == 0 || matchType.Length == 0) return;

            var job = identity.ClassJobId;
            var players = (PlayerPacket*)((byte*)packetAddress + 0x48);
            var playerResults = new List<CrystallinePlayerResult>(10);
            for (var index = 0; index < 10; index++)
            {
                var name = ReadName(players + index);
                if (players[index].ClassJobId != 0 && players[index].WorldId != 0 &&
                    !string.IsNullOrWhiteSpace(name))
                {
                    playerResults.Add(new CrystallinePlayerResult(name, players[index].WorldId,
                        players[index].ClassJobId, players[index].Team,
                        players[index].Kills, players[index].Deaths, players[index].Assists,
                        players[index].DamageDealt, players[index].DamageTaken,
                        players[index].HpRestored, players[index].TimeOnCrystal,
                        players[index].RankId));
                }
                if (players[index].WorldId == identity.WorldId &&
                    string.Equals(name, identity.Name, StringComparison.OrdinalIgnoreCase))
                {
                    job = players[index].ClassJobId;
                }
            }
            if (!CrystallineRosterValidation.IsComplete(playerResults))
                throw new InvalidOperationException(
                    $"Crystalline result roster is incomplete: {playerResults.Count}/10 players.");

            var ended = DateTime.UtcNow;
            var matchUid = $"cc-{ended:yyyyMMddHHmmss}-{identity.WorldId}-{packet->MatchLength}";
            sink(new CrystallineMatchRecord(ended, identity.Name, identity.WorldId,
                mapKey, matchType, packet->Result == 1, job, packet->MatchLength,
                packet->TierBefore, packet->TierAfter, packet->RiserBefore, packet->RiserAfter,
                packet->StarsBefore, packet->StarsAfter, packet->CreditBefore, packet->CreditAfter,
                packet->AstraProgress, packet->UmbraProgress, playerResults, matchUid,
                Outcome: packet->Result switch { 1 => "win", 2 => "loss", _ => "draw" }));
            if (matchType == "ranked")
            {
                rankSink(new CrystallineRankObservation(ended.AddSeconds(-packet->MatchLength),
                    identity.Name, identity.WorldId, packet->TierBefore, packet->RiserBefore,
                    packet->StarsBefore, packet->CreditBefore, "cc_result_before"));
                rankSink(new CrystallineRankObservation(ended, identity.Name, identity.WorldId,
                    packet->TierAfter, packet->RiserAfter, packet->StarsAfter,
                    packet->CreditAfter, "cc_result_after"));
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "Crystalline Conflict result packet was rejected.");
        }
        finally
        {
            hook!.Original(director, packetAddress, value, unknown);
        }
    }

    private static string ReadName(PlayerPacket* player)
    {
        var bytes = new ReadOnlySpan<byte>(player->PlayerName, 42);
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(bytes[..(end < 0 ? bytes.Length : end)]);
    }

    private static string MapKey(uint territoryId) => territoryId switch
    {
        1032 or 1058 => "palaistra",
        1033 or 1059 => "volcanic",
        1034 or 1060 => "cloudnine",
        1116 or 1117 => "castletown",
        1138 or 1139 => "redsands",
        1293 or 1294 => "bayside",
        1357 or 1358 => "harmonias",
        _ => string.Empty
    };

    private static string MatchType(uint dutyId) => dutyId switch
    {
        835 or 836 or 837 or 912 or 967 or 1046 or 1102 => "casual",
        838 or 841 or 847 or 850 or 853 or 856 or 859 or
        839 or 842 or 848 or 851 or 854 or 857 or 860 or
        840 or 843 or 849 or 852 or 855 or 858 or 861 or
        913 or 914 or 915 or 916 or 917 or 918 or 919 or 920 or 921 or 922 or
        968 or 969 or 970 or 971 or 972 or 973 or 974 or 975 or 976 or 977 or
        1047 or 1048 or 1049 or 1050 or 1051 or 1052 or 1053 or 1054 or 1055 or 1056 or
        1103 or 1104 or 1105 or 1106 or 1107 or 1108 or 1109 or 1110 or 1111 or 1112 => "ranked",
        _ => string.Empty
    };

    public void Dispose() => hook?.Dispose();
}
