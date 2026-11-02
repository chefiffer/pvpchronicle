using System;
using System.Collections.Generic;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Versioned evidence contract for Worqor Chirteh weather.
/// Input: the current PvPMKS integer array or replay objective rows.
/// Output: a direct UI warning/active weather state and the reviewed world-space
/// radius of Blessed Blast. Aurora is deliberately a map-wide text state; it is
/// never represented as a player status or a map object.
/// </summary>
internal static class WorqorWeatherMechanics
{
    public const string Version = "worqor-weather-ui-2026-11-v3";
    public const uint DutyId = WorqorObjectiveCatalog.DutyId;
    public const uint TerritoryId = 1313;
    public const uint WeatherObjectiveId = 0xFFF1_0806;
    public const string WeatherLabel = "worqor_weather";
    public const string SnowWarningState = "snow_warning";
    public const string SnowActiveState = "snow_active";
    public const string AuroraWarningState = "aurora_warning";
    public const string AuroraActiveState = "aurora_active";
    public const string InactiveState = "inactive";
    public const int WeatherCodeIndex = 602;
    public const int CountdownIndex = 603;
    public const int SnowWarningCode = 5;
    public const int AuroraWarningCode = 6;
    public const uint BlessedBlastActionId = 46_744;
    public const uint SnowFortStatusId = 4_965;
    public const double BlessedBlastRadiusYalms = 10d;
    public const int BlessedBlastCastMs = 5_600;
    // Two independent territory-1313 captures unload every BaseId-19293 snow
    // spirit 301.118/301.119 seconds after the direct UI warning ends. The
    // one-second excess is the object-table sampling cadence, so v2 calibrates
    // the active snow phase to 300 seconds and writes an explicit end state.
    public const long SnowActiveDurationMs = 300_000;
    // The weather HUD exposes a direct 15-second start warning but no separate
    // end countdown.  Until a direct end field is identified, both weather
    // phases use the same versioned five-minute cadence.  The state is an
    // elapsed-time boundary, not a claim that a second UI event was observed.
    public const long AuroraActiveDurationMs = 300_000;

    internal enum WeatherKind { None, Snow, Aurora }

    internal readonly record struct Warning(WeatherKind Kind, int Seconds);

    public static bool TryReadWarning(IReadOnlyList<int>? values, out Warning warning)
    {
        warning = default;
        if (values == null || values.Count <= CountdownIndex) return false;
        var kind = values[WeatherCodeIndex] switch
        {
            SnowWarningCode => WeatherKind.Snow,
            AuroraWarningCode => WeatherKind.Aurora,
            _ => WeatherKind.None
        };
        var seconds = values[CountdownIndex];
        if (kind == WeatherKind.None || seconds is < 1 or > 15) return false;
        warning = new Warning(kind, seconds);
        return true;
    }

    public static bool IsWeatherObjective(ObjectiveStateEvent row) =>
        row.ObjectId == WeatherObjectiveId || row.Label == WeatherLabel;

    public static string WarningState(WeatherKind kind) => kind switch
    {
        WeatherKind.Snow => SnowWarningState,
        WeatherKind.Aurora => AuroraWarningState,
        _ => string.Empty
    };

    public static string ActiveState(WeatherKind kind) => kind switch
    {
        WeatherKind.Snow => SnowActiveState,
        WeatherKind.Aurora => AuroraActiveState,
        _ => string.Empty
    };

    public static WeatherKind KindFromState(string? state) => state switch
    {
        SnowWarningState or SnowActiveState => WeatherKind.Snow,
        AuroraWarningState or AuroraActiveState => WeatherKind.Aurora,
        _ => WeatherKind.None
    };

    public static bool IsWarningState(string? state) =>
        state is SnowWarningState or AuroraWarningState;

    public static long ActiveDurationMs(WeatherKind kind) => kind switch
    {
        WeatherKind.Snow => SnowActiveDurationMs,
        WeatherKind.Aurora => AuroraActiveDurationMs,
        _ => 0
    };

    public static string TextKey(string? state) => state switch
    {
        SnowWarningState => "worqorSnowWarning",
        AuroraWarningState => "worqorAuroraWarning",
        SnowActiveState => "worqorSnowActive",
        AuroraActiveState => "worqorAuroraActive",
        _ => string.Empty
    };

    public static double? ResolveBlessedBlastRadius(uint actionId) =>
        actionId == BlessedBlastActionId ? BlessedBlastRadiusYalms : null;

    /// <summary>
    /// Identifies an otherwise anonymous action source as a Worqor snow spirit.
    /// Both the territory and the direct Blessed Blast action must match; an
    /// arbitrary unnamed battle NPC is never promoted from its entity ID alone.
    /// </summary>
    public static bool IsSnowSpiritSource(uint territoryId, uint actionId) =>
        territoryId == TerritoryId && actionId == BlessedBlastActionId;

    public static IReadOnlyList<(double X, double Z)> BuildWorldCircle(
        double centerX, double centerZ, int segments = 32)
    {
        if (segments < 8) throw new ArgumentOutOfRangeException(nameof(segments));
        var points = new (double X, double Z)[segments];
        for (var index = 0; index < segments; index++)
        {
            var angle = Math.Tau * index / segments;
            points[index] = (centerX + BlessedBlastRadiusYalms * Math.Cos(angle),
                centerZ + BlessedBlastRadiusYalms * Math.Sin(angle));
        }
        return points;
    }
}
