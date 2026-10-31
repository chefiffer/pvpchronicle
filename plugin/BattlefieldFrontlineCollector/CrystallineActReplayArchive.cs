using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BattlefieldFrontlineCollector;

internal sealed record CrystallineActReplayPlayer(
    string ActorId, string Name, ushort WorldId, byte ClassJobId,
    long Damage, long DamageTaken, long Healing, long EffectiveHealing,
    long Overheal, int Kills, int Deaths, int Assists);

internal sealed record CrystallineActReplayAction(
    string ActorId, string ActionId, string ActionName, int Uses,
    long Damage, long RawHealing, long EffectiveHealing);

internal sealed record CrystallineActReplayBucket(
    string ActorId, int ElapsedSeconds, long Damage, long DamageTaken,
    long Healing, int Kills, int Deaths);

internal sealed record CrystallineActReplayHit(
    long ElapsedMs, string Source, string ActionId, string ActionName, long Amount);

internal sealed record CrystallineActReplayDeath(
    long ElapsedMs, string Victim, string Killer, IReadOnlyList<CrystallineActReplayHit> RecentDamage);

internal sealed record CrystallineActReplay(
    string MatchUid, string Realm, string Source, string Evidence, string? Winner,
    string QueueType, string ZoneId, string MapKey, string MapName,
    DateTime StartedAtUtc, DateTime EndedAtUtc, int DurationSeconds,
    string LocalPlayerName, string LocalPlayerActorId, int VisiblePlayerCount,
    int AbilityEvents, IReadOnlyList<CrystallineActReplayPlayer> Players,
    IReadOnlyList<CrystallineActReplayAction> Actions,
    IReadOnlyList<CrystallineActReplayBucket> Buckets,
    IReadOnlyList<CrystallineActReplayDeath> Deaths);

internal sealed record CrystallineActReplayEnvelope(
    string Schema, DateTime GeneratedAtUtc, IReadOnlyList<CrystallineActReplay> Matches);

/// <summary>
/// Read-only compact replays exported from old ACT network logs.  They are kept
/// separate from authoritative in-plugin result packets because winner, queue
/// type and full-map visibility are not guaranteed in historical ACT evidence.
/// </summary>
internal sealed class CrystallineActReplayArchive
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string path;
    private IReadOnlyList<CrystallineActReplay> matches = Array.Empty<CrystallineActReplay>();

    public CrystallineActReplayArchive(string path)
    {
        this.path = path;
        // ACT combat replays are no longer part of the CC page. Keep this
        // legacy reader lazy so an old multi-megabyte archive is not loaded at
        // plugin startup; only explicit developer tooling can call Reload().
        // ACT 战斗复盘已退出正式界面，启动时不得再读取旧大文件。
    }

    public string Path => path;
    public IReadOnlyList<CrystallineActReplay> Snapshot() => matches;

    public void Reload()
    {
        try
        {
            if (!File.Exists(path)) { matches = Array.Empty<CrystallineActReplay>(); return; }
            var envelope = JsonSerializer.Deserialize<CrystallineActReplayEnvelope>(
                File.ReadAllText(path), JsonOptions);
            if (envelope?.Schema != "battlefield-crystalline-act-replay/v2")
            {
                matches = Array.Empty<CrystallineActReplay>();
                return;
            }
            matches = envelope.Matches
                .Where(value => !string.IsNullOrWhiteSpace(value.MatchUid) &&
                    value.DurationSeconds > 0 && value.Players != null && value.Actions != null &&
                    value.Buckets != null && value.Deaths != null)
                .OrderBy(value => value.StartedAtUtc).ToArray();
        }
        catch
        {
            matches = Array.Empty<CrystallineActReplay>();
        }
    }
}
