using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Observes only this client's field-marker request functions.  It does not
/// automate markers.  A successful request is merely a pending observation;
/// Plugin must still pair it with the same slot/payload in MarkingController.
/// </summary>
internal sealed unsafe class LocalFieldMarkerCommandCapture : IDisposable
{
    private delegate byte PlaceDelegate(MarkingController* self, uint index, Vector3* position);
    private delegate byte ClearDelegate(MarkingController* self, uint index);

    private readonly Hook<PlaceDelegate>? placeHook;
    private readonly Hook<ClearDelegate>? clearHook;
    private readonly Func<(string? MatchUid, long ElapsedMs, uint LocalEntityId)> context;
    private readonly Action<FieldMarkerCommandObservation> sink;
    private readonly IPluginLog log;

    public LocalFieldMarkerCommandCapture(IGameInteropProvider interop,
        Func<(string? MatchUid, long ElapsedMs, uint LocalEntityId)> context,
        Action<FieldMarkerCommandObservation> sink, IPluginLog log)
    {
        this.context = context; this.sink = sink; this.log = log;
        try
        {
            placeHook = interop.HookFromAddress<PlaceDelegate>(
                MarkingController.Addresses.PlaceFieldMarker.Value, PlaceDetour);
            clearHook = interop.HookFromAddress<ClearDelegate>(
                MarkingController.Addresses.ClearFieldMarker.Value, ClearDetour);
            placeHook.Enable(); clearHook.Enable();
        }
        catch (Exception exception)
        {
            placeHook?.Dispose(); clearHook?.Dispose();
            log.Warning(exception,
                "Local field-marker command capture is unavailable for this game version; state capture remains active.");
        }
    }

    private byte PlaceDetour(MarkingController* self, uint index, Vector3* position)
    {
        var result = placeHook!.Original(self, index, position);
        try
        {
            var (matchUid, elapsedMs, actor) = context();
            if (result == 0 && matchUid != null && actor != 0 && index < 8 && position != null)
                sink(new FieldMarkerCommandObservation(elapsedMs, actor, (int)index,
                    "apply", position->X, position->Y, position->Z));
        }
        catch (Exception exception) { log.Verbose(exception, "Skipped malformed local field-marker request."); }
        return result;
    }

    private byte ClearDetour(MarkingController* self, uint index)
    {
        var result = clearHook!.Original(self, index);
        try
        {
            var (matchUid, elapsedMs, actor) = context();
            if (result == 0 && matchUid != null && actor != 0 && index < 8)
                sink(new FieldMarkerCommandObservation(elapsedMs, actor, (int)index,
                    "remove", 0, 0, 0));
        }
        catch (Exception exception) { log.Verbose(exception, "Skipped malformed local field-marker clear request."); }
        return result;
    }

    public void Dispose()
    {
        placeHook?.Dispose(); clearHook?.Dispose();
    }
}
