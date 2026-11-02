using System.Numerics;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// One shared Frontline faction palette for live and replay maps.
/// Team indices follow the capture contract: Maelstrom=0, Adders=1, Flames=2.
/// </summary>
internal static class FrontlineTeamPalette
{
    public static readonly Vector4[] Colors =
    [
        new(.96f, .31f, .37f, 1f),
        new(.97f, .76f, .23f, 1f),
        new(.28f, .64f, 1f, 1f)
    ];
}
