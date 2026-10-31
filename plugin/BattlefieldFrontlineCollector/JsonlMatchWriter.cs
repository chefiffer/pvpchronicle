using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Serializes match events on a background thread so disk latency never blocks Framework.Update.
/// 在后台线程顺序写盘，避免磁盘延迟阻塞游戏更新线程。
/// </summary>
internal sealed class JsonlMatchWriter : IDisposable
{
    private abstract record Command;
    private sealed record StartCommand(string Path, object Header, TaskCompletionSource<bool> Done) : Command;
    private sealed record ValueCommand(object Value) : Command;
    private sealed record EndCommand(object Footer, TaskCompletionSource<bool> Done) : Command;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Channel<Command> commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(8192)
    {
        // TryWrite must report a full queue so the capture can count dropped
        // optional samples without ever blocking the game framework thread.
        // TryWrite 必须在队列已满时返回 false，才能准确记录被舍弃的可选采样。
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly string directory;
    private readonly string assetRoot;
    private readonly Task worker;
    private volatile int droppedEvents;

    public string? CurrentPath { get; private set; }
    public string? LastCompactPath { get; private set; }
    public Exception? LastCompactError { get; private set; }
    public int DroppedEvents => droppedEvents;

    public JsonlMatchWriter(string directory, string assetRoot)
    {
        this.directory = directory;
        this.assetRoot = assetRoot;
        Directory.CreateDirectory(directory);
        worker = Task.Run(WorkerAsync);
    }

    public void Start(string matchUid, uint dutyId, uint territoryId, string localPlayerName, string realm,
        string mode)
    {
        Interlocked.Exchange(ref droppedEvents, 0);
        LastCompactPath = null; LastCompactError = null;
        var modeDirectory = Path.Combine(directory, mode switch
        {
            "crystalline_conflict" => "crystalline-conflict",
            "rival_wings" => "rival-wings",
            _ => "frontline"
        });
        Directory.CreateDirectory(modeDirectory);
        CurrentPath = Path.Combine(modeDirectory, $"{mode}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{matchUid}.jsonl");
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var header = new { schema = "battlefield-frontline-collector/v2", @event = "match_start", matchUid,
            observedAtUtc = DateTime.UtcNow, dutyId, territoryId, localPlayerName, realm,
            layoutVersion = GameLayout.LayoutVersion, captureVersion = "0.8.93",
            partyKeySource = "result_packet_verified",
            captureFeatures = new[] { "action_effect", "status_change", "periodic_effect", "death_event",
                "coordination_chat_summary", "target_marker", "field_marker", "marker_state_diff_100ms",
                "network_target_marker_actor_target_v1", "local_target_marker_command_pairing",
                "network_target_marker_state_pairing",
                "local_field_marker_command_pairing",
                "network_field_marker_payload_pairing_v1",
                "marker_operator_fail_closed", "field_marker_nearby_actor_candidate",
                "target_marker_object_metadata",
                "persistent_actor_owner_cache",
                "exact_action_positions", "movement_anomaly_screening_250ms",
                "movement_anomaly_exact_path", "movement_speed_histogram_250ms",
                "action_category_recast_metadata", "action_metadata_lumina_v2",
                "cast_start_observed_100ms", "forced_movement_target_trajectory_250ms",
                "validated_gcd_weave_windows", "objective_object_discovery_500ms",
                "objective_event_map_markers_500ms", "objective_action_target_position",
                "objective_global_map_end_timestamp", "objective_ui_number_arrays_delta_500ms",
                "objective_hp_range_freshness_gate_190y",
                "cc_mechanism_object_catalog_v1", "cc_mechanism_source_actions",
                "cc_micro_decision_raw_observations",
                "local_use_action_read_only_v2", "local_action_state_read_only_50ms",
                "local_cast_continuity", "local_aftercast_movement_onset" } };
        if (!commands.Writer.TryWrite(new StartCommand(CurrentPath, header, done)))
            throw new IOException("Capture writer queue is unavailable.");
        // Do not wait for disk open on the framework thread. Channel ordering
        // guarantees that subsequent samples cannot overtake this header.
        _ = done.Task.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public void Write(object value)
    {
        if (!commands.Writer.TryWrite(new ValueCommand(value))) Interlocked.Increment(ref droppedEvents);
    }

    public async Task CompleteAsync(object finalValue, string matchUid, string reason)
    {
        // Results are critical and wait for queue capacity on a worker thread;
        // periodic score/vitals samples remain best-effort and non-blocking.
        await commands.Writer.WriteAsync(new ValueCommand(finalValue));
        await EndAsync(matchUid, reason);
    }

    public async Task EndAsync(string matchUid, string reason)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var footer = new { schema = "battlefield-frontline-collector/v2", @event = "match_end", matchUid,
            observedAtUtc = DateTime.UtcNow, reason, droppedEvents };
        await commands.Writer.WriteAsync(new EndCommand(footer, done));
        await done.Task;
    }

    private async Task WorkerAsync()
    {
        FileStream? writer = null;
        BfcapCodec.StreamingEncoder? compact = null;
        long rawBytesSinceFlush = 0;
        async Task WriteLineAsync(object value)
        {
            if (writer == null) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            await writer.WriteAsync(bytes);
            await writer.WriteAsync(new byte[] { (byte)'\n' });
            rawBytesSinceFlush += bytes.Length + 1L;
            if (rawBytesSinceFlush >= 4L * 1024 * 1024)
            {
                // Bound crash-recovery loss without placing synchronous flushes
                // on Framework.Update. Finalization still performs Flush(true).
                await writer.FlushAsync(); rawBytesSinceFlush = 0;
            }
            if (compact != null)
            {
                try { compact.AppendLine(bytes); }
                catch (Exception ex)
                {
                    LastCompactError = ex;
                    compact.Dispose(); compact = null;
                }
            }
        }
        await foreach (var command in commands.Reader.ReadAllAsync())
        {
            try
            {
                switch (command)
                {
                    case StartCommand start:
                        writer?.Dispose();
                        compact?.Dispose(); compact = null;
                        writer = new FileStream(start.Path, FileMode.CreateNew, FileAccess.Write,
                            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        rawBytesSinceFlush = 0;
                        try { compact = BfcapCodec.BeginStreaming(start.Path, assetRoot, 3); }
                        catch (Exception ex) { LastCompactError = ex; }
                        await WriteLineAsync(start.Header);
                        await writer.FlushAsync();
                        start.Done.SetResult(true);
                        break;
                    case ValueCommand value when writer != null:
                        await WriteLineAsync(value.Value);
                        break;
                    case EndCommand end:
                        if (writer != null)
                        {
                            await WriteLineAsync(end.Footer);
                            await writer.FlushAsync(); writer.Flush(true);
                            if (compact != null)
                            {
                                try { LastCompactPath = compact.Complete(); }
                                catch (Exception ex) { LastCompactError = ex; }
                                finally { compact.Dispose(); compact = null; }
                            }
                            writer.Dispose(); writer = null;
                        }
                        end.Done.SetResult(true);
                        break;
                }
            }
            catch (Exception ex)
            {
                if (command is StartCommand start) start.Done.TrySetException(ex);
                if (command is EndCommand end) end.Done.TrySetException(ex);
            }
        }
        compact?.Dispose(); writer?.Dispose();
    }

    public void Dispose()
    {
        commands.Writer.TryComplete();
        try { worker.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
