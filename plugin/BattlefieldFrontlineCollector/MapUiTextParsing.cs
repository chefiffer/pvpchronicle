using System;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Shared parsers for text rendered by the in-game PvP map UI.
/// Inputs are nullable localized labels; outputs are bounded scalar values or
/// <see langword="null"/> when the label does not contain unambiguous evidence.
/// This helper performs no schedule inference and has no game-state side effects.
/// </summary>
internal static class MapUiTextParsing
{
    /// <summary>
    /// Returns the first valid <c>m:ss</c> token in <paramref name="value"/>,
    /// bounded by <paramref name="maximumSeconds"/>. Seconds must be in 0..59.
    /// </summary>
    public static int? ParseBoundedMinuteSecond(string? value, int maximumSeconds)
    {
        if (string.IsNullOrWhiteSpace(value) || maximumSeconds < 0) return null;
        var text = value.AsSpan();
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i])) continue;
            var minuteStart = i;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            if (i >= text.Length || text[i] != ':') continue;
            if (!int.TryParse(text[minuteStart..i], out var minutes)) continue;
            var secondStart = ++i;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            if (secondStart == i || !int.TryParse(text[secondStart..i], out var seconds) ||
                seconds > 59) continue;
            var total = minutes * 60 + seconds;
            return total is >= 0 && total <= maximumSeconds ? total : null;
        }
        return null;
    }
}
