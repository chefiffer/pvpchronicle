using System;
using System.Numerics;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Shared FFXIV world-to-map projection used by live and replay canvases.
/// Inputs are world X/Z plus the Territory Map sheet's SizeFactor and offsets;
/// output is normalized map UV. Frontline image assets use the neutral defaults.
/// </summary>
internal static class MapCoordinateProjection
{
    public static Vector2 WorldToUv(double x, double z, int sizeFactor = 100,
        int offsetX = 0, int offsetY = 0)
    {
        var scale = Math.Max(1, sizeFactor) / 100f;
        return new Vector2(((float)x + offsetX) * scale / 2048f + .5f,
            ((float)z + offsetY) * scale / 2048f + .5f);
    }
}
