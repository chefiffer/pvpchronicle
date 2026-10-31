using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Shared compact combat-state widgets.  Keeping the HP/shield rendering here
/// prevents the live slice and the death recap from drifting apart visually.
/// 通用的紧凑战斗状态控件，确保实时切片与死亡记录使用完全一致的血量/护盾表达。
/// </summary>
internal static class CombatUiPrimitives
{
    public static uint ShieldAmount(uint maxHp, byte shieldPercent) =>
        maxHp == 0 || shieldPercent == 0 ? 0 : (uint)Math.Round(maxHp * shieldPercent / 100d);

    public static string FormatHpShield(uint currentHp, uint maxHp, byte shieldPercent)
    {
        if (maxHp == 0) return "—";
        var shield = ShieldAmount(maxHp, shieldPercent);
        return shield == 0 ? $"{currentHp:N0}/{maxHp:N0}" : $"{currentHp:N0}/{maxHp:N0} (+{shield:N0})";
    }

    public static void DrawHpShieldBar(ImDrawListPtr draw, Vector2 origin, Vector2 size,
        uint currentHp, uint maxHp, byte shieldPercent, bool showText)
    {
        var border = ImGui.ColorConvertFloat4ToU32(new Vector4(.07f, .08f, .09f, .95f));
        draw.AddRectFilled(origin, origin + size, border, 2);
        if (maxHp > 0)
        {
            var hpRatio = Math.Clamp(currentHp / (float)maxHp, 0, 1);
            var shieldRatio = Math.Clamp(shieldPercent / 100f, 0, 1);
            var hpEnd = origin.X + size.X * hpRatio;
            draw.AddRectFilled(origin + Vector2.One, new Vector2(hpEnd, origin.Y + size.Y - 1),
                ImGui.ColorConvertFloat4ToU32(new Vector4(.83f, .19f, .22f, 1f)), 2);
            var shieldEnd = origin.X + size.X * Math.Clamp(hpRatio + shieldRatio, 0, 1);
            if (shieldEnd > hpEnd)
                draw.AddRectFilled(new Vector2(hpEnd, origin.Y + 1),
                    new Vector2(shieldEnd, origin.Y + size.Y - 1),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(.28f, .72f, 1f, 1f)), 2);
        }
        draw.AddRect(origin, origin + size,
            ImGui.ColorConvertFloat4ToU32(new Vector4(.8f, .84f, .87f, .5f)), 3);
        if (!showText) return;
        var text = FormatHpShield(currentHp, maxHp, shieldPercent);
        var extent = ImGui.CalcTextSize(text);
        draw.AddText(origin + new Vector2(Math.Max(2, (size.X - extent.X) / 2),
                Math.Max(0, (size.Y - extent.Y) / 2)),
            ImGui.ColorConvertFloat4ToU32(Vector4.One), text);
    }

    public static void DrawHpShieldBar(uint currentHp, uint maxHp, byte shieldPercent, Vector2 size,
        string id)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##hp-shield-{id}", size);
        DrawHpShieldBar(ImGui.GetWindowDrawList(), origin, size, currentHp, maxHp, shieldPercent, true);
    }
}
