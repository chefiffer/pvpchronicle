using System;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Decodes the verified Crystalline Conflict PvP-map icon family.
/// Static XIVAPI assets establish icon appearance; runtime BFCAP/director
/// evidence establishes team and role. Unknown icons are not named or drawn.
/// </summary>
internal static class CrystallineMapMarkerCatalog
{
    public const string EvidenceScope = "cc-global-map-ui-2026-11-v1";
    public const uint TacticalCrystalIcon = 63_935;

    public static bool IsKnownIcon(uint iconId) => iconId is
        TacticalCrystalIcon or 63_944 or 63_945 or 63_946 or 63_947;

    public static bool IsTacticalCrystal(uint iconId) => iconId == TacticalCrystalIcon;
    public static bool IsCheckpointDiamond(uint iconId) => iconId is 63_944 or 63_945;
    public static bool IsCheckpointRing(uint iconId) => iconId is 63_946 or 63_947;

    public static byte? TeamFromIcon(uint iconId) => iconId switch
    {
        63_944 or 63_946 => 0, // Astra / blue
        63_945 or 63_947 => 1, // Umbra / red
        _ => null
    };

    public static string Label(uint iconId) => IsTacticalCrystal(iconId)
        ? "cc_tactical_crystal"
        : IsCheckpointDiamond(iconId) || IsCheckpointRing(iconId)
            ? "cc_tactical_checkpoint"
            : string.Empty;

    public static string State(uint iconId) => IsKnownIcon(iconId)
        ? $"ccmap:icon={iconId}:team={(TeamFromIcon(iconId)?.ToString() ?? "none")}" : string.Empty;

    public static bool TryGetIcon(string? state, out uint iconId)
    {
        iconId = 0;
        if (string.IsNullOrWhiteSpace(state)) return false;
        const string token = "icon=";
        var start = state.LastIndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        start += token.Length;
        var end = start;
        while (end < state.Length && char.IsDigit(state[end])) end++;
        return end > start && uint.TryParse(state[start..end], out iconId) && IsKnownIcon(iconId);
    }
}
