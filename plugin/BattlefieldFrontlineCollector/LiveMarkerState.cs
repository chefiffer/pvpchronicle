using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

internal sealed record LiveFieldMarker(int Index, string Key, float X, float Y, float Z);
internal sealed record LiveTargetMarker(int Index, string Key, uint EntityId, string Name,
    ushort WorldId, uint JobId, byte Team, float X, float Y, float Z, bool PositionVisible);
internal sealed record LiveObjectiveMarker(uint Id, string Label, string State, byte OwnerTeam,
    float X, float Y, float Z, uint CurrentHp = 0, uint MaxHp = 0,
    string EvidenceScope = "", int? RemainingSeconds = null,
    double? ProgressPercent = null, string DisplayText = "");
internal sealed record LiveMarkerSnapshot(IReadOnlyList<LiveFieldMarker> Fields,
    IReadOnlyList<LiveTargetMarker> Targets, IReadOnlyList<LiveObjectiveMarker> Objectives);

/// <summary>
/// Small lock-protected state cache. It never performs IO and is updated only
/// with the 8 field-marker and 17 target-marker slots observed from the client.
/// 小型只读快照缓存；不执行磁盘或网络操作，避免实时 UI 阻塞游戏线程。
/// </summary>
internal sealed class LiveMarkerState
{
    private readonly object gate = new();
    private readonly Dictionary<int, LiveFieldMarker> fields = new();
    private readonly Dictionary<int, LiveTargetMarker> targets = new();
    private readonly Dictionary<uint, LiveObjectiveMarker> objectives = new();

    public void Reset()
    {
        lock (gate) { fields.Clear(); targets.Clear(); objectives.Clear(); }
    }

    public void SetField(LiveFieldMarker marker)
    {
        lock (gate) fields[marker.Index] = marker;
    }

    public void RemoveField(int index)
    {
        lock (gate) fields.Remove(index);
    }

    public void SetTarget(LiveTargetMarker marker)
    {
        lock (gate)
        {
            if (marker.PositionVisible) targets[marker.Index] = marker;
            else targets.Remove(marker.Index);
        }
    }

    public void RemoveTarget(int index)
    {
        lock (gate) targets.Remove(index);
    }

    public void SetObjective(LiveObjectiveMarker marker)
    {
        lock (gate) objectives[marker.Id] = marker;
    }

    public void RemoveObjective(uint id)
    {
        lock (gate) objectives.Remove(id);
    }

    public LiveMarkerSnapshot Snapshot()
    {
        lock (gate) return new LiveMarkerSnapshot(fields.Values.OrderBy(x => x.Index).ToArray(),
            targets.Values.OrderBy(x => x.Index).ToArray(),
            objectives.Values.OrderBy(x => x.Label).ThenBy(x => x.Id).ToArray());
    }
}
