using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BattlefieldFrontlineCollector;

internal sealed record CrystallineRankObservation(
    DateTime ObservedAtUtc,
    string PlayerName,
    ushort WorldId,
    byte Tier,
    byte Riser,
    byte Stars,
    ushort Credit,
    string Source)
{
    private static readonly int[] Divisions = [1, 3, 3, 4, 4, 5, 1, 1, 1];

    /// <summary>A monotone display coordinate; it is not an inferred rank.</summary>
    public double PlotValue
    {
        get
        {
            var tier = Math.Clamp((int)Tier, 0, 8);
            if (tier == 0) return 0;
            if (tier >= 6)
                return tier + Math.Clamp(Credit / 10000d, 0, .85);
            var divisions = Divisions[tier];
            var riser = Math.Clamp((int)Riser, 1, divisions);
            var within = ((divisions - riser) * 3 + Math.Clamp((int)Stars, 0, 3)) /
                         (double)(divisions * 3);
            return tier + Math.Clamp(within, 0, .95);
        }
    }
}

/// <summary>
/// Local, evidence-preserving history of the player's own ranked CC state.
/// Only game profile/result values are stored; wins never infer a rank.
/// </summary>
internal sealed class CrystallineRankHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly object gate = new();
    private readonly string path;
    private readonly List<CrystallineRankObservation> observations = [];

    public CrystallineRankHistory(string path)
    {
        this.path = path;
        try
        {
            if (File.Exists(path))
                observations.AddRange(JsonSerializer.Deserialize<List<CrystallineRankObservation>>(
                    File.ReadAllText(path), JsonOptions) ?? []);
        }
        catch
        {
            // Keep the damaged file for manual recovery; start a new in-memory history.
        }
    }

    public IReadOnlyList<CrystallineRankObservation> Snapshot(string playerName, ushort worldId)
    {
        lock (gate)
            return observations.Where(value => value.WorldId == worldId &&
                    string.Equals(value.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.ObservedAtUtc).ToArray();
    }

    public void Observe(CrystallineRankObservation value)
    {
        if (string.IsNullOrWhiteSpace(value.PlayerName) || value.WorldId == 0 || value.Tier > 8)
            return;
        lock (gate)
        {
            var previous = observations.LastOrDefault(item => item.WorldId == value.WorldId &&
                string.Equals(item.PlayerName, value.PlayerName, StringComparison.OrdinalIgnoreCase));
            if (previous != null && previous.Tier == value.Tier && previous.Riser == value.Riser &&
                previous.Stars == value.Stars && previous.Credit == value.Credit)
                return;
            observations.Add(value with { ObservedAtUtc = DateTime.SpecifyKind(value.ObservedAtUtc, DateTimeKind.Utc) });
            observations.Sort((left, right) => left.ObservedAtUtc.CompareTo(right.ObservedAtUtc));
            Persist();
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        File.WriteAllText(temporary, JsonSerializer.Serialize(observations, JsonOptions));
        File.Move(temporary, path, true);
    }
}
