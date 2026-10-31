using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BattlefieldFrontlineCollector;

internal sealed class ActionLocalizationCatalog
{
    private sealed record Entry(string? ZhHans, string? ZhHant, string? En, string? Ja,
        string? De = null, string? Fr = null);
    private sealed record Document(string Schema, Dictionary<string, Entry> Actions);

    private readonly Dictionary<uint, Entry> actions = new();

    public ActionLocalizationCatalog(string assetRoot)
    {
        foreach (var directory in new[] { "assets", "Assets" })
        {
            var path = Path.Combine(assetRoot, directory, "localization", "action_names.json");
            if (!File.Exists(path)) continue;
            try
            {
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (document?.Actions == null) return;
                foreach (var pair in document.Actions)
                    if (uint.TryParse(pair.Key, out var id)) actions[id] = pair.Value;
                return;
            }
            catch { return; }
        }
    }

    public string? Find(uint actionId, string language)
    {
        if (!actions.TryGetValue(actionId, out var entry)) return null;
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
