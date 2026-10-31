namespace BattlefieldFrontlineCollector;

/// <summary>
/// Pure map viewport calculations shared by the ImGui renderer and contract tests.
/// Zoom 1 fits the whole map; every larger value reduces both visible axes.
/// </summary>
internal static class MapViewportMath
{
    public static (float SpanX, float SpanY) CalculateSpan(float aspectRatio, float zoom)
    {
        var aspect = System.Math.Max(.25f, aspectRatio);
        var safeZoom = System.Math.Max(1f, zoom);
        var fittedX = aspect >= 1f ? 1f : aspect;
        var fittedY = aspect >= 1f ? 1f / aspect : 1f;
        return (fittedX / safeZoom, fittedY / safeZoom);
    }

    /// <summary>
    /// Rejects trail segments which cross death, respawn, loading gaps or a
    /// map-scale teleport. Actor vitals are normally sampled every two seconds.
    /// </summary>
    public static bool CanConnectActorTrail(long previousElapsedMs, long currentElapsedMs,
        uint previousHp, uint currentHp, bool previousFellToZero, bool currentFellToZero,
        double previousX, double previousY, double previousZ,
        double currentX, double currentY, double currentZ)
    {
        var elapsed = currentElapsedMs - previousElapsedMs;
        if (elapsed <= 0 || elapsed > 3_000 || previousHp == 0 || currentHp == 0 ||
            previousFellToZero || currentFellToZero) return false;
        var dx = currentX - previousX;
        var dy = currentY - previousY;
        var dz = currentZ - previousZ;
        return dx * dx + dy * dy + dz * dz <= 45d * 45d;
    }
}
