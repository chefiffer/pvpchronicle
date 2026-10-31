using System;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Versioned, territory-scoped mapping for Crystalline Conflict objects seen
/// in real captures. Object row IDs are not globally unique mechanic IDs:
/// BaseId 8062 is a trick floor in Clockwork Castletown, but a geyser/antlion
/// presentation object in Red Sands. Unknown objects remain raw evidence.
/// </summary>
internal static class CrystallineMechanismCatalog
{
    public const string Version = "cc-mechanism-object-catalog-v8";
    public const uint VolcanicBombExplosionActionId = 28726;
    // Verified in the 2026-10-30 Cloud Nine capture: BaseId 9020 is named
    // Turbulence and emits this damage/knockback action while the post-event
    // twister moves across the arena.  It is replay evidence only, never a
    // client-side hit/range adjudicator.
    public const uint CloudNineTwisterActionId = 28729;
    // Visual replay geometry only. The official arena guide establishes the
    // cross-shaped eruption; these conservative map dimensions remain
    // versioned with the catalogue and are not used for hit adjudication.
    public const float VolcanicBombCrossArmYalms = 30f;
    public const float VolcanicBombCrossHalfWidthYalms = 3f;

    internal readonly record struct Entry(string Label, bool HasMeaningfulHp);

    public static bool TryResolve(uint territoryId, uint baseId, string? name, out Entry entry)
    {
        var redSands = territoryId is 1138 or 1139;
        var castletown = territoryId is 1116 or 1117;
        var volcanic = territoryId is 1033 or 1059;
        var cloudNine = territoryId is 1034 or 1060;
        var harmonias = ArcheiaHarmoniasMechanics.IsTerritory(territoryId);

        entry = baseId switch
        {
            // Pickable medicine kit. BaseId 2014796 is only its persistent map
            // placeholder and is deliberately not promoted.
            2012832 => new("cc_medicine_kit", false),
            14471 when volcanic => new("cc_bomb_core", true),
            // Cloud Nine rows below were matched against names and lifetimes
            // in an actual territory 1034 capture. An unrelated empty-name
            // BaseId 9020 is already loaded at the central point near match
            // start, so 9020/14534 become a twister only with a verified
            // Turbulence name. Spatial merging prevents the verified pair from
            // producing duplicate labels.
            14472 when cloudNine => new("cc_cloudnine_black_feather", false),
            9020 or 14534 when IsVerifiedCloudNineTurbulence(territoryId, baseId, name) =>
                new("cc_cloudnine_twister", false),
            // Verified against the same territory-1034 capture and the
            // official one-player pickup/respawn rule: twelve empty-name
            // objects appear with Turbulence at fixed route positions;
            // individual objects unload beside a player gaining status 4385
            // (Swift Sprint), then respawn at the identical coordinate.
            // The simultaneous final unload is the end of the arena event.
            2014410 when cloudNine => new("cc_cloudnine_gale_sphere", false),
            ClockworkCastletownMechanics.TrickFloorBaseId when castletown =>
                new("cc_trick_floor", false),
            ClockworkCastletownMechanics.KageyamaBaseId when castletown =>
                new("cc_kageyama", false),
            ClockworkCastletownMechanics.ClockworkYojimboBaseId when castletown =>
                new("cc_clockwork_yojimbo", false),
            ClockworkCastletownMechanics.ClockworkOnmyojiBaseId when castletown =>
                new("cc_clockwork_onmyoji", false),
            ArcheiaHarmoniasMechanics.HealingGlyphBaseId when harmonias =>
                new(ArcheiaHarmoniasMechanics.HealingGlyphLabel, false),
            // BaseId 8062 is a shared presentation helper in Red Sands. The
            // name prevents the antlion helper from being mislabeled as a
            // geyser; active telegraphs are still bounded by action evidence.
            8062 when redSands && IsRedSandsAntlionName(name) =>
                new("cc_redsands_antlion", false),
            8062 when redSands => new("cc_redsands_geyser", false),
            16563 when redSands => new("cc_redsands_antlion", false),
            16040 when redSands => new("cc_redsands_sabotender", false),
            16564 when redSands => new("cc_redsands_acutender", false),
            _ => default
        };
        if (!string.IsNullOrEmpty(entry.Label)) return true;

        // Narrow English fallbacks support captures whose BaseId was absent.
        // Localized display names are produced by the UI catalogue, not by
        // copying these client strings into the replay.
        var value = name?.Trim() ?? string.Empty;
        if (volcanic && value.Equals("Bomb Core", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_bomb_core", true);
        else if (cloudNine && value.Equals("Black Chocobo Feather", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_cloudnine_black_feather", false);
        else if (cloudNine && value.Equals("Turbulence", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_cloudnine_twister", false);
        else if (castletown && value.Equals("Trick Floor", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_trick_floor", false);
        else if (castletown && value.Equals("Clockwork Yojimbo", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_clockwork_yojimbo", false);
        else if (castletown && value.Equals("Clockwork Onmyoji", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_clockwork_onmyoji", false);
        else if (castletown && value.Equals("Kageyama", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_kageyama", false);
        else if (redSands && value.Equals("Geyser", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_redsands_geyser", false);
        else if (redSands && value.Contains("Red Sands Antlion", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_redsands_antlion", false);
        else if (redSands && value.Equals("Sabotender", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_redsands_sabotender", false);
        else if (redSands && value.Equals("Acutender", StringComparison.OrdinalIgnoreCase))
            entry = new("cc_redsands_acutender", false);
        return !string.IsNullOrEmpty(entry.Label);
    }

    /// <summary>
    /// True only for the named Cloud Nine Turbulence object pair. BaseId alone
    /// is insufficient: the real 2026-10-30 capture contains an empty-name
    /// BaseId 9020 at (0,0) long before Turbulence begins.
    /// </summary>
    public static bool IsVerifiedCloudNineTurbulence(uint territoryId, uint baseId, string? name)
    {
        if (territoryId is not (1034 or 1060) || baseId is not (9020 or 14534)) return false;
        var value = name?.Trim() ?? string.Empty;
        return value.Equals("Turbulence", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("乱気流", StringComparison.Ordinal) ||
               value.Equals("湍流", StringComparison.Ordinal);
    }

    /// <summary>
    /// Territory-scoped localized names observed or supplied by the supported
    /// game locales. BaseId 8062 is shared, so a name match is required before
    /// classifying its antlion helper.
    /// </summary>
    public static bool IsRedSandsAntlionName(string? name)
    {
        var value = name?.Trim() ?? string.Empty;
        return value.Contains("Antlion", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Ameisenlöwe", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Fourmilion", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("アントリオン", StringComparison.Ordinal) ||
               value.Contains("红沙蚁狮", StringComparison.Ordinal) ||
               value.Contains("紅沙蟻獅", StringComparison.Ordinal);
    }
}
