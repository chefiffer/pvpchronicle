using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BattlefieldFrontlineCollector;

internal sealed record PvpRotationSlot(string Key, uint DutyId, DateTime StartUtc, DateTime EndUtc,
    IReadOnlyDictionary<string, string> Names)
{
    public string Name(string language)
    {
        var code = language == "auto" ? System.Globalization.CultureInfo.CurrentUICulture.Name : language;
        code = code.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
               code.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ? "zh-TW"
            : code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN"
            : code.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "ja"
            : code.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "de"
            : code.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr"
            : code.StartsWith("th", StringComparison.OrdinalIgnoreCase) ? "th"
            : code.StartsWith("vi", StringComparison.OrdinalIgnoreCase) ? "vi" : "en";
        return Names.TryGetValue(code, out var value) ? value : Names.GetValueOrDefault("en", Key);
    }
}

/// <summary>Versioned, deterministic PvP rotation shared by the plugin and website.</summary>
internal sealed class PvpRotationCalendar
{
    private sealed record MapDefinition(uint DutyId, IReadOnlyDictionary<string, string> Names);
    private sealed record RotationDefinition(DateTime ReferenceUtc, int IntervalMinutes,
        string[] Sequence, IReadOnlyDictionary<string, MapDefinition> Maps);

    private readonly RotationDefinition frontline;
    private readonly RotationDefinition crystallineConflict;

    private PvpRotationCalendar(RotationDefinition frontline, RotationDefinition crystallineConflict)
    {
        this.frontline = frontline;
        this.crystallineConflict = crystallineConflict;
    }

    public static PvpRotationCalendar Load(string assetRoot)
    {
        var candidates = new[]
        {
            Path.Combine(assetRoot, "assets", "contracts", "pvp-rotation-v1.json"),
            Path.Combine(assetRoot, "Assets", "contracts", "pvp-rotation-v1.json")
        };
        var path = candidates.FirstOrDefault(File.Exists) ??
                   throw new FileNotFoundException("pvp-rotation-v1.json is missing");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new PvpRotationCalendar(Parse(document.RootElement.GetProperty("frontline")),
            Parse(document.RootElement.GetProperty("crystallineConflict")));
    }

    public PvpRotationSlot Frontline(DateTime utc) => Slot(frontline, utc);
    public PvpRotationSlot CrystallineConflict(DateTime utc) => Slot(crystallineConflict, utc);

    public PvpRotationSlot NextFrontline(DateTime utc)
    {
        var current = Frontline(utc);
        return Frontline(current.EndUtc.AddTicks(1));
    }

    public IReadOnlyList<PvpRotationSlot> FrontlineCycle(DateTime utc)
    {
        var result = new List<PvpRotationSlot>(frontline.Sequence.Length);
        var cursor = Frontline(utc);
        for (var index = 0; index < frontline.Sequence.Length; index++)
        {
            result.Add(cursor);
            cursor = Frontline(cursor.EndUtc.AddTicks(1));
        }
        return result;
    }

    public PvpRotationSlot NextCrystallineConflict(DateTime utc)
    {
        var current = CrystallineConflict(utc);
        return CrystallineConflict(current.EndUtc.AddTicks(1));
    }

    public IReadOnlyList<PvpRotationSlot> CrystallineConflictCycle(DateTime utc)
    {
        var result = new List<PvpRotationSlot>(crystallineConflict.Sequence.Length);
        var cursor = CrystallineConflict(utc);
        for (var index = 0; index < crystallineConflict.Sequence.Length; index++)
        {
            result.Add(cursor);
            cursor = CrystallineConflict(cursor.EndUtc.AddTicks(1));
        }
        return result;
    }

    public IReadOnlyList<PvpRotationSlot> FrontlineMonth(int year, int month, TimeZoneInfo zone)
    {
        var days = DateTime.DaysInMonth(year, month);
        var result = new List<PvpRotationSlot>(days);
        for (var day = 1; day <= days; day++)
        {
            // A Frontline rotation begins at 15:00 UTC. In UTC+8 that is
            // 23:00 local time, so assigning a day by the slot's local start
            // labels almost the entire date with tomorrow's map. The calendar
            // day is therefore defined by the map active at local noon.
            // 纷争前线月历按当地正午的生效地图归日，避免 23:00 换图把
            // 当天提前标成下一张地图。
            var localNoon = new DateTime(year, month, day, 12, 0, 0,
                DateTimeKind.Unspecified);
            result.Add(Frontline(TimeZoneInfo.ConvertTimeToUtc(localNoon, zone)));
        }
        return result;
    }

    private static RotationDefinition Parse(JsonElement element)
    {
        var maps = new Dictionary<string, MapDefinition>(StringComparer.Ordinal);
        foreach (var map in element.GetProperty("maps").EnumerateObject())
        {
            var names = map.Value.GetProperty("names").EnumerateObject()
                .ToDictionary(x => x.Name, x => x.Value.GetString() ?? map.Name,
                    StringComparer.OrdinalIgnoreCase);
            var dutyId = map.Value.TryGetProperty("dutyId", out var duty) ? duty.GetUInt32() : 0;
            maps[map.Name] = new MapDefinition(dutyId, names);
        }
        return new RotationDefinition(
            element.GetProperty("referenceUtc").GetDateTime().ToUniversalTime(),
            element.GetProperty("intervalMinutes").GetInt32(),
            element.GetProperty("sequence").EnumerateArray().Select(x => x.GetString()!).ToArray(), maps);
    }

    private static PvpRotationSlot Slot(RotationDefinition definition, DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        var intervalTicks = TimeSpan.FromMinutes(definition.IntervalMinutes).Ticks;
        var deltaTicks = utc.Ticks - definition.ReferenceUtc.Ticks;
        var slot = FloorDiv(deltaTicks, intervalTicks);
        var sequenceIndex = (int)(((slot % definition.Sequence.Length) + definition.Sequence.Length) % definition.Sequence.Length);
        var key = definition.Sequence[sequenceIndex];
        var map = definition.Maps[key];
        var start = definition.ReferenceUtc.AddTicks(slot * intervalTicks);
        return new PvpRotationSlot(key, map.DutyId, start, start.AddTicks(intervalTicks), map.Names);
    }

    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }
}
