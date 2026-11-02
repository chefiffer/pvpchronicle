using System;
using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Versioned map-UI evidence for Worqor Chirteh (duty 1080).
/// The global map icon proves a strategic point's state and owning faction.
/// It does not contain player identities; names may only be associated from
/// observer-loaded movement samples near the same point.
/// </summary>
internal static class WorqorObjectiveCatalog
{
    public const uint DutyId = 1080;
    public const uint TerritoryId = 1313;
    public const string EvidenceScope = "worqor-global-map-objectives-2026-11-v1";
    // Disabled after repeated production captures proved that neither the
    // rendered helper state nor ActorControl 0x199 exposes reliable sub-pad
    // ownership. Whole-point ownership and countdowns remain enabled.
    public static readonly bool PerFieldOwnershipEnabled = false;
    public const uint FieldVisualCandidateBaseId = 2_007_457;
    public const string FieldStateLabel = "worqor_field_state";
    public const string FieldStateScope = "worqor-field-render-animation-2026-11-v3";
    public const string LegacyFieldStateScope = "worqor-field-shared-group-stain-2026-11-v2";
    public const byte NeutralFieldOwner = 255;
    public const byte UnknownFieldOwner = 254;

    /// <summary>
    /// Exact per-field ownership derived only from individually observed field
    /// states. Missing fields remain unknown; they must never be silently
    /// converted to neutral fields.
    /// </summary>
    internal readonly record struct FieldOwnershipBreakdown(
        int Maelstrom, int Adders, int Flames, int Neutral, int Unknown, int Total);

    internal readonly record struct FieldOwnershipSample(double X, double Z, byte Owner);

    public static FieldOwnershipBreakdown CountObservedFields(int total,
        IEnumerable<byte> observedOwners)
    {
        if (total is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(total));
        var owners = observedOwners.Take(total).ToArray();
        var maelstrom = owners.Count(owner => owner == 0);
        var adders = owners.Count(owner => owner == 1);
        var flames = owners.Count(owner => owner == 2);
        var neutral = owners.Count(owner => owner == NeutralFieldOwner);
        var explicitUnknown = owners.Count(owner => owner == UnknownFieldOwner || owner > 2 && owner != NeutralFieldOwner);
        var unknown = explicitUnknown + Math.Max(0, total - owners.Length);
        return new FieldOwnershipBreakdown(maelstrom, adders, flames, neutral, unknown, total);
    }

    /// <summary>
    /// Collapses the several visual helper objects that the client places at
    /// one physical field into one ownership observation.  Without this step a
    /// single pad can occupy several numerator slots while another pad is
    /// omitted. Conflicting colours at the same position remain unknown.
    /// </summary>
    public static FieldOwnershipBreakdown CountObservedFieldSamples(int total,
        IEnumerable<FieldOwnershipSample> samples)
    {
        if (total is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(total));
        var clusters = new List<(double X, double Z, HashSet<byte> Owners)>();
        foreach (var sample in samples)
        {
            var index = clusters.FindIndex(cluster =>
            {
                var dx = cluster.X - sample.X;
                var dz = cluster.Z - sample.Z;
                return dx * dx + dz * dz <= .75d * .75d;
            });
            if (index < 0)
            {
                clusters.Add((sample.X, sample.Z, [sample.Owner]));
                continue;
            }
            clusters[index].Owners.Add(sample.Owner);
        }

        var owners = clusters.Take(total).Select(cluster =>
        {
            var known = cluster.Owners.Where(owner => owner <= 2 || owner == NeutralFieldOwner)
                .Distinct().ToArray();
            return known.Length == 1 ? known[0] : UnknownFieldOwner;
        });
        return CountObservedFields(total, owners);
    }

    public static bool IsFieldState(string? label, string? evidenceScope) =>
        string.Equals(label, FieldStateLabel, StringComparison.Ordinal) &&
        (string.Equals(evidenceScope, FieldStateScope, StringComparison.Ordinal) ||
         string.Equals(evidenceScope, LegacyFieldStateScope, StringComparison.Ordinal));

    /// <summary>
    /// Legacy helper retained only for reading old probes. Stain index zero is
    /// the layout engine's "default/no stain" sentinel, not proof that a field
    /// is neutral. It must therefore remain unknown.
    /// </summary>
    public static byte? OwnerFromStain(byte stainIndex, byte red, byte green, byte blue)
    {
        if (stainIndex == 0) return null;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        if (maximum < 48 || maximum - minimum < 28) return null;
        if (red >= green * 1.22 && red >= blue * 1.22) return 0;
        if (red >= blue * 1.28 && green >= blue * 1.20 && Math.Abs(red - green) <= 92) return 1;
        if (blue >= red * 1.18 && blue >= green * 1.12) return 2;
        return null;
    }

    /// <summary>
    /// Maps the colour actually returned by the active layout renderer. The
    /// renderer uses normalized linear components. Black, transparent and
    /// weak/desaturated values are diagnostic only and never become neutral.
    /// Neutral ownership requires its own explicit animation state; it is not
    /// inferred from the absence of a faction tint.
    /// </summary>
    public static byte? OwnerFromRenderColor(float red, float green, float blue, float alpha)
    {
        if (!float.IsFinite(red) || !float.IsFinite(green) ||
            !float.IsFinite(blue) || !float.IsFinite(alpha) || alpha < .05f)
            return null;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        if (maximum < .08f || maximum - minimum < .10f) return null;
        if (red >= green * 1.18f && red >= blue * 1.18f) return 0;
        if (red >= blue * 1.22f && green >= blue * 1.15f && Math.Abs(red - green) <= .40f)
            return 1;
        if (blue >= red * 1.14f && blue >= green * 1.08f) return 2;
        return null;
    }

    public static bool IsFieldActiveState(string? state) =>
        !string.IsNullOrWhiteSpace(state) &&
        state.StartsWith("active:", StringComparison.Ordinal);

    public static bool IsLegacyFieldState(string? evidenceScope) =>
        string.Equals(evidenceScope, LegacyFieldStateScope, StringComparison.Ordinal);

    public static bool IsCurrentFieldState(string? evidenceScope) =>
        string.Equals(evidenceScope, FieldStateScope, StringComparison.Ordinal);

    public static bool IsFieldObservedState(string? state) =>
        !string.IsNullOrWhiteSpace(state) &&
        (state.StartsWith("active:", StringComparison.Ordinal) ||
         state.StartsWith("observed:", StringComparison.Ordinal));

    public static bool TryGetSharedTimelineState(string? state, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(state)) return false;
        const string token = "sharedTimeline=";
        var start = state.IndexOf(token, StringComparison.Ordinal);
        if (start < 0) return false;
        start += token.Length;
        var end = start;
        while (end < state.Length && char.IsDigit(state[end])) end++;
        return end > start && ushort.TryParse(state[start..end], out value);
    }

    public static bool IsCountdownIcon(uint iconId) => iconId is
        60_620 or 60_621 or 60_622 or
        60_624 or 60_625 or 60_626 or
        60_628 or 60_629 or 60_630;

    // This radius describes the observer-bounded review area around a point.
    // It is not a claim about the exact collision geometry of the three pads.
    public const double ObservedOccupantRadiusYalms = 15d;

    /// <summary>
    /// Strict allow-list verified against the 2026-11-01 duty-1080 capture.
    /// Player, party, ground-effect and current-position icons are excluded.
    /// </summary>
    public static bool IsObjectiveMapIcon(uint iconId) => iconId is
        60_484 or 60_485 or 60_486 or             // faction colour overlays
        60_585 or 60_586 or 60_587 or 60_588 or  // B point
        60_589 or 60_590 or 60_591 or 60_592 or  // A point
        60_593 or 60_594 or                       // S neutral / observed team 0
        60_620 or 60_621 or 60_622 or             // S countdown
        60_624 or 60_625 or 60_626 or             // A countdown
        60_628 or 60_629 or 60_630;               // B countdown

    public static byte? OwnerFromIcon(uint iconId) => iconId switch
    {
        60_484 or 60_586 or 60_590 or 60_594 => 0, // Maelstrom / red
        60_485 or 60_587 or 60_591 => 1,           // Adders / yellow
        60_486 or 60_588 or 60_592 => 2,           // Flames / blue
        _ => null
    };

    public static bool IsNeutralIcon(uint iconId) => iconId is
        60_585 or 60_589 or 60_593 or
        60_620 or 60_621 or 60_622 or
        60_624 or 60_625 or 60_626 or
        60_628 or 60_629 or 60_630;

    public static bool TryGetMapIcon(string? state, out uint iconId)
    {
        iconId = 0;
        if (string.IsNullOrWhiteSpace(state)) return false;
        const string token = "icon=";
        var start = state.LastIndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        start += token.Length;
        var end = start;
        while (end < state.Length && char.IsDigit(state[end])) end++;
        return end > start && uint.TryParse(state[start..end], out iconId);
    }

    public static bool IsObjectiveState(string? state) =>
        TryGetMapIcon(state, out var iconId) && IsObjectiveMapIcon(iconId);

    public static byte? OwnerFromState(string? state) =>
        TryGetMapIcon(state, out var iconId) ? OwnerFromIcon(iconId) : null;

    public static bool IsNeutralState(string? state) =>
        TryGetMapIcon(state, out var iconId) && IsNeutralIcon(iconId);

    /// <summary>
    /// Extracts the human point number from the first tooltip line. This keeps
    /// countdown/rank/state evidence in the capture while presenting only the
    /// compact map label requested by the player (for example, "09").
    /// </summary>
    public static string ShortPointCode(string? label)
    {
        var first = (label ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
        var end = first.Length;
        var start = end;
        while (start > 0 && char.IsDigit(first[start - 1])) start--;
        if (start == end || !int.TryParse(first[start..end], out var value) || value is < 1 or > 99)
            return first;
        return value.ToString("D2");
    }

    /// <summary>
    /// Official rank geometry: B has three fields, A four, S five. The value is
    /// a denominator only; it does not invent partial ownership observations.
    /// </summary>
    public static int FieldCount(string? label, string? state = null)
    {
        var lines = (label ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).ToArray();
        if (lines.Any(line => line.StartsWith("S", StringComparison.OrdinalIgnoreCase))) return 5;
        if (lines.Any(line => line.StartsWith("A", StringComparison.OrdinalIgnoreCase))) return 4;
        if (lines.Any(line => line.StartsWith("B", StringComparison.OrdinalIgnoreCase))) return 3;
        if (!TryGetMapIcon(state, out var icon)) return 0;
        return icon switch
        {
            60_593 or 60_594 or 60_620 or 60_621 or 60_622 => 5,
            60_589 or 60_590 or 60_591 or 60_592 or 60_624 or 60_625 or 60_626 => 4,
            60_585 or 60_586 or 60_587 or 60_588 or 60_628 or 60_629 or 60_630 => 3,
            _ => 0
        };
    }

    public static int? ParseDisplayedCountdownSeconds(string? value) =>
        MapUiTextParsing.ParseBoundedMinuteSecond(value, 3_600);

    public static bool IsWithinObservedOccupantArea(double pointX, double pointY, double pointZ,
        double actorX, double actorY, double actorZ)
    {
        if (Math.Abs(actorY - pointY) > 7d) return false;
        var dx = actorX - pointX;
        var dz = actorZ - pointZ;
        return dx * dx + dz * dz <= ObservedOccupantRadiusYalms * ObservedOccupantRadiusYalms;
    }
}
