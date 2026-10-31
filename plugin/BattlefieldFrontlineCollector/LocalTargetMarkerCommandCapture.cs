using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Passively observes this client's object-marker request functions.  These
/// detours call the original game functions unchanged and only enqueue a local
/// attribution candidate after a successful request.  A later, independently
/// sampled MarkingController state change must still match the slot and target.
///
/// 仅被动观察本机发出的人头标点请求；不设置、不清除标点。请求还必须与随后
/// 采样到的同槽位、同目标状态差分唯一配对，才可认定命令者为本机玩家。
/// </summary>
internal sealed unsafe class LocalTargetMarkerCommandCapture : IDisposable
{
    private const string SetSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 57 48 83 EC 20 8D 42 FF 49 8B E8 8B FA 48 8B D9 83 F8 10";
    private const string ClearSignature =
        "40 56 48 83 EC 30 BE 00 00 00 E0 48 3B D6 74 ?? 48 89 5C 24 ?? 4C 8D 41 10";

    private delegate byte SetDelegate(MarkingController* self, uint oneBasedMarkerId, ulong targetId);
    private delegate void ClearDelegate(MarkingController* self, ulong targetId);

    private readonly Hook<SetDelegate>? setHook;
    private readonly Hook<ClearDelegate>? clearHook;
    private readonly Func<(string? MatchUid, long ElapsedMs, uint LocalEntityId)> context;
    private readonly Action<TargetMarkerCommandObservation> sink;
    private readonly IPluginLog log;

    public LocalTargetMarkerCommandCapture(IGameInteropProvider interop,
        Func<(string? MatchUid, long ElapsedMs, uint LocalEntityId)> context,
        Action<TargetMarkerCommandObservation> sink, IPluginLog log)
    {
        this.context = context; this.sink = sink; this.log = log;
        try
        {
            setHook = interop.HookFromSignature<SetDelegate>(SetSignature, SetDetour);
            clearHook = interop.HookFromSignature<ClearDelegate>(ClearSignature, ClearDetour);
            setHook.Enable(); clearHook.Enable();
        }
        catch (Exception exception)
        {
            setHook?.Dispose(); clearHook?.Dispose();
            log.Warning(exception,
                "Local target-marker command capture is unavailable for this game version; state capture remains active.");
        }
    }

    private byte SetDetour(MarkingController* self, uint oneBasedMarkerId, ulong targetId)
    {
        var result = setHook!.Original(self, oneBasedMarkerId, targetId);
        try
        {
            var (matchUid, elapsedMs, actor) = context();
            var target = unchecked((uint)targetId);
            if (result == 0 && matchUid != null && actor != 0 &&
                oneBasedMarkerId is >= 1 and <= 17 && RecordableTarget(target))
                sink(new TargetMarkerCommandObservation(elapsedMs, actor,
                    (int)oneBasedMarkerId - 1, "apply", target));
        }
        catch (Exception exception) { log.Verbose(exception, "Skipped malformed local target-marker request."); }
        return result;
    }

    private void ClearDetour(MarkingController* self, ulong targetId)
    {
        var target = unchecked((uint)targetId);
        var markerIndex = FindMarker(self, targetId);
        clearHook!.Original(self, targetId);
        try
        {
            var (matchUid, elapsedMs, actor) = context();
            if (matchUid != null && actor != 0 && markerIndex >= 0 && RecordableTarget(target))
                sink(new TargetMarkerCommandObservation(elapsedMs, actor,
                    markerIndex, "remove", target));
        }
        catch (Exception exception) { log.Verbose(exception, "Skipped malformed local target-marker clear request."); }
    }

    private static int FindMarker(MarkingController* controller, ulong targetId)
    {
        if (controller == null || targetId == 0 || unchecked((uint)targetId) == 0xE0000000u) return -1;
        for (var index = 0; index < 17; index++)
            if (controller->Markers[index].ObjectId == unchecked((uint)targetId)) return index;
        return -1;
    }

    private static bool RecordableTarget(uint id) => id != 0 && id != 0xE0000000u;

    public void Dispose()
    {
        setHook?.Dispose(); clearHook?.Dispose();
    }
}
