using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Offline four-language Status-sheet catalogue.  It deliberately follows the
/// plugin language, which can differ from the game client's sheet language.
/// </summary>
internal sealed class StatusLocalizationCatalog
{
    private sealed record Entry(string? ZhHans, string? ZhHant, string? En, string? Ja,
        string? De = null, string? Fr = null);
    private sealed record Document(string Schema, Dictionary<string, Entry> Statuses);

    private readonly Dictionary<long, Entry> statuses = new();

    public StatusLocalizationCatalog(string assetRoot)
    {
        foreach (var directory in new[] { "assets", "Assets" })
        {
            var path = Path.Combine(assetRoot, directory, "localization", "status_names.json");
            if (!File.Exists(path)) continue;
            try
            {
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (document?.Statuses == null) return;
                foreach (var pair in document.Statuses)
                    if (long.TryParse(pair.Key, out var id)) statuses[id] = pair.Value;
                return;
            }
            catch { return; }
        }
    }

    public string? Find(long statusId, string language)
    {
        if (!statuses.TryGetValue(statusId, out var entry)) return null;
        return language switch
        {
            "zh-CN" => entry.ZhHans,
            "zh-TW" => entry.ZhHant,
            "en" => entry.En,
            "ja" => entry.Ja,
            "de" => entry.De,
            "fr" => entry.Fr,
            "th" or "vi" => entry.En,
            _ => null
        };
    }
}
