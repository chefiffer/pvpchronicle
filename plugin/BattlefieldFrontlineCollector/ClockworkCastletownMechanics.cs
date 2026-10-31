using System;
using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Evidence-bounded geometry and action identifiers for the Clockwork Castletown.
/// The replay promotes only objects and effects cross-checked between the
/// official mechanic guide and real territory 1116/1117 captures.
/// </summary>
internal static class ClockworkCastletownMechanics
{
    internal const uint TrickFloorBaseId = 8062;
    internal const uint KageyamaBaseId = 15774;
    internal const uint ClockworkYojimboBaseId = 15775;
    internal const uint ClockworkOnmyojiBaseId = 15776;

    // Verified in two territory 1116 captures recorded on 2026-10-30.
    // These are effect observations, not cast-start or button-press times.
    internal const uint TatamiTwistActionId = 32364;
    internal const uint YojimboStrikeActionId = 32365;
    internal const uint OnmyojiGazeActionId = 32366;
    internal const long ReplayEvidenceWindowMs = 1_800;
    internal const double TatamiTileSizeYalms = 10d;

    internal enum HazardKind
    {
        None,
        TatamiTwist,
        YojimboStrike,
        OnmyojiGaze
    }

    internal readonly record struct TatamiCell(uint ObjectId, double X, double Z);
    internal sealed record TatamiCross(TatamiCell Center, IReadOnlyList<TatamiCell> Cells);

    internal static bool IsTerritory(uint territoryId) => territoryId is 1116 or 1117;

    internal static HazardKind ClassifyAction(uint actionId) => actionId switch
    {
        TatamiTwistActionId => HazardKind.TatamiTwist,
        YojimboStrikeActionId => HazardKind.YojimboStrike,
        OnmyojiGazeActionId => HazardKind.OnmyojiGaze,
        _ => HazardKind.None
    };

    /// <summary>
    /// Returns replay opacity for a directly observed effect packet.
    /// Input and output are deterministic and have no side effects.
    /// </summary>
    internal static float EvidenceAlpha(long actionElapsedMs, long selectedElapsedMs)
    {
        var age = Math.Abs(selectedElapsedMs - actionElapsedMs);
        if (age > ReplayEvidenceWindowMs) return 0f;
        return (float)Math.Clamp(1d - age / (double)ReplayEvidenceWindowMs, .18d, 1d);
    }

    /// <summary>
    /// Kageyama is outside the playable arena and the trick-floor squares have
    /// their own evidence renderer. Neither should fall through to the generic
    /// circular objective glyph.
    /// </summary>
    internal static bool UseGenericMapGlyph(string? label) => label switch
    {
        "cc_kageyama" or "cc_trick_floor" or "Kageyama" or "Trick Floor" => false,
        _ => true
    };

    /// <summary>
    /// Clockwork parade actors and trick floors are invulnerable mechanics.
    /// Their client-side 100/100 placeholder is not a combat HP contract.
    /// </summary>
    internal static bool HasMeaningfulHealth(string? label) => label switch
    {
        "cc_kageyama" or "cc_trick_floor" or
        "cc_clockwork_yojimbo" or "cc_clockwork_onmyoji" or
        "Kageyama" or "Trick Floor" or
        "Clockwork Yojimbo" or "Clockwork Onmyoji" => false,
        _ => true
    };

    /// <summary>
    /// Reconstructs a five-tile cross only when all five equally sized floor
    /// objects were directly observed. Partial data is withheld rather than
    /// replaced by an invented green circle.
    /// </summary>
    internal static IReadOnlyList<TatamiCross> FindCompleteTatamiCrosses(
        IEnumerable<TatamiCell> observations)
    {
        const double tolerance = 1.25d;
        var cells = observations
            .GroupBy(value => ((int)Math.Round(value.X * 2), (int)Math.Round(value.Z * 2)))
            .Select(group => group.First()).ToArray();
        var result = new List<TatamiCross>();
        foreach (var center in cells)
        {
            var members = new[]
            {
                Find(center.X, center.Z),
                Find(center.X - TatamiTileSizeYalms, center.Z),
                Find(center.X + TatamiTileSizeYalms, center.Z),
                Find(center.X, center.Z - TatamiTileSizeYalms),
                Find(center.X, center.Z + TatamiTileSizeYalms)
            };
            if (members.Any(value => value is null)) continue;
            var resolved = members.Select(value => value!.Value).ToArray();
            if (result.Any(existing => existing.Cells.Select(value => value.ObjectId)
                    .Order().SequenceEqual(resolved.Select(value => value.ObjectId).Order()))) continue;
            result.Add(new TatamiCross(center, resolved));
        }
        return result;

        TatamiCell? Find(double x, double z)
        {
            foreach (var value in cells)
                if (Math.Abs(value.X - x) <= tolerance && Math.Abs(value.Z - z) <= tolerance)
                    return value;
            return null;
        }
    }

    internal static double ObservedHitRadius(double sourceX, double sourceZ,
        IEnumerable<(double X, double Z)> targets) => targets
        .Select(value => Math.Sqrt(Math.Pow(value.X - sourceX, 2) + Math.Pow(value.Z - sourceZ, 2)))
        .DefaultIfEmpty(0d).Max();
}
