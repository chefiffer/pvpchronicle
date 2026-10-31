using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Shared marker rendering for live observation and historical replay.
/// 实时观察与历史复盘共用同一套标点图形，避免同一标点在两个页面产生不同语义。
/// </summary>
internal static class MarkerUiPrimitives
{
    public readonly record struct MapViewport(Vector2 UvMin, Vector2 UvMax,
        float SpanX, float SpanY, bool WheelConsumed);

    /// <summary>
    /// Game icon rows verified against the extracted "09 头顶标记（大饼 123）"
    /// resources. 61301-8 are attack markers, 61311-3 bind, 61321-2
    /// ignore, 61331-4 shapes. The extracted field-marker icon order is
    /// A, B, C, 1, 2, 3, D, 4 (not the semantic A-D/1-4 order).
    /// </summary>
    public static uint IconId(string key, bool field, int markerIndex = -1)
    {
        key = NormalizeKey(key, field, markerIndex);
        if (field)
            return key switch
            {
                "A" => 61341, "B" => 61342, "C" => 61343, "D" => 61347,
                "1" => 61344, "2" => 61345, "3" => 61346, "4" => 61348,
                _ => 0
            };
        if (key.StartsWith("attack_", StringComparison.Ordinal) &&
            int.TryParse(key[7..], out var attack) && attack is >= 1 and <= 8)
            return (uint)(61300 + attack);
        if (key.StartsWith("bind_", StringComparison.Ordinal) &&
            int.TryParse(key[5..], out var bind) && bind is >= 1 and <= 3)
            return (uint)(61310 + bind);
        if (key.StartsWith("ignore_", StringComparison.Ordinal) &&
            int.TryParse(key[7..], out var ignore) && ignore is >= 1 and <= 2)
            return (uint)(61320 + ignore);
        return key switch
        {
            "square" => 61331, "circle" => 61332,
            "cross" => 61333, "triangle" => 61334, _ => 0
        };
    }

    /// <summary>
    /// Old uploaded records may only contain target_#/field_# even though the
    /// marker index is still authoritative. Normalize those records before
    /// choosing the icon; never turn an implementation key into a fake badge.
    /// 舊資料可能只留下 target_#/field_#，此時以仍然可靠的索引還原遊戲標記。
    /// </summary>
    public static string NormalizeKey(string key, bool field, int markerIndex = -1)
    {
        if (field)
        {
            if (key is "A" or "B" or "C" or "D" or "1" or "2" or "3" or "4") return key;
            return markerIndex switch
            {
                0 => "A", 1 => "B", 2 => "C", 3 => "D",
                4 => "1", 5 => "2", 6 => "3", 7 => "4", _ => key
            };
        }

        if (key.StartsWith("attack_", StringComparison.Ordinal) ||
            key.StartsWith("bind_", StringComparison.Ordinal) ||
            key.StartsWith("ignore_", StringComparison.Ordinal) ||
            key is "square" or "circle" or "cross" or "triangle") return key;
        return markerIndex switch
        {
            >= 0 and <= 4 => $"attack_{markerIndex + 1}",
            >= 5 and <= 7 => $"bind_{markerIndex - 4}",
            >= 8 and <= 9 => $"ignore_{markerIndex - 7}",
            10 => "square", 11 => "circle", 12 => "cross", 13 => "triangle",
            >= 14 and <= 16 => $"attack_{markerIndex - 8}", _ => key
        };
    }

    public static string AssetFileName(uint iconId) => $"{iconId:D6}_hr1.png";

    /// <summary>
    /// Creates one stable, input-owning map canvas. The view is updated before
    /// it is drawn and uses per-frame MouseDelta, avoiding the old global drag
    /// reset that made surrounding ImGui content visibly jump.
    /// </summary>
    public static MapViewport BeginMapCanvas(string id, Vector2 size, ref float zoom,
        ref Vector2 centerUv, float resetZoom, float rightControlInset = 0)
    {
        // Overlay controls are drawn after the canvas.  ImGui does not allow a
        // later overlapping item to steal the active ID from this full-canvas
        // button, so reserve the control strip in the hit-test geometry itself.
        // The visual map and pan calculation still use the complete map size.
        var interactionSize = new Vector2(Math.Max(1, size.X - Math.Max(0, rightControlInset)), size.Y);
        ImGui.InvisibleButton(id, interactionSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var io = ImGui.GetIO();
        // The map deliberately does not own the mouse wheel. The detail page is
        // vertically scrollable, so wheel zoom made one gesture move both the map
        // and the page. Zoom is now controlled by the slider drawn inside the map.
        var wheelConsumed = false;

        var aspect = Math.Max(.25f, size.X / Math.Max(1, size.Y));
        var (spanX, spanY) = MapViewportMath.CalculateSpan(aspect, zoom);
        if ((hovered || active) &&
            (ImGui.IsMouseDragging(ImGuiMouseButton.Left) ||
             ImGui.IsMouseDragging(ImGuiMouseButton.Right)))
        {
            centerUv -= new Vector2(io.MouseDelta.X / Math.Max(1, size.X) * spanX,
                io.MouseDelta.Y / Math.Max(1, size.Y) * spanY);
        }
        if (hovered && (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) ||
                        ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Right)))
        {
            zoom = resetZoom;
            centerUv = new Vector2(.5f, .5f);
            (spanX, spanY) = MapViewportMath.CalculateSpan(aspect, zoom);
        }
        centerUv.X = Math.Clamp(centerUv.X, spanX / 2, 1 - spanX / 2);
        centerUv.Y = Math.Clamp(centerUv.Y, spanY / 2, 1 - spanY / 2);
        return new MapViewport(centerUv - new Vector2(spanX, spanY) / 2,
            centerUv + new Vector2(spanX, spanY) / 2, spanX, spanY, wheelConsumed);
    }

    public static void Draw(ImDrawListPtr draw, Vector2 center, string key, bool field, float radius)
    {
        var color = Color(key, field);
        var fill = ImGui.ColorConvertFloat4ToU32(color with { W = .92f });
        var outline = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, .88f));
        if (!field && key is ("square" or "circle" or "cross" or "triangle"))
        {
            if (key == "square") draw.AddRectFilled(center - new Vector2(radius), center + new Vector2(radius), fill, 2);
            else if (key == "circle") draw.AddCircleFilled(center, radius, fill, 24);
            else if (key == "triangle") draw.AddTriangleFilled(center + new Vector2(0, -radius),
                center + new Vector2(radius, radius), center + new Vector2(-radius, radius), fill);
            else
            {
                draw.AddLine(center - new Vector2(radius), center + new Vector2(radius), fill, 4);
                draw.AddLine(center + new Vector2(radius, -radius), center + new Vector2(-radius, radius), fill, 4);
            }
            return;
        }

        draw.AddCircleFilled(center, radius, fill, 24);
        draw.AddCircle(center, radius, outline, 24, 1.2f);
        var label = Glyph(key, field);
        var extent = ImGui.CalcTextSize(label);
        draw.AddText(center - extent / 2, ImGui.ColorConvertFloat4ToU32(Vector4.One), label);
    }

    public static string Glyph(string key, bool field)
    {
        if (field) return key;
        if (key.StartsWith("attack_", StringComparison.Ordinal)) return key[7..];
        if (key.StartsWith("bind_", StringComparison.Ordinal)) return "B" + key[5..];
        if (key.StartsWith("ignore_", StringComparison.Ordinal)) return "X" + key[7..];
        return key.Length == 0 ? "?" : key[..1].ToUpperInvariant();
    }

    public static Vector4 Color(string key, bool field)
    {
        if (field)
            return key is "A" or "1" ? new Vector4(.96f, .28f, .24f, 1) :
                key is "B" or "2" ? new Vector4(.95f, .78f, .18f, 1) :
                key is "C" or "3" ? new Vector4(.24f, .62f, 1f, 1) :
                new Vector4(.72f, .35f, .96f, 1);
        if (key.StartsWith("attack_", StringComparison.Ordinal)) return new Vector4(.96f, .25f, .22f, 1);
        if (key.StartsWith("bind_", StringComparison.Ordinal)) return new Vector4(.30f, .62f, 1f, 1);
        if (key.StartsWith("ignore_", StringComparison.Ordinal)) return new Vector4(.62f, .66f, .70f, 1);
        return new Vector4(.94f, .76f, .25f, 1);
    }
}
