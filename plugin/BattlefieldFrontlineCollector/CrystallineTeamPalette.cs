using System.Numerics;

namespace BattlefieldFrontlineCollector;

/// <summary>Shared Astra/Umbra colours for every Crystalline Conflict view.</summary>
internal static class CrystallineTeamPalette
{
    public static readonly Vector4[] Colors =
    [
        new(.28f, .64f, 1f, 1f), // Astra / blue
        new(.96f, .31f, .37f, 1f) // Umbra / red
    ];
}
