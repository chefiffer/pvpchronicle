using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Passively recognizes bounded field-marker-shaped broadcasts for controlled
/// diagnostics. Packet actor values remain unverified candidates and are
/// rejected by MarkerAttributionTracker. They may become operator evidence only
/// after a separately versioned controlled-trial catalogue is implemented.
/// </summary>
internal sealed unsafe class FieldMarkerNetworkCapture : IDisposable
{
    private const int ZoneHeaderBytes = 0x20;
    private const long MaximumDiagnosticBytes = 32L * 1024 * 1024;
    private const int DiagnosticRetentionDays = 7;
    private readonly Hook<PacketDispatcher.Delegates.OnReceivePacket>? receiveHook;
    private readonly Func<(string? MatchUid, long ElapsedMs)> context;
    private readonly Action<FieldMarkerCommandObservation> sink;
    private readonly IPluginLog log;
    private readonly Func<bool> diagnosticsEnabled;
    private readonly string diagnosticDirectory;
    private readonly object diagnosticGate = new();
    private readonly List<DiagnosticPacket> diagnosticRing = new(256);
    private readonly List<PendingStateChange> pendingChanges = [];

    private sealed record DiagnosticActorCandidate(int Offset, uint ActorId);
    private sealed record DiagnosticPacket(long ElapsedMs, int PayloadLength,
        uint DispatcherActorId, uint HeaderActorId, uint SelectedActorId,
        string PayloadPrefixSha256, IReadOnlyList<DiagnosticActorCandidate> ActorCandidates);
    private sealed record PendingStateChange(long ElapsedMs, int MarkerIndex,
        string Change, float X, float Y, float Z);

    public FieldMarkerNetworkCapture(IGameInteropProvider interop,
        Func<(string? MatchUid, long ElapsedMs)> context,
        Action<FieldMarkerCommandObservation> sink, IPluginLog log,
        string diagnosticDirectory, Func<bool> diagnosticsEnabled)
    {
        this.context = context;
        this.sink = sink;
        this.log = log;
        this.diagnosticDirectory = diagnosticDirectory;
        this.diagnosticsEnabled = diagnosticsEnabled;
        try
        {
            var address = (nint)PacketDispatcher.StaticVirtualTablePointer->OnReceivePacket;
            receiveHook = interop.HookFromAddress<PacketDispatcher.Delegates.OnReceivePacket>(
                address, ReceiveDetour);
            receiveHook.Enable();
        }
        catch (Exception exception)
        {
            receiveHook?.Dispose();
            log.Warning(exception,
                "Server field-marker packet capture is unavailable; marker state capture remains active.");
        }
    }

    private void ReceiveDetour(PacketDispatcher* dispatcher, uint packetActorId, nint dispatcherData)
    {
        try
        {
            Capture(packetActorId, dispatcherData);
        }
        catch (Exception exception)
        {
            log.Verbose(exception, "Skipped malformed server field-marker packet candidate.");
        }
        receiveHook!.Original(dispatcher, packetActorId, dispatcherData);
    }

    private void Capture(uint dispatcherActorId, nint dispatcherData)
    {
        var (matchUid, elapsedMs) = context();
        if (matchUid == null || dispatcherData == 0) return;

        // PacketDispatcher receives a pointer 0x10 bytes into the zone header.
        // The decoded payload starts another 0x10 bytes later.
        var packetStart = dispatcherData - 0x10;
        var messageLength = Marshal.ReadInt32(packetStart);
        if (messageLength is < 0x30 or > 0x90) return;
        var payloadLength = messageLength - ZoneHeaderBytes;
        var payload = dispatcherData + 0x10;
        var headerActorId = unchecked((uint)Marshal.ReadInt32(packetStart, 4));
        var actorId = RecordableActor(dispatcherActorId) ? dispatcherActorId : headerActorId;
        if (diagnosticsEnabled()) RecordDiagnostic(payload, payloadLength, elapsedMs,
            dispatcherActorId, headerActorId, actorId);
        if (!RecordableActor(actorId)) return;

        var accepted = payloadLength is 16 or 20
            ? CaptureSingle(payload, payloadLength, elapsedMs, actorId)
            : payloadLength is 100 or 104 && CapturePreset(payload, elapsedMs, actorId);
        _ = accepted;
    }

    private void RecordDiagnostic(nint payload, int payloadLength, long elapsedMs,
        uint dispatcherActorId, uint headerActorId, uint actorId)
    {
        // Raw bytes exist only long enough to hash and inspect aligned entity
        // candidates. Unrelated short packets may contain names or chat text,
        // so raw payloads are never retained in memory or written to disk.
        var raw = new byte[Math.Min(payloadLength, 96)];
        Marshal.Copy(payload, raw, 0, raw.Length);
        var candidates = new List<DiagnosticActorCandidate>();
        for (var offset = 0; offset + 4 <= raw.Length; offset += 4)
        {
            var value = BitConverter.ToUInt32(raw, offset);
            if (LooksLikePlayerEntity(value))
                candidates.Add(new DiagnosticActorCandidate(offset, value));
        }
        var digest = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        lock (diagnosticGate)
        {
            diagnosticRing.Add(new DiagnosticPacket(elapsedMs, payloadLength,
                dispatcherActorId, headerActorId, actorId, digest, candidates));
            if (diagnosticRing.Count > 256) diagnosticRing.RemoveAt(0);
        }
    }

    private bool CaptureSingle(nint payload, int payloadLength, long elapsedMs, uint actorId)
    {
        // Global/modern packets use byte flags; retain the historical CN uint
        // layout as a read-only fallback for old imported client generations.
        var markerFlag = Marshal.ReadByte(payload);
        var statusOffset = 1;
        var coordinateOffset = 4;
        if (!TryMarkerIndex(markerFlag, out var markerIndex) && payloadLength >= 20)
        {
            var wideFlag = unchecked((uint)Marshal.ReadInt32(payload));
            if (!TryMarkerIndex(wideFlag, out markerIndex)) return false;
            statusOffset = 4;
            coordinateOffset = 8;
        }
        var status = Marshal.ReadByte(payload, statusOffset);
        if (status > 1 || coordinateOffset + 12 > payloadLength) return false;
        var x = Marshal.ReadInt32(payload, coordinateOffset) / 1000f;
        var z = Marshal.ReadInt32(payload, coordinateOffset + 4) / 1000f;
        var y = Marshal.ReadInt32(payload, coordinateOffset + 8) / 1000f;
        if (!SaneCoordinate(x, y, z)) return false;
        sink(new FieldMarkerCommandObservation(elapsedMs, actorId, markerIndex,
            status == 0 ? "remove" : "apply", x, y, z,
            "network_field_marker_candidate_unverified"));
        return true;
    }

    private bool CapturePreset(nint payload, long elapsedMs, uint actorId)
    {
        var activeFlags = Marshal.ReadByte(payload);
        var accepted = false;
        for (var index = 0; index < 8; index++)
        {
            var active = (activeFlags & (1 << index)) != 0;
            var x = Marshal.ReadInt32(payload, 4 + index * 4) / 1000f;
            var z = Marshal.ReadInt32(payload, 36 + index * 4) / 1000f;
            var y = Marshal.ReadInt32(payload, 68 + index * 4) / 1000f;
            if (active && !SaneCoordinate(x, y, z)) continue;
            accepted = true;
            sink(new FieldMarkerCommandObservation(elapsedMs, actorId, index,
                active ? "apply" : "remove", x, y, z,
                "network_field_marker_candidate_unverified"));
        }
        return accepted;
    }

    /// <summary>
    /// Freeze only strict marker-shaped packets around an observed state
    /// transition. This is diagnostic evidence for controlled two-player
    /// trials; it never promotes a candidate actor to an operator by itself.
    /// 仅冻结严格标点形状的短窗，供双人对照试验，不直接认定命令者。
    /// </summary>
    public void ObserveStateChange(long elapsedMs, int markerIndex, string change,
        float x, float y, float z)
    {
        if (!diagnosticsEnabled()) return;
        lock (diagnosticGate)
            pendingChanges.Add(new PendingStateChange(elapsedMs, markerIndex, change, x, y, z));
    }

    public void FlushDiagnostics(long elapsedMs)
    {
        if (!diagnosticsEnabled())
        {
            lock (diagnosticGate) { diagnosticRing.Clear(); pendingChanges.Clear(); }
            return;
        }
        List<(PendingStateChange Change, DiagnosticPacket[] Packets)> ready = [];
        lock (diagnosticGate)
        {
            foreach (var change in pendingChanges.Where(value => elapsedMs >= value.ElapsedMs + 250).ToArray())
            {
                ready.Add((change, diagnosticRing.Where(packet =>
                    packet.ElapsedMs >= change.ElapsedMs - 750 &&
                    packet.ElapsedMs <= change.ElapsedMs + 250).ToArray()));
                pendingChanges.Remove(change);
            }
            diagnosticRing.RemoveAll(packet => elapsedMs - packet.ElapsedMs > 2_000);
        }
        if (ready.Count == 0) return;
        try
        {
            Directory.CreateDirectory(diagnosticDirectory);
            PruneDiagnostics();
            var now = DateTime.UtcNow;
            var stem = $"field-marker-diagnostic-{now:yyyyMMdd}";
            var path = Path.Combine(diagnosticDirectory, stem + ".jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= MaximumDiagnosticBytes)
            {
                var suffix = 1;
                do path = Path.Combine(diagnosticDirectory, $"{stem}-{suffix++:00}.jsonl");
                while (File.Exists(path) && new FileInfo(path).Length >= MaximumDiagnosticBytes);
            }
            foreach (var row in ready)
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    schema = "battlefield-field-marker-diagnostic/v2",
                    generatedAtUtc = DateTime.UtcNow,
                    stateChange = row.Change,
                    candidatePackets = row.Packets,
                    conclusion = "operator_unknown_pending_controlled_validation"
                }) + Environment.NewLine);
        }
        catch (Exception exception)
        {
            log.Verbose(exception, "Could not write bounded field-marker diagnostics.");
        }
    }

    private void PruneDiagnostics()
    {
        var cutoff = DateTime.UtcNow.AddDays(-DiagnosticRetentionDays);
        foreach (var path in Directory.EnumerateFiles(diagnosticDirectory,
                     "field-marker-diagnostic-*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TryMarkerIndex(uint flag, out int index)
    {
        index = flag switch
        {
            0x01 => 0, 0x02 => 1, 0x04 => 2, 0x08 => 3,
            0x10 => 4, 0x20 => 5, 0x40 => 6, 0x80 => 7,
            _ => -1,
        };
        return index >= 0;
    }

    private static bool SaneCoordinate(float x, float y, float z) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) &&
        MathF.Abs(x) <= 1000 && MathF.Abs(y) <= 1000 && MathF.Abs(z) <= 1000;

    private static bool RecordableActor(uint id) => id != 0 && id != 0xE0000000u;

    private static bool LooksLikePlayerEntity(uint id) =>
        (id & 0xF0000000u) == 0x10000000u;

    public void Dispose() => receiveHook?.Dispose();
}
