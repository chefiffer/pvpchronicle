using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Versioned evidence boundary for The Fields of Glory (Shatter).
///
/// The global AgentMap marker is the authority for an ice node's displayed
/// respawn countdown and displayed remaining-HP percentage.  Loaded battle
/// objects and action targets remain observer-bounded exact-HP evidence; they
/// are never used to invent a global countdown for an old capture.
/// </summary>
internal static class ShatterObjectiveCatalog
{
    public const uint DutyId = 180;
    public const string GlobalMapEvidenceScope = "shatter-global-map-ui-2026-11-v1";

    // Verified against a real duty-180 AgentMap capture and the corresponding
    // XIVAPI icon assets. 60901/60902/60989 are the A-node inactive, HP and
    // countdown layers; 60903/60904/60990 are the equivalent B-node layers.
    // Player/alliance icons such as 60359/60360 can pass directly over an ice
    // anchor, so coordinates and percentage-looking text are never sufficient
    // evidence that a map marker is an ice objective.
    public static IReadOnlySet<uint> IceMapIcons { get; } = new HashSet<uint>
    {
        60_901, 60_902, 60_989,
        60_903, 60_904, 60_990,
    };

    // The map keeps one dormant location layer loaded for every possible ice
    // node.  Those rows are map reference data, not evidence that an ice is
    // currently present.  Only the HP layer is attackable; the countdown layer
    // may be shown when its client-rendered remaining time was decoded.
    public static bool IsDormantMapIcon(uint iconId) => iconId is 60_901 or 60_903;
    public static bool IsAttackableMapIcon(uint iconId) => iconId is 60_902 or 60_904;
    public static bool IsCountdownMapIcon(uint iconId) => iconId is 60_989 or 60_990;

    internal readonly record struct Node(string Code, float X, float Y, float Z, bool Large);

    // Directly observed from eight duty-180 captures made on 2026-10-26.
    // These anchors are a fallback when a localized map tooltip is missing;
    // they do not create an objective unless a real AgentMap marker exists.
    public static IReadOnlyList<Node> ObservedNodes { get; } =
    [
        new("A1", 0f, -2.91f, 0f, true),
        new("A2", -142.95f, .79f, 84.70f, true),
        new("A3", 136.92f, -1.97f, 80f, true),
        new("A4", 3.11f, 2.92f, -156.72f, true),
        new("B1", -89.67f, 21.36f, 97.31f, false),
        new("B2", -39.37f, 21.36f, 88.79f, false),
        new("B3", -133.41f, 23.36f, 15.50f, false),
        new("B4", -91.24f, 23.36f, -19.26f, false),
        new("B5", -38.05f, 18.36f, -127.84f, false),
        new("B6", -60.17f, 18.86f, -72.53f, false),
        new("B7", 38.36f, 24.71f, -124.02f, false),
        new("B8", 64.79f, 19.36f, -71.27f, false),
        new("B9", 129.92f, 15.01f, 25.76f, false),
        new("B10", 93.34f, 14.36f, -20.96f, false),
        new("B13", 40.53f, -3.64f, 24.40f, false),
        new("B14", -40.65f, -3.64f, 20.19f, false),
        new("B15", .93f, -3.64f, -45.37f, false),
    ];

    /// <summary>
    /// Input is one live AgentMap icon ID; output is a capture allow/deny
    /// decision. This deliberately fails closed instead of accepting a nearby
    /// player marker and relabeling it as the nearest ice node.
    /// </summary>
    public static bool IsIceMapIcon(uint iconId) => IceMapIcons.Contains(iconId);

    public static bool IsIceMapMarker(uint iconId) => IsIceMapIcon(iconId);

    /// <summary>
    /// Reads the map icon embedded in an objective-state row. This is used to
    /// cleanly replay older captures that already contain unrelated AgentMap
    /// markers; only the verified Shatter icon family survives.
    /// </summary>
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
        return end > start && uint.TryParse(state[start..end], NumberStyles.None,
            CultureInfo.InvariantCulture, out iconId);
    }

    public static bool IsIceObjectiveState(string? state) =>
        TryGetMapIcon(state, out var iconId) && IsIceMapIcon(iconId);

    /// <summary>
    /// Converts one directly observed AgentMap layer into the semantic state
    /// used by both live rendering and replay. Dormant location layers are
    /// deliberately written as inactive so they cannot populate the map at
    /// match start; the raw marker observation still retains the icon ID.
    /// </summary>
    public static string CaptureState(uint iconId, int eventState)
    {
        if (IsDormantMapIcon(iconId)) return "inactive";
        var phase = IsCountdownMapIcon(iconId) ? "countdown" :
            IsAttackableMapIcon(iconId) ? "attackable" : "unknown";
        return $"{phase}:event={eventState}:icon={iconId}";
    }

    /// <summary>
    /// Input: one global Shatter map row and its decoded UI values.
    /// Output: whether that row represents something a player should see now.
    /// Attackable HP icons are visible. Countdown icons are visible only when
    /// the client text yielded a real remaining time. Dormant/unknown layers
    /// are never rendered as ice objectives.
    /// </summary>
    public static bool ShouldDisplayMapState(string? state, int? remainingSeconds,
        double? mapUiPercent, string? displayText = null, string? label = null)
    {
        if (!TryGetMapIcon(state, out var iconId)) return false;
        if (IsAttackableMapIcon(iconId)) return true;
        if (!IsCountdownMapIcon(iconId)) return false;
        return remainingSeconds.HasValue ||
               ParseDisplayedCountdownSeconds(displayText).HasValue ||
               ParseDisplayedCountdownSeconds(label).HasValue;
    }

    public static bool IsIceLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Contains("冰封", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("氷封", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("アイスドトームリス", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Icebound Tomelith", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Icebound", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Tomelith", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Echolith", StringComparison.OrdinalIgnoreCase) &&
               value.Contains("vereis", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("mémolithe", StringComparison.OrdinalIgnoreCase) &&
               value.Contains("congel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the concrete battle-object IDs for every Shatter ice that has
    /// direct positive-damage evidence. This does not depend on the secondary
    /// objective timeline: an older capture may retain action targets while
    /// omitting that timeline. Requiring an entity ID, damage and a localized
    /// ice name prevents players, map anchors and untouched spawn positions
    /// from entering the mechanism-damage table.
    /// </summary>
    public static HashSet<uint> DirectlyAttackedObjectIds(IEnumerable<ActionSliceEvent>? actions)
    {
        if (actions == null) return [];
        return actions
            .Where(row => row.TargetEntityId != 0 && row.Damage > 0 && IsIceLabel(row.TargetName))
            .Select(row => row.TargetEntityId)
            .ToHashSet();
    }

    /// <summary>
    /// Finds the nearest directly observed, named attack row for one concrete
    /// ice entity. The name and coordinates are immutable identity evidence,
    /// not replay state: damage, HP, buffs and timing still come from the
    /// selected slice. This lets an unnamed row in that slice inherit A/B code
    /// evidence without borrowing damage from another time window.
    /// </summary>
    public static ActionSliceEvent? NearestNamedAttack(IEnumerable<ActionSliceEvent>? actions,
        uint objectId, long referenceMs)
    {
        if (actions == null || objectId == 0) return null;
        return actions
            .Where(row => row.TargetEntityId == objectId && row.Damage > 0 &&
                          IsIceLabel(row.TargetName))
            .OrderBy(row => row.ElapsedMs <= referenceMs ? 0 : 1)
            .ThenBy(row => Math.Abs(row.ElapsedMs - referenceMs))
            .FirstOrDefault();
    }

    /// <summary>
    /// Extracts A1-A4 or B1-B15 from localized client text.  A token is only
    /// accepted when the surrounding label is an ice label; arbitrary player
    /// or map labels containing A1/B1 are not promoted to Shatter objectives.
    /// </summary>
    public static string ShortPointCode(string? value)
    {
        if (!IsIceLabel(value)) return string.Empty;
        return ParsePointCode(value!);
    }

    public static string CompactPointCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        var code = ParsePointCode(trimmed);
        return code.Length == trimmed.Length ? code : string.Empty;
    }

    private static string ParsePointCode(string text)
    {
        for (var index = 0; index < text.Length - 1; index++)
        {
            var prefix = char.ToUpperInvariant(text[index]);
            if (prefix is not ('A' or 'B')) continue;
            var firstDigit = index + 1;
            while (firstDigit < text.Length &&
                   (text[firstDigit] == '-' || char.IsWhiteSpace(text[firstDigit]))) firstDigit++;
            if (firstDigit >= text.Length || !char.IsDigit(text[firstDigit])) continue;
            var end = firstDigit;
            while (end < text.Length && char.IsDigit(text[end])) end++;
            if (!int.TryParse(text[firstDigit..end], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number)) continue;
            if (prefix == 'A' && number is >= 1 and <= 4 ||
                prefix == 'B' && number is >= 1 and <= 15)
                return $"{prefix}{number}";
        }
        return string.Empty;
    }

    public static bool TryResolveObservedNode(double x, double z, out Node node)
    {
        var nearest = ObservedNodes.OrderBy(candidate =>
            Math.Pow(candidate.X - x, 2) + Math.Pow(candidate.Z - z, 2)).First();
        if (Math.Pow(nearest.X - x, 2) + Math.Pow(nearest.Z - z, 2) <= 12d * 12d)
        {
            node = nearest;
            return true;
        }
        node = default;
        return false;
    }

    public static string DisplayCode(string? label, double x, double z)
    {
        var code = ShortPointCode(label);
        if (code.Length == 0) code = CompactPointCode(label);
        if (code.Length > 0) return code;
        return TryResolveObservedNode(x, z, out var node) ? node.Code : string.Empty;
    }

    /// <summary>
    /// Reads a client-rendered countdown.  The preferred form is m:ss.  A
    /// localized seconds suffix is accepted only when it is adjacent to the
    /// number, preventing an HP percentage or point code from becoming time.
    /// No schedule is inferred from match elapsed time.
    /// </summary>
    public static int? ParseDisplayedCountdownSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.AsSpan();
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i])) continue;
            var start = i;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            if (i < text.Length && text[i] == ':')
            {
                if (!int.TryParse(text[start..i], out var minutes)) continue;
                var secondStart = ++i;
                while (i < text.Length && char.IsDigit(text[i])) i++;
                if (secondStart == i || !int.TryParse(text[secondStart..i], out var seconds) ||
                    seconds > 59) continue;
                var total = minutes * 60 + seconds;
                return total is >= 0 and <= 3600 ? total : null;
            }

            if (!int.TryParse(text[start..i], out var plainSeconds) ||
                plainSeconds is < 0 or > 3600) continue;
            var suffix = text[i..].TrimStart();
            if (suffix.StartsWith("秒".AsSpan(), StringComparison.Ordinal) ||
                suffix.StartsWith("sec".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith("second".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith("s ".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                suffix.SequenceEqual("s".AsSpan()))
                return plainSeconds;
        }
        return null;
    }

    public static double? ParseDisplayedHealthPercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var percent = value.IndexOf('%');
        if (percent < 1) return null;
        var start = percent - 1;
        while (start >= 0 && (char.IsDigit(value[start]) || value[start] == '.')) start--;
        var token = value[(start + 1)..percent];
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               parsed is >= 0 and <= 100 ? parsed : null;
    }

    /// <summary>
    /// Resolves the percentage shown to a human. Exact observer-bounded HP has
    /// priority; otherwise the global map UI percentage is used. The latter is
    /// deliberately kept as a percentage and never converted into fake HP.
    /// </summary>
    public static double? ResolveDisplayedHealthPercent(uint currentHp, uint maxHp,
        double? mapUiPercent)
    {
        if (maxHp > 0) return Math.Clamp(100d * currentHp / maxHp, 0, 100);
        return mapUiPercent.HasValue ? Math.Clamp(mapUiPercent.Value, 0, 100) : null;
    }
}
