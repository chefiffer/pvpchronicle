using System;
using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Resolves the observer's Crystalline Conflict side without inventing one.
/// The local-player name is captured directly from Dalamud's LocalPlayer.
/// Result rows and observer-loaded movement frames may both attest its team;
/// every usable attestation must agree on Astra (0) or Umbra (1).
/// </summary>
internal static class CrystallineTeamIdentity
{
    public static byte? Resolve(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        if (match.Schema != LocalMatchAnalyzer.CrystallineSchema ||
            string.IsNullOrWhiteSpace(match.LocalPlayerName)) return null;

        var localName = match.LocalPlayerName.Split('@', 2,
            StringSplitOptions.TrimEntries)[0];
        if (string.IsNullOrWhiteSpace(localName)) return null;

        var teams = new HashSet<byte>();
        foreach (var player in match.Players.Where(player =>
                     player.Team <= 1 && string.Equals(player.Name, localName,
                         StringComparison.OrdinalIgnoreCase)))
            teams.Add(player.Team);

        foreach (var actor in (detail.MovementTimeline ?? [])
                     .SelectMany(frame => frame.Actors ?? [])
                     .Where(actor => actor.Team <= 1 && string.Equals(actor.Name, localName,
                         StringComparison.OrdinalIgnoreCase)))
            teams.Add(actor.Team);

        return teams.Count == 1 ? teams.Single() : null;
    }
}
