using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Shared vector glyphs for the live slice map and historical replay map.
/// Inputs are decoded objective semantics; output is only drawing commands.
/// Keeping this renderer shared prevents one page from showing a circle while
/// the other page shows the rank polygon for the same objective.
/// </summary>
internal static class ObjectiveMapGlyphRenderer
{
    public static void Draw(ImDrawListPtr draw, Vector2 point, uint dutyId, string label,
        uint maxHp, Vector4 color, bool capturing, string state = "")
    {
        if (CrystallineMapMarkerCatalog.TryGetIcon(state, out var ccMapIcon))
        {
            DrawCrystallineMapMarker(draw, point, ccMapIcon, color);
            return;
        }
        if (label.Equals("cc_medicine_kit", StringComparison.OrdinalIgnoreCase))
        {
            var green = Pack(new Vector4(.24f, .88f, .46f, 1f));
            draw.AddLine(point + new Vector2(-5, 0), point + new Vector2(5, 0), green, 3f);
            draw.AddLine(point + new Vector2(0, -5), point + new Vector2(0, 5), green, 3f);
            return;
        }
        if (label.Equals(ArcheiaHarmoniasMechanics.HealingGlyphLabel,
                StringComparison.OrdinalIgnoreCase))
        {
            var green = Pack(new Vector4(.20f, .94f, .46f, 1f));
            draw.AddCircle(point, 8f, green, 24, 2f);
            draw.AddLine(point + new Vector2(-5, 0), point + new Vector2(5, 0), green, 2.5f);
            draw.AddLine(point + new Vector2(0, -5), point + new Vector2(0, 5), green, 2.5f);
            return;
        }
        if (label.Equals("cc_cloudnine_black_feather", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Black Chocobo Feather", StringComparison.OrdinalIgnoreCase))
        {
            var gold = Pack(new Vector4(1f, .78f, .24f, 1f));
            draw.AddLine(point + new Vector2(-6, 7), point + new Vector2(6, -7), gold, 2.5f);
            draw.AddLine(point + new Vector2(-4, 3), point + new Vector2(-8, -1), gold, 1.6f);
            draw.AddLine(point + new Vector2(0, -1), point + new Vector2(-3, -6), gold, 1.6f);
            draw.AddLine(point + new Vector2(3, -4), point + new Vector2(8, -2), gold, 1.6f);
            return;
        }
        if (label.Equals("cc_cloudnine_twister", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Turbulence", StringComparison.OrdinalIgnoreCase))
        {
            var cyan = Pack(new Vector4(.36f, .86f, 1f, 1f));
            draw.AddCircle(point + new Vector2(0, -4), 7, cyan, 18, 2f);
            draw.AddCircle(point + new Vector2(0, 2), 5, cyan, 16, 2f);
            draw.AddLine(point + new Vector2(-3, 8), point + new Vector2(3, 8), cyan, 2f);
            return;
        }
        if (label.Equals("cc_cloudnine_gale_sphere", StringComparison.OrdinalIgnoreCase))
        {
            var turquoise = Pack(new Vector4(.12f, .96f, .88f, 1f));
            draw.AddCircleFilled(point, 6f, turquoise, 20);
            draw.AddCircle(point, 9f, turquoise, 24, 2f);
            draw.AddCircle(point, 12f, Pack(new Vector4(.12f, .96f, .88f, .36f)), 24, 1.5f);
            return;
        }

        var packed = Pack(color);
        var rank = dutyId == SealRockObjectiveCatalog.DutyId
            ? SealRockObjectiveCatalog.RankFromEvidence(label, state)
            : dutyId == OnsalObjectiveCatalog.DutyId
                ? OnsalObjectiveCatalog.RankFromEvidence(label, state)
                : ObjectiveRank(label);
        var sides = dutyId is 130 or 701 or 1080
            ? dutyId == SealRockObjectiveCatalog.DutyId
                ? SealRockObjectiveCatalog.PolygonSides(rank)
                : rank switch { 'S' => 5, 'A' => 4, 'B' => 3, _ => 0 }
            : 0;
        if (sides > 0)
        {
            var rotation = sides == 4 ? MathF.PI / 4 : -MathF.PI / 2;
            if (dutyId is SealRockObjectiveCatalog.DutyId or OnsalObjectiveCatalog.DutyId)
                DrawPolygonFilled(draw, point, 11, sides, Pack(color with { W = .46f }), rotation);
            DrawPolygon(draw, point, 11, sides, packed, 3f, rotation);
        }
        else if (dutyId is SealRockObjectiveCatalog.DutyId or OnsalObjectiveCatalog.DutyId)
        {
            return;
        }
        else
        {
            var radius = dutyId == ShatterObjectiveCatalog.DutyId
                ? IceObjectiveRadius(label, maxHp) : 11f;
            draw.AddCircle(point, radius, packed, 24, 3f);
        }
        if (capturing)
            for (var segment = 0; segment < 16; segment += 2)
                draw.AddLine(point + Polar(14, segment * MathF.PI / 8),
                    point + Polar(14, (segment + 1) * MathF.PI / 8), packed, 2f);
    }

    public static void DrawSpawnLabel(ImDrawListPtr draw, Vector2 point, string text,
        Vector4 color, bool isLocalTeam)
    {
        var packed = Pack(color);
        var extent = ImGui.CalcTextSize(text);
        var at = point - new Vector2(extent.X / 2, extent.Y / 2);
        draw.AddText(at, packed, text);
        if (isLocalTeam)
            draw.AddLine(new Vector2(at.X, at.Y + extent.Y + 3),
                new Vector2(at.X + extent.X, at.Y + extent.Y + 3), packed, 4f);
    }

    public static void DrawSecureNode(ImDrawListPtr draw, Vector2 point, string code,
        bool isHome, Vector4 ownerColor, Vector4 homeOrLabelColor)
    {
        var ownerPacked = Pack(ownerColor);
        var labelPacked = Pack(homeOrLabelColor);
        if (isHome)
        {
            draw.AddQuadFilled(point + new Vector2(0, -7), point + new Vector2(7, 0),
                point + new Vector2(0, 7), point + new Vector2(-7, 0), labelPacked);
            draw.AddQuad(point + new Vector2(0, -8), point + new Vector2(8, 0),
                point + new Vector2(0, 8), point + new Vector2(-8, 0), ownerPacked, 2f);
        }
        else
        {
            draw.AddCircleFilled(point, 5.5f, Pack(ownerColor with { W = .32f }));
            draw.AddCircle(point, 6.5f, ownerPacked, 20, 2f);
        }
        draw.AddText(point + new Vector2(10, -8), labelPacked, code);
    }

    private static void DrawCrystallineMapMarker(ImDrawListPtr draw, Vector2 point,
        uint iconId, Vector4 color)
    {
        var packed = Pack(color);
        if (CrystallineMapMarkerCatalog.IsTacticalCrystal(iconId))
        {
            // The PvP-map asset is a chain-like central marker. Two linked
            // rings preserve that distinction without copying the game asset.
            draw.AddCircle(point + new Vector2(-4, 0), 6f, packed, 20, 2.5f);
            draw.AddCircle(point + new Vector2(4, 0), 6f, packed, 20, 2.5f);
            draw.AddLine(point + new Vector2(-1, -3), point + new Vector2(1, 3), packed, 2f);
            return;
        }
        if (CrystallineMapMarkerCatalog.IsCheckpointDiamond(iconId))
        {
            draw.AddQuadFilled(point + new Vector2(0, -8), point + new Vector2(8, 0),
                point + new Vector2(0, 8), point + new Vector2(-8, 0), Pack(color with { W = .42f }));
            draw.AddQuad(point + new Vector2(0, -9), point + new Vector2(9, 0),
                point + new Vector2(0, 9), point + new Vector2(-9, 0), packed, 2.5f);
            return;
        }
        if (CrystallineMapMarkerCatalog.IsCheckpointRing(iconId))
        {
            draw.AddCircleFilled(point, 6f, Pack(color with { W = .32f }), 24);
            draw.AddCircle(point, 8f, packed, 24, 2.5f);
        }
    }

    private static char ObjectiveRank(string label)
    {
        foreach (var line in label.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Rank ", StringComparison.OrdinalIgnoreCase)) continue;
            var value = trimmed[5..].TrimStart();
            if (value.Length > 0 && value[0] is 'S' or 'A' or 'B') return value[0];
        }
        foreach (var rank in new[] { 'S', 'A', 'B' })
            if (label.Contains($"{rank}级", StringComparison.OrdinalIgnoreCase) ||
                label.Contains($"{rank}級", StringComparison.OrdinalIgnoreCase) ||
                label.Contains($"{rank}ランク", StringComparison.OrdinalIgnoreCase))
                return rank;
        for (var i = 0; i < label.Length; i++)
            if ((label[i] is 'S' or 'A' or 'B') &&
                (i == 0 || !char.IsLetter(label[i - 1])) &&
                (i + 1 == label.Length || !char.IsLetter(label[i + 1]))) return label[i];
        return '\0';
    }

    private static float IceObjectiveRadius(string label, uint maxHp)
    {
        var code = ShatterObjectiveCatalog.ShortPointCode(label);
        if (code.Length == 0) code = ShatterObjectiveCatalog.CompactPointCode(label);
        if (code.StartsWith('A')) return 15f;
        if (code is "B13" or "B14" or "B15") return 11f;
        if (code.StartsWith('B')) return 7f;
        if (label.Contains("large", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("大冰", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("大型", StringComparison.OrdinalIgnoreCase)) return 15f;
        if (label.Contains("small", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("小冰", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("小型", StringComparison.OrdinalIgnoreCase)) return 7f;
        return maxHp > 0 ? 11f : 9f;
    }

    private static void DrawPolygon(ImDrawListPtr draw, Vector2 center, float radius, int sides,
        uint color, float thickness, float rotation)
    {
        for (var i = 0; i < sides; i++)
            draw.AddLine(center + Polar(radius, rotation + i * MathF.Tau / sides),
                center + Polar(radius, rotation + (i + 1) * MathF.Tau / sides), color, thickness);
    }

    private static void DrawPolygonFilled(ImDrawListPtr draw, Vector2 center, float radius, int sides,
        uint color, float rotation)
    {
        for (var i = 0; i < sides; i++)
            draw.AddTriangleFilled(center,
                center + Polar(radius, rotation + i * MathF.Tau / sides),
                center + Polar(radius, rotation + (i + 1) * MathF.Tau / sides), color);
    }

    private static Vector2 Polar(float radius, float angle) =>
        new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);

    private static uint Pack(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);
}
