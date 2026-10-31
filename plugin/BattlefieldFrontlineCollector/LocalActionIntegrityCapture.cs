using System;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Captures local-client evidence needed for animation-lock, aftercast movement,
/// and cast-duration review. This component is passive: the original UseAction
/// function is called exactly once with unchanged arguments, and no game field
/// is written.
/// </summary>
internal sealed unsafe class LocalActionIntegrityCapture : IDisposable
{
    private const string Schema = "battlefield-frontline-collector/v2";
    private unsafe delegate bool UseActionDelegate(ActionManager* self, ActionType actionType,
        uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode,
        uint comboRouteId, bool* outOptAreaTargeted);

    private readonly IObjectTable objectTable;
    private readonly JsonlMatchWriter output;
    private readonly Func<(string? MatchUid, long ElapsedMs)> context;
    private readonly IPluginLog log;
    private Hook<UseActionDelegate>? requestHook;
    private long requestOrdinal;

    public bool RequestHookAvailable => requestHook?.IsEnabled == true;

    public LocalActionIntegrityCapture(IGameInteropProvider interop, IObjectTable objectTable,
        JsonlMatchWriter output, Func<(string? MatchUid, long ElapsedMs)> context, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.output = output;
        this.context = context;
        this.log = log;
        try
        {
            requestHook = interop.HookFromSignature<UseActionDelegate>(
                "E8 ?? ?? ?? ?? B0 01 EB B6", UseActionDetour);
            requestHook.Enable();
        }
        catch (Exception exception)
        {
            // A signature drift removes strong evidence only. It must never
            // prevent the recorder from loading or fall back to guessed input.
            log.Warning(exception, "Local UseAction evidence is unavailable; effect-only capture remains active.");
        }
    }

    private bool UseActionDetour(ActionManager* self, ActionType actionType, uint actionId,
        ulong targetId, uint extraParam, ActionManager.UseActionMode mode,
        uint comboRouteId, bool* outOptAreaTargeted)
    {
        var lockBefore = self == null ? 0 : self->AnimationLock;
        var queuedBefore = self != null && self->ActionQueued;
        var queuedActionBefore = self == null ? 0u : self->QueuedActionId;
        var result = requestHook!.Original(self, actionType, actionId, targetId,
            extraParam, mode, comboRouteId, outOptAreaTargeted);
        try
        {
            var (matchUid, elapsedMs) = context();
            var player = objectTable.LocalPlayer;
            if (matchUid == null || player == null) return result;
            var battle = player as Dalamud.Game.ClientState.Objects.Types.IBattleChara;
            output.Write(new LocalActionRequestEvent(Schema, "local_action_request", matchUid,
                DateTime.UtcNow, elapsedMs, Interlocked.Increment(ref requestOrdinal),
                player.EntityId, player.Name.TextValue, (ushort)player.HomeWorld.RowId,
                (byte)(battle?.ClassJob.RowId ?? 0), (uint)actionType, actionId, targetId,
                (uint)mode, result, lockBefore, self == null ? 0 : self->AnimationLock,
                queuedBefore, queuedActionBefore, self != null && self->ActionQueued,
                self == null ? 0u : self->QueuedActionId,
                player.Position.X, player.Position.Y, player.Position.Z,
                "local_use_action_read_only_v2"));
        }
        catch (Exception exception)
        {
            log.Verbose(exception, "Skipped malformed local action-request evidence.");
        }
        return result;
    }

    public void Sample()
    {
        try
        {
            var (matchUid, elapsedMs) = context();
            var player = objectTable.LocalPlayer;
            var battle = player as Dalamud.Game.ClientState.Objects.Types.IBattleChara;
            var manager = ActionManager.Instance();
            if (matchUid == null || player == null || battle == null || manager == null) return;
            output.Write(new LocalActionStateEvent(Schema, "local_action_state", matchUid,
                DateTime.UtcNow, elapsedMs, player.EntityId, player.Name.TextValue,
                (ushort)player.HomeWorld.RowId, (byte)battle.ClassJob.RowId,
                player.Position.X, player.Position.Y, player.Position.Z,
                battle.IsCasting, battle.IsCasting ? battle.CastActionId : 0u,
                Math.Max(0, battle.CurrentCastTime), Math.Max(0, battle.TotalCastTime),
                Math.Max(0, manager->AnimationLock), manager->ActionQueued,
                manager->QueuedActionId, "continuous_local_loaded_50ms",
                "local_action_state_read_only_v1"));
        }
        catch (Exception exception)
        {
            log.Verbose(exception, "Skipped malformed local action-state sample.");
        }
    }

    public void Reset() => Interlocked.Exchange(ref requestOrdinal, 0);

    public void Dispose() => requestHook?.Dispose();
}
