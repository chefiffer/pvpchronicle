using System;
using System.Collections.Generic;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Fixed terrain locations for Borderland Ruins (Secure).  The catalogue only
/// supplies geometry; ownership still comes from client map markers and is
/// never inferred from proximity.
/// </summary>
internal static class SecureObjectiveCatalog
{
    public const string EvidenceScope = "secure-fixed-node-geometry-2026-10-v2";
    public const uint TowerExplosionActionId = 42940;
    public const uint TowerUnmitigatedExplosionActionId = 42941;
    public const uint NormalDroneBaseId = 3097;
    public const uint GoldDroneBaseId = 18365;
    public const uint InterceptorNodeBaseId = 3096;
    public const uint TowerCircleBaseId = 9020;
    public const uint TowerControllerBaseId = 18366;
    // Current gameplay-reference reward values for one completed mechanism.
    // Current patch values are 10 / 30 / 24 respectively. These constants
    // never make an observer-bounded damage share authoritative; only the
    // result packet provides category totals.
    public const int NormalDroneScorePool = 10;
    public const int GoldDroneScorePool = 30;
    public const int CommandSystemScorePool = 24;
    // The node resets after each of the first five depletions and is removed on
    // the sixth. The current Secure reference assigns 150 points per depletion.
    public const int InterceptorNodePhaseCount = 6;
    public const int InterceptorNodePhaseScorePool = 150;
    public const int InterceptorNodeScorePool =
        InterceptorNodePhaseCount * InterceptorNodePhaseScorePool;
    /// <summary>
    /// Stable Secure occupation-point geometry.
    /// Input is the fixed client-map coordinate measured for duty 127; output is
    /// a replay anchor whose identity never depends on a transient EventMarker.
    /// <paramref name="HomeTeam"/> is populated only for the three home nodes,
    /// so the renderer can colour the spawn emblem while still using the same
    /// short, unique two-character code as the mechanism table.
    /// </summary>
    internal readonly record struct Node(uint Id, string Code, string Name,
        float X, float Y, float Z, byte? HomeTeam = null);
    internal readonly record struct Tower(string Slot, float X, float Y, float Z);

    public static IReadOnlyList<Node> Nodes { get; } =
    [
        new(0x7F000001, "SW", "Storm Outpost", -126.65f, -18f, 73.12f, 0),
        new(0x7F000002, "SE", "Flame Outpost", 128.99f, -18f, 74.47f, 2),
        new(0x7F000003, "NO", "Serpent Outpost", 0f, -18f, -139.99f, 1),
        new(0x7F000004, "W1", "Allagan Manors South", -183.93f, -3f, -48.98f),
        new(0x7F000005, "W2", "Allagan Manors North", -133.93f, -3f, -135.58f),
        new(0x7F000006, "E1", "Allagan Markets South", 184.44f, -3f, -48.22f),
        new(0x7F000007, "E2", "Allagan Markets North", 134.44f, -3f, -134.82f),
        new(0x7F000008, "S1", "Allagan Sun Temple West", -50.47f, -3f, 183.79f),
        new(0x7F000009, "S2", "Allagan Sun Temple East", 49.53f, -3f, 183.79f),
    ];

    /// <summary>
    /// Six high-platform tower circles.  These are geometry anchors only.  A
    /// circle becoming active, being soaked, or exploding must still be proven
    /// by the observed map/object/action stream.
    /// </summary>
    public static IReadOnlyList<Tower> Towers { get; } =
    [
        new("NW", -16.86f, 28.0f, 9.72f),
        new("SW", -16.80f, 28.0f, -9.78f),
        new("N", -0.02f, 28.5f, 19.36f),
        new("S", 0.02f, 28.0f, -19.61f),
        new("SE", 16.86f, 28.5f, -9.75f),
        new("NE", 16.98f, 28.5f, 9.69f),
    ];

    /// <summary>
    /// AgentMap also exposes player/party icons through EventMarkers.  Keep a
    /// strict Secure allow-list so those icons never become fake objectives.
    /// </summary>
    public static bool IsMechanicMapIcon(uint iconId) => iconId is
        60_575 or 60_576 or 60_577 or 60_578 or // occupation ownership
        63_979 or 63_980 or 63_987 or           // tower countdown/active
        63_986 or 60_992 or 60_999 or           // drone countdown/HP
        60_991 or 63_985 or                     // interceptor node
        63_982 or 63_983 or 63_984;             // displacement panels

    /// <summary>
    /// Extracts the icon encoded by <c>Plugin.PublishObjectiveState</c> in an
    /// objective-state string such as <c>active:event=2:icon=63986</c>.
    /// Returns false for fixed catalogue rows and older captures that did not
    /// preserve the icon. This parser never guesses from a localized label.
    /// </summary>
    public static bool TryGetMapIcon(string? state, out uint iconId)
    {
        iconId = 0;
        if (string.IsNullOrWhiteSpace(state)) return false;
        const string token = "icon=";
        var offset = state.LastIndexOf(token, System.StringComparison.OrdinalIgnoreCase);
        if (offset < 0) return false;
        offset += token.Length;
        var end = offset;
        while (end < state.Length && char.IsDigit(state[end])) end++;
        return end > offset && uint.TryParse(state[offset..end], out iconId);
    }

    public static bool IsFixedNodeId(uint id) => id is >= 0x7F000001 and <= 0x7F000009;

    /// <summary>
    /// Resolves a transient AgentMap marker to the same stable two-character
    /// node code used by the fixed map layer and mechanism tables. Input may be
    /// the client label, its map coordinates, or both; output is false rather
    /// than a generic "Secure node" when neither is sufficient evidence.
    /// </summary>
    public static bool TryResolveNode(string? label, double x, double z, out Node node)
    {
        var normalized = (label ?? string.Empty).Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        foreach (var candidate in Nodes)
        {
            if (normalized.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase))
            {
                node = candidate;
                return true;
            }
        }
        var nearest = Nodes.OrderBy(candidate =>
                Math.Pow(candidate.X - x, 2) + Math.Pow(candidate.Z - z, 2))
            .First();
        if (Math.Pow(nearest.X - x, 2) + Math.Pow(nearest.Z - z, 2) <= 12 * 12)
        {
            node = nearest;
            return true;
        }
        node = default;
        return false;
    }

    public static bool TryResolveNode(string? label, out Node node)
    {
        var normalized = (label ?? string.Empty).Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        foreach (var candidate in Nodes)
        {
            if (!normalized.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)) continue;
            node = candidate;
            return true;
        }
        node = default;
        return false;
    }

    public static bool IsOccupationIcon(uint iconId) => iconId is 60_575 or 60_576 or 60_577 or 60_578;

    public static bool IsDroneMapIcon(uint iconId) => iconId is 63_986 or 60_992 or 60_999 or 60_991 or 63_985;

    public static bool IsCommandCountdownIcon(uint iconId) => iconId == 63_979;
    public static bool IsCommandResolutionIcon(uint iconId) => iconId == 63_980;
    public static bool IsCommandSystemIcon(uint iconId) => iconId is 63_979 or 63_980 or 63_987;

    /// <summary>
    /// Transient Secure mechanisms are allowed through the raw-ID filter so
    /// their client-owned countdown remains visible. Fixed occupation points
    /// are deliberately excluded because the renderer draws their catalogue
    /// geometry for the whole match and overlays ownership separately.
    /// </summary>
    public static bool IsTransientMechanicState(string? state) =>
        TryGetMapIcon(state, out var iconId) && IsMechanicMapIcon(iconId) && !IsOccupationIcon(iconId);

    /// <summary>
    /// Reads the final client-rendered <c>m:ss</c> token from a map-marker
    /// tooltip. Input is UI text such as "Interceptor Drone\nSpawns in: 0:40";
    /// output is a bounded number of seconds, or null when no unambiguous timer
    /// is present. This function does not infer a schedule from match time.
    /// </summary>
    public static int? ParseDisplayedCountdownSeconds(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        for (var colon = text.Length - 1; colon >= 0; colon--)
        {
            if (text[colon] != ':') continue;
            var minuteStart = colon - 1;
            while (minuteStart >= 0 && char.IsDigit(text[minuteStart])) minuteStart--;
            var secondEnd = colon + 1;
            while (secondEnd < text.Length && char.IsDigit(text[secondEnd])) secondEnd++;
            var minuteText = text[(minuteStart + 1)..colon];
            var secondText = text[(colon + 1)..secondEnd];
            if (minuteText.Length == 0 || secondText.Length != 2 ||
                !int.TryParse(minuteText, out var minutes) ||
                !int.TryParse(secondText, out var seconds) || seconds > 59) continue;
            var total = minutes * 60 + seconds;
            return total is >= 0 and <= 86_400 ? total : null;
        }
        return null;
    }

    public static byte? OwnerFromIcon(uint iconId) => iconId switch
    {
        60_576 => 0, // Maelstrom / red
        60_577 => 1, // Adders / yellow
        60_578 => 2, // Flames / blue
        _ => null    // 60575 is explicitly neutral; other icons have no owner
    };

    public static bool IsNeutralOccupationIcon(uint iconId) => iconId == 60_575;
    public static bool IsInactiveDisplacementPanel(uint iconId, string? label) =>
        iconId == 63_983 || (label ?? string.Empty).Contains("Inactive", System.StringComparison.OrdinalIgnoreCase);

    public static bool IsDrone(uint baseId, string? name) =>
        baseId is NormalDroneBaseId or GoldDroneBaseId ||
        (name ?? string.Empty).Contains("Interceptor Drone", System.StringComparison.OrdinalIgnoreCase);

    public static bool IsGoldDrone(uint baseId, string? name) =>
        baseId == GoldDroneBaseId || (name ?? string.Empty).Contains('\u03B1') ||
        (name ?? string.Empty).Contains("alpha", System.StringComparison.OrdinalIgnoreCase);

    public static string TowerSlot(double x, double z)
    {
        var best = Towers[0];
        var bestDistance = double.MaxValue;
        foreach (var tower in Towers)
        {
            var distance = (tower.X - x) * (tower.X - x) + (tower.Z - z) * (tower.Z - z);
            if (distance >= bestDistance) continue;
            best = tower;
            bestDistance = distance;
        }
        return bestDistance <= 8 * 8 ? best.Slot : "?";
    }

    public static string TowerCode(double x, double z) => $"T-{TowerSlot(x, z)}";
}
