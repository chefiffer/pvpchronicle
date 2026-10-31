using System;
using System.Collections.Generic;

namespace BattlefieldFrontlineCollector;

internal readonly record struct MarkerOperatorAttribution(
    uint EntityId, string Evidence, long ObservedElapsedMs);

internal readonly record struct TargetMarkerCommandObservation(
    long ElapsedMs, uint OperatorEntityId, int MarkerIndex, string Change,
    uint TargetEntityId, string Evidence = "local_command_verified");

internal readonly record struct FieldMarkerCommandObservation(
    long ElapsedMs, uint OperatorEntityId, int MarkerIndex, string Change,
    float X, float Y, float Z, string Evidence = "local_command_verified");

/// <summary>
/// Pairs a command-bearing observation with the independently sampled marker state.
/// A nearby actor or a merely close timestamp is never enough: payload, slot and
/// state transition must produce exactly one match inside the bounded window.
/// </summary>
internal sealed class MarkerAttributionTracker
{
    private const long PairWindowMs = 400;
    private const float CoordinateTolerance = .08f;
    private readonly object gate = new();
    private readonly List<TargetMarkerCommandObservation> target = [];
    private readonly List<FieldMarkerCommandObservation> field = [];

    public void Observe(TargetMarkerCommandObservation value)
    {
        if (!RecordableActor(value.OperatorEntityId) || value.MarkerIndex is < 0 or > 16 ||
            (value.Change == "apply" && !RecordableActor(value.TargetEntityId)) ||
            value.Change is not ("apply" or "remove") ||
            value.Evidence is not ("local_command_verified" or "network_target_marker_verified")) return;
        lock (gate)
        {
            Prune(value.ElapsedMs);
            // The local request hook and the server broadcast can describe the same
            // operation.  Collapse only an identical actor/slot/payload observation;
            // competing actors remain ambiguous and are deliberately rejected.
            var duplicate = target.FindIndex(x =>
                WithinWindow(value.ElapsedMs, x.ElapsedMs) &&
                x.OperatorEntityId == value.OperatorEntityId &&
                x.MarkerIndex == value.MarkerIndex && x.Change == value.Change &&
                x.TargetEntityId == value.TargetEntityId);
            if (duplicate >= 0)
            {
                if (value.Evidence == "local_command_verified") target[duplicate] = value;
                return;
            }
            target.Add(value);
        }
    }

    public void Observe(FieldMarkerCommandObservation value)
    {
        if (!RecordableActor(value.OperatorEntityId) || value.MarkerIndex is < 0 or > 7 ||
            value.Evidence is not ("local_command_verified" or
                "network_field_marker_operator_verified_v1")) return;
        lock (gate)
        {
            Prune(value.ElapsedMs);
            var duplicate = field.FindIndex(x =>
                WithinWindow(value.ElapsedMs, x.ElapsedMs) &&
                x.OperatorEntityId == value.OperatorEntityId &&
                x.MarkerIndex == value.MarkerIndex && x.Change == value.Change &&
                (value.Change == "remove" ||
                 (MathF.Abs(value.X - x.X) <= CoordinateTolerance &&
                  MathF.Abs(value.Y - x.Y) <= CoordinateTolerance &&
                  MathF.Abs(value.Z - x.Z) <= CoordinateTolerance)));
            if (duplicate >= 0)
            {
                // Local request evidence is stronger than a decoded broadcast,
                // but both describe the same operation and must not make the
                // subsequent state transition look ambiguous.
                if (value.Evidence == "local_command_verified") field[duplicate] = value;
                return;
            }
            field.Add(value);
        }
    }

    public MarkerOperatorAttribution? MatchTargetChange(
        long elapsedMs, int markerIndex, uint oldTarget, uint newTarget)
    {
        lock (gate)
        {
            Prune(elapsedMs);
            var matches = new List<int>();
            for (var i = 0; i < target.Count; i++)
            {
                var value = target[i];
                if (!WithinWindow(elapsedMs, value.ElapsedMs)) continue;
                if (value.MarkerIndex != markerIndex) continue;
                var changedTarget = newTarget != 0 ? newTarget : oldTarget;
                if (value.Change != (newTarget != 0 ? "apply" : "remove")) continue;
                // A network removal encodes the empty 0xE0000000 sentinel rather
                // than the former target.  The slot plus unique bounded transition
                // therefore proves the removal; apply still requires exact target.
                if (newTarget != 0 && value.TargetEntityId != changedTarget) continue;
                if (newTarget == 0 && value.TargetEntityId != 0 &&
                    value.TargetEntityId != changedTarget) continue;
                matches.Add(i);
            }
            if (matches.Count != 1)
            {
                // Competing observations are explicitly ambiguous and may not be
                // reused to attribute a later state change.
                for (var i = matches.Count - 1; i >= 0; i--) target.RemoveAt(matches[i]);
                return null;
            }
            var matched = target[matches[0]];
            target.RemoveAt(matches[0]);
            return new MarkerOperatorAttribution(matched.OperatorEntityId,
                matched.Evidence, matched.ElapsedMs);
        }
    }

    public MarkerOperatorAttribution? MatchFieldChange(long elapsedMs, int markerIndex,
        string change, float x, float y, float z)
    {
        lock (gate)
        {
            Prune(elapsedMs);
            var matches = new List<int>();
            for (var i = 0; i < field.Count; i++)
            {
                var value = field[i];
                if (!WithinWindow(elapsedMs, value.ElapsedMs) ||
                    value.MarkerIndex != markerIndex || value.Change != change) continue;
                if (change != "remove" &&
                    (MathF.Abs(value.X - x) > CoordinateTolerance ||
                     MathF.Abs(value.Y - y) > CoordinateTolerance ||
                     MathF.Abs(value.Z - z) > CoordinateTolerance)) continue;
                matches.Add(i);
            }
            if (matches.Count != 1)
            {
                for (var i = matches.Count - 1; i >= 0; i--) field.RemoveAt(matches[i]);
                return null;
            }
            var matched = field[matches[0]];
            field.RemoveAt(matches[0]);
            return new MarkerOperatorAttribution(matched.OperatorEntityId,
                matched.Evidence, matched.ElapsedMs);
        }
    }

    public void Reset()
    {
        lock (gate) { target.Clear(); field.Clear(); }
    }

    private void Prune(long now)
    {
        target.RemoveAll(x => now - x.ElapsedMs > PairWindowMs || x.ElapsedMs - now > PairWindowMs);
        field.RemoveAll(x => now - x.ElapsedMs > PairWindowMs || x.ElapsedMs - now > PairWindowMs);
    }

    private static bool WithinWindow(long left, long right) => Math.Abs(left - right) <= PairWindowMs;
    private static bool RecordableActor(uint id) => id != 0 && id != 0xE0000000u;
}
