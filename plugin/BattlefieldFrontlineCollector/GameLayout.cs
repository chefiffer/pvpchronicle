namespace BattlefieldFrontlineCollector;

internal static class GameLayout
{
    public const string LayoutVersion = "ffxiv-2026-10-pvpstats-ba41904";
    public const string ResultSignature = "4C 8B DC 55 56 57 41 56 49 8D AB";
    public const int ScoreOffset = 0x24D2;
    public const int TeamStride = 0x20;
    public const int BattleHighRelativeOffset = 0xEE5;

    // ContentFinderCondition row IDs. Keep this allow-list versioned with the layout.
    public static bool IsFrontlineDuty(uint dutyId) => dutyId is 127 or 130 or 180 or 701 or 1080;

    public static bool IsCrystallineDuty(uint dutyId) => dutyId is
        835 or 836 or 837 or 912 or 967 or 1046 or 1102 or
        838 or 841 or 847 or 850 or 853 or 856 or 859 or
        839 or 842 or 848 or 851 or 854 or 857 or 860 or
        840 or 843 or 849 or 852 or 855 or 858 or 861 or
        913 or 914 or 915 or 916 or 917 or 918 or 919 or 920 or 921 or 922 or
        968 or 969 or 970 or 971 or 972 or 973 or 974 or 975 or 976 or 977 or
        1047 or 1048 or 1049 or 1050 or 1051 or 1052 or 1053 or 1054 or 1055 or 1056 or
        1103 or 1104 or 1105 or 1106 or 1107 or 1108 or 1109 or 1110 or 1111 or 1112;

    public static bool IsSupportedDuty(uint dutyId) => IsFrontlineDuty(dutyId) || IsCrystallineDuty(dutyId);
}
