using System;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Versioned global-map UI contract for Onsal Hakair (Danshig Naadam).
///
/// Inputs are the client EventMarker icon, state and tooltip. Outputs are only
/// the stable two-digit point code, rank geometry, faction owner and countdown.
/// Nearby game objects and player positions are not used to invent global state.
/// </summary>
internal static class OnsalObjectiveCatalog
{
    public const uint DutyId = 701;
    public const uint TerritoryId = 888;
    public const string EvidenceScope = "onsal-global-map-objectives-2026-11-v1";
    public const string LegacyEvidenceScope = "client_global_event_map_marker_v2";
    public const int SpawnCountdownSeconds = 30;

    public static bool IsObjectiveMapIcon(uint iconId) => iconId is
        >= 60_585 and <= 60_596 or
        60_620 or 60_621 or 60_622 or
        60_624 or 60_625 or 60_626 or
        60_628 or 60_629 or 60_630;

    public static byte? OwnerFromIcon(uint iconId) => iconId switch
    {
        60_586 or 60_590 or 60_594 => 0,
        60_587 or 60_591 or 60_595 => 1,
        60_588 or 60_592 or 60_596 => 2,
        _ => null
    };

    public static bool IsNeutralIcon(uint iconId) => iconId is
        60_585 or 60_589 or 60_593 or
        60_620 or 60_621 or 60_622 or
        60_624 or 60_625 or 60_626 or
        60_628 or 60_629 or 60_630;

    public static char RankFromIcon(uint iconId) => iconId switch
    {
        >= 60_585 and <= 60_588 or 60_628 or 60_629 or 60_630 => 'B',
        >= 60_589 and <= 60_592 or 60_624 or 60_625 or 60_626 => 'A',
        >= 60_593 and <= 60_596 or 60_620 or 60_621 or 60_622 => 'S',
        _ => '\0'
    };

    public static int PolygonSides(char rank) => rank switch
    {
        'B' => 3,
        'A' => 4,
        'S' => 5,
        _ => 0
    };

    public static bool TryGetMapIcon(string? state, out uint iconId)
    {
        iconId = 0;
        state ??= string.Empty;
        const string token = "icon=";
        var start = state.LastIndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        start += token.Length;
        var end = start;
        while (end < state.Length && char.IsDigit(state[end])) end++;
        return end > start && uint.TryParse(state[start..end], out iconId);
    }

    public static byte? OwnerFromState(string? state) =>
        TryGetMapIcon(state, out var iconId) ? OwnerFromIcon(iconId) : null;

    public static bool IsNeutralState(string? state) =>
        TryGetMapIcon(state, out var iconId) && IsNeutralIcon(iconId);

    public static bool IsObjectiveState(string? state) =>
        TryGetMapIcon(state, out var iconId) && IsObjectiveMapIcon(iconId);

    /// <summary>
    /// Selects replay rows before grouping by marker ID. Older captures stored
    /// the rank icon and an unrelated faction-overlay icon under the same ID;
    /// filtering after grouping let the overlay hide the real objective. An
    /// inactive row is retained only when its tooltip still proves a numbered
    /// Ovoo, so the point can disappear at the correct replay time.
    /// </summary>
    public static bool IsReplayRow(string? label, string? state, string? evidenceScope)
    {
        if (!string.Equals(evidenceScope, EvidenceScope, StringComparison.Ordinal) &&
            !string.Equals(evidenceScope, LegacyEvidenceScope, StringComparison.Ordinal)) return false;
        if (IsObjectiveState(state)) return !string.IsNullOrEmpty(ShortPointCode(label));
        return !string.IsNullOrEmpty(ShortPointCode(label)) && IsInactiveState(state);
    }

    public static bool IsInactiveState(string? state) =>
        string.Equals(state, "inactive", StringComparison.OrdinalIgnoreCase) ||
        (state ?? string.Empty).Contains("unload", StringComparison.OrdinalIgnoreCase) ||
        (state ?? string.Empty).Contains("destroy", StringComparison.OrdinalIgnoreCase);

    public static char RankFromEvidence(string? label, string? state)
    {
        if (TryGetMapIcon(state, out var iconId))
        {
            var rank = RankFromIcon(iconId);
            if (rank != '\0') return rank;
        }

        label ??= string.Empty;
        foreach (var line in label.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.TrimStart();
            if (value.Length > 0 && value[0] is 'S' or 'A' or 'B') return value[0];
            if (value.StartsWith("Rank ", StringComparison.OrdinalIgnoreCase) && value.Length > 5 &&
                value[5] is 'S' or 'A' or 'B') return value[5];
        }
        return '\0';
    }

    /// <summary>
    /// Extracts only the final point number from the first tooltip line.
    /// Rank, faction and availability text remain captured evidence but are
    /// deliberately omitted from the map label.
    /// </summary>
    public static string ShortPointCode(string? label)
    {
        var first = (label ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
        var end = first.Length;
        var start = end;
        while (start > 0 && char.IsDigit(first[start - 1])) start--;
        // The current map has exactly 13 numbered Ovoos. A trailing number on
        // a generic player/ground-effect tooltip must never create a point.
        if (start == end || !int.TryParse(first[start..end], out var value) || value is < 1 or > 13)
            return string.Empty;
        return value.ToString("D2");
    }

    public static double CountdownPercent(int remainingSeconds) =>
        100d * Math.Clamp(remainingSeconds, 0, SpawnCountdownSeconds) / SpawnCountdownSeconds;

    public static int? ParseDisplayedCountdownSeconds(string? value) =>
        MapUiTextParsing.ParseBoundedMinuteSecond(value, 3_600);
}
