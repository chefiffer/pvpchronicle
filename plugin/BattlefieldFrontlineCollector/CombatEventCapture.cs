using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace BattlefieldFrontlineCollector;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct AddStatusEffectPacket
{
    public uint Unknown1, RelatedActionSequence, ActorId, CurrentHp, MaxHp;
    public ushort CurrentMp, Unknown3;
    public byte DamageShield, EffectCount;
    public ushort Unknown6;
    public fixed byte Effects[64];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct StatusEffectAddEntryPacket
{
    public byte EffectIndex, Unknown1;
    public ushort EffectId, StackCount, Unknown3;
    public float Duration;
    public uint SourceActorId;
}

/// <summary>
/// Captures the client events actually received in the uploader's loaded area.
/// The hot detours only copy primitive values into the bounded background writer;
/// sheet lookup, aggregation and database work happen later.
///
/// Event-model design adapted from Kouzukii/ffxiv-deathrecap (MIT); this is an
/// independent Frontline-only implementation and does not load DeathRecap itself.
/// </summary>
internal sealed unsafe class CombatEventCapture : IDisposable
{
    private const string Schema = "battlefield-frontline-collector/v2";
    private readonly JsonlMatchWriter output;
    private readonly Func<(string? MatchUid, long ElapsedMs)> context;
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly Func<uint, ActionActorIdentity?> identityResolver;
    private readonly Action<ActionEffectEvent, LiveTargetState?>? liveSink;
    private readonly Action<ExactDeathEvent>? liveDeathSink;
    private readonly Action<TargetMarkerCommandObservation>? targetMarkerProtocolSink;
    private readonly Func<uint, ActionCaptureMetadata>? actionMetadataResolver;

    private unsafe delegate void ActionEffectDelegate(uint casterEntityId, Character* caster,
        Vector3* targetPosition, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds);
    private delegate void ActorControlDelegate(uint category, uint eventId, uint param1, uint param2,
        uint param3, uint param4, uint param5, uint param6, uint param7, uint param8,
        ulong targetId, byte param9);
    private delegate void EffectResultDelegate(uint targetId, IntPtr actionIntegrityData, byte isReplay);

    private readonly Hook<ActionEffectDelegate> actionEffectHook;
    [Signature("E8 ?? ?? ?? ?? 0F B7 0B 83 E9 64", DetourName = nameof(ActorControlDetour))]
    private readonly Hook<ActorControlDelegate> actorControlHook = null!;
    [Signature("48 8B C4 44 88 40 18 89 48 08", DetourName = nameof(EffectResultDetour))]
    private readonly Hook<EffectResultDelegate> effectResultHook = null!;

    public CombatEventCapture(IGameInteropProvider interop, IObjectTable objectTable, JsonlMatchWriter output,
        Func<(string? MatchUid, long ElapsedMs)> context,
        Func<uint, ActionActorIdentity?> identityResolver, IPluginLog log,
        Func<uint, ActionCaptureMetadata>? actionMetadataResolver = null,
        Action<ActionEffectEvent, LiveTargetState?>? liveSink = null,
        Action<TargetMarkerCommandObservation>? targetMarkerProtocolSink = null,
        Action<ExactDeathEvent>? liveDeathSink = null)
    {
        this.output = output;
        this.objectTable = objectTable;
        this.context = context;
        this.identityResolver = identityResolver;
        this.log = log;
        this.actionMetadataResolver = actionMetadataResolver;
        this.liveSink = liveSink;
        this.liveDeathSink = liveDeathSink;
        this.targetMarkerProtocolSink = targetMarkerProtocolSink;
        interop.InitializeFromAttributes(this);
        actionEffectHook = interop.HookFromSignature<ActionEffectDelegate>(
            ActionEffectHandler.Addresses.Receive.String, ActionEffectDetour);
        actionEffectHook.Enable(); actorControlHook.Enable(); effectResultHook.Enable();
    }

    private void ActionEffectDetour(uint casterEntityId, Character* caster, Vector3* targetPosition,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effectArray,
        GameObjectId* targetEntityIds)
    {
        actionEffectHook.Original(casterEntityId, caster, targetPosition, header, effectArray, targetEntityIds);
        try
        {
            var (matchUid, elapsedMs) = context();
            if (matchUid == null || header->NumTargets == 0) return;
            var copied = new List<RawActionEffect>(Math.Min(16, header->NumTargets * 2));
            var actors = new Dictionary<uint, ActionActorIdentity>();
            LiveTargetState? localTargetState = null;
            AddIdentity(actors, casterEntityId);
            var sourceObject = objectTable.SearchById(casterEntityId);
            for (var targetIndex = 0; targetIndex < header->NumTargets; targetIndex++)
            {
                var targetId = (uint)(targetEntityIds[targetIndex] & uint.MaxValue);
                var target = objectTable.SearchById(targetId);
                AddIdentity(actors, targetId);
                var currentHp = target is Dalamud.Game.ClientState.Objects.Types.IBattleChara battle ? battle.CurrentHp : 0u;
                var maxHp = target is Dalamud.Game.ClientState.Objects.Types.IBattleChara battleMax ? battleMax.MaxHp : 0u;
                var shield = target is Dalamud.Game.ClientState.Objects.Types.IBattleChara shielded ? shielded.ShieldPercentage : (byte)0;
                if (targetId == objectTable.LocalPlayer?.EntityId && target is IBattleChara localBattle)
                    localTargetState = new LiveTargetState(targetId,
                        localBattle.StatusList.Select(status => status.StatusId).Where(id => id != 0).ToArray());
                for (byte slot = 0; slot < 8; slot++)
                {
                    ref var effect = ref effectArray[targetIndex].Effects[slot];
                    // Keep combat, resource, status, control and interrupt semantics.
                    // Pure VFX/gauge/enmity/combo bookkeeping is not useful for replay
                    // and would substantially inflate a 72-player match capture.
                    if (effect.Type is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or
                        10 or 11 or 14 or 15 or 16 or 17 or 18 or 20 or 32 or 55 or 74 or 75)) continue;
                    uint amount = effect.Value;
                    if ((effect.Param4 & 0x40) != 0) amount += (uint)effect.Param3 << 16;
                    copied.Add(new RawActionEffect(targetId, slot, effect.Type, amount,
                        effect.Param0, effect.Param1, effect.Param2, effect.Param3, effect.Param4,
                        currentHp, maxHp, shield, target?.Position.X, target?.Position.Y,
                        target?.Position.Z, (byte)targetIndex));
                }
            }
            if (copied.Count > 0)
            {
                var metadata = actionMetadataResolver?.Invoke(header->SpellId) ?? default;
                var captured = new ActionEffectEvent(Schema, "action_effect", matchUid,
                    DateTime.UtcNow, elapsedMs, header->SpellId, header->ActionType,
                    casterEntityId, copied, new List<ActionActorIdentity>(actors.Values),
                    sourceObject?.Position.X, sourceObject?.Position.Y, sourceObject?.Position.Z,
                    metadata.NominalRange, metadata.EffectRange, metadata.ActionCategoryId,
                    metadata.Cast100Ms, metadata.Recast100Ms, metadata.CooldownGroup,
                    metadata.MaxCharges, metadata.AffectsPosition, "lumina-action-v2",
                    header->GlobalSequence, header->SourceSequence, header->AnimationLock);
                output.Write(captured);
                liveSink?.Invoke(captured, localTargetState);
            }
        }
        catch (Exception exception) { log.Verbose(exception, "Skipped malformed action-effect packet."); }
    }

    private void AddIdentity(Dictionary<uint, ActionActorIdentity> actors, uint entityId)
    {
        if (entityId == 0 || actors.ContainsKey(entityId)) return;
        actors[entityId] = identityResolver(entityId) ??
            new ActionActorIdentity(entityId, string.Empty, 0, 0, 0, string.Empty, 0, 0);
    }

    private void ActorControlDetour(uint entityId, uint category, uint param1, uint param2,
        uint param3, uint param4, uint param5, uint param6, uint param7, uint param8,
        ulong targetId, byte param9)
    {
        actorControlHook.Original(entityId, category, param1, param2, param3, param4,
            param5, param6, param7, param8, targetId, param9);
        try
        {
            var (matchUid, elapsedMs) = context();
            if (matchUid == null) return;
            if (category == 0x6)
            {
                var death = new ExactDeathEvent(Schema, "death", matchUid, DateTime.UtcNow,
                    elapsedMs, entityId);
                output.Write(death);
                liveDeathSink?.Invoke(death);
            }
            else if (category is 0x604 or 0x605)
                output.Write(new PeriodicEffectEvent(Schema, "periodic_effect", matchUid,
                    DateTime.UtcNow, elapsedMs, entityId, param1, param2,
                    category == 0x604 ? "hot" : "dot"));
            else if (category == 0x1F6)
            {
                output.Write(new TargetMarkerProtocolEvent(Schema,
                    "target_marker_protocol", matchUid, DateTime.UtcNow, elapsedMs,
                    entityId, param1, param2, param3, param4, targetId));
                var target = unchecked((uint)targetId);
                var remove = target is 0 or 0xE0000000u;
                targetMarkerProtocolSink?.Invoke(new TargetMarkerCommandObservation(
                    elapsedMs, param2, unchecked((int)param1),
                    remove ? "remove" : "apply", remove ? 0u : target,
                    "network_target_marker_verified"));
            }
        }
        catch (Exception exception) { log.Verbose(exception, "Skipped malformed actor-control packet."); }
    }

    private void EffectResultDetour(uint targetId, IntPtr actionIntegrityData, byte isReplay)
    {
        effectResultHook.Original(targetId, actionIntegrityData, isReplay);
        try
        {
            var (matchUid, elapsedMs) = context();
            if (matchUid == null || actionIntegrityData == IntPtr.Zero) return;
            var message = (AddStatusEffectPacket*)actionIntegrityData;
            var effects = (StatusEffectAddEntryPacket*)message->Effects;
            var count = Math.Min(message->EffectCount, 4u);
            for (uint index = 0; index < count; index++)
            {
                var effect = effects[index];
                if (effect.EffectId == 0) continue;
                output.Write(new StatusChangeEvent(Schema, "status_change", matchUid,
                    DateTime.UtcNow, elapsedMs, targetId, effect.SourceActorId,
                    effect.EffectId, effect.StackCount, effect.Duration,
                    effect.Duration < 0 ? "remove" : "apply"));
            }
        }
        catch (Exception exception) { log.Verbose(exception, "Skipped malformed status-effect packet."); }
    }

    public void Dispose()
    {
        actionEffectHook.Dispose(); actorControlHook.Dispose(); effectResultHook.Dispose();
    }
}
