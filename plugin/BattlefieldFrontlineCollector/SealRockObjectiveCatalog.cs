using System;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Versioned Seal Rock map-UI catalogue.
///
/// Inputs are client EventMarker icon IDs, tooltip text, and the serialized
/// objective-state string. Outputs are the directly observed faction owner,
/// neutral state, point rank, compact point code, and polygon geometry.
/// No combat packet or nearby actor observation is used to guess ownership.
/// </summary>
internal static class SealRockObjectiveCatalog
{
    public const uint DutyId = 130;
    public const string Version = "seal-rock-map-ui-2026-11-v2";
    public const string DirectMapEvidenceScope = "client_global_event_map_marker_v2";
    public const string ReducedMapEvidenceScope = "client_global_event_map_marker_reduced_v1";
    public const string CompleteSnapshotEvidenceScope = "client_global_event_map_snapshot_v1";
    public const string CompleteSnapshotLabel = "seal_rock_map_snapshot";
    public const uint CompleteSnapshotObjectId = 0xFFF1_3001;

    private const uint MaelstromOverlay = 60_484;
    private const uint AddersOverlay = 60_485;
    private const uint FlamesOverlay = 60_486;

    public static bool IsPointIcon(uint iconId) =>
        iconId is >= 60_585 and <= 60_596 ||
        iconId is MaelstromOverlay or AddersOverlay or FlamesOverlay;

    public static bool IsDirectMapEvidence(string? evidenceScope) =>
        string.Equals(evidenceScope, DirectMapEvidenceScope, StringComparison.Ordinal) ||
        string.Equals(evidenceScope, ReducedMapEvidenceScope, StringComparison.Ordinal);

    public static bool IsRawMapEvidence(string? evidenceScope) =>
        string.Equals(evidenceScope, DirectMapEvidenceScope, StringComparison.Ordinal);

    public static bool IsCompleteSnapshot(ObjectiveStateEvent row) =>
        row.ObjectId == CompleteSnapshotObjectId &&
        string.Equals(row.Label, CompleteSnapshotLabel, StringComparison.Ordinal) &&
        string.Equals(row.EvidenceScope, CompleteSnapshotEvidenceScope, StringComparison.Ordinal);

    public static bool IsRankIcon(uint iconId) => iconId is >= 60_585 and <= 60_596;

    public static bool IsNeutralIcon(uint iconId) => iconId is 60_585 or 60_589 or 60_593;

    /// <summary>
    /// Returns the canonical Frontline team index: Maelstrom=0, Adders=1,
    /// Flames=2. Neutral and unknown icons return null.
    /// </summary>
    public static byte? OwnerFromIcon(uint iconId) => iconId switch
    {
        MaelstromOverlay or 60_586 or 60_590 or 60_594 => 0,
        AddersOverlay or 60_587 or 60_591 or 60_595 => 1,
        FlamesOverlay or 60_588 or 60_592 or 60_596 => 2,
        _ => null
    };

    public static char RankFromIcon(uint iconId) => iconId switch
    {
        >= 60_585 and <= 60_588 => 'B',
        >= 60_589 and <= 60_592 => 'A',
        >= 60_593 and <= 60_596 => 'S',
        _ => '\0'
    };

    public static int PolygonSides(char rank) => rank switch
    {
        'B' => 3,
        'A' => 4,
        'S' => 5,
        _ => 0
    };

    /// <summary>
    /// Creates the official rank/owner map icon used by a reduced point state.
    /// A null owner produces the neutral icon for that rank.
    /// </summary>
    public static uint IconForRankAndOwner(char rank, byte? ownerTeam)
    {
        var neutral = rank switch { 'B' => 60_585u, 'A' => 60_589u, 'S' => 60_593u, _ => 0u };
        if (neutral == 0 || !ownerTeam.HasValue) return neutral;
        return ownerTeam.Value < 3 ? neutral + ownerTeam.Value + 1u : neutral;
    }

    public static double? ProgressPercentFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var percent = text.IndexOf('%');
        if (percent <= 0) return null;
        var start = percent - 1;
        while (start >= 0 && (char.IsDigit(text[start]) || text[start] == '.')) start--;
        return double.TryParse(text[(start + 1)..percent],
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var value) &&
               value is >= 0 and <= 100 ? value : null;
    }

    public static bool TryGetMapIcon(string? state, out uint iconId)
    {
        iconId = 0;
        state ??= string.Empty;
        var marker = state.IndexOf("icon=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        marker += 5;
        var end = marker;
        while (end < state.Length && char.IsDigit(state[end])) end++;
        return end > marker && uint.TryParse(state[marker..end], out iconId);
    }

    public static byte? OwnerFromState(string? state)
    {
        if (!TryGetMapIcon(state, out var iconId)) return null;
        return OwnerFromIcon(iconId);
    }

    public static bool IsNeutralState(string? state, string? label)
    {
        if (TryGetMapIcon(state, out var iconId) && IsNeutralIcon(iconId)) return true;
        label ??= string.Empty;
        return label.Contains("中立状态", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("中立狀態", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("中立状態", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("Neutral", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("Neutre", StringComparison.OrdinalIgnoreCase);
    }

    public static char RankFromEvidence(string? label, string? state)
    {
        if (TryGetMapIcon(state, out var iconId))
        {
            var rank = RankFromIcon(iconId);
            if (rank != '\0') return rank;
        }
        return RankFromLabel(label);
    }

    public static char RankFromLabel(string? label)
    {
        label ??= string.Empty;
        foreach (var rank in new[] { 'S', 'A', 'B' })
        {
            if (label.Contains($"Rank {rank}", StringComparison.OrdinalIgnoreCase) ||
                label.Contains($"{rank}级", StringComparison.OrdinalIgnoreCase) ||
                label.Contains($"{rank}級", StringComparison.OrdinalIgnoreCase) ||
                label.Contains($"{rank}ランク", StringComparison.OrdinalIgnoreCase))
                return rank;
        }
        return '\0';
    }

    /// <summary>
    /// Extracts only the stable human point code (for example D2 or B4).
    /// Localized prefixes such as "Allagan Tomelith" and all state/rank text
    /// are deliberately discarded.
    /// </summary>
    public static string ShortPointCode(string? label)
    {
        label ??= string.Empty;
        string result = string.Empty;
        for (var i = 0; i + 1 < label.Length; i++)
        {
            var letter = char.ToUpperInvariant(label[i]);
            if (letter is < 'A' or > 'D' || !char.IsDigit(label[i + 1])) continue;
            var end = i + 1;
            while (end < label.Length && char.IsDigit(label[end]) && end - i <= 2) end++;
            result = letter + label[(i + 1)..end];
        }
        return result;
    }

    /// <summary>
    /// Keeps simultaneous rank and faction-overlay markers distinct. This is
    /// used only for Seal Rock because both UI layers share the same position,
    /// marker type, and data ID; without the icon discriminator one overwrites
    /// the other before it reaches the replay archive.
    /// </summary>
    public static uint MarkerDiscriminator(uint iconId) => IsPointIcon(iconId) ? iconId : 0;
}
