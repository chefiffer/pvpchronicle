using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BattlefieldFrontlineCollector;

internal sealed record CrystallineMatchRecord(
    DateTime EndedAtUtc,
    string PlayerName,
    ushort WorldId,
    string MapKey,
    string MatchType,
    bool Won,
    byte ClassJobId,
    ushort MatchLengthSeconds,
    byte TierBefore,
    byte TierAfter,
    byte RiserBefore,
    byte RiserAfter,
    byte StarsBefore,
    byte StarsAfter,
    ushort CreditBefore,
    ushort CreditAfter,
    uint AstraProgress = 0,
    uint UmbraProgress = 0,
    IReadOnlyList<CrystallinePlayerResult>? Players = null,
    string MatchUid = "",
    double? OverallLogs = null,
    string Outcome = "",
    byte LocalKills = 0,
    byte LocalDeaths = 0,
    byte LocalAssists = 0,
    string EvidenceSource = "result_packet_verified")
{
    // Older JSON rows only contain Won. New rows preserve the result packet's
    // third state so draws are excluded from the official win-rate denominator.
    public string EffectiveOutcome => Outcome is "win" or "loss" or "draw"
        ? Outcome : (Won ? "win" : "loss");

    public bool HasCompleteRoster => CrystallineRosterValidation.IsComplete(Players);

    public (byte Kills, byte Deaths, byte Assists) LocalKda
    {
        get
        {
            var local = Players?.FirstOrDefault(player => player.WorldId == WorldId &&
                string.Equals(player.Name, PlayerName, StringComparison.OrdinalIgnoreCase));
            return local == null ? (LocalKills, LocalDeaths, LocalAssists) :
                (local.Kills, local.Deaths, local.Assists);
        }
    }
}

internal static class CrystallineRosterValidation
{
    internal static bool IsComplete(IReadOnlyList<CrystallinePlayerResult>? players)
    {
        if (players == null || players.Count != 10) return false;
        if (players.Any(player => string.IsNullOrWhiteSpace(player.Name) || player.WorldId == 0 ||
                                  player.ClassJobId == 0 || player.Team > 1)) return false;
        if (players.Select(player => (player.Name, player.WorldId)).Distinct().Count() != 10)
            return false;
        return players.Count(player => player.Team == 0) == 5 &&
               players.Count(player => player.Team == 1) == 5;
    }
}

/// <summary>
/// Authoritative ten-player row copied from the Crystalline Conflict result packet.
/// Scores stay nullable until a versioned server-side CC population model exists.
/// 水晶冲突结算包中的十人权威行；总体样本模型建立前，Logs 必须保持空值而不是伪造 0 分。
/// </summary>
internal sealed record CrystallinePlayerResult(
    string Name,
    ushort WorldId,
    byte ClassJobId,
    byte Team,
    byte Kills,
    byte Deaths,
    byte Assists,
    int DamageDealt,
    int DamageTaken,
    int HpRestored,
    ushort TimeOnCrystalSeconds,
    byte RankId,
    double? KillLogs = null,
    double? DeathLogs = null,
    double? AssistLogs = null,
    double? DamageLogs = null,
    double? HealingLogs = null,
    double? CrystalLogs = null,
    double? OverallLogs = null);

/// <summary>
/// Local-only CC result history.  A row is written only from the authoritative
/// result packet; rank-profile polling is never used to infer a win or a map.
/// 水晶冲突本地战绩只接受结算包，绝不从段位升降反推胜负或地图。
/// </summary>
internal sealed class CrystallineMatchHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly object gate = new();
    private readonly string path;
    private readonly List<CrystallineMatchRecord> matches = [];

    public CrystallineMatchHistory(string path)
    {
        this.path = path;
        try
        {
            if (File.Exists(path))
                matches.AddRange(JsonSerializer.Deserialize<List<CrystallineMatchRecord>>(
                    File.ReadAllText(path), JsonOptions) ?? []);
        }
        catch
        {
            // Preserve a damaged file for manual recovery and continue empty.
        }
    }

    public IReadOnlyList<CrystallineMatchRecord> Snapshot(string playerName, ushort worldId)
    {
        lock (gate)
            return matches.Where(value => value.WorldId == worldId &&
                    string.Equals(value.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.EndedAtUtc).ToArray();
    }

    public void Observe(CrystallineMatchRecord value)
    {
        if (string.IsNullOrWhiteSpace(value.PlayerName) || value.WorldId == 0 ||
            string.IsNullOrWhiteSpace(value.MapKey) || value.MatchType is not ("casual" or "ranked") ||
            value.MatchLengthSeconds == 0)
            return;
        value = value with { EndedAtUtc = DateTime.SpecifyKind(value.EndedAtUtc, DateTimeKind.Utc) };
        lock (gate)
        {
            var duplicateIndex = !string.IsNullOrWhiteSpace(value.MatchUid)
                ? matches.FindIndex(item => string.Equals(item.MatchUid, value.MatchUid,
                    StringComparison.OrdinalIgnoreCase))
                : -1;
            if (duplicateIndex < 0)
                duplicateIndex = matches.FindIndex(item => item.WorldId == value.WorldId &&
                string.Equals(item.PlayerName, value.PlayerName, StringComparison.OrdinalIgnoreCase) &&
                item.MapKey == value.MapKey && item.MatchType == value.MatchType &&
                item.ClassJobId == value.ClassJobId &&
                item.EffectiveOutcome == value.EffectiveOutcome &&
                item.MatchLengthSeconds == value.MatchLengthSeconds &&
                Math.Abs((item.EndedAtUtc - value.EndedAtUtc).TotalSeconds) < 20);
            if (duplicateIndex >= 0)
            {
                var existing = matches[duplicateIndex];
                if (existing.LocalKda != value.LocalKda)
                    AuditLegacyConflict(existing, value, "kda_mismatch_same_canonical_match");
                if (value.EvidenceSource == "result_packet_verified" &&
                    existing.EvidenceSource != "result_packet_verified")
                {
                    matches[duplicateIndex] = value;
                    matches.Sort((left, right) => left.EndedAtUtc.CompareTo(right.EndedAtUtc));
                    Persist();
                }
                return;
            }
            matches.Add(value);
            matches.Sort((left, right) => left.EndedAtUtc.CompareTo(right.EndedAtUtc));
            Persist();
        }
    }

    /// <summary>
    /// Import result-only legacy summaries. The file deliberately cannot carry
    /// replay events or a ten-player roster, so imported rows affect only
    /// historical wins, KDA and job usage.
    /// 旧资料只导入胜负、KDA 与职业，不允许伪装成可复盘战局。
    /// </summary>
    public int ImportLegacySummaries(string importPath)
    {
        if (!File.Exists(importPath)) return 0;
        LegacyCrystallineSummaryEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<LegacyCrystallineSummaryEnvelope>(
                File.ReadAllText(importPath), JsonOptions);
        }
        catch { return 0; }
        if (envelope?.Schema != "battlefield-crystalline-history-summary/v1") return 0;
        var before = matches.Count;
        foreach (var row in envelope.Matches ?? [])
        {
            if (row.Outcome is not ("win" or "loss" or "draw")) continue;
            Observe(new CrystallineMatchRecord(row.EndedAtUtc, row.PlayerName, row.WorldId,
                row.MapKey, row.MatchType, row.Outcome == "win", row.ClassJobId,
                row.MatchLengthSeconds, 0, 0, 0, 0, 0, 0,
                CreditBefore: 0, CreditAfter: 0,
                MatchUid: row.MatchUid, Outcome: row.Outcome,
                LocalKills: row.Kills, LocalDeaths: row.Deaths,
                LocalAssists: row.Assists, EvidenceSource: LegacyEvidenceSource(envelope.Source)));
        }
        return Math.Max(0, matches.Count - before);
    }

    private static string LegacyEvidenceSource(string? source) =>
        source?.Contains("ccstats", StringComparison.OrdinalIgnoreCase) == true
            ? "ccstats_history_summary"
            : source?.Contains("act", StringComparison.OrdinalIgnoreCase) == true
                ? "act_history_summary"
                : "legacy_history_summary";

    private void AuditLegacyConflict(CrystallineMatchRecord existing,
        CrystallineMatchRecord incoming, string reason)
    {
        var auditPath = path + ".conflicts.jsonl";
        Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
        File.AppendAllText(auditPath, JsonSerializer.Serialize(new
        {
            schema = "battlefield-crystalline-history-conflict/v1",
            observedAtUtc = DateTime.UtcNow,
            reason,
            matchUid = !string.IsNullOrWhiteSpace(existing.MatchUid)
                ? existing.MatchUid : incoming.MatchUid,
            playerName = incoming.PlayerName,
            worldId = incoming.WorldId,
            existing = new { existing.EvidenceSource, kda = existing.LocalKda },
            incoming = new { incoming.EvidenceSource, kda = incoming.LocalKda },
            resolution = incoming.EvidenceSource == "result_packet_verified"
                ? "native_result_replaced_legacy_summary"
                : "incoming_summary_rejected_no_overwrite"
        }, JsonOptions) + Environment.NewLine);
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        File.WriteAllText(temporary, JsonSerializer.Serialize(matches, JsonOptions));
        File.Move(temporary, path, true);
    }
}

internal sealed record LegacyCrystallineSummaryEnvelope(string Schema,
    IReadOnlyList<LegacyCrystallineSummary>? Matches, string? Source = null);

internal sealed record LegacyCrystallineSummary(DateTime EndedAtUtc, string PlayerName,
    ushort WorldId, string MapKey, string MatchType, string Outcome, byte ClassJobId,
    ushort MatchLengthSeconds, byte Kills, byte Deaths, byte Assists, string MatchUid = "");
