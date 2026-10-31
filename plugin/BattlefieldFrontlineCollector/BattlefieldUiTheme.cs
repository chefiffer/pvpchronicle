using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Shared flat themes for every plugin window. Themes change the complete
/// surface hierarchy, not only the accent color, and retain compact 2px radii.
/// 全窗口共用的扁平主题；同时改变背景、表格、控件和文字层级，而不是只换强调色。
/// </summary>
internal static class BattlefieldUiTheme
{
    private const int StyleVarCount = 10;
    private const int StyleColorCount = 21;

    private sealed record Palette(Vector4 Window, Vector4 Child, Vector4 Popup,
        Vector4 Text, Vector4 Muted, Vector4 Border, Vector4 Frame,
        Vector4 FrameHover, Vector4 FrameActive, Vector4 Button,
        Vector4 ButtonHover, Vector4 ButtonActive, Vector4 Header,
        Vector4 HeaderHover, Vector4 HeaderActive, Vector4 Tab,
        Vector4 TabHover, Vector4 TabActive, Vector4 TableHeader,
        Vector4 TableAlt, Vector4 Accent);

    private static Palette Select(string theme) => theme switch
    {
        "day" => new(
            new(.93f,.94f,.95f,.99f), new(.97f,.975f,.98f,.97f), new(.98f,.985f,.99f,1),
            new(.09f,.11f,.13f,1), new(.35f,.39f,.43f,1), new(.66f,.69f,.72f,.95f),
            new(.86f,.88f,.90f,1), new(.79f,.84f,.88f,1), new(.70f,.79f,.85f,1),
            new(.82f,.85f,.87f,1), new(.72f,.79f,.84f,1), new(.62f,.73f,.80f,1),
            new(.82f,.86f,.89f,1), new(.72f,.80f,.85f,1), new(.63f,.74f,.80f,1),
            new(.86f,.88f,.90f,1), new(.73f,.80f,.85f,1), new(.64f,.75f,.81f,1),
            new(.79f,.83f,.86f,1), new(.88f,.90f,.92f,.72f), new(.08f,.39f,.58f,1)),
        "eye" => new(
            new(.88f,.855f,.79f,.995f), new(.94f,.915f,.85f,.98f), new(.95f,.925f,.86f,1),
            new(.16f,.135f,.10f,1), new(.40f,.36f,.28f,1), new(.59f,.54f,.43f,.92f),
            new(.82f,.78f,.68f,1), new(.76f,.71f,.60f,1), new(.69f,.63f,.51f,1),
            new(.80f,.75f,.64f,1), new(.73f,.67f,.55f,1), new(.65f,.58f,.45f,1),
            new(.82f,.77f,.66f,1), new(.74f,.68f,.55f,1), new(.66f,.59f,.45f,1),
            new(.84f,.80f,.71f,1), new(.74f,.68f,.55f,1), new(.65f,.58f,.44f,1),
            new(.79f,.74f,.64f,1), new(.86f,.82f,.74f,.75f), new(.66f,.36f,.12f,1)),
        "contrast" => new(
            new(.008f,.008f,.008f,1), new(.018f,.018f,.018f,1), new(.012f,.012f,.012f,1),
            Vector4.One, new(.76f,.76f,.76f,1), new(.72f,.72f,.72f,1),
            new(.07f,.07f,.07f,1), new(.16f,.16f,.16f,1), new(.25f,.25f,.25f,1),
            new(.08f,.08f,.08f,1), new(.18f,.18f,.18f,1), new(.30f,.30f,.30f,1),
            new(.08f,.08f,.08f,1), new(.20f,.20f,.20f,1), new(.32f,.32f,.32f,1),
            new(.06f,.06f,.06f,1), new(.20f,.20f,.20f,1), new(.33f,.33f,.33f,1),
            new(.10f,.10f,.10f,1), new(.055f,.055f,.055f,1), new(1f,.84f,.10f,1)),
        _ => new(
            new(.025f,.035f,.045f,.985f), new(.035f,.048f,.060f,.94f), new(.035f,.048f,.060f,.99f),
            new(.91f,.93f,.94f,1), new(.53f,.61f,.66f,1), new(.18f,.27f,.31f,.9f),
            new(.075f,.10f,.12f,.96f), new(.105f,.16f,.18f,1), new(.12f,.20f,.22f,1),
            new(.075f,.12f,.14f,1), new(.10f,.22f,.23f,1), new(.12f,.31f,.30f,1),
            new(.07f,.15f,.17f,.95f), new(.10f,.24f,.24f,1), new(.12f,.31f,.30f,1),
            new(.055f,.085f,.10f,1), new(.10f,.24f,.24f,1), new(.10f,.29f,.28f,1),
            new(.065f,.105f,.125f,1), new(.06f,.085f,.10f,.48f), new(.28f,.82f,.76f,1))
    };

    public static Vector4 Accent(string theme) => Select(theme).Accent;
    public static Vector4 Text(string theme) => Select(theme).Text;
    public static Vector4 Muted(string theme) => Select(theme).Muted;
    public static Vector4 ChartBackground(string theme) => Select(theme).Child;
    public static Vector4 Border(string theme) => Select(theme).Border;
    public static Vector4 Panel(string theme) => Select(theme).Child;
    public static Vector4 SelectedPanel(string theme)
    {
        var p = Select(theme);
        return new Vector4(
            p.Child.X * .62f + p.Accent.X * .38f,
            p.Child.Y * .62f + p.Accent.Y * .38f,
            p.Child.Z * .62f + p.Accent.Z * .38f,
            p.Child.W);
    }
    public static Vector4 WarningPanel(string theme) => theme == "day"
        ? new Vector4(.99f, .94f, .82f, 1f)
        : theme == "contrast" ? new Vector4(.12f, .10f, .02f, 1f)
        : theme == "eye" ? new Vector4(.92f, .84f, .65f, 1f)
        : new Vector4(.10f, .075f, .04f, .92f);
    public static Vector4 Grid(string theme) => theme == "day"
        ? new Vector4(.42f,.46f,.49f,.34f) : theme == "contrast"
            ? new Vector4(.92f,.92f,.92f,.72f) : theme == "eye"
                ? new Vector4(.41f,.37f,.29f,.31f) : new Vector4(.45f,.52f,.55f,.28f);

    /// <summary>
    /// A map keeps the same hue family between themes; only luminance and
    /// saturation change. 地图色相身份固定，主题仅调整明度与饱和度。
    /// </summary>
    public static Vector4 MapColor(string theme, string key)
    {
        var family = key switch
        {
            "seize" or "palaistra" => 0,
            "shatter" or "bayside" => 1,
            "naadam" or "castletown" => 2,
            "triumph" or "volcanic" or "redsands" => 3,
            "secure" or "cloudnine" => 4,
            "harmonias" => 5,
            _ => 5
        };
        var colors = theme switch
        {
            "day" => new[]
            {
                new Vector4(.84f,.58f,.16f,1), new(.18f,.48f,.86f,1),
                new(.13f,.58f,.42f,1), new(.86f,.29f,.30f,1),
                new(.49f,.27f,.79f,1), new(.17f,.53f,.61f,1)
            },
            "eye" => new[]
            {
                new Vector4(.62f,.45f,.21f,1), new(.30f,.43f,.55f,1),
                new(.31f,.48f,.36f,1), new(.58f,.34f,.30f,1),
                new(.43f,.35f,.55f,1), new(.31f,.47f,.49f,1)
            },
            "contrast" => new[]
            {
                new Vector4(1f,.82f,0f,1), new(0f,.55f,1f,1),
                new(0f,.82f,.52f,1), new(1f,.24f,.22f,1),
                new(.67f,.25f,1f,1), new(0f,.82f,.92f,1)
            },
            _ => new[]
            {
                new Vector4(.57f,.39f,.10f,1), new(.06f,.34f,.62f,1),
                new(.05f,.40f,.30f,1), new(.61f,.18f,.20f,1),
                new(.36f,.18f,.58f,1), new(.08f,.39f,.45f,1)
            }
        };
        return colors[family];
    }

    public static Vector4 MapForeground(string theme, string key)
    {
        var color = MapColor(theme, key);
        var luminance = color.X * .2126f + color.Y * .7152f + color.Z * .0722f;
        return luminance > .64f ? new Vector4(.02f,.025f,.03f,1) : Vector4.One;
    }

    public static Vector4 Focus(string theme) => theme == "contrast"
        ? new Vector4(1f, .92f, 0f, 1f)
        : theme == "eye" ? new Vector4(.74f, .38f, .10f, 1f)
        : new Vector4(1f, .73f, .12f, 1f);

    public static void Push(string theme)
    {
        var p = Select(theme);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 9));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(7, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(7, 4));

        foreach (var pair in new[]
        {
            (ImGuiCol.WindowBg,p.Window),(ImGuiCol.ChildBg,p.Child),(ImGuiCol.PopupBg,p.Popup),
            (ImGuiCol.Text,p.Text),(ImGuiCol.TextDisabled,p.Muted),(ImGuiCol.Border,p.Border),
            (ImGuiCol.FrameBg,p.Frame),(ImGuiCol.FrameBgHovered,p.FrameHover),(ImGuiCol.FrameBgActive,p.FrameActive),
            (ImGuiCol.Button,p.Button),(ImGuiCol.ButtonHovered,p.ButtonHover),(ImGuiCol.ButtonActive,p.ButtonActive),
            (ImGuiCol.Header,p.Header),(ImGuiCol.HeaderHovered,p.HeaderHover),(ImGuiCol.HeaderActive,p.HeaderActive),
            (ImGuiCol.Tab,p.Tab),(ImGuiCol.TabHovered,p.TabHover),(ImGuiCol.TabActive,p.TabActive),
            (ImGuiCol.TableHeaderBg,p.TableHeader),(ImGuiCol.TableRowBgAlt,p.TableAlt),(ImGuiCol.CheckMark,p.Accent)
        }) ImGui.PushStyleColor(pair.Item1, pair.Item2);
    }

    public static void Pop()
    {
        ImGui.PopStyleColor(StyleColorCount);
        ImGui.PopStyleVar(StyleVarCount);
    }
}
