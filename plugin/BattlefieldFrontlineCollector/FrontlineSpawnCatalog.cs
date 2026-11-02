using System;
using System.Globalization;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Versioned Frontline home-base evidence decoded from AgentMap.
/// 60572-60574 are the three ordinary faction crests; 60597-60599 are the
/// corresponding local-return variants with a star. Both variants describe
/// the same team spawn and are normalized before either map renders them.
/// </summary>
internal static class FrontlineSpawnCatalog
{
    public const string EvidenceScope = "frontline-spawn-map-ui-2026-11-v1";

    public static bool IsSpawnIcon(uint iconId) => iconId is
        60_572 or 60_573 or 60_574 or 60_597 or 60_598 or 60_599;

    public static byte? TeamFromIcon(uint iconId) => iconId switch
    {
        60_572 or 60_597 => 0,
        60_573 or 60_598 => 1,
        60_574 or 60_599 => 2,
        _ => null,
    };

    public static string State(uint iconId) => TeamFromIcon(iconId) is byte team
        ? $"spawn:team={team}:icon={iconId}"
        : string.Empty;

    public static string Label(byte team) => $"frontline_spawn_team_{team}";

    public static bool TryGetIcon(string? state, out uint iconId)
    {
        iconId = 0;
        state ??= string.Empty;
        const string token = "icon=";
        var start = state.LastIndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        start += token.Length;
        var end = start;
        while (end < state.Length && char.IsDigit(state[end])) end++;
        return end > start && uint.TryParse(state[start..end], NumberStyles.None,
            CultureInfo.InvariantCulture, out iconId);
    }

    public static bool TryGetTeam(string? state, out byte team)
    {
        team = 255;
        if (!TryGetIcon(state, out var iconId) || TeamFromIcon(iconId) is not byte resolved)
            return false;
        team = resolved;
        return true;
    }
}
