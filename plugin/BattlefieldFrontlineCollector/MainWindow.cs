using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using WorldSheet = Lumina.Excel.Sheets.World;
using ClassJobSheet = Lumina.Excel.Sheets.ClassJob;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace BattlefieldFrontlineCollector;

internal sealed record CollectorUiState(bool Capturing, string MatchUid, long ElapsedMs,
    string OutputPath, int VisibleActors, int DroppedEvents, string LocalPlayerName,
    uint LocalPlayerEntityId, ushort LocalPlayerWorldId, byte LocalPlayerBattalion, uint DutyId,
    LiveScoreProjection Score);

internal sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly MatchRepository repository;
    private readonly UploadService uploads;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly Dictionary<ushort, string> worldNames = new();
    private readonly Dictionary<byte, string> jobNames = new();
    private readonly Dictionary<byte, ISharedImmediateTexture> jobIcons = new();
    private readonly Dictionary<uint, ISharedImmediateTexture> actionIcons = new();
    private readonly Action saveConfiguration;
    private readonly Action<LocalMatchAnalysis> openDetailedAnalysis;
    private readonly Action openLiveCombat;
    private readonly string captureDirectory;
    private readonly string importDirectory;
    private string CaptureDirectory(string mode) => Path.Combine(captureDirectory, mode);
    private string ImportDirectory(string mode) => Path.Combine(importDirectory, mode);
    private readonly Func<CollectorUiState> state;
    private readonly PvpRotationCalendar rotationCalendar;
    private readonly CrystallineRankHistory crystallineRankHistory;
    private readonly CrystallineMatchHistory crystallineMatchHistory;
    private readonly CrystallineActReplayArchive crystallineActReplayArchive;
    private CrystallineActReplay? selectedCrystallineReplay;
    private string selectedCrystallineActorId = string.Empty;
    private CrystallineMatchRecord? selectedCrystallineResult;
    private LocalMatchAnalysis? selected;
    private DateTime calendarMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1);
    private DateTime calendarDay = DateTime.Today;
    private bool calendarTabVisibleLastFrame;
    private long centeredCrystallineHourKey = long.MinValue;
    private int crystallineCalendarFirstDayOffset = -1;
    private int crystallineCalendarHalfStartHour = DateTime.Now.Hour < 12 ? 0 : 12;
    private byte historyJobFilter;
    private uint historyDutyFilter;
    private bool selectFrontlineTab;
    private bool selectCrystallineTab;
    private bool selectRivalWingsTab;
    private int playerSortColumn = 9;
    private bool playerSortDescending = true;
    private int crystallineResultSortColumn = 10;
    private bool crystallineResultSortDescending = true;
    private int crystallineActSortColumn = 5;
    private bool crystallineActSortDescending = true;
    private int crystallineActionSortColumn = 2;
    private bool crystallineActionSortDescending = true;
    private volatile bool importRunning;
    private string importStatus = string.Empty;
    private Task<CredentialRegistrationOutcome>? credentialRegistration;
    private CancellationTokenSource? credentialRegistrationCancellation;
    private string credentialRegistrationStatus = string.Empty;

    private sealed record CredentialRegistrationOutcome(bool Ok, string? Token, string Message);

    public MainWindow(Configuration configuration, MatchRepository repository, UploadService uploads,
        IDataManager dataManager, ITextureProvider textureProvider,
        Action saveConfiguration, Func<CollectorUiState> state,
        Action<LocalMatchAnalysis> openDetailedAnalysis, Action openLiveCombat,
        string captureDirectory, string importDirectory, PvpRotationCalendar rotationCalendar,
        CrystallineRankHistory crystallineRankHistory, CrystallineMatchHistory crystallineMatchHistory,
        CrystallineActReplayArchive crystallineActReplayArchive)
        : base("Battlefield Chronicle · 戰場紀錄###BattlefieldChronicle",
            ImGuiWindowFlags.NoScrollbar)
    {
        this.configuration = configuration;
        this.repository = repository;
        this.uploads = uploads;
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;
        this.saveConfiguration = saveConfiguration;
        this.state = state;
        this.openDetailedAnalysis = openDetailedAnalysis;
        this.openLiveCombat = openLiveCombat;
        this.captureDirectory = captureDirectory;
        this.importDirectory = importDirectory;
        foreach (var mode in new[] { "frontline", "crystalline-conflict", "rival-wings" })
        {
            Directory.CreateDirectory(CaptureDirectory(mode));
            Directory.CreateDirectory(ImportDirectory(mode));
        }
        this.rotationCalendar = rotationCalendar;
        this.crystallineRankHistory = crystallineRankHistory;
        this.crystallineMatchHistory = crystallineMatchHistory;
        this.crystallineActReplayArchive = crystallineActReplayArchive;
        Size = new Vector2(1080, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    public override void PreDraw() => BattlefieldUiTheme.Push(configuration.UiTheme);
    public override void PostDraw() => BattlefieldUiTheme.Pop();

    public override void Draw()
    {
        var snapshot = state();
        DrawHeader(snapshot);
        ImGui.Spacing();
        if (ImGui.BeginTabBar("main-tabs"))
        {
            if (ImGui.BeginTabItem(T("tabStatus") + "###status")) { DrawStatus(snapshot); ImGui.EndTabItem(); }
            var calendarTabVisible = ImGui.BeginTabItem(T("tabCalendar") + "###calendar");
            if (calendarTabVisible)
            {
                DrawCalendar(!calendarTabVisibleLastFrame);
                ImGui.EndTabItem();
            }
            calendarTabVisibleLastFrame = calendarTabVisible;
            var frontlineFlags = selectFrontlineTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (configuration.ShowFrontline &&
                ImGui.BeginTabItem(T("tabFrontlineMatches") + "###frontline-matches", frontlineFlags))
            {
                selectFrontlineTab = false;
                DrawMatches();
                ImGui.EndTabItem();
            }
            var crystallineFlags = selectCrystallineTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (configuration.ShowCrystallineConflict &&
                ImGui.BeginTabItem(T("tabCrystallineMatches") + "###crystalline-matches", crystallineFlags))
            {
                selectCrystallineTab = false;
                DrawCrystallineMatches();
                ImGui.EndTabItem();
            }
            var rivalFlags = selectRivalWingsTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (configuration.ShowRivalWings &&
                ImGui.BeginTabItem(T("tabRivalWingsMatches") + "###rival-wings-matches", rivalFlags))
            {
                selectRivalWingsTab = false;
                DrawRivalWingsMatches();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(T("tabSettings") + "###settings")) { DrawSettings(snapshot); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    private sealed record CalendarStats(int Matches, int Wins, int Decided)
    {
        public double? WinRate => Decided == 0 ? null : Wins * 100d / Decided;
    }

    private void DrawCalendar(bool centerCurrentCrystallineHour)
    {
        ImGui.BeginChild("calendar-scroll", Vector2.Zero, false);
        var nowUtc = DateTime.UtcNow;
        var zone = TimeZoneInfo.Local;
        var culture = UiCulture();
        var frontline = rotationCalendar.Frontline(nowUtc);
        var nextFrontline = rotationCalendar.NextFrontline(nowUtc);
        var crystalline = rotationCalendar.CrystallineConflict(nowUtc);
        var nextCrystalline = rotationCalendar.NextCrystallineConflict(nowUtc);
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("calendarTitle"));
        ImGui.SameLine();
        ImGui.TextDisabled(string.Format(T("rotationTimeZone"), zone.StandardName));
        ImGui.TextWrapped(T("calendarHelp"));
        if (!configuration.ShowFrontline && !configuration.ShowCrystallineConflict)
            ImGui.TextDisabled(T("allModesHidden"));

        if (configuration.ShowFrontline)
        {
            ImGui.Spacing(); ImGui.Separator();
            ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("frontlineMode"));
            DrawModeRotationStrip("frontline-rotation", T("frontlineNow"),
                $"{T("nextRotationMap")} · {T("frontlineMode")}", frontline, nextFrontline,
                nowUtc, culture);
            ImGui.Spacing();

        if (ImGui.Button($"‹ {T("calendarPrevious")}")) calendarMonth = calendarMonth.AddMonths(-1);
        ImGui.SameLine();
        if (ImGui.Button(T("today")))
        {
            calendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            calendarDay = DateTime.Today;
        }
        ImGui.SameLine();
        if (ImGui.Button($"{T("calendarNext")} ›")) calendarMonth = calendarMonth.AddMonths(1);
        ImGui.SameLine();
        ImGui.SetWindowFontScale(1.15f);
        ImGui.TextUnformatted(calendarMonth.ToString("Y", culture));
        ImGui.SetWindowFontScale(1f);

        var slots = rotationCalendar.FrontlineMonth(calendarMonth.Year, calendarMonth.Month, zone)
            .Select((slot, index) => (Date: new DateTime(calendarMonth.Year,
                calendarMonth.Month, index + 1), Slot: slot))
            .ToDictionary(value => value.Date, value => value.Slot);
        var stats = BuildCalendarStats(zone);
        var first = new DateTime(calendarMonth.Year, calendarMonth.Month, 1);
        var offset = ((int)first.DayOfWeek + 6) % 7; // Monday-first, stable across locales.
        var days = DateTime.DaysInMonth(calendarMonth.Year, calendarMonth.Month);
        if (ImGui.BeginTable("frontline-calendar", 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
        {
            var dayNames = WeekdayNames(culture);
            for (var column = 0; column < 7; column++)
            {
                ImGui.TableNextColumn();
                var available = ImGui.GetContentRegionAvail().X;
                var measured = ImGui.CalcTextSize(dayNames[column]).X;
                if (measured > available && available > 10)
                    ImGui.SetWindowFontScale(Math.Max(.72f, available / measured));
                ImGui.TextDisabled(dayNames[column]);
                ImGui.SetWindowFontScale(1f);
            }
            for (var cell = 0; cell < offset + days; cell++)
            {
                ImGui.TableNextColumn();
                if (cell < offset) continue;
                var date = new DateTime(calendarMonth.Year, calendarMonth.Month, cell - offset + 1);
                slots.TryGetValue(date, out var slot);
                stats.TryGetValue(date, out var dayStats);
                DrawCalendarDay(date, slot, dayStats);
            }
            ImGui.EndTable();
        }
            DrawSelectedDay(calendarDay, slots.GetValueOrDefault(calendarDay), stats.GetValueOrDefault(calendarDay));
            ImGui.Spacing(); ImGui.Separator();
            DrawFrontlineMapPerformance(nowUtc);
        }
        if (configuration.ShowCrystallineConflict)
            DrawCrystallineSummaryColumns(crystalline, nextCrystalline, nowUtc, culture,
                centerCurrentCrystallineHour);
        ImGui.EndChild();
    }

    private void DrawModeRotationStrip(string id, string currentTitle, string nextTitle,
        PvpRotationSlot current, PvpRotationSlot next, DateTime nowUtc, CultureInfo culture)
    {
        var available = ImGui.GetContentRegionAvail().X;
        var columns = available >= 540 ? 2 : 1;
        if (!ImGui.BeginTable(id, columns,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX)) return;
        ImGui.TableNextColumn();
        DrawRotationCard($"{id}-current", currentTitle, current, true, nowUtc, culture);
        ImGui.TableNextColumn();
        DrawRotationCard($"{id}-next", nextTitle, next, false, nowUtc, culture);
        ImGui.EndTable();
    }

    private void DrawRotationCard(string id, string title, PvpRotationSlot slot,
        bool current, DateTime nowUtc, CultureInfo culture)
    {
        var background = BattlefieldUiTheme.MapColor(configuration.UiTheme, slot.Key) with { W = .40f };
        var foreground = BattlefieldUiTheme.Text(configuration.UiTheme);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Text, foreground);
        ImGui.PushStyleColor(ImGuiCol.Border, current
            ? BattlefieldUiTheme.Focus(configuration.UiTheme)
            : BattlefieldUiTheme.Border(configuration.UiTheme));
        ImGui.BeginChild(id, new Vector2(0, 116), true, ImGuiWindowFlags.NoScrollbar);
        var cardMin = ImGui.GetWindowPos();
        var cardMax = cardMin + ImGui.GetWindowSize();
        ImGui.TextColored(foreground with { W = .72f }, title);
        ImGui.SetWindowFontScale(1.08f);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(100, ImGui.GetContentRegionAvail().X - 62));
        ImGui.TextWrapped(slot.Name(configuration.Language));
        ImGui.PopTextWrapPos();
        ImGui.SetWindowFontScale(1f);
        ImGui.TextColored(foreground with { W = .76f }, LocalInterval(slot, culture));
        var remaining = (current ? slot.EndUtc : slot.StartUtc) - nowUtc;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        ImGui.TextColored(foreground, $"{T("nextRotation")}  {remaining:hh\\:mm\\:ss}");
        DrawMapGlyph(ImGui.GetWindowDrawList(), cardMin + new Vector2(cardMax.X - cardMin.X - 66, 31),
            cardMin + new Vector2(cardMax.X - cardMin.X - 12, 92), slot.Key,
            ImGui.ColorConvertFloat4ToU32(foreground with { W = .22f }));
        ImGui.EndChild();
        ImGui.PopStyleColor(3);
    }

    /// <summary>
    /// Small original mechanism silhouettes shared by rotation cards, the
    /// month calendar and the weekly schedule. These are deliberately drawn
    /// from primitives so the UI does not depend on copied game or website art.
    /// 轮换卡、月历和周历共用的原创机制剪影，不依赖外部图标资源。
    /// </summary>
    private static void DrawMapGlyph(ImDrawListPtr draw, Vector2 minimum, Vector2 maximum,
        string key, uint color)
    {
        var width = Math.Max(8, maximum.X - minimum.X);
        var height = Math.Max(8, maximum.Y - minimum.Y);
        var center = (minimum + maximum) * .5f;
        var stroke = Math.Max(1.2f, Math.Min(width, height) / 18f);
        Vector2 P(float x, float y) => minimum + new Vector2(width * x, height * y);

        switch (key)
        {
            case "seize": // weathered standing stone
                draw.AddTriangleFilled(P(.28f,.88f), P(.46f,.12f), P(.58f,.88f), color);
                draw.AddRectFilled(P(.43f,.28f), P(.72f,.88f), color, 1);
                draw.AddLine(P(.50f,.34f), P(.58f,.72f), 0x66000000, stroke);
                break;
            case "naadam": // wind-bent prairie grass
                draw.AddLine(P(.20f,.90f), P(.42f,.18f), color, stroke * 1.5f);
                draw.AddLine(P(.38f,.90f), P(.70f,.30f), color, stroke * 1.5f);
                draw.AddLine(P(.55f,.90f), P(.88f,.48f), color, stroke * 1.5f);
                break;
            case "shatter": // central faceted ice
                draw.AddTriangleFilled(P(.50f,.06f), P(.28f,.62f), P(.50f,.90f), color);
                draw.AddTriangleFilled(P(.50f,.06f), P(.72f,.62f), P(.50f,.90f), color);
                draw.AddTriangleFilled(P(.27f,.46f), P(.10f,.82f), P(.48f,.84f), color);
                draw.AddTriangleFilled(P(.73f,.46f), P(.90f,.82f), P(.52f,.84f), color);
                break;
            case "triumph": // stepped highland beacon
                draw.AddTriangleFilled(P(.14f,.86f), P(.56f,.30f), P(.82f,.86f), color);
                draw.AddLine(P(.56f,.30f), P(.56f,.06f), color, stroke);
                draw.AddTriangleFilled(P(.56f,.06f), P(.88f,.16f), P(.56f,.28f), color);
                break;
            case "secure": // three-wing drone
                draw.AddCircle(center, Math.Min(width,height) * .14f, color, 18, stroke);
                for (var index = 0; index < 3; index++)
                {
                    var angle = -MathF.PI / 2 + index * MathF.PI * 2 / 3;
                    var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    var hub = center + direction * Math.Min(width,height) * .15f;
                    var tip = center + direction * Math.Min(width,height) * .40f;
                    draw.AddLine(hub, tip, color, stroke * 1.25f);
                    draw.AddCircle(tip, Math.Min(width,height) * .09f, color, 14, stroke);
                }
                break;
            case "palaistra": // arena arch
                draw.AddRect(P(.22f,.30f), P(.78f,.86f), color, 1, 0, stroke);
                draw.AddLine(P(.16f,.30f), P(.84f,.30f), color, stroke * 1.5f);
                draw.AddLine(P(.34f,.30f), P(.34f,.86f), color, stroke);
                draw.AddLine(P(.66f,.30f), P(.66f,.86f), color, stroke);
                break;
            case "volcanic":
                draw.AddTriangleFilled(P(.12f,.86f), P(.50f,.10f), P(.88f,.86f), color);
                draw.AddTriangleFilled(P(.38f,.86f), P(.54f,.48f), P(.66f,.86f), 0x66000000);
                break;
            case "bayside": // three waves
                draw.AddLine(P(.10f,.68f), P(.32f,.50f), color, stroke);
                draw.AddLine(P(.32f,.50f), P(.52f,.68f), color, stroke);
                draw.AddLine(P(.38f,.68f), P(.60f,.50f), color, stroke);
                draw.AddLine(P(.60f,.50f), P(.82f,.68f), color, stroke);
                draw.AddLine(P(.16f,.82f), P(.84f,.82f), color, stroke);
                break;
            case "cloudnine":
                draw.AddCircle(P(.36f,.58f), Math.Min(width,height) * .18f, color, 18, stroke);
                draw.AddCircle(P(.58f,.48f), Math.Min(width,height) * .23f, color, 18, stroke);
                draw.AddCircle(P(.75f,.62f), Math.Min(width,height) * .15f, color, 18, stroke);
                draw.AddLine(P(.22f,.74f), P(.86f,.74f), color, stroke);
                break;
            case "castletown":
                draw.AddRect(P(.22f,.24f), P(.78f,.82f), color, 1, 0, stroke);
                draw.AddCircle(center, Math.Min(width,height) * .17f, color, 18, stroke);
                draw.AddLine(P(.50f,.12f), P(.50f,.26f), color, stroke);
                draw.AddLine(P(.50f,.80f), P(.50f,.94f), color, stroke);
                break;
            case "harmonias": // open book
                draw.AddTriangleFilled(P(.10f,.30f), P(.48f,.40f), P(.48f,.86f), color);
                draw.AddTriangleFilled(P(.90f,.30f), P(.52f,.40f), P(.52f,.86f), color);
                draw.AddLine(P(.50f,.38f), P(.50f,.88f), color, stroke);
                break;
            case "redsands":
                draw.AddTriangleFilled(P(.08f,.84f), P(.42f,.48f), P(.66f,.84f), color);
                draw.AddTriangleFilled(P(.38f,.84f), P(.72f,.30f), P(.94f,.84f), color);
                break;
            default:
                draw.AddCircle(center, Math.Min(width,height) * .30f, color, 20, stroke);
                break;
        }
    }

    private static string LocalInterval(PvpRotationSlot slot, CultureInfo culture)
    {
        var start = TimeZoneInfo.ConvertTimeFromUtc(slot.StartUtc, TimeZoneInfo.Local);
        var end = TimeZoneInfo.ConvertTimeFromUtc(slot.EndUtc, TimeZoneInfo.Local);
        return start.Date == end.Date
            ? $"{start.ToString("HH:mm", culture)}–{end.ToString("HH:mm", culture)}"
            : $"{start.ToString("MM-dd HH:mm", culture)}–{end.ToString("MM-dd HH:mm", culture)}";
    }

    private void DrawFrontlineMapPerformance(DateTime nowUtc)
    {
        var matches = repository.Snapshot().Where(value =>
            value.Schema == LocalMatchAnalyzer.Schema && IsCurrentAccountMatch(value)).ToArray();
        var maps = rotationCalendar.FrontlineCycle(nowUtc).GroupBy(value => value.Key)
            .Select(value => value.First()).ToArray();
        ImGui.Spacing();
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("flMapPerformance"));
        ImGui.SameLine(); ImGui.TextDisabled(T("localAccountResultsOnly"));
        if (ImGui.BeginTable("frontline-map-performance-table", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
                ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn(T("map"), ImGuiTableColumnFlags.WidthStretch, 1.45f);
            ImGui.TableSetupColumn(T("matchesPlayed"), ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn(T("wins"), ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn(T("losses"), ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn(T("placementFirst"), ImGuiTableColumnFlags.WidthFixed, 88);
            ImGui.TableSetupColumn(T("placementSecond"), ImGuiTableColumnFlags.WidthFixed, 88);
            ImGui.TableSetupColumn(T("placementThird"), ImGuiTableColumnFlags.WidthFixed, 88);
            ImGui.TableSetupColumn(T("winRate"), ImGuiTableColumnFlags.WidthFixed, 82);
            ImGui.TableHeadersRow();
            foreach (var map in maps)
                DrawFrontlinePerformanceRow(map.Name(configuration.Language),
                    matches.Where(value => value.DutyId == map.DutyId).ToArray());
            DrawFrontlinePerformanceRow(T("allMaps"), matches, true);
            ImGui.EndTable();
        }
    }

    private void DrawFrontlinePerformanceRow(string label, IReadOnlyList<LocalMatchAnalysis> matches,
        bool total = false)
    {
        var placements = matches.Select(match =>
        {
            var player = FindLocalPlayer(match);
            var team = player?.Team switch
            {
                0 => "maelstrom", 1 => "adders", 2 => "flames", _ => string.Empty
            };
            return match.Teams.FirstOrDefault(value =>
                string.Equals(value.Team, team, StringComparison.OrdinalIgnoreCase))?.Placement ?? 0;
        }).Where(value => value is >= 1 and <= 3).ToArray();
        var first = placements.Count(value => value == 1);
        var second = placements.Count(value => value == 2);
        var third = placements.Count(value => value == 3);
        var denominator = placements.Length;
        string PlacementCell(int count) => denominator == 0 ? "—" : $"{count} · {count * 100d / denominator:0.0}%";

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (total) ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), label);
        else ImGui.TextWrapped(label);
        ImGui.TableNextColumn(); ImGui.TextUnformatted(denominator.ToString(UiCulture()));
        ImGui.TableNextColumn(); ImGui.TextUnformatted(first.ToString(UiCulture()));
        ImGui.TableNextColumn(); ImGui.TextUnformatted((second + third).ToString(UiCulture()));
        ImGui.TableNextColumn(); ImGui.TextUnformatted(PlacementCell(first));
        ImGui.TableNextColumn(); ImGui.TextUnformatted(PlacementCell(second));
        ImGui.TableNextColumn(); ImGui.TextUnformatted(PlacementCell(third));
        ImGui.TableNextColumn();
        if (denominator == 0) ImGui.TextDisabled("—");
        else ImGui.TextColored(LogsColor(first * 100d / denominator), $"{first * 100d / denominator:0.0}%");
    }

    private bool IsCurrentAccountMatch(LocalMatchAnalysis match)
    {
        var current = state();
        if (string.IsNullOrWhiteSpace(current.LocalPlayerName) || current.LocalPlayerWorldId == 0)
            return false;
        var player = FindLocalPlayer(match);
        return player != null && player.WorldId == current.LocalPlayerWorldId &&
               string.Equals(player.Name, current.LocalPlayerName, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawCalendarDay(DateTime date, PvpRotationSlot? slot, CalendarStats? stats)
    {
        if (ImGui.Selectable($"##calendar-{date:yyyyMMdd}", calendarDay == date,
                ImGuiSelectableFlags.None, new Vector2(0, 78)))
            calendarDay = date;
        var selectedDay = calendarDay == date;
        var hovered = ImGui.IsItemHovered();
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        var foreground = BattlefieldUiTheme.Text(configuration.UiTheme);
        var text = ImGui.ColorConvertFloat4ToU32(foreground);
        var muted = ImGui.ColorConvertFloat4ToU32(foreground with { W = .72f });
        var focus = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Focus(configuration.UiTheme));
        if (slot != null)
        {
            var mapColor = BattlefieldUiTheme.MapColor(configuration.UiTheme, slot.Key) with { W = .40f };
            draw.AddRectFilled(minimum + Vector2.One, maximum - Vector2.One,
                ImGui.ColorConvertFloat4ToU32(mapColor), 1);
            DrawMapGlyph(draw, minimum + new Vector2((maximum.X - minimum.X) * .58f, 14),
                maximum - new Vector2(9, 12), slot.Key,
                ImGui.ColorConvertFloat4ToU32(foreground with { W = .22f }));
        }
        if (hovered)
            draw.AddRect(minimum + new Vector2(1, 1), maximum - new Vector2(1, 1), text, 1, 0, 1.4f);
        if (selectedDay)
            draw.AddRect(minimum + new Vector2(2, 2), maximum - new Vector2(2, 2), focus, 1, 0, 2.4f);
        if (date == DateTime.Today)
        {
            draw.AddRect(minimum + new Vector2(2, 2), maximum - new Vector2(2, 2), focus, 1, 0, 3.4f);
            draw.AddRect(minimum + new Vector2(5, 5), maximum - new Vector2(5, 5), focus, 1, 0, 1.1f);
            var today = T("today");
            var todaySize = ImGui.CalcTextSize(today);
            var badgeMaximum = maximum - new Vector2(4, 4);
            var badgeMinimum = badgeMaximum - new Vector2(todaySize.X + 12, todaySize.Y + 6);
            draw.AddRectFilled(badgeMinimum, badgeMaximum, focus, 1);
            draw.AddText(badgeMinimum + new Vector2(6, 3),
                ImGui.ColorConvertFloat4ToU32(new Vector4(.03f, .03f, .03f, 1)), today);
        }
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * 1.32f,
            minimum + new Vector2(7, 5), text, date.Day.ToString(UiCulture()));
        if (slot != null)
            draw.AddText(minimum + new Vector2(42, 8), text,
                slot.Name(configuration.Language));
        if (stats is { Matches: > 0 })
            draw.AddText(minimum + new Vector2(8, 49), text,
                stats.WinRate is double decidedRate
                    ? string.Format(T("calendarDayStats"), stats.Matches, decidedRate)
                    : string.Format(T("calendarDayNoDecision"), stats.Matches));
        else
            draw.AddText(minimum + new Vector2(8, 49), muted, T("calendarNoLocalMatches"));
    }

    private Dictionary<DateTime, CalendarStats> BuildCalendarStats(TimeZoneInfo zone)
    {
        var accumulator = new Dictionary<DateTime, (int Matches, int Wins, int Decided)>();
        foreach (var match in repository.Snapshot().Where(value =>
                     value.Schema == LocalMatchAnalyzer.Schema && IsCurrentAccountMatch(value)))
        {
            var player = FindLocalPlayer(match);
            if (player == null) continue;
            var date = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(match.EndedAtUtc, DateTimeKind.Utc), zone).Date;
            var team = player.Team switch { 0 => "maelstrom", 1 => "adders", 2 => "flames", _ => string.Empty };
            var won = match.Teams.FirstOrDefault(value =>
                string.Equals(value.Team, team, StringComparison.OrdinalIgnoreCase))?.Placement == 1;
            var current = accumulator.GetValueOrDefault(date);
            accumulator[date] = (current.Matches + 1, current.Wins + (won ? 1 : 0), current.Decided + 1);
        }
        var local = state();
        if (!string.IsNullOrWhiteSpace(local.LocalPlayerName) && local.LocalPlayerWorldId != 0)
        {
            foreach (var match in crystallineMatchHistory.Snapshot(local.LocalPlayerName,
                         local.LocalPlayerWorldId))
            {
                var date = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(match.EndedAtUtc, DateTimeKind.Utc), zone).Date;
                var current = accumulator.GetValueOrDefault(date);
                var outcome = match.EffectiveOutcome;
                accumulator[date] = (current.Matches + 1,
                    current.Wins + (outcome == "win" ? 1 : 0),
                    current.Decided + (outcome is "win" or "loss" ? 1 : 0));
            }
        }
        return accumulator.ToDictionary(x => x.Key,
            x => new CalendarStats(x.Value.Matches, x.Value.Wins, x.Value.Decided));
    }

    private void DrawSelectedDay(DateTime date, PvpRotationSlot? slot, CalendarStats? stats)
    {
        ImGui.Separator();
        ImGui.SetWindowFontScale(1.12f);
        ImGui.TextUnformatted(date.ToString("D", UiCulture()));
        ImGui.SetWindowFontScale(1f);
        if (slot != null) ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme),
            slot.Name(configuration.Language));
        if (stats is not { Matches: > 0 }) ImGui.TextDisabled(T("noMatches"));
        else
        {
            ImGui.TextUnformatted($"{T("matchesPlayed")}  {stats.Matches}"); ImGui.SameLine(210);
            ImGui.TextUnformatted($"{T("wins")}  {stats.Wins}"); ImGui.SameLine(360);
            if (stats.WinRate is double rate)
                ImGui.TextColored(LogsColor(rate), $"{T("winRate")}  {rate:0.0}%");
            else ImGui.TextDisabled($"{T("winRate")}  —");
        }
        DrawSelectedDayMatchCards(date);
    }

    private void DrawSelectedDayMatchCards(DateTime localDate)
    {
        var frontline = repository.Snapshot()
            .Where(match => match.Schema == LocalMatchAnalyzer.Schema && IsCurrentAccountMatch(match) &&
                            match.EndedAtUtc.ToLocalTime().Date == localDate.Date)
            .OrderByDescending(match => match.EndedAtUtc)
            .Select(ToHistoryRow).ToArray();
        // This month calendar is an index for Frontline only. Crystalline
        // Conflict records belong below its own half-day schedule.
        if (frontline.Length == 0) return;

        ImGui.Spacing();
        ImGui.TextDisabled(T("calendarLocalMatches"));
        var columns = Math.Max(1, Math.Min(4, (int)(ImGui.GetContentRegionAvail().X / 245f)));
        if (!ImGui.BeginTable($"calendar-local-{localDate:yyyyMMdd}", columns,
                ImGuiTableFlags.SizingStretchSame)) return;
        foreach (var row in frontline)
        {
            ImGui.TableNextColumn();
            DrawCalendarFrontlineCard(row);
        }
        ImGui.EndTable();
    }

    private void DrawCalendarFrontlineCard(MatchHistoryRow row)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BattlefieldUiTheme.Panel(configuration.UiTheme));
        ImGui.BeginChild($"calendar-fl-{row.Match.MatchUid}", new Vector2(0, 84), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.TextDisabled($"{row.Match.EndedAtUtc.ToLocalTime():HH:mm} · {T("frontlineMode")}");
        ImGui.TextUnformatted(MapName(row.Match.DutyId));
        if (row.Player != null)
        {
            DrawJobIcon(row.Player.ClassJobId, 17); ImGui.SameLine(0, 5);
            ImGui.TextUnformatted($"KDA {row.Player.Kills}/{row.Player.Deaths}/{row.Player.Assists}");
            ImGui.SameLine();
            if (row.Score?.Overall is double logs)
                ImGui.TextColored(LogsColor(logs), $"Logs {logs:0.0}");
        }
        ImGui.EndChild(); ImGui.PopStyleColor();
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            selected = row.Match;
            selectFrontlineTab = true;
        }
    }

    private void DrawCalendarCrystallineCard(CrystallineMatchRecord match, string idPrefix = "calendar-cc")
    {
        var active = selectedCrystallineResult == match;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, active
            ? BattlefieldUiTheme.SelectedPanel(configuration.UiTheme)
            : BattlefieldUiTheme.Panel(configuration.UiTheme));
        ImGui.PushStyleColor(ImGuiCol.Border, active
            ? BattlefieldUiTheme.Accent(configuration.UiTheme)
            : BattlefieldUiTheme.Border(configuration.UiTheme));
        ImGui.BeginChild($"{idPrefix}-{match.EndedAtUtc.Ticks}-{match.MapKey}", new Vector2(0, 104), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.TextDisabled($"{match.EndedAtUtc.ToLocalTime():HH:mm} · {T("crystallineMode")}");
        ImGui.TextUnformatted(CrystallineMapName(match.MapKey));
        DrawJobIcon(match.ClassJobId, 17); ImGui.SameLine(0, 5);
        DrawCrystallineOutcome(match);
        ImGui.SameLine(); ImGui.TextDisabled(T(match.MatchType == "ranked" ? "ccRanked" : "ccPractice"));
        var kda = match.LocalKda;
        ImGui.TextDisabled($"KDA {kda.Kills}/{kda.Deaths}/{kda.Assists}");
        ImGui.SameLine();
        if (match.OverallLogs is double logs)
            ImGui.TextColored(LogsColor(logs), $"Logs {logs:0.0}");
        else ImGui.TextDisabled($"Logs —");
        ImGui.EndChild(); ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            selectedCrystallineResult = match;
            selectCrystallineTab = true;
        }
    }

    private void DrawCrystallineSummaryColumns(PvpRotationSlot current, PvpRotationSlot next,
        DateTime nowUtc, CultureInfo culture, bool centerCurrentHour)
    {
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("crystallineMode"));
        DrawModeRotationStrip("crystalline-rotation", T("crystallineNow"),
            $"{T("nextRotationMap")} · {T("crystallineMode")}", current, next, nowUtc, culture);
        ImGui.Spacing();
        DrawCrystallineHalfDayCalendar(nowUtc, centerCurrentHour);
        ImGui.Spacing();
        DrawCrystallineMapStats();
        ImGui.Spacing();
        DrawCrystallineRankHistory();
    }

    private void DrawCrystallineHalfDayCalendar(DateTime nowUtc, bool resetToCurrentWindow)
    {
        var zone = TimeZoneInfo.Local;
        var culture = UiCulture();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        if (resetToCurrentWindow)
        {
            crystallineCalendarFirstDayOffset = -1;
            crystallineCalendarHalfStartHour = localNow.Hour < 12 ? 0 : 12;
        }

        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("ccHalfDayCalendar"));
        ImGui.SameLine();
        ImGui.TextDisabled(string.Format(T("ccHalfDayCalendarHelp"), zone.StandardName));
        ImGui.SameLine();
        if (ImGui.SmallButton("<##cc-days-back")) crystallineCalendarFirstDayOffset--;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("ccPreviousDays"));
        ImGui.SameLine();
        if (ImGui.SmallButton(T("today") + "##cc-days-today")) crystallineCalendarFirstDayOffset = -1;
        ImGui.SameLine();
        if (ImGui.SmallButton(">##cc-days-forward")) crystallineCalendarFirstDayOffset++;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("ccNextDays"));
        ImGui.SameLine();
        if (ImGui.SmallButton(T("ccFirstHalf") + "##cc-first-half")) crystallineCalendarHalfStartHour = 0;
        ImGui.SameLine();
        if (ImGui.SmallButton(T("ccSecondHalf") + "##cc-second-half")) crystallineCalendarHalfStartHour = 12;

        var firstDate = localNow.Date.AddDays(crystallineCalendarFirstDayOffset);
        var dayNames = WeekdayNames(culture);
        if (ImGui.BeginTable("cc-half-day-calendar-table", 13,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn(T("date"), ImGuiTableColumnFlags.WidthFixed, 112);
            for (var offset = 0; offset < 12; offset++)
                ImGui.TableSetupColumn($"{crystallineCalendarHalfStartHour + offset:00}:00",
                    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            for (var day = 0; day < 3; day++)
            {
                var date = firstDate.AddDays(day);
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 42);
                ImGui.TableNextColumn();
                if (date == localNow.Date)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
                        ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.SelectedPanel(configuration.UiTheme)));
                var relative = date == localNow.Date ? T("today")
                    : date == localNow.Date.AddDays(-1) ? T("yesterday")
                    : date == localNow.Date.AddDays(1) ? T("tomorrow")
                    : dayNames[((int)date.DayOfWeek + 6) % 7];
                ImGui.TextUnformatted(relative);
                ImGui.TextDisabled(date.ToString("MM-dd", culture));

                for (var offset = 0; offset < 12; offset++)
                {
                    ImGui.TableNextColumn();
                    var hour = crystallineCalendarHalfStartHour + offset;
                    var localHour = new DateTime(date.Year, date.Month, date.Day, hour, 0, 0,
                        DateTimeKind.Unspecified);
                    if (zone.IsInvalidTime(localHour))
                    {
                        ImGui.TextDisabled("-");
                        continue;
                    }
                    DateTime utc;
                    try { utc = TimeZoneInfo.ConvertTimeToUtc(localHour, zone); }
                    catch (ArgumentException) { ImGui.TextDisabled("-"); continue; }
                    var slot = rotationCalendar.CrystallineConflict(utc);
                    var isCurrent = nowUtc >= slot.StartUtc && nowUtc < slot.EndUtc;
                    DrawCrystallineMapTile(slot, $"cc-half-slot-{date:yyyyMMdd}-{hour}", 34, isCurrent);
                }
            }
            ImGui.EndTable();
        }
        DrawCrystallineTodayStats(localNow.Date, zone);
    }

    private void DrawCrystallineWeekCalendar(DateTime nowUtc, bool forceCenterCurrentHour)
    {
        var zone = TimeZoneInfo.Local;
        var culture = UiCulture();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var mondayOffset = ((int)localNow.DayOfWeek + 6) % 7;
        var weekStart = localNow.Date.AddDays(-mondayOffset);
        var dayNames = WeekdayNames(culture);
        var currentHourKey = localNow.Date.Ticks + localNow.Hour;
        var centerCurrentHour = forceCenterCurrentHour || centeredCrystallineHourKey != currentHourKey;

        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("ccWeekCalendar"));
        ImGui.SameLine(); ImGui.TextDisabled(string.Format(T("ccWeekCalendarHelp"), zone.StandardName));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BattlefieldUiTheme.Panel(configuration.UiTheme));
        ImGui.BeginChild("cc-week-calendar", new Vector2(0, 330), true);
        if (ImGui.BeginTable("cc-week-calendar-table", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.SizingStretchSame,
                new Vector2(0, 292)))
        {
            ImGui.TableSetupColumn(T("time"), ImGuiTableColumnFlags.WidthFixed, 62);
            for (var day = 0; day < 7; day++)
            {
                var date = weekStart.AddDays(day);
                ImGui.TableSetupColumn($"{dayNames[day]}\n{date.ToString("MM-dd", culture)}",
                    ImGuiTableColumnFlags.WidthStretch);
            }
            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableHeadersRow();

            for (var hour = 0; hour < 24; hour++)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 38);
                ImGui.TableNextColumn();
                if (hour == localNow.Hour)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
                        ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.SelectedPanel(configuration.UiTheme)));
                ImGui.TextUnformatted($"{hour:00}:00");
                if (hour == localNow.Hour && centerCurrentHour)
                {
                    // Keep the active hour immediately below the frozen weekday
                    // header. Centering it wastes the strongest visual anchor.
                    // 当前小时固定到星期表头正下方，进入周历即可直接看见。
                    ImGui.SetScrollHereY(0f);
                    centeredCrystallineHourKey = currentHourKey;
                }

                for (var day = 0; day < 7; day++)
                {
                    var date = weekStart.AddDays(day);
                    ImGui.TableNextColumn();
                    var localHour = new DateTime(date.Year, date.Month, date.Day, hour, 0, 0,
                        DateTimeKind.Unspecified);
                    if (zone.IsInvalidTime(localHour))
                    {
                        ImGui.TextDisabled("—");
                        continue;
                    }
                    DateTime utc;
                    try { utc = TimeZoneInfo.ConvertTimeToUtc(localHour, zone); }
                    catch (ArgumentException) { ImGui.TextDisabled("—"); continue; }
                    var slot = rotationCalendar.CrystallineConflict(utc);
                    var isCurrent = nowUtc >= slot.StartUtc && nowUtc < slot.EndUtc;
                    DrawCrystallineMapTile(slot, $"cc-slot-{date:yyyyMMdd}-{hour}", 29, isCurrent);
                }
            }
            ImGui.EndTable();
        }
        ImGui.EndChild(); ImGui.PopStyleColor();
        DrawCrystallineTodayStats(localNow.Date, zone);
    }

    private void DrawCrystallineMapTile(PvpRotationSlot slot, string id, float height, bool isCurrent = false)
    {
        var cellMinimum = ImGui.GetCursorScreenPos();
        var cellWidth = Math.Max(28, ImGui.GetContentRegionAvail().X);
        ImGui.InvisibleButton($"##{id}", new Vector2(cellWidth, height));
        var cellMaximum = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        var mapColor = BattlefieldUiTheme.MapColor(configuration.UiTheme, slot.Key) with { W = .40f };
        var foreground = BattlefieldUiTheme.Text(configuration.UiTheme);
        draw.AddRectFilled(cellMinimum, cellMaximum, ImGui.ColorConvertFloat4ToU32(mapColor), 1);
        var glyphSize = Math.Clamp(height - 8, 18, 27);
        DrawMapGlyph(draw, cellMinimum + new Vector2(4, 4),
            cellMinimum + new Vector2(4 + glyphSize, 4 + glyphSize), slot.Key,
            ImGui.ColorConvertFloat4ToU32(foreground with { W = .22f }));
        draw.AddText(cellMinimum + new Vector2(glyphSize + 9, Math.Max(4, (height - ImGui.GetTextLineHeight()) * .5f)),
            ImGui.ColorConvertFloat4ToU32(foreground), slot.Name(configuration.Language));
        if (isCurrent)
            draw.AddRect(cellMinimum + Vector2.One, cellMaximum - Vector2.One,
                ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Focus(configuration.UiTheme)),
                1, 0, 2.4f);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(slot.Name(configuration.Language));
    }

    private void DrawCrystallineTodayStats(DateTime localDate, TimeZoneInfo zone)
    {
        var player = state();
        var rows = crystallineMatchHistory.Snapshot(player.LocalPlayerName, player.LocalPlayerWorldId)
            .Where(value => TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(value.EndedAtUtc, DateTimeKind.Utc), zone).Date == localDate)
            .OrderByDescending(value => value.EndedAtUtc).ToArray();
        var wins = rows.Count(value => value.EffectiveOutcome == "win");
        var losses = rows.Count(value => value.EffectiveOutcome == "loss");
        var draws = rows.Count(value => value.EffectiveOutcome == "draw");
        var decided = wins + losses;
        ImGui.Spacing(); ImGui.Separator();
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("ccTodayPerformance"));
        ImGui.SameLine();
        if (rows.Length == 0)
        {
            ImGui.TextDisabled(T("ccTodayNoMatches"));
            return;
        }
        var rate = decided == 0 ? (double?)null : wins * 100d / decided;
        ImGui.TextUnformatted($"{T("matchesPlayed")}  {rows.Length}");
        ImGui.SameLine(190); ImGui.TextUnformatted($"{T("wins")}  {wins}");
        ImGui.SameLine(300); ImGui.TextUnformatted($"{T("losses")}  {losses}");
        ImGui.SameLine(410); ImGui.TextUnformatted($"{T("draws")}  {draws}");
        ImGui.SameLine(520);
        if (rate is double value) ImGui.TextColored(LogsColor(value), $"{T("winRate")}  {value:0.0}%");
        else ImGui.TextDisabled($"{T("winRate")}  —");
        ImGui.Spacing();
        var columns = Math.Max(1, Math.Min(4, (int)(ImGui.GetContentRegionAvail().X / 260f)));
        if (!ImGui.BeginTable($"cc-today-matches-{localDate:yyyyMMdd}", columns,
                ImGuiTableFlags.SizingStretchSame)) return;
        foreach (var match in rows)
        {
            ImGui.TableNextColumn();
            DrawCalendarCrystallineCard(match, "cc-today");
        }
        ImGui.EndTable();
    }

    private void DrawCrystallineMatches()
    {
        ImGui.BeginChild("crystalline-matches-scroll", Vector2.Zero, false);
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("tabCrystallineMatches"));
        ImGui.SameLine(); ImGui.TextDisabled(T("crystallineRecordsHelp"));
        ImGui.SameLine();
        if (ImGui.SmallButton($"{T("openFolder")}##cc-cache")) OpenFolder(CaptureDirectory("crystalline-conflict"));
        ImGui.SameLine();
        if (ImGui.SmallButton($"{T("openImportFolder")}##cc-import")) OpenFolder(ImportDirectory("crystalline-conflict"));
        ImGui.SameLine();
        if (!importRunning && ImGui.SmallButton($"{T("importCaptures")}##cc-scan"))
            StartImport(ImportDirectory("crystalline-conflict"));
        DrawCrystallineResultRecords();
        ImGui.EndChild();
    }

    private void DrawCrystallineResultRecords()
    {
        var local = state();
        var history = crystallineMatchHistory.Snapshot(local.LocalPlayerName, local.LocalPlayerWorldId)
            .OrderByDescending(value => value.EndedAtUtc).ToArray();
        ImGui.Spacing();
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("crystallineLocalResults"));
        ImGui.SameLine(); ImGui.TextDisabled(T("ccResultEvidence"));
        if (history.Length == 0)
        {
            ImGui.TextDisabled(T("ccNoMatchData"));
            return;
        }
        selectedCrystallineResult ??= history[0];
        if (!history.Contains(selectedCrystallineResult)) selectedCrystallineResult = history[0];
        var columns = ImGui.GetContentRegionAvail().X >= 820 ? 2 : 1;
        if (ImGui.BeginTable("cc-result-browser", columns,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("matches", ImGuiTableColumnFlags.WidthFixed,
                columns == 2 ? Math.Clamp(ImGui.GetContentRegionAvail().X * .28f, 270, 390) : 0);
            if (columns == 2) ImGui.TableSetupColumn("scoreboard", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextColumn();
            foreach (var match in history)
            {
                DrawCrystallineResultCard(match);
                ImGui.Spacing();
            }
            if (columns == 1) { ImGui.TableNextRow(); ImGui.TableNextColumn(); }
            else ImGui.TableNextColumn();
            if (selectedCrystallineResult != null) DrawCrystallineResultDetail(selectedCrystallineResult);
            ImGui.EndTable();
        }
    }

    private void DrawCrystallineResultCard(CrystallineMatchRecord match)
    {
        var active = selectedCrystallineResult == match;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, active
            ? BattlefieldUiTheme.SelectedPanel(configuration.UiTheme)
            : BattlefieldUiTheme.Panel(configuration.UiTheme));
        ImGui.PushStyleColor(ImGuiCol.Border, active
            ? BattlefieldUiTheme.Accent(configuration.UiTheme)
            : BattlefieldUiTheme.Border(configuration.UiTheme));
        ImGui.BeginChild($"cc-result-{match.EndedAtUtc.Ticks}-{match.MapKey}", new Vector2(0, 112), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.TextDisabled(match.EndedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        ImGui.TextUnformatted(CrystallineMapName(match.MapKey));
        DrawJobIcon(match.ClassJobId, 18); ImGui.SameLine(0, 5);
        DrawCrystallineOutcome(match);
        ImGui.SameLine(); ImGui.TextDisabled(T(match.MatchType == "ranked" ? "ccRanked" : "ccPractice"));
        var local = match.Players?.FirstOrDefault(value => value.WorldId == match.WorldId &&
            string.Equals(value.Name, match.PlayerName, StringComparison.OrdinalIgnoreCase));
        var kda = match.LocalKda;
        ImGui.TextDisabled(local == null
            ? $"KDA {kda.Kills}/{kda.Deaths}/{kda.Assists}  ·  {T("historySummaryOnly")}"
            : $"KDA {kda.Kills}/{kda.Deaths}/{kda.Assists}  ·  " +
              $"{T("crystalTime")} {local.TimeOnCrystalSeconds}s");
        var analysis = repository.Snapshot().FirstOrDefault(value =>
            value.Schema == LocalMatchAnalyzer.CrystallineSchema && value.MatchUid == match.MatchUid);
        var overallLogs = analysis?.MatchLogs ?? match.OverallLogs;
        if (overallLogs is double logs)
            ImGui.TextColored(LogsColor(logs), $"Logs {logs:0.0}");
        else ImGui.TextDisabled($"Logs — · {T("ccScorePending")}");
        ImGui.EndChild(); ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            selectedCrystallineResult = match;
    }

    private void DrawCrystallineResultDetail(CrystallineMatchRecord match)
    {
        // The parent page owns vertical scrolling. Keeping detail in the same
        // flow avoids a second wheel target beside the match-list scrollbar.
        ImGui.PushID("cc-result-detail");
        ImGui.Separator();
        ImGui.TextUnformatted(CrystallineMapName(match.MapKey));
        ImGui.SameLine();
        DrawCrystallineOutcome(match);
        ImGui.TextDisabled($"{match.EndedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · " +
            $"{T(match.MatchType == "ranked" ? "ccRanked" : "ccPractice")} · " +
            $"{match.MatchLengthSeconds / 60}:{match.MatchLengthSeconds % 60:00}");
        if (match.EvidenceSource is "act_history_summary" or "ccstats_history_summary" or
            "legacy_history_summary")
        {
            var kda = match.LocalKda;
            DrawJobIcon(match.ClassJobId, 22); ImGui.SameLine(0, 6);
            ImGui.TextUnformatted($"{JobName(match.ClassJobId)} · KDA " +
                $"{kda.Kills}/{kda.Deaths}/{kda.Assists}");
            ImGui.TextDisabled(T("historySummaryNoReplay"));
            ImGui.PopID();
            return;
        }
        if (!match.HasCompleteRoster)
            ImGui.TextColored(new Vector4(1f, .62f, .28f, 1f),
                string.Format(T("ccRosterIncomplete"), match.Players?.Count ?? 0));
        DrawJobIcon(match.ClassJobId, 22); ImGui.SameLine(0, 6);
        ImGui.TextUnformatted(JobName(match.ClassJobId));
        if (match.MatchType == "ranked")
        {
            ImGui.SameLine(220);
            ImGui.TextDisabled($"{T("rankBeforeAfter")}  {RankTierName(match.TierBefore)} → {RankTierName(match.TierAfter)}");
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{T("ccAstra")} {match.AstraProgress / 10f:0.0}%  ·  " +
            $"{T("ccUmbra")} {match.UmbraProgress / 10f:0.0}%");
        var analysis = repository.Snapshot().FirstOrDefault(value =>
            value.Schema == LocalMatchAnalyzer.CrystallineSchema && value.MatchUid == match.MatchUid);
        if (analysis != null)
        {
            DrawUploadProgress(analysis.MatchUid);
            ImGui.TextColored(UploadColor(analysis.UploadState),
                $"● {UploadLabel(analysis.UploadState)}");
            if (analysis.UploadState == "failed" && !string.IsNullOrWhiteSpace(analysis.UploadError))
                ImGui.TextColored(new Vector4(1f, .55f, .4f, 1f),
                    $"{T("uploadError")}：{UploadErrorText(analysis.UploadError)}");
            if (analysis.UploadState is "local_only" or "failed")
            {
                if (ImGui.Button(analysis.UploadState == "failed" ? T("retry") : T("uploadRank")))
                {
                    configuration.AutoUpload = true; saveConfiguration();
                    uploads.Enqueue(analysis.MatchUid, analysis.CapturePath, analysis.Realm);
                }
            }
            if (analysis.UploadState == "complete")
            {
                var detailButton = !string.IsNullOrWhiteSpace(analysis.DetailError)
                    ? T("retry")
                    : analysis.Detail == null ? T("requestDetail") : T("refreshDetail");
                if (ImGui.Button(detailButton))
                    uploads.EnqueueDetails(analysis.MatchUid,
                        analysis.RemoteMatchUid ?? analysis.MatchUid, analysis.Realm);
                ImGui.SameLine();
                if (ImGui.Button(T("openDetailWindow"))) openDetailedAnalysis(analysis);
                if (!string.IsNullOrWhiteSpace(analysis.DetailError))
                {
                    ImGui.TextColored(new Vector4(.34f, .78f, .95f, 1f),
                        T("ccUploadCompleteDetailPending"));
                    ImGui.TextColored(new Vector4(1f, .55f, .4f, 1f),
                        $"{T("detailError")}：{UploadErrorText(analysis.DetailError)}");
                }
            }
        }
        ImGui.Spacing();
        DrawCrystallineScoreboard(match);
        ImGui.PopID();
    }

    private void DrawCrystallineScoreboard(CrystallineMatchRecord match)
    {
        var analysis = repository.Snapshot().FirstOrDefault(value =>
            value.Schema == LocalMatchAnalyzer.CrystallineSchema && value.MatchUid == match.MatchUid);
        var players = match.Players?.Where(value => value.ClassJobId != 0).ToArray() ?? [];
        if (players.Length == 0)
        {
            ImGui.TextWrapped(T("ccLegacyResultNoScoreboard"));
            return;
        }
        if (!ImGui.BeginTable("cc-result-scoreboard", 12,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable)) return;
        var titles = new[] { T("player"), T("job"), T("team"), "K", "D", "A",
            T("damage"), T("damageTaken"), T("healing"), T("crystalTime"),
            T("overallLogs"), T("tendency") };
        foreach (var title in titles)
            ImGui.TableSetupColumn(title);
        ImGui.TableSetupScrollFreeze(2, 1);
        DrawSortableHeaders(titles, ref crystallineResultSortColumn,
            ref crystallineResultSortDescending);
        foreach (var player in SortCrystallinePlayers(analysis, players))
        {
            ImGui.TableNextRow();
            var local = player.WorldId == match.WorldId &&
                string.Equals(player.Name, match.PlayerName, StringComparison.OrdinalIgnoreCase);
            if (local) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.SelectedPanel(configuration.UiTheme)));
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{player.Name}@{WorldName(player.WorldId)}");
            ImGui.TableNextColumn(); DrawJobIcon(player.ClassJobId);
            ImGui.TableNextColumn();
            ImGui.TextColored(player.Team == 0 ? new Vector4(.35f, .72f, 1f, 1f) :
                new Vector4(1f, .55f, .72f, 1f), T(player.Team == 0 ? "ccAstra" : "ccUmbra"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Kills.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Deaths.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Assists.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(player.DamageDealt.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(player.DamageTaken.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(player.HpRestored.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{player.TimeOnCrystalSeconds}s");
            var scoreKey = $"{player.Name}@{player.WorldId}";
            var score = analysis?.RemoteScores?.GetValueOrDefault(scoreKey);
            ImGui.TableNextColumn(); DrawLogs(score?.Overall ?? player.OverallLogs);
            ImGui.TableNextColumn(); ImGui.TextDisabled(T("ccTendencyPending"));
        }
        ImGui.EndTable();
    }

    private void DrawRivalWingsMatches()
    {
        ImGui.BeginChild("rival-wings-scroll", Vector2.Zero, false);
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("tabRivalWingsMatches"));
        ImGui.TextWrapped(T("rivalWingsUnavailable"));
        ImGui.Spacing();
        if (ImGui.Button(T("openFolder"))) OpenFolder(CaptureDirectory("rival-wings"));
        ImGui.SameLine();
        if (ImGui.Button(T("openImportFolder"))) OpenFolder(ImportDirectory("rival-wings"));
        ImGui.SameLine();
        if (!importRunning && ImGui.Button(T("importCaptures"))) StartImport(ImportDirectory("rival-wings"));
        ImGui.EndChild();
    }

    private void DrawCrystallineActReplays()
    {
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("ccActReplayArchive"));
        ImGui.SameLine(); ImGui.TextDisabled(T("ccActReplayEvidence"));
        ImGui.SameLine();
        if (ImGui.SmallButton(T("reload"))) crystallineActReplayArchive.Reload();
        var matches = crystallineActReplayArchive.Snapshot();
        if (matches.Count == 0)
        {
            ImGui.TextWrapped(T("ccActReplayEmpty"));
            return;
        }
        selectedCrystallineReplay ??= matches[^1];
        if (!matches.Any(value => value.MatchUid == selectedCrystallineReplay.MatchUid))
            selectedCrystallineReplay = matches[^1];

        var columns = ImGui.GetContentRegionAvail().X >= 850 ? 2 : 1;
        if (!ImGui.BeginTable("cc-act-replays", columns,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
            return;
        ImGui.TableNextColumn();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BattlefieldUiTheme.Panel(configuration.UiTheme));
        ImGui.BeginChild("cc-act-replay-list", new Vector2(0, 260), true);
        foreach (var match in matches.OrderByDescending(value => value.StartedAtUtc))
        {
            var active = selectedCrystallineReplay.MatchUid == match.MatchUid;
            var label = $"{match.StartedAtUtc.ToLocalTime():MM-dd HH:mm} · {CrystallineMapName(match.MapKey)} · {match.Realm}";
            if (ImGui.Selectable($"{label}##{match.MatchUid}", active, ImGuiSelectableFlags.None,
                    new Vector2(0, 28))) selectedCrystallineReplay = match;
        }
        ImGui.EndChild(); ImGui.PopStyleColor();

        if (columns == 1) { ImGui.TableNextRow(); ImGui.TableNextColumn(); }
        else ImGui.TableNextColumn();
        DrawCrystallineActReplay(selectedCrystallineReplay);
        ImGui.EndTable();
    }

    private void DrawCrystallineActReplay(CrystallineActReplay match)
    {
        ImGui.TextUnformatted(CrystallineMapName(match.MapKey));
        ImGui.SameLine(); ImGui.TextDisabled($"{match.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {match.DurationSeconds / 60}:{match.DurationSeconds % 60:00}");
        ImGui.TextColored(new Vector4(1f, .66f, .25f, 1f), T("ccActReplayUnknownResult"));
        ImGui.TextDisabled(string.Format(T("ccActReplayCoverage"), match.VisiblePlayerCount,
            match.AbilityEvents));
        var selectedPlayer = match.Players.FirstOrDefault(value => value.ActorId == selectedCrystallineActorId)
            ?? match.Players.FirstOrDefault(value => value.ActorId == match.LocalPlayerActorId)
            ?? match.Players.FirstOrDefault();
        if (selectedPlayer != null) selectedCrystallineActorId = selectedPlayer.ActorId;
        DrawCrystallineActTimeline(match, selectedPlayer?.ActorId ?? string.Empty);

        var height = Math.Min(238f, 32f + Math.Max(1, match.Players.Count) * 25f);
        if (ImGui.BeginTable("cc-act-player-table", 8,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable,
                new Vector2(0, height)))
        {
            var titles = new[] { T("player"), T("job"), "K", "D", "A", T("damage"),
                T("damageTaken"), T("healing") };
            foreach (var title in titles)
                ImGui.TableSetupColumn(title);
            ImGui.TableSetupScrollFreeze(0, 1);
            DrawSortableHeaders(titles, ref crystallineActSortColumn,
                ref crystallineActSortDescending);
            foreach (var player in SortCrystallineActPlayers(match.Players))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var local = player.ActorId == match.LocalPlayerActorId ||
                    string.Equals(player.Name, match.LocalPlayerName, StringComparison.OrdinalIgnoreCase);
                var active = player.ActorId == selectedCrystallineActorId;
                var displayName = $"{player.Name}@{WorldName(player.WorldId)}";
                if (ImGui.Selectable($"{displayName}##cc-player-{match.MatchUid}-{player.ActorId}", active,
                        ImGuiSelectableFlags.SpanAllColumns))
                    selectedCrystallineActorId = player.ActorId;
                if (local && ImGui.IsItemHovered()) ImGui.SetTooltip(T("localPlayer"));
                ImGui.TableNextColumn(); DrawJobIcon(player.ClassJobId);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Kills.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Deaths.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Assists.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Damage.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.DamageTaken.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Healing.ToString("N0"));
            }
            ImGui.EndTable();
        }
        if (selectedPlayer != null)
        {
            DrawCrystallineActionBreakdown(match, selectedPlayer);
            DrawCrystallineDeathRecap(match, selectedPlayer);
        }
    }

    private void DrawCrystallineActTimeline(CrystallineActReplay match, string actorId)
    {
        var size = new Vector2(Math.Max(280, ImGui.GetContentRegionAvail().X), 116);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"cc-act-timeline-{match.MatchUid}", size);
        var draw = ImGui.GetWindowDrawList();
        var background = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.ChartBackground(configuration.UiTheme));
        var border = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Border(configuration.UiTheme));
        var grid = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Grid(configuration.UiTheme));
        var damage = ImGui.ColorConvertFloat4ToU32(new Vector4(.95f, .38f, .28f, .9f));
        var healing = ImGui.ColorConvertFloat4ToU32(new Vector4(.25f, .72f, .48f, .9f));
        draw.AddRectFilled(origin, origin + size, background, 2); draw.AddRect(origin, origin + size, border, 2);
        for (var index = 1; index < 4; index++)
        {
            var y = origin.Y + size.Y * index / 4;
            draw.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + size.X, y), grid);
        }
        var buckets = match.Buckets.Where(value => value.ActorId == actorId)
            .OrderBy(value => value.ElapsedSeconds).ToArray();
        if (buckets.Length == 0) return;
        var maximum = Math.Max(1L, buckets.Max(value => Math.Max(value.Damage,
            Math.Max(value.DamageTaken, value.Healing))));
        var width = size.X / buckets.Length;
        for (var index = 0; index < buckets.Length; index++)
        {
            var bucket = buckets[index];
            var x0 = origin.X + index * width + 1;
            var mid = origin.Y + size.Y * .52f;
            var damageHeight = (float)(bucket.Damage / (double)maximum) * size.Y * .46f;
            var healingHeight = (float)(bucket.Healing / (double)maximum) * size.Y * .42f;
            draw.AddRectFilled(new Vector2(x0, mid - damageHeight),
                new Vector2(x0 + Math.Max(1, width - 2), mid), damage);
            draw.AddRectFilled(new Vector2(x0, mid + 1),
                new Vector2(x0 + Math.Max(1, width - 2), mid + 1 + healingHeight), healing);
        }
        draw.AddText(origin + new Vector2(5, 4), damage, T("damage"));
        draw.AddText(origin + new Vector2(5, size.Y - 20), healing, T("healing"));
    }

    private void DrawCrystallineActionBreakdown(CrystallineActReplay match,
        CrystallineActReplayPlayer player)
    {
        var actions = match.Actions.Where(value => value.ActorId == player.ActorId).ToArray();
        ImGui.Spacing();
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("ccObservedActions"));
        ImGui.SameLine(); ImGui.TextDisabled($"{player.Name}@{WorldName(player.WorldId)}");
        if (actions.Length == 0) { ImGui.TextDisabled(T("noObservedActions")); return; }
        if (!ImGui.BeginTable("cc-action-breakdown", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable,
                new Vector2(0, Math.Min(220, 30 + actions.Length * 25))))
            return;
        var titles = new[] { T("action"), T("count"), T("damage"), T("healing"),
            T("effectiveHealing") };
        foreach (var title in titles) ImGui.TableSetupColumn(title);
        ImGui.TableSetupScrollFreeze(0, 1);
        DrawSortableHeaders(titles, ref crystallineActionSortColumn,
            ref crystallineActionSortDescending);
        IEnumerable<CrystallineActReplayAction> orderedActions;
        if (crystallineActionSortColumn == 0)
            orderedActions = crystallineActionSortDescending
                ? actions.OrderByDescending(value => ActionDisplayName(ParseActionId(value.ActionId),
                    value.ActionName), StringComparer.CurrentCultureIgnoreCase)
                : actions.OrderBy(value => ActionDisplayName(ParseActionId(value.ActionId),
                    value.ActionName), StringComparer.CurrentCultureIgnoreCase);
        else
        {
            Func<CrystallineActReplayAction, long> metric = crystallineActionSortColumn switch
            {
                1 => value => value.Uses,
                2 => value => value.Damage,
                3 => value => value.RawHealing,
                4 => value => value.EffectiveHealing,
                _ => _ => 0
            };
            orderedActions = crystallineActionSortDescending
                ? actions.OrderByDescending(metric).ThenBy(value => value.ActionName)
                : actions.OrderBy(metric).ThenBy(value => value.ActionName);
        }
        foreach (var action in orderedActions)
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            var actionId = ParseActionId(action.ActionId);
            DrawCompactActionIcon(actionId, action.ActionName); ImGui.SameLine(0, 5);
            ImGui.TextUnformatted(ActionDisplayName(actionId, action.ActionName));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(action.Uses.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(action.Damage.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(action.RawHealing.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(action.EffectiveHealing.ToString("N0"));
        }
        ImGui.EndTable();
    }

    private void DrawCrystallineDeathRecap(CrystallineActReplay match,
        CrystallineActReplayPlayer player)
    {
        var deaths = match.Deaths.Where(value => string.Equals(value.Victim, player.Name,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (deaths.Length == 0) return;
        ImGui.Spacing();
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("deathRecords"));
        foreach (var death in deaths)
        {
            ImGui.TextDisabled($"{FormatElapsed(death.ElapsedMs)}  {player.Name}  ←  {death.Killer}");
            foreach (var hit in death.RecentDamage)
            {
                var actionId = ParseActionId(hit.ActionId);
                ImGui.Indent(12); ImGui.TextDisabled(FormatElapsed(hit.ElapsedMs)); ImGui.SameLine();
                DrawCompactActionIcon(actionId, hit.ActionName); ImGui.SameLine(0, 5);
                ImGui.TextUnformatted($"{hit.Source} · {ActionDisplayName(actionId, hit.ActionName)} · {hit.Amount:N0}");
                ImGui.Unindent(12);
            }
        }
    }

    private static string FormatElapsed(long elapsedMs) =>
        $"{elapsedMs / 60000:00}:{elapsedMs / 1000 % 60:00}.{elapsedMs % 1000 / 100}";

    private static uint ParseActionId(string value)
    {
        if (uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex;
        return uint.TryParse(value, out var number) ? number : 0;
    }

    private string ActionDisplayName(uint actionId, string fallback)
    {
        try
        {
            var row = dataManager.GetExcelSheet<ActionSheet>().GetRow(actionId);
            var name = row.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }
        return string.IsNullOrWhiteSpace(fallback) ? $"{T("action")} #{actionId}" : fallback;
    }

    private void DrawCompactActionIcon(uint actionId, string fallback)
    {
        try
        {
            var row = dataManager.GetExcelSheet<ActionSheet>().GetRow(actionId);
            var iconId = row.Icon;
            if (iconId > 0)
            {
                if (!actionIcons.TryGetValue(actionId, out var shared))
                    actionIcons[actionId] = shared = textureProvider.GetFromGameIcon(
                        new GameIconLookup(iconId, hiRes: true));
                var wrap = shared.GetWrapOrDefault();
                if (wrap != null) { ImGui.Image(wrap.Handle, new Vector2(20, 20)); return; }
            }
        }
        catch { }
        ImGui.TextDisabled("·");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ActionDisplayName(actionId, fallback));
    }

    private string CrystallineMapName(string key) => rotationCalendar
        .CrystallineConflictCycle(DateTime.UtcNow)
        .FirstOrDefault(value => value.Key == key)?.Name(configuration.Language) ?? key;

    private void DrawCrystallineRankHistory()
    {
        var player = state();
        var history = crystallineRankHistory.Snapshot(player.LocalPlayerName, player.LocalPlayerWorldId);
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("ccRankHistory"));
        ImGui.SameLine(); ImGui.TextDisabled(T("ccRankHistoryEvidence"));

        var size = new Vector2(Math.Max(240, ImGui.GetContentRegionAvail().X), 230);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("cc-rank-history-plot", size);
        var draw = ImGui.GetWindowDrawList();
        var background = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.ChartBackground(configuration.UiTheme));
        var border = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Border(configuration.UiTheme));
        var grid = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Grid(configuration.UiTheme));
        var muted = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Muted(configuration.UiTheme));
        var accent = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Accent(configuration.UiTheme));
        draw.AddRectFilled(origin, origin + size, background, 2);
        draw.AddRect(origin, origin + size, border, 2);
        const float left = 76, right = 12, top = 10, bottom = 26;
        var chartMin = origin + new Vector2(left, top);
        var chartMax = origin + size - new Vector2(right, bottom);
        for (byte tier = 0; tier <= 8; tier++)
        {
            var y = chartMax.Y - (chartMax.Y - chartMin.Y) * tier / 8.85f;
            draw.AddLine(new Vector2(chartMin.X, y), new Vector2(chartMax.X, y), grid);
            var label = RankTierName(tier);
            draw.AddText(new Vector2(origin.X + left - ImGui.CalcTextSize(label).X - 7, y - 7), muted, label);
        }
        if (history.Count == 0)
        {
            var message = T("noCcRankHistory");
            draw.AddText(origin + (size - ImGui.CalcTextSize(message)) / 2, muted, message);
            return;
        }

        var first = history[0].ObservedAtUtc;
        var last = history[^1].ObservedAtUtc;
        var span = Math.Max(1, (last - first).TotalSeconds);
        Vector2 Point(CrystallineRankObservation value)
        {
            var x = history.Count == 1 ? .5 : (value.ObservedAtUtc - first).TotalSeconds / span;
            var y = Math.Clamp(value.PlotValue / 8.85, 0, 1);
            return new Vector2(chartMin.X + (float)x * (chartMax.X - chartMin.X),
                chartMax.Y - (float)y * (chartMax.Y - chartMin.Y));
        }

        for (var index = 1; index < history.Count; index++)
            draw.AddLine(Point(history[index - 1]), Point(history[index]), accent, 2f);
        CrystallineRankObservation? hovered = null;
        var bestDistance = 9f;
        var mouse = ImGui.GetMousePos();
        foreach (var value in history)
        {
            var point = Point(value);
            draw.AddCircleFilled(point, 4.5f, accent, 18);
            var distance = Vector2.Distance(mouse, point);
            if (ImGui.IsItemHovered() && distance < bestDistance)
            {
                bestDistance = distance;
                hovered = value;
            }
        }
        draw.AddText(new Vector2(chartMin.X, chartMax.Y + 6), muted, first.ToLocalTime().ToString("yyyy-MM-dd"));
        var lastLabel = last.ToLocalTime().ToString("yyyy-MM-dd");
        draw.AddText(new Vector2(chartMax.X - ImGui.CalcTextSize(lastLabel).X, chartMax.Y + 6), muted, lastLabel);
        if (hovered != null)
            ImGui.SetTooltip($"{hovered.ObservedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n{RankLabel(hovered)}\n{T("ccRankSourceProfile")}");
    }

    private void DrawCrystallineMapStats()
    {
        var player = state();
        var history = crystallineMatchHistory.Snapshot(player.LocalPlayerName,
            player.LocalPlayerWorldId);
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("ccMapPerformance"));
        ImGui.SameLine(); ImGui.TextDisabled(T("ccResultEvidence"));
        ImGui.TextWrapped(T("ccWinRateRule"));

        var maps = rotationCalendar.CrystallineConflictCycle(DateTime.UtcNow)
            .GroupBy(value => value.Key).Select(value => value.First()).ToArray();
        if (ImGui.BeginTable("cc-map-stats", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(T("map"), ImGuiTableColumnFlags.WidthStretch, 1.25f);
            ImGui.TableSetupColumn(T("ccPractice"), ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn(T("ccRanked"), ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var map in maps)
            {
                var rows = history.Where(value => value.MapKey == map.Key).ToArray();
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 44);
                ImGui.TableNextColumn();
                DrawCrystallineMapTile(map, $"cc-map-stat-{map.Key}", 36);
                ImGui.TableNextColumn(); DrawCrystallineModeStats(rows, "casual");
                ImGui.TableNextColumn(); DrawCrystallineModeStats(rows, "ranked");
            }
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("allMaps"));
            ImGui.TableNextColumn(); DrawCrystallineModeStats(history, "casual");
            ImGui.TableNextColumn(); DrawCrystallineModeStats(history, "ranked");
            ImGui.EndTable();
        }
    }

    private void DrawCrystallineModeStats(IReadOnlyList<CrystallineMatchRecord> rows, string matchType)
    {
        var selected = rows.Where(value => value.MatchType == matchType).ToArray();
        ImGui.TextDisabled($"{T("matchesPlayed")}  {selected.Length}");
        if (selected.Length == 0) return;
        var wins = selected.Count(value => value.EffectiveOutcome == "win");
        var losses = selected.Count(value => value.EffectiveOutcome == "loss");
        var draws = selected.Count(value => value.EffectiveOutcome == "draw");
        var decided = wins + losses;
        var winRate = decided == 0 ? (double?)null : wins * 100d / decided;
        var result = string.Format(T("ccWinLossDraw"), wins, losses, draws,
            winRate is double rate ? $"{rate:0.0}%" : "—");
        if (winRate is double coloredRate) ImGui.TextColored(LogsColor(coloredRate), result);
        else ImGui.TextDisabled(result);
        var jobs = selected.GroupBy(value => value.ClassJobId)
            .OrderByDescending(value => value.Count()).ThenBy(value => JobSortKey(value.Key))
            .Take(3).Select(value => value.Key).ToArray();
        foreach (var job in jobs)
        {
            DrawJobIcon(job, 17);
            if (job != jobs[^1]) ImGui.SameLine(0, 3);
        }
    }

    private void DrawCrystallineOutcome(CrystallineMatchRecord match)
    {
        var outcome = match.EffectiveOutcome;
        var color = outcome switch
        {
            "win" => new Vector4(.25f, .82f, .55f, 1f),
            "loss" => new Vector4(.92f, .43f, .38f, 1f),
            _ => new Vector4(.88f, .72f, .28f, 1f)
        };
        ImGui.TextColored(color, T(outcome switch
        {
            "win" => "victory",
            "loss" => "defeat",
            _ => "draw"
        }));
    }

    private string RankLabel(CrystallineRankObservation value)
    {
        var tier = RankTierName(value.Tier);
        if (value.Tier >= 6) return $"{tier}  {value.Credit}";
        if (value.Tier == 0) return tier;
        return $"{tier} {value.Riser}  {new string('★', Math.Clamp(value.Stars, (byte)0, (byte)3))}";
    }

    private string RankTierName(byte tier) => tier switch
    {
        1 => T("rankBronze"), 2 => T("rankSilver"), 3 => T("rankGold"),
        4 => T("rankPlatinum"), 5 => T("rankDiamond"), 6 => T("rankCrystal"),
        7 => T("rankOmega"), 8 => T("rankUltima"), _ => T("rankUnranked")
    };

    private void DrawHeader(CollectorUiState value)
    {
        var accent = value.Capturing ? new Vector4(.24f, .84f, .62f, 1) : new Vector4(.42f, .59f, .82f, 1);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BattlefieldUiTheme.Panel(configuration.UiTheme));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(accent.X, accent.Y, accent.Z, .42f));
        ImGui.BeginChild("hero", new Vector2(0,
            uploads.ServerCompatibility.Maintenance ? 98 : 78), true, ImGuiWindowFlags.NoScrollbar);
        ImGui.TextColored(accent, "● " + (value.Capturing ? T("recording") : T("waiting")));
        ImGui.SetWindowFontScale(1.15f);
        ImGui.TextUnformatted(T("tagline"));
        ImGui.SetWindowFontScale(1f);
        ImGui.TextDisabled(T("mapDev"));
        if (uploads.ServerCompatibility.Maintenance)
            ImGui.TextColored(new Vector4(1f, .68f, .24f, 1), T("serverMaintenance"));
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private static void DrawMetric(string label, string value, Vector4 color)
    {
        ImGui.BeginGroup();
        ImGui.TextDisabled(label);
        ImGui.TextColored(color, value);
        ImGui.EndGroup();
    }

    private void DrawStatus(CollectorUiState value)
    {
        ImGui.Spacing();
        DrawMetric(T("captureState"), value.Capturing ? T("active") : T("idle"), value.Capturing
            ? new Vector4(.25f, .85f, .62f, 1) : new Vector4(.65f, .7f, .76f, 1));
        ImGui.SameLine(180); DrawMetric(T("elapsed"), TimeSpan.FromMilliseconds(value.ElapsedMs).ToString(@"mm\:ss"), Vector4.One);
        ImGui.SameLine(340); DrawMetric(T("visible"), value.VisibleActors.ToString(), Vector4.One);
        ImGui.SameLine(520); DrawMetric(T("dropped"), value.DroppedEvents.ToString(), value.DroppedEvents == 0
            ? new Vector4(.25f, .85f, .62f, 1) : new Vector4(1f, .48f, .36f, 1));
        ImGui.Separator();
        ImGui.TextWrapped(value.Capturing
            ? $"{T("matchId")}：{value.MatchUid}\n{T("file")}：{value.OutputPath}"
            : T("waitingHelp"));
        if (ImGui.Button(T("openLiveCombat"))) openLiveCombat();
        ImGui.SameLine(); ImGui.TextDisabled(T("liveCombatHint"));
        if (!string.IsNullOrWhiteSpace(value.OutputPath) && ImGui.Button(T("openFolder")))
            OpenFolder(Path.GetDirectoryName(value.OutputPath)!);
    }

    private void DrawMatches()
    {
        if (ImGui.Button(T("openFolder"))) OpenFolder(CaptureDirectory("frontline"));
        ImGui.SameLine();
        if (ImGui.Button(T("openImportFolder"))) OpenFolder(ImportDirectory("frontline"));
        ImGui.SameLine();
        if (importRunning) ImGui.TextDisabled(T("importRunning"));
        else if (ImGui.Button(T("importCaptures"))) StartImport(ImportDirectory("frontline"));
        if (!string.IsNullOrWhiteSpace(importStatus))
            ImGui.TextDisabled(importStatus);
        ImGui.Separator();
        var matches = repository.Snapshot().Where(value =>
            value.Schema == LocalMatchAnalyzer.Schema).ToArray();
        var history = matches.Select(ToHistoryRow).ToArray();
        ImGui.BeginChild("match-list", new Vector2(370, 0), true);
        ImGui.TextDisabled($"{T("localMatches")}  {matches.Length}"); ImGui.Separator();
        if (ImGui.Selectable($"{T("logsTimeHistory")}##history-overview", selected == null,
                ImGuiSelectableFlags.None, new Vector2(0, 34)))
            selected = null;
        ImGui.Separator();
        foreach (var row in history) DrawMatchCard(row);
        ImGui.EndChild(); ImGui.SameLine();
        ImGui.BeginChild("match-detail", new Vector2(0, 0), true);
        if (selected != null) selected = matches.FirstOrDefault(x => x.MatchUid == selected.MatchUid);
        if (matches.Length == 0) ImGui.TextDisabled(T("empty"));
        else if (selected == null)
            DrawMatchHistory(history);
        else
        {
            if (ImGui.Button($"← {T("logsTimeHistory")}"))
            {
                selected = null;
                ImGui.EndChild();
                return;
            }
            ImGui.SameLine(); ImGui.TextDisabled(T("matchDetailModeHint"));
            ImGui.Separator();
            DrawMatchDetail(selected!);
        }
        ImGui.EndChild();
    }

    private sealed record MatchHistoryRow(LocalMatchAnalysis Match, LocalPlayerAnalysis? Player,
        RemoteScore? Score, BehaviorTendency? Tendency);

    private MatchHistoryRow ToHistoryRow(LocalMatchAnalysis match)
    {
        var player = FindLocalPlayer(match);
        var score = player == null ? null : ScoreFor(match, player);
        BehaviorTendency? tendency = null;
        if (player != null && match.BehaviorTable?.TryGetValue($"{player.Name}@{player.WorldId}", out var values) == true)
            tendency = values.Where(IsVisibleBehaviorTendency).OrderByDescending(x => x.Score).FirstOrDefault();
        return new MatchHistoryRow(match, player, score, tendency);
    }

    private LocalPlayerAnalysis? FindLocalPlayer(LocalMatchAnalysis match)
    {
        var parts = match.LocalPlayerName.Split('@', 2, StringSplitOptions.TrimEntries);
        var name = parts[0];
        if (string.IsNullOrWhiteSpace(name)) return null;
        var candidates = match.Players.Where(player =>
            string.Equals(player.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length > 1 && parts.Length == 2)
            return candidates.FirstOrDefault(player =>
                string.Equals(WorldName(player.WorldId), parts[1], StringComparison.OrdinalIgnoreCase));
        // A duplicated name without world evidence is deliberately unresolved.
        // 缺少服务器证据的同名玩家不得猜成记录者本人。
        return null;
    }

    private void DrawMatchCard(MatchHistoryRow row)
    {
        var match = row.Match;
        var active = selected?.MatchUid == match.MatchUid;
        var title = $"{match.EndedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {MapName(match.DutyId)}";
        ImGui.PushStyleColor(ImGuiCol.ChildBg, active
            ? BattlefieldUiTheme.SelectedPanel(configuration.UiTheme)
            : BattlefieldUiTheme.Panel(configuration.UiTheme));
        ImGui.PushStyleColor(ImGuiCol.Border, active
            ? BattlefieldUiTheme.Accent(configuration.UiTheme)
            : BattlefieldUiTheme.Border(configuration.UiTheme));
        ImGui.BeginChild($"match-card-{match.MatchUid}", new Vector2(0, 86), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.TextUnformatted(title);
        ImGui.SetWindowFontScale(.78f);
        var displayedUid = match.RemoteMatchUid ?? match.MatchUid;
        ImGui.TextDisabled($"#{displayedUid[..Math.Min(16, displayedUid.Length)]}");
        ImGui.SameLine();
        ImGui.TextColored(UploadColor(match.UploadState), $"● {UploadLabel(match.UploadState)}");
        if (row.Player != null)
        {
            DrawJobIcon(row.Player.ClassJobId, 17);
            ImGui.SameLine();
            if (row.Score?.Overall is double logs)
                ImGui.TextColored(LogsColor(logs), $"Logs {logs:0.0}");
            else ImGui.TextDisabled(T("unrankedMatch"));
            ImGui.SameLine();
            ImGui.TextUnformatted($"KDA {row.Player.Kills}/{row.Player.Deaths}/{row.Player.Assists}");
            if (row.Tendency != null)
                ImGui.TextColored(LogsColor(row.Tendency.Score),
                    $"{T(row.Tendency.Key)} {row.Tendency.Score:0.0}");
            else ImGui.TextDisabled(T("noPlaystyleResult"));
        }
        else ImGui.TextDisabled(T("localPlayerUnresolved"));
        ImGui.SetWindowFontScale(1f);
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        // The card is intentionally one hit target. Text, icon and empty padding
        // all select the same match instead of forcing a precise title click.
        // 整张卡片都是选择区域，标题、摘要和留白均可点击。
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) selected = match;
        ImGui.Spacing();
    }

    private void DrawMatchHistory(IReadOnlyList<MatchHistoryRow> history)
    {
        ImGui.TextUnformatted(T("logsTimeHistory"));
        ImGui.SameLine(); ImGui.TextDisabled(T("logsTimeHistoryHelp"));

        var jobIds = history.Where(x => x.Player != null).Select(x => x.Player!.ClassJobId)
            .Distinct().OrderBy(JobSortKey).ToArray();
        if (historyJobFilter != 0 && !jobIds.Contains(historyJobFilter)) historyJobFilter = 0;
        ImGui.SetNextItemWidth(180);
        var jobPreview = historyJobFilter == 0 ? T("allJobs") : JobName(historyJobFilter);
        if (ImGui.BeginCombo(T("jobFilter") + "##history-job", jobPreview))
        {
            if (ImGui.Selectable(T("allJobs"), historyJobFilter == 0)) historyJobFilter = 0;
            foreach (var jobId in jobIds)
                if (ImGui.Selectable(JobName(jobId), historyJobFilter == jobId)) historyJobFilter = jobId;
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        var dutyIds = history.Select(x => x.Match.DutyId).Distinct().OrderBy(x => x).ToArray();
        if (historyDutyFilter != 0 && !dutyIds.Contains(historyDutyFilter)) historyDutyFilter = 0;
        ImGui.SetNextItemWidth(245);
        var mapPreview = historyDutyFilter == 0 ? T("allMaps") : MapName(historyDutyFilter);
        if (ImGui.BeginCombo(T("mapFilter") + "##history-map", mapPreview))
        {
            if (ImGui.Selectable(T("allMaps"), historyDutyFilter == 0)) historyDutyFilter = 0;
            foreach (var dutyId in dutyIds)
                if (ImGui.Selectable(MapName(dutyId), historyDutyFilter == dutyId)) historyDutyFilter = dutyId;
            ImGui.EndCombo();
        }

        var filtered = history.Where(row =>
                (historyJobFilter == 0 || row.Player?.ClassJobId == historyJobFilter) &&
                (historyDutyFilter == 0 || row.Match.DutyId == historyDutyFilter))
            .OrderBy(row => row.Match.EndedAtUtc).ToArray();
        var ranked = filtered.Where(row => row.Score?.Overall != null).ToArray();
        ImGui.SameLine();
        ImGui.TextDisabled(string.Format(T("rankedHistoryCount"), ranked.Length, filtered.Length));
        if (historyDutyFilter != 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(T("compressedMapTimeline"));

        var size = new Vector2(Math.Max(360, ImGui.GetContentRegionAvail().X), 178);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("logs-time-plot", size);
        var draw = ImGui.GetWindowDrawList();
        var background = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.ChartBackground(configuration.UiTheme));
        var border = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Border(configuration.UiTheme));
        var grid = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Grid(configuration.UiTheme));
        var muted = ImGui.ColorConvertFloat4ToU32(new Vector4(.58f, .63f, .68f, 1f));
        draw.AddRectFilled(origin, origin + size, background, 2);
        draw.AddRect(origin, origin + size, border, 2);
        const float left = 36, right = 10, top = 10, bottom = 25;
        var chartMin = origin + new Vector2(left, top);
        var chartMax = origin + size - new Vector2(right, bottom);
        foreach (var tick in new[] { 0, 25, 50, 75, 100 })
        {
            var y = chartMax.Y - (chartMax.Y - chartMin.Y) * tick / 100f;
            draw.AddLine(new Vector2(chartMin.X, y), new Vector2(chartMax.X, y), grid);
            draw.AddText(new Vector2(origin.X + 5, y - 7), muted, tick.ToString());
        }
        if (ranked.Length == 0)
        {
            var message = T("noRankedHistory");
            draw.AddText(origin + (size - ImGui.CalcTextSize(message)) / 2, muted, message);
            return;
        }

        var first = ranked[0].Match.EndedAtUtc;
        var last = ranked[^1].Match.EndedAtUtc;
        var span = Math.Max(1, (last - first).TotalSeconds);
        var ordinal = ranked.Select((row, index) => (row.Match.MatchUid, index))
            .ToDictionary(x => x.MatchUid, x => x.index, StringComparer.Ordinal);
        Vector2 Point(MatchHistoryRow row)
        {
            // A single-map filter compresses the days on which another Frontline
            // map was active, while retaining each actual date in the tooltip.
            // 单地图筛选压缩轮换空档，但悬停仍显示真实结算日期。
            var xRatio = ranked.Length == 1 ? .5 : historyDutyFilter != 0
                ? ordinal[row.Match.MatchUid] / (double)(ranked.Length - 1)
                : (row.Match.EndedAtUtc - first).TotalSeconds / span;
            var yRatio = Math.Clamp(row.Score!.Overall!.Value / 100d, 0, 1);
            return new Vector2(chartMin.X + (float)xRatio * (chartMax.X - chartMin.X),
                chartMax.Y - (float)yRatio * (chartMax.Y - chartMin.Y));
        }

        MatchHistoryRow? hovered = null;
        var mouse = ImGui.GetMousePos();
        var bestDistance = 9f;
        foreach (var row in ranked)
        {
            var point = Point(row);
            var chosen = selected?.MatchUid == row.Match.MatchUid;
            if (chosen) draw.AddCircle(point, 7.5f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(.92f, .95f, 1f, .95f)), 20, 2f);
            draw.AddCircleFilled(point, chosen ? 5.5f : 4.5f,
                ImGui.ColorConvertFloat4ToU32(LogsColor(row.Score!.Overall!.Value)), 20);
            var distance = Vector2.Distance(mouse, point);
            if (ImGui.IsItemHovered() && distance < bestDistance)
            {
                bestDistance = distance;
                hovered = row;
            }
        }
        draw.AddText(new Vector2(chartMin.X, chartMax.Y + 6), muted,
            first.ToLocalTime().ToString("yyyy-MM-dd"));
        var lastLabel = last.ToLocalTime().ToString("yyyy-MM-dd");
        draw.AddText(new Vector2(chartMax.X - ImGui.CalcTextSize(lastLabel).X, chartMax.Y + 6), muted, lastLabel);

        if (hovered != null)
        {
            var player = hovered.Player!;
            var lines = new List<string>
            {
                $"{hovered.Match.EndedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {MapName(hovered.Match.DutyId)}",
                $"{JobName(player.ClassJobId)} · Logs {hovered.Score!.Overall!.Value:0.0} · KDA {player.Kills}/{player.Deaths}/{player.Assists}"
            };
            if (hovered.Tendency != null)
                lines.Add($"{T(hovered.Tendency.Key)} {hovered.Tendency.Score:0.0}");
            ImGui.SetTooltip(string.Join("\n", lines));
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) selected = hovered.Match;
        }
    }

    private void StartImport(string sourceDirectory)
    {
        if (importRunning) return;
        importRunning = true;
        importStatus = T("importRunning");
        _ = Task.Run(() => repository.ImportCaptureFolder(sourceDirectory, configuration.Realm))
            .ContinueWith(task =>
            {
                importRunning = false;
                importStatus = task.IsFaulted
                    ? $"{T("importFailed")}: {task.Exception?.GetBaseException().Message}"
                    : string.Format(T("importComplete"), task.Result.Imported,
                        task.Result.Skipped, task.Result.Failed);
            }, TaskScheduler.Default);
    }

    private void DrawMatchDetail(LocalMatchAnalysis match)
    {
        var displayedUid = match.RemoteMatchUid ?? match.MatchUid;
        ImGui.TextUnformatted($"{T("match")} {displayedUid[..Math.Min(14, displayedUid.Length)]}");
        if (match.RemoteMatchUid != null && match.RemoteMatchUid != match.MatchUid)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({T("localCaptureId")} {match.MatchUid[..Math.Min(8, match.MatchUid.Length)]})");
        }
        ImGui.SameLine();
        if (match.MatchLogs is double logs) ImGui.TextColored(LogsColor(logs), $"Logs {logs:0.0}");
        else ImGui.TextDisabled(T("noLogs"));
        ImGui.TextWrapped(T("evidence"));
        DrawUploadProgress(match.MatchUid);
        if (match.UploadState == "failed" && !string.IsNullOrWhiteSpace(match.UploadError))
            ImGui.TextColored(new Vector4(1f, .55f, .4f, 1f), $"{T("uploadError")}：{UploadErrorText(match.UploadError)}");
        if (match.UploadState is "local_only" or "failed")
        {
            if (ImGui.Button(match.UploadState == "failed" ? T("retry") : T("uploadRank")))
            {
                configuration.AutoUpload = true; saveConfiguration();
                uploads.Enqueue(match.MatchUid, match.CapturePath,
                    string.IsNullOrWhiteSpace(match.Realm) ? configuration.Realm : match.Realm);
            }
        }
        if (!string.IsNullOrWhiteSpace(match.RemoteAnalysisUrl))
        {
            ImGui.SameLine();
            if (ImGui.Button(T("openWeb"))) Process.Start(new ProcessStartInfo(match.RemoteAnalysisUrl) { UseShellExecute = true });
        }
        if (match.UploadState == "complete")
        {
            if (!string.IsNullOrWhiteSpace(match.RemoteAnalysisUrl)) ImGui.SameLine();
            if (ImGui.Button(match.Detail == null ? T("requestDetail") : T("refreshDetail")))
                uploads.EnqueueDetails(match.MatchUid, match.RemoteMatchUid ?? match.MatchUid,
                    string.IsNullOrWhiteSpace(match.Realm) ? configuration.Realm : match.Realm);
        }
        if (match.Detail != null)
        {
            ImGui.SameLine();
            if (ImGui.Button(T("openDetailWindow"))) openDetailedAnalysis(match);
        }
        if (!string.IsNullOrWhiteSpace(match.DetailError))
            ImGui.TextColored(new Vector4(1f, .55f, .4f, 1f), $"{T("detailError")}：{UploadErrorText(match.DetailError)}");
        ImGui.Separator();
        const int columns = 15;
        // A frozen header only works when the table owns its vertical scroll.
        // Give the table the remaining pane height instead of relying on the
        // parent child window to scroll the entire table away.
        var playerTableHeight = Math.Max(180f, ImGui.GetContentRegionAvail().Y);
        if (ImGui.BeginTable("players", columns, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable,
            new Vector2(0, playerTableHeight)))
        {
            var titles = new[] { T("player"), T("job"), T("team"), "K", "K Logs", "D", "D Logs",
                "A", "A Logs", T("damage"), T("damageLogs"), T("healing"), T("healingLogs"),
                T("overallLogs"), T("tendency") };
            for (var i = 0; i < columns; i++) ImGui.TableSetupColumn(titles[i]);
            ImGui.TableSetupScrollFreeze(0, 1);
            DrawSortableHeaders(titles);
            foreach (var player in SortPlayers(match))
            {
                var score = ScoreFor(match, player);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{player.Name}@{WorldName(player.WorldId)}");
                ImGui.TableNextColumn(); DrawJobIcon(player.ClassJobId);
                ImGui.TableNextColumn(); DrawCompactSquadBadge(player.Team, player.Alliance);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Kills.ToString());
                ImGui.TableNextColumn(); DrawLogs(score?.Kills);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Deaths.ToString());
                ImGui.TableNextColumn(); DrawLogs(score?.Survival);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.Assists.ToString());
                ImGui.TableNextColumn(); DrawLogs(score?.Assists);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.DamageDealt.ToString("N0"));
                ImGui.TableNextColumn(); DrawLogs(score?.Damage);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(player.HpRestored.ToString("N0"));
                ImGui.TableNextColumn(); DrawLogs(score?.Healing);
                ImGui.TableNextColumn(); DrawLogs(score?.Overall);
                var key = $"{player.Name}@{player.WorldId}";
                ImGui.TableNextColumn();
                if (match.BehaviorTable?.TryGetValue(key, out var tendencies) == true)
                {
                    var positive = tendencies.Where(IsVisibleBehaviorTendency)
                        .OrderByDescending(x => x.Score).ToArray();
                    if (positive.Length == 0)
                    {
                        ImGui.TextDisabled("—");
                        continue;
                    }
                    ImGui.TextUnformatted($"{T(positive[0].Key)} {positive[0].Score:0}");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Join("\n",
                            positive.Select(x => $"{T(x.Key)}  {x.Score:0.0} · {ConfidenceLabel(x.Confidence)}")));
                }
                else ImGui.TextDisabled("—");
            }
            ImGui.EndTable();
        }
    }

    private void DrawSortableHeaders(string[] titles)
        => DrawSortableHeaders(titles, ref playerSortColumn, ref playerSortDescending);

    private static void DrawSortableHeaders(string[] titles, ref int sortColumn,
        ref bool sortDescending)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var i = 0; i < titles.Length; i++)
        {
            ImGui.TableSetColumnIndex(i);
            var arrow = i == sortColumn ? (sortDescending ? " ▼" : " ▲") : string.Empty;
            ImGui.TableHeader(titles[i] + arrow);
            if (ImGui.IsItemClicked())
            {
                if (sortColumn == i) sortDescending = !sortDescending;
                else { sortColumn = i; sortDescending = true; }
            }
        }
    }

    private IEnumerable<CrystallinePlayerResult> SortCrystallinePlayers(
        LocalMatchAnalysis? analysis, IEnumerable<CrystallinePlayerResult> source)
    {
        var players = source.ToArray();
        double Logs(CrystallinePlayerResult player)
        {
            var key = $"{player.Name}@{player.WorldId}";
            return analysis?.RemoteScores?.GetValueOrDefault(key)?.Overall ??
                   player.OverallLogs ?? -1;
        }
        Func<CrystallinePlayerResult, double> number = crystallineResultSortColumn switch
        {
            1 => player => JobSortKey(player.ClassJobId),
            2 => player => player.Team,
            3 => player => player.Kills,
            4 => player => player.Deaths,
            5 => player => player.Assists,
            6 => player => player.DamageDealt,
            7 => player => player.DamageTaken,
            8 => player => player.HpRestored,
            9 => player => player.TimeOnCrystalSeconds,
            10 => Logs,
            _ => _ => 0
        };
        if (crystallineResultSortColumn == 0)
            return crystallineResultSortDescending
                ? players.OrderByDescending(player => player.Name,
                    StringComparer.CurrentCultureIgnoreCase)
                : players.OrderBy(player => player.Name, StringComparer.CurrentCultureIgnoreCase);
        if (crystallineResultSortColumn == 1)
        {
            var ordered = players.OrderBy(player => JobRole(player.ClassJobId))
                .ThenBy(player => player.ClassJobId).ThenBy(player => player.Name);
            return crystallineResultSortDescending ? ordered.Reverse() : ordered;
        }
        // Tendency is intentionally pending for CC and therefore has no
        // invented numeric ordering.
        if (crystallineResultSortColumn == 11)
            return players.OrderBy(player => player.Team).ThenBy(player => player.Name);
        return crystallineResultSortDescending
            ? players.OrderByDescending(number).ThenBy(player => player.Name)
            : players.OrderBy(number).ThenBy(player => player.Name);
    }

    private IEnumerable<CrystallineActReplayPlayer> SortCrystallineActPlayers(
        IEnumerable<CrystallineActReplayPlayer> source)
    {
        var players = source.ToArray();
        Func<CrystallineActReplayPlayer, double> number = crystallineActSortColumn switch
        {
            1 => player => JobSortKey(player.ClassJobId),
            2 => player => player.Kills,
            3 => player => player.Deaths,
            4 => player => player.Assists,
            5 => player => player.Damage,
            6 => player => player.DamageTaken,
            7 => player => player.Healing,
            _ => _ => 0
        };
        if (crystallineActSortColumn == 0)
            return crystallineActSortDescending
                ? players.OrderByDescending(player => player.Name,
                    StringComparer.CurrentCultureIgnoreCase)
                : players.OrderBy(player => player.Name, StringComparer.CurrentCultureIgnoreCase);
        if (crystallineActSortColumn == 1)
        {
            var ordered = players.OrderBy(player => JobRole(player.ClassJobId))
                .ThenBy(player => player.ClassJobId).ThenBy(player => player.Name);
            return crystallineActSortDescending ? ordered.Reverse() : ordered;
        }
        return crystallineActSortDescending
            ? players.OrderByDescending(number).ThenBy(player => player.Name)
            : players.OrderBy(number).ThenBy(player => player.Name);
    }

    private IEnumerable<LocalPlayerAnalysis> SortPlayers(LocalMatchAnalysis match)
    {
        var players = match.Players;
        Func<LocalPlayerAnalysis, double> number = playerSortColumn switch
        {
            1 => p => JobSortKey(p.ClassJobId),
            2 => p => p.Team * 10 + p.Alliance % 3,
            3 => p => p.Kills,
            4 => p => ScoreFor(match, p)?.Kills ?? -1,
            5 => p => p.Deaths,
            6 => p => ScoreFor(match, p)?.Survival ?? -1,
            7 => p => p.Assists,
            8 => p => ScoreFor(match, p)?.Assists ?? -1,
            9 => p => p.DamageDealt,
            10 => p => ScoreFor(match, p)?.Damage ?? -1,
            11 => p => p.HpRestored,
            12 => p => ScoreFor(match, p)?.Healing ?? -1,
            13 => p => ScoreFor(match, p)?.Overall ?? -1,
            14 => p => TopTendencyScore(match, p),
            _ => _ => 0
        };
        if (playerSortColumn == 0)
            return playerSortDescending
                ? players.OrderByDescending(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                : players.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
        if (playerSortColumn == 1)
            return players.OrderBy(p => JobRole(p.ClassJobId)).ThenBy(p => p.ClassJobId).ThenBy(p => p.Name);
        return playerSortDescending ? players.OrderByDescending(number).ThenBy(p => p.Name)
            : players.OrderBy(number).ThenBy(p => p.Name);
    }

    private static RemoteScore? ScoreFor(LocalMatchAnalysis match, LocalPlayerAnalysis player)
    {
        var key = $"{player.Name}@{player.WorldId}";
        return match.RemoteScores?.TryGetValue(key, out var score) == true ? score : null;
    }

    private static double TopTendencyScore(LocalMatchAnalysis match, LocalPlayerAnalysis player)
    {
        var key = $"{player.Name}@{player.WorldId}";
        return match.BehaviorTable?.TryGetValue(key, out var values) == true
            ? values.Where(IsVisibleBehaviorTendency).Select(x => x.Score).DefaultIfEmpty(-1).Max()
            : -1;
    }

    private static bool IsVisibleBehaviorTendency(BehaviorTendency value) =>
        value.Key is not ("fragility" or "navigation" or "positioning" or "survival");

    private static bool IsNegativeBehaviorIndicator(BehaviorTendency value) =>
        value.Key is "fragility" or "navigation";

    private static void DrawLogs(double? value)
    {
        if (value is double logs) ImGui.TextColored(LogsColor(logs), logs.ToString("0.0"));
        else ImGui.TextDisabled("—");
    }

    private void DrawJobIcon(byte classJobId, float size = 24)
    {
        try
        {
            if (!jobIcons.TryGetValue(classJobId, out var shared))
            {
                // ClassJob rows use the same suffix as the game's framed icon range:
                // PLD 19 -> 062119, MNK 20 -> 062120, ... PCT 42 -> 062142.
                // ClassJob 表行号与游戏内 0621xx 带框职业图标一一对应。
                var lookup = new GameIconLookup((uint)(62100 + classJobId), hiRes: true);
                jobIcons[classJobId] = shared = textureProvider.GetFromGameIcon(lookup);
            }
            var wrap = shared?.GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, new Vector2(size, size));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(JobName(classJobId));
                return;
            }
        }
        catch { /* Fall through to a neutral missing-icon marker. */ }
        ImGui.TextDisabled("?");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(JobName(classJobId));
    }

    private static int JobRole(byte id) => id switch
    {
        19 or 21 or 32 or 37 => 0,
        24 or 28 or 33 or 40 => 1,
        20 or 22 or 30 or 34 or 39 or 41 => 2,
        23 or 31 or 38 => 3,
        25 or 27 or 35 or 42 => 4,
        _ => 9
    };
    private static double JobSortKey(byte id) => JobRole(id) * 1000 + id;
    private string ConfidenceLabel(string value) => value switch
    {
        "low" => T("confidenceLow"), "medium" => T("confidenceMedium"),
        "high" => T("confidenceHigh"), _ => value
    };

    private string TeamName(byte team) => team switch
    {
        0 => T("maelstrom"), 1 => T("adders"), 2 => T("flames"), _ => T("unknownTeam")
    };
    private static Vector4 TeamColor(byte team) => team switch
    {
        0 => new Vector4(.95f, .3f, .3f, 1f),
        1 => new Vector4(.95f, .78f, .25f, 1f),
        2 => new Vector4(.35f, .65f, 1f, 1f),
        _ => new Vector4(.7f, .7f, .7f, 1f)
    };

    private void DrawCompactSquadBadge(byte team, byte alliance)
    {
        if (alliance >= 9) { ImGui.TextDisabled("?"); return; }
        var label = ((char)('A' + alliance % 3)).ToString();
        var at = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"{label}##main-squad-{team}-{alliance}-{at.Y:0}",
            new Vector2(15, ImGui.GetTextLineHeight()));
        var packed = ImGui.ColorConvertFloat4ToU32(TeamColor(team));
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(at, packed, label);
        draw.AddText(at + new Vector2(.65f, 0), packed, label);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{TeamName(team)} · {label}");
    }

    private string MapName(uint dutyId) => dutyId switch
    {
        127 => T("mapBorderland"),
        130 => T("mapSealRock"),
        180 => T("mapShatter"),
        701 => T("mapOnsal"),
        1080 => T("mapWorqor"),
        _ => $"{T("unknownMap")} #{dutyId}"
    };

    private void DrawSettings(CollectorUiState snapshot)
    {
        CompleteCredentialRegistrationIfReady();
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("visibleModes"));
        ImGui.TextDisabled(T("visibleModesHelp"));
        var showFrontline = configuration.ShowFrontline;
        if (ImGui.Checkbox(T("tabFrontlineMatches") + "##show-frontline", ref showFrontline))
        {
            configuration.ShowFrontline = showFrontline;
            if (!showFrontline) selectFrontlineTab = false;
            saveConfiguration();
        }
        ImGui.SameLine();
        var showCrystalline = configuration.ShowCrystallineConflict;
        if (ImGui.Checkbox(T("tabCrystallineMatches") + "##show-crystalline", ref showCrystalline))
        {
            configuration.ShowCrystallineConflict = showCrystalline;
            if (!showCrystalline) selectCrystallineTab = false;
            saveConfiguration();
        }
        ImGui.SameLine();
        var showRivalWings = configuration.ShowRivalWings;
        if (ImGui.Checkbox(T("tabRivalWingsMatches") + "##show-rival-wings", ref showRivalWings))
        {
            configuration.ShowRivalWings = showRivalWings;
            if (!showRivalWings) selectRivalWingsTab = false;
            saveConfiguration();
        }
        ImGui.TextWrapped(T("hiddenModesKeepData"));
        ImGui.Separator();
        var auto = configuration.AutoUpload;
        if (ImGui.Checkbox(T("autoUpload"), ref auto)) { configuration.AutoUpload = auto; saveConfiguration(); }
        ImGui.TextDisabled(T("uploadHelp"));
        var open = configuration.OpenAfterMatch;
        if (ImGui.Checkbox(T("openAfter"), ref open)) { configuration.OpenAfterMatch = open; saveConfiguration(); }
        var server = configuration.ServerBaseUrl;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##server", T("serverHint"), ref server, 512)) { configuration.ServerBaseUrl = server; saveConfiguration(); }
        ImGui.Separator();
        ImGui.TextUnformatted(T("automaticRegistration"));
        var machineDisplay = uploads.MachineBindingId[..Math.Min(8, uploads.MachineBindingId.Length)];
        ImGui.TextDisabled($"{T("detectedMachine")}：{machineDisplay}");
        var registrationBusy = credentialRegistration is { IsCompleted: false };
        if (registrationBusy) ImGui.BeginDisabled();
        if (ImGui.Button(T("registerMachine")))
            BeginCredentialRegistration("machine", uploads.MachineBindingId,
                $"{T("thisComputer")} · {machineDisplay}");
        if (registrationBusy) ImGui.EndDisabled();
        ImGui.SameLine();
        var accountBinding = MachineIdentity.GetGameAccountId(snapshot.LocalPlayerName,
            snapshot.LocalPlayerWorldId);
        var accountLabel = accountBinding == null ? T("accountUnavailable") :
            $"{snapshot.LocalPlayerName}@{WorldName(snapshot.LocalPlayerWorldId)}";
        if (registrationBusy || accountBinding == null) ImGui.BeginDisabled();
        if (ImGui.Button(T("registerAccount")) && accountBinding != null)
            BeginCredentialRegistration("account", accountBinding, accountLabel);
        if (registrationBusy || accountBinding == null) ImGui.EndDisabled();
        ImGui.TextDisabled($"{T("detectedAccount")}：{accountLabel}");
        if (!string.IsNullOrWhiteSpace(credentialRegistrationStatus))
            ImGui.TextWrapped(credentialRegistrationStatus);
        ImGui.TextDisabled(T("automaticRegistrationHelp"));
        if (ImGui.Button(T("openAccount"))) OpenAccountPage(configuration.ServerBaseUrl);
        ImGui.SameLine(); ImGui.TextDisabled(T("accountHelp"));
        var token = configuration.GetCollectorToken();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##token", T("tokenHint"), ref token, 512, ImGuiInputTextFlags.Password))
        { configuration.SetCollectorToken(token); saveConfiguration(); }
        if (ImGui.IsItemDeactivatedAfterEdit() && !string.IsNullOrWhiteSpace(token) &&
            !uploads.GetIntegrityReferenceSnapshotState().Available)
            uploads.RefreshIntegrityReferenceSnapshot();
        ImGui.TextUnformatted($"{T("region")}：{configuration.Realm}");
        ImGui.SameLine(); ImGui.TextDisabled(T("regionLockedToHomeWorld"));
        var languageIndex = Array.IndexOf(I18n.Codes, configuration.Language);
        if (languageIndex < 0) languageIndex = 0;
        if (ImGui.Combo(T("language"), ref languageIndex, string.Join('\0', I18n.Names) + "\0"))
        { configuration.Language = I18n.Codes[languageIndex]; saveConfiguration(); }
        var themes = new[] { "day", "night", "eye", "contrast" };
        var themeIndex = Array.IndexOf(themes, configuration.UiTheme);
        if (themeIndex < 0) themeIndex = 1;
        if (ImGui.Combo(T("uiTheme"), ref themeIndex,
                string.Join('\0', themes.Select(value => T($"uiTheme_{value}"))) + "\0"))
        { configuration.UiTheme = themes[themeIndex]; saveConfiguration(); }
        ImGui.Separator();
        ImGui.TextWrapped(T("privacy"));
    }

    private void BeginCredentialRegistration(string keyKind, string bindingId, string label)
    {
        if (credentialRegistration is { IsCompleted: false }) return;
        credentialRegistrationCancellation?.Cancel();
        credentialRegistrationCancellation?.Dispose();
        credentialRegistrationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(11));
        credentialRegistrationStatus = T("registrationWaiting");
        credentialRegistration = RegisterCredentialAsync(keyKind, bindingId, label,
            credentialRegistrationCancellation.Token);
    }

    private async Task<CredentialRegistrationOutcome> RegisterCredentialAsync(string keyKind,
        string bindingId, string label, CancellationToken cancellationToken)
    {
        try
        {
            var started = await uploads.StartPluginRegistrationAsync(keyKind, bindingId, label,
                cancellationToken).ConfigureAwait(false);
            if (!started.Ok || string.IsNullOrWhiteSpace(started.RequestId) ||
                string.IsNullOrWhiteSpace(started.ClaimSecret) ||
                !TryOpenRegistrationPage(started.BrowserUrl))
                return new CredentialRegistrationOutcome(false, null,
                    started.Error ?? T("registrationStartFailed"));
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                var claim = await uploads.ClaimPluginRegistrationAsync(started.RequestId,
                    started.ClaimSecret, cancellationToken).ConfigureAwait(false);
                if (claim.Ok && claim.Status == "pending") continue;
                if (claim.Ok && claim.Status == "complete" && !string.IsNullOrWhiteSpace(claim.Token))
                    return new CredentialRegistrationOutcome(true, claim.Token,
                        T("registrationComplete"));
                return new CredentialRegistrationOutcome(false, null,
                    claim.Error ?? T("registrationClaimFailed"));
            }
            return new CredentialRegistrationOutcome(false, null, T("registrationExpired"));
        }
        catch (OperationCanceledException)
        {
            return new CredentialRegistrationOutcome(false, null, T("registrationExpired"));
        }
        catch (Exception)
        {
            return new CredentialRegistrationOutcome(false, null, T("registrationNetworkFailed"));
        }
    }

    private void CompleteCredentialRegistrationIfReady()
    {
        if (credentialRegistration is not { IsCompleted: true } completed) return;
        credentialRegistration = null;
        var outcome = completed.GetAwaiter().GetResult();
        credentialRegistrationStatus = outcome.Message;
        if (!outcome.Ok || string.IsNullOrWhiteSpace(outcome.Token)) return;
        configuration.SetCollectorToken(outcome.Token);
        saveConfiguration();
        uploads.RefreshIntegrityReferenceSnapshot();
    }

    private static bool TryOpenRegistrationPage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http")) return false;
        if (uri.Scheme == "http" && uri.Host is not ("127.0.0.1" or "localhost")) return false;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return true;
    }

    private static Vector4 UploadColor(string state) => state switch
    {
        "complete" => new Vector4(.25f, .85f, .62f, 1),
        "uploading" => new Vector4(.35f, .7f, 1f, 1),
        "processing" => new Vector4(.48f, .62f, 1f, 1),
        "failed" => new Vector4(1f, .42f, .35f, 1),
        _ => new Vector4(.64f, .68f, .73f, 1)
    };
    private string UploadLabel(string state) => state switch
    { "complete" => T("ranked"), "uploading" or "processing" => T("uploading"), "failed" => T("failed"), _ => T("local") };
    private void DrawUploadProgress(string matchUid)
    {
        if (!uploads.TryGetUploadProgress(matchUid, out var progress)) return;
        var label = progress.Stage switch
        {
            "compressing" => T("uploadCompressing"),
            "uploading" => T("uploadSending"),
            "processing" => T("uploadProcessing"),
            "complete" => T("uploadComplete"),
            "failed" => T("uploadFailedStage"),
            _ => progress.Stage
        };
        var transferred = progress.CompletedBytes / 1048576d;
        var total = progress.TotalBytes / 1048576d;
        var speed = progress.BytesPerSecond / 1048576d;
        var chunks = progress.TotalChunks > 0
            ? $" · {progress.CompletedChunks}/{progress.TotalChunks}"
            : string.Empty;
        var throughput = progress.BytesPerSecond > 0
            ? $" · {speed:0.00} MiB/s"
            : string.Empty;
        ImGui.TextColored(UploadColor(progress.Stage == "complete" ? "complete" :
            progress.Stage == "failed" ? "failed" : "uploading"),
            $"{label} · {transferred:0.00}/{total:0.00} MiB{chunks}{throughput} · {progress.Elapsed.TotalSeconds:0.0}s");
    }
    private static Vector4 LogsColor(double value) => value >= 99 ? new Vector4(1f, .78f, .2f, 1)
        : value >= 95 ? new Vector4(1f, .35f, .78f, 1) : value >= 75 ? new Vector4(1f, .55f, .2f, 1)
        : value >= 50 ? new Vector4(.3f, .65f, 1f, 1) : value >= 25 ? new Vector4(.35f, .8f, .45f, 1)
        : new Vector4(.6f, .62f, .65f, 1);
    private static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    private static void OpenAccountPage(string serverBaseUrl)
    {
        if (!Uri.TryCreate(serverBaseUrl.TrimEnd('/') + "/account", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http")) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    private string WorldName(ushort id)
    {
        if (worldNames.TryGetValue(id, out var cached)) return cached;
        try { cached = dataManager.GetExcelSheet<WorldSheet>().GetRow(id).Name.ToString(); }
        catch { cached = id.ToString(); }
        if (string.IsNullOrWhiteSpace(cached)) cached = id.ToString();
        worldNames[id] = cached;
        return cached;
    }
    private string JobName(byte id)
    {
        if (jobNames.TryGetValue(id, out var cached)) return cached;
        try { cached = dataManager.GetExcelSheet<ClassJobSheet>().GetRow(id).Abbreviation.ToString(); }
        catch { cached = id.ToString(); }
        if (string.IsNullOrWhiteSpace(cached)) cached = id.ToString();
        jobNames[id] = cached;
        return cached;
    }
    private string UploadErrorText(string error) => error switch
    {
        "missing_token" => T("missingToken"),
        "invalid_token" => T("invalidToken"),
        "network_error" => T("networkError"),
        "analysis_timeout" => T("analysisTimeout"),
        _ => error
    };

    private CultureInfo UiCulture()
    {
        var code = configuration.Language == "auto"
            ? CultureInfo.CurrentUICulture.Name
            : configuration.Language;
        code = code.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
               code.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ? "zh-TW"
            : code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN"
            : code.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "ja-JP"
            : code.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "de-DE"
            : code.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr-FR"
            : code.StartsWith("th", StringComparison.OrdinalIgnoreCase) ? "th-TH"
            : code.StartsWith("vi", StringComparison.OrdinalIgnoreCase) ? "vi-VN"
            : "en-US";
        try { return CultureInfo.GetCultureInfo(code); }
        catch { return CultureInfo.GetCultureInfo("en-US"); }
    }

    private string[] WeekdayNames(CultureInfo culture)
    {
        if (culture.Name.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return ["月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日", "日曜日"];
        if (culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
        var names = culture.DateTimeFormat.DayNames;
        return Enumerable.Range(0, 7).Select(index => names[(index + 1) % 7]).ToArray();
    }

    private string T(string key) => I18n.Get(configuration.Language, key);
}
