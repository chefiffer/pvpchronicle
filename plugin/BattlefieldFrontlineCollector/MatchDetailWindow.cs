using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ClassJobSheet = Lumina.Excel.Sheets.ClassJob;
using WorldSheet = Lumina.Excel.Sheets.World;
using ActionSheet = Lumina.Excel.Sheets.Action;
using StatusSheet = Lumina.Excel.Sheets.Status;
using MapSheet = Lumina.Excel.Sheets.Map;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Dedicated replay workspace. It only visualizes fields whose provenance is explicit.
/// 独立复盘工作台；只展示已有明确证据来源的数据，不用血量差值伪造技能伤害。
/// </summary>
internal sealed class MatchDetailWindow : Window
{
    private readonly Configuration configuration;
    private readonly MatchRepository repository;
    private readonly UploadService uploads;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly string assetRoot;
    private readonly ActionLocalizationCatalog actionCatalog;
    private readonly StatusLocalizationCatalog statusCatalog;
    private readonly Dictionary<ushort, string> worldNames = new();
    private readonly Dictionary<byte, string> jobNames = new();
    private readonly Dictionary<(string Language, long Id), string> statusNames = new();
    private readonly Dictionary<(string Language, uint Id), string> actionNames = new();
    private readonly Dictionary<byte, ISharedImmediateTexture> jobIcons = new();
    private readonly Dictionary<uint, ISharedImmediateTexture> actionIcons = new();
    private readonly Dictionary<long, ISharedImmediateTexture> statusIcons = new();
    private readonly Dictionary<uint, ISharedImmediateTexture> mapTextures = new();
    private readonly Dictionary<uint, ISharedImmediateTexture> markerIcons = new();
    private readonly Dictionary<string, byte[]> copyTextBuffers = new(StringComparer.Ordinal);
    private string? matchUid;
    private float selectedMs;
    private int sliceSortColumn = 4;
    private bool sliceSortDescending = true;
    private int actionSortColumn = 4;
    private bool actionSortDescending = true;
    private int mechanismSortColumn = 4;
    private bool mechanismSortDescending = true;
    private uint? selectedActorId;
    private string? focusedPlayerName;
    private ushort focusedPlayerWorldId;
    private uint? selectedDeathEntityId;
    private long? selectedDeathMs;
    private int copyFieldSequence;
    private float mapZoom = 1.75f;
    private Vector2 mapCenterUv = new(.5f, .5f);
    private string? selectedAnomalyKey;
    private byte? selectedIntegrityJobId;
    private string selectedIntegritySection = "skills";
    private bool scrollToSelectedAnomaly;
    private bool scrollIntegrityToTop;
    private bool selectDeathTab;
    private bool currentMatchIsCrystalline;
    private uint currentTerritoryId;
    private readonly HashSet<string> requestedIntegrityCohorts = new(StringComparer.Ordinal);

    private sealed record MapAsset(string FileName, string NameKey);
    private sealed record MechanismDamageRow(string PlayerName, ushort WorldId,
        byte JobId, byte Team, uint ActionId, string ActionName, long Damage,
        long PlayerTotalDamage, long FirstMs, long LastMs);
    private sealed record SecureMechanismRound(long ElapsedMs, string Id, string Label,
        int OfficialPool, long[] ObservedDamage, string Evidence);
    private sealed record DeathTimelineRow(long ElapsedMs, string Kind,
        byte SourceJobId = 0, string SourceName = "", ushort SourceWorldId = 0,
        byte SourceTeam = 255, byte SourceAlliance = 255,
        uint ActionId = 0, long StatusId = 0, string FallbackName = "",
        byte TargetJobId = 0, string TargetName = "", ushort TargetWorldId = 0,
        byte TargetTeam = 255, byte TargetAlliance = 255,
        long Amount = 0, uint CurrentHp = 0, uint MaxHp = 0, byte ShieldPercent = 0,
        uint CurrentMp = 0, uint MaxMp = 0, byte ActionType = 1,
        uint SourceOwnerEntityId = 0, string SourceOwnerName = "", ushort SourceOwnerWorldId = 0,
        byte SourceOwnerJobId = 0, uint TargetOwnerEntityId = 0, string TargetOwnerName = "",
        ushort TargetOwnerWorldId = 0, byte TargetOwnerJobId = 0, string EffectKind = "");
    private static readonly IReadOnlyDictionary<uint, MapAsset> MapAssets = new Dictionary<uint, MapAsset>
    {
        [127] = new("borderland_ruins.png", "mapBorderland"),
        [130] = new("seal_rock.png", "mapSealRock"),
        [180] = new("fields_of_glory.png", "mapShatter"),
        [701] = new("onsal_hakair.png", "mapOnsal"),
        [1080] = new("worqor_chirteh.png", "mapWorqor")
    };

    private Vector4 Cyan => BattlefieldUiTheme.Accent(configuration.UiTheme);
    private Vector4 TextColor => BattlefieldUiTheme.Text(configuration.UiTheme);
    private Vector4 Muted => BattlefieldUiTheme.Muted(configuration.UiTheme);
    private static readonly Vector4[] TeamColors =
    [
        new(.92f, .28f, .32f, 1f),
        new(.94f, .76f, .25f, 1f),
        new(.27f, .62f, .96f, 1f)
    ];

    public MatchDetailWindow(Configuration configuration, MatchRepository repository, UploadService uploads,
        IDataManager dataManager,
        ITextureProvider textureProvider, string assetRoot)
        : base("Battlefield Chronicle · Detailed analysis###BattlefieldDetailedAnalysis",
            ImGuiWindowFlags.NoScrollbar)
    {
        this.configuration = configuration;
        this.repository = repository;
        this.uploads = uploads;
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;
        this.assetRoot = assetRoot;
        actionCatalog = new ActionLocalizationCatalog(assetRoot);
        statusCatalog = new StatusLocalizationCatalog(assetRoot);
        Size = new Vector2(1420, 860);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    public override void PreDraw() => BattlefieldUiTheme.Push(configuration.UiTheme);
    public override void PostDraw() => BattlefieldUiTheme.Pop();

    public void Open(LocalMatchAnalysis match)
    {
        matchUid = match.MatchUid;
        selectedMs = 0;
        selectedActorId = null;
        focusedPlayerName = null;
        focusedPlayerWorldId = 0;
        selectedDeathEntityId = null;
        selectedDeathMs = null;
        copyTextBuffers.Clear();
        mapZoom = 1.75f;
        mapCenterUv = new Vector2(.5f, .5f);
        selectedAnomalyKey = null;
        selectedIntegrityJobId = null;
        selectedIntegritySection = "skills";
        scrollToSelectedAnomaly = false;
        scrollIntegrityToTop = false;
        requestedIntegrityCohorts.Clear();
        // Old cached details use the former coarse latency bins. They cannot be overlaid
        // honestly on the current 30 ms anonymous reference. Refresh the selected match
        // once from its already uploaded raw package instead of silently hiding gray bars
        // or inventing precision by splitting old coarse bins.
        if (match.UploadState == "complete" &&
            match.Detail?.AnomalyModel != UploadService.CurrentAnomalyModel)
            uploads.EnqueueDetails(match.MatchUid, match.RemoteMatchUid ?? match.MatchUid,
                string.IsNullOrWhiteSpace(match.Realm) ? configuration.Realm : match.Realm);
        IsOpen = true;
    }

    public override void Draw()
    {
        copyFieldSequence = 0;
        WindowName = T("detailWindowTitle") + "###BattlefieldDetailedAnalysis";
        var match = repository.Snapshot().FirstOrDefault(x => x.MatchUid == matchUid);
        if (match == null) { ImGui.TextDisabled(T("detailUnavailable")); return; }
        var detail = match.Detail;
        if (detail == null) { ImGui.TextDisabled(T("detailUnavailable")); return; }
        currentMatchIsCrystalline = match.Schema == LocalMatchAnalyzer.CrystallineSchema;
        currentTerritoryId = match.TerritoryId;

        DrawHero(match, detail);
        if (detail.CaptureFeatures?.Contains("action_effect") != true)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, BattlefieldUiTheme.WarningPanel(configuration.UiTheme));
            ImGui.BeginChild("legacy-capture-warning", new Vector2(0, 64), true, ImGuiWindowFlags.NoScrollbar);
            ImGui.TextColored(new Vector4(1f, .52f, .24f, 1f), T("legacyCaptureTitle"));
            ImGui.TextWrapped(T("legacyCaptureWarning"));
            ImGui.EndChild(); ImGui.PopStyleColor();
        }
        ImGui.Spacing();
        DrawTimeline(match, detail);
        ImGui.Spacing();
        if (match.Schema == LocalMatchAnalyzer.CrystallineSchema)
        {
            DrawCrystallineTabs(match, detail);
            return;
        }
        if (!ImGui.BeginTabBar("detail-tabs")) return;
        if (ImGui.BeginTabItem(T("replayOverview")))
        {
            ImGui.BeginChild("overview-scroll", Vector2.Zero, false, ImGuiWindowFlags.AlwaysVerticalScrollbar);
            DrawOverview(match, detail);
            ImGui.EndChild(); ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(T("markerHistory")))
        {
            ImGui.BeginChild("markers-scroll", Vector2.Zero, false, ImGuiWindowFlags.AlwaysVerticalScrollbar);
            DrawMarkerHistory(detail);
            ImGui.EndChild(); ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(T("behaviorCandidates")))
        {
            ImGui.BeginChild("behaviors-scroll", Vector2.Zero, false, ImGuiWindowFlags.AlwaysVerticalScrollbar);
            DrawBehaviors(match, detail); ImGui.EndChild(); ImGui.EndTabItem();
        }
        var deathTabFlags = selectDeathTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (ImGui.BeginTabItem(T("deathRecords"), deathTabFlags))
        {
            selectDeathTab = false;
            ImGui.BeginChild("deaths-scroll", Vector2.Zero, false, ImGuiWindowFlags.AlwaysVerticalScrollbar);
            DrawDeathRecords(match, detail); ImGui.EndChild(); ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(T("anomalyCandidates")))
        {
            ImGui.BeginChild("anomalies-scroll", Vector2.Zero, false, ImGuiWindowFlags.AlwaysVerticalScrollbar);
            DrawAnomalyCandidates(match, detail);
            DrawIntegrityBackToTop();
            ImGui.EndChild(); ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawCrystallineTabs(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        if (!ImGui.BeginTabBar("cc-detail-tabs")) return;
        if (ImGui.BeginTabItem(T("replayOverview")))
        {
            ImGui.BeginChild("cc-overview-scroll", Vector2.Zero, false,
                ImGuiWindowFlags.AlwaysVerticalScrollbar);
            if (ImGui.BeginTable("cc-overview-columns", 2,
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("cc-map", ImGuiTableColumnFlags.WidthFixed, 620);
                ImGui.TableSetupColumn("cc-slice", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                // Keep both cells at a fixed outer height. Changing the replay
                // cursor may add/remove action rows, but only this child scrolls;
                // the progress chart below no longer jumps vertically.
                ImGui.BeginChild("cc-map-stable", new Vector2(0, 470), false,
                    ImGuiWindowFlags.NoScrollbar);
                DrawPanelTitle(T("ccCrystalReplay"), T("ccCrystalReplayEvidence"));
                DrawMovementMap(match, detail, new Vector2(0, 390));
                ImGui.EndChild();
                ImGui.TableNextColumn();
                ImGui.BeginChild("cc-slice-stable", new Vector2(0, 470), false,
                    ImGuiWindowFlags.AlwaysVerticalScrollbar);
                DrawPanelTitle(T("currentSlice"), T("sliceEvidence"));
                DrawCombatSlice(match, detail);
                ImGui.EndChild();
                ImGui.EndTable();
            }
            ImGui.Spacing();
            DrawPanelTitle(T("ccProgressTimeline"), T("ccProgressTimelineEvidence"));
            DrawCrystallineProgressTimeline(match, detail, new Vector2(0, 230));
            ImGui.Spacing();
            // Chronological combat flow remains full width below the two-column replay.
            DrawPanelTitle(T("observedCombatFlow"), T("hpFlowEvidence"));
            DrawHealthFlow(match, detail, new Vector2(0, 250));
            ImGui.Spacing();
            // Crystalline Conflict uses the same objective-source evidence as
            // Frontline.  This used to be omitted from the CC tab entirely,
            // even though Trick Floor/Yojimbo/Onmyoji actions were captured.
            DrawMechanismAnalysis(match, detail);
            DrawEvidenceBoundary(detail);
            ImGui.EndChild(); ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(T("markerHistory")))
        {
            ImGui.BeginChild("cc-markers-scroll", Vector2.Zero, false,
                ImGuiWindowFlags.AlwaysVerticalScrollbar);
            DrawMarkerHistory(detail);
            ImGui.EndChild(); ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(T("behaviorCandidates")))
        {
            ImGui.TextWrapped(T("ccTendencyPending"));
            ImGui.EndTabItem();
        }
        var deathTabFlags = selectDeathTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (ImGui.BeginTabItem(T("deathRecords"), deathTabFlags))
        {
            selectDeathTab = false;
            ImGui.BeginChild("cc-deaths-scroll", Vector2.Zero, false,
                ImGuiWindowFlags.AlwaysVerticalScrollbar);
            DrawDeathRecords(match, detail);
            ImGui.EndChild(); ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(T("anomalyCandidates")))
        {
            ImGui.BeginChild("cc-anomalies-scroll", Vector2.Zero, false,
                ImGuiWindowFlags.AlwaysVerticalScrollbar);
            DrawAnomalyCandidates(match, detail);
            DrawIntegrityBackToTop();
            ImGui.EndChild(); ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    /// <summary>
    /// Draws a fixed, theme-aware back-to-top control inside the integrity-review scroll region.
    /// The gradual scroll is intentionally scoped to the child window so other tabs keep their position.
    /// 在行为完整性复核的滚动区域内绘制固定的返回顶部按钮；只改变当前子页面的位置。
    /// </summary>
    private void DrawIntegrityBackToTop()
    {
        var scrollY = ImGui.GetScrollY();
        if (scrollIntegrityToTop)
        {
            var nextY = MathF.Max(0f, scrollY - MathF.Max(120f, scrollY * .28f));
            ImGui.SetScrollY(nextY);
            scrollY = nextY;
            if (nextY <= .5f) scrollIntegrityToTop = false;
        }

        // Keep the page visually quiet while it is already at the top.
        if (scrollY < 96f && !scrollIntegrityToTop) return;

        const float size = 38f;
        const float inset = 18f;
        var savedCursor = ImGui.GetCursorScreenPos();
        var childPos = ImGui.GetWindowPos();
        var childSize = ImGui.GetWindowSize();
        var buttonPos = childPos + childSize - new Vector2(size + inset, size + inset);

        ImGui.SetCursorScreenPos(buttonPos);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, size * .5f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(Cyan.X, Cyan.Y, Cyan.Z, .88f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(Cyan.X, Cyan.Y, Cyan.Z, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(Cyan.X * .82f, Cyan.Y * .82f, Cyan.Z * .82f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(.035f, .045f, .05f, 1f));
        if (ImGui.Button("↑##integrity-back-to-top", new Vector2(size, size)))
            scrollIntegrityToTop = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("backToTop"));
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
        ImGui.SetCursorScreenPos(savedCursor);
    }

    private void DrawMechanismAnalysis(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        const long windowMs = 5_000;
        var endMs = (long)selectedMs;
        var startMs = Math.Max(0, endMs - windowMs);
        var timeline = detail.ObjectiveTimeline ?? [];
        var discovery = detail.ObjectiveDiscovery ?? [];
        var objectiveIds = timeline.Select(row => row.ObjectId)
            .Concat(discovery.Select(row => row.ObjectId)).Where(id => id != 0).ToHashSet();
        var allObjectiveActions = (detail.Actions ?? [])
            .Where(row => objectiveIds.Contains(row.TargetEntityId) && row.Damage > 0).ToArray();
        var mechanismSourceActions = (detail.Actions ?? [])
            .Where(row => objectiveIds.Contains(row.SourceEntityId) &&
                          row.ElapsedMs > startMs && row.ElapsedMs <= endMs &&
                          !(match.DutyId == 127 &&
                            row.ActionId is SecureObjectiveCatalog.TowerExplosionActionId or
                                SecureObjectiveCatalog.TowerUnmitigatedExplosionActionId) &&
                          (row.Damage > 0 || row.Healing > 0 ||
                           !string.IsNullOrWhiteSpace(row.EffectKind)))
            .OrderBy(row => row.ElapsedMs).ToArray();
        var sliceActions = allObjectiveActions
            .Where(row => row.ElapsedMs > startMs && row.ElapsedMs <= endMs).ToArray();
        var sliceInteractions = timeline.Where(row => row.ElapsedMs > startMs && row.ElapsedMs <= endMs &&
            (row.ActorEntityId != 0 || !string.IsNullOrWhiteSpace(row.ActorName) ||
             row.InteractionDurationMs.HasValue || row.InterrupterEntityId != 0 ||
             !string.IsNullOrWhiteSpace(row.InterrupterName))).ToArray();
        var targetIds = sliceActions.Select(row => row.TargetEntityId)
            .Concat(sliceInteractions.Select(row => row.ObjectId)).Distinct().ToArray();
        var hasRedSandsEnvironmentStatus = match.TerritoryId is 1138 or 1139 &&
            (detail.StatusTimeline ?? []).Any(row => row.StatusId == 3754 &&
                row.ElapsedMs > startMs && row.ElapsedMs <= endMs);

        ImGui.TextColored(Cyan, $"{T("mechanismParticipation")} · {Clock(startMs)}–{Clock(endMs)}");
        ImGui.TextWrapped(T("mechanismSliceEvidence"));
        if (match.DutyId == 127)
        {
            DrawSecureMechanismScoreEvidence(match, detail);
            DrawSecureNodeSnapshot(detail, endMs);
            DrawSecureTowerEvents(detail, startMs, endMs);
        }
        if (targetIds.Length == 0 && mechanismSourceActions.Length == 0 &&
            !hasRedSandsEnvironmentStatus)
        {
            ImGui.TextDisabled(T("noMechanismActionEvidence"));
            return;
        }

        foreach (var targetId in targetIds)
        {
            var targetActions = sliceActions.Where(row => row.TargetEntityId == targetId)
                .OrderBy(row => row.ElapsedMs).ToArray();
            var lastTimeline = timeline.Where(row => row.ObjectId == targetId && row.ElapsedMs <= endMs)
                .OrderByDescending(row => row.ElapsedMs).FirstOrDefault();
            var targetName = targetActions.Select(row => row.TargetName)
                .LastOrDefault(name => !string.IsNullOrWhiteSpace(name));
            if (string.IsNullOrWhiteSpace(targetName)) targetName = lastTimeline?.Label;
            if (string.IsNullOrWhiteSpace(targetName) || IsRawObjectiveIdentifier(targetName))
                targetName = T("unresolvedObjective");
            else targetName = ObjectiveInstanceDisplayName(match.DutyId, detail,
                targetId, targetName, endMs);

            var activeStatuses = (detail.StatusTimeline ?? [])
                .Where(row => row.TargetEntityId == targetId && row.ElapsedMs <= endMs)
                .GroupBy(row => (row.StatusId, row.SourceEntityId))
                .Select(group => group.OrderByDescending(row => row.ElapsedMs).First())
                .Where(row => !row.Change.Equals("remove", StringComparison.OrdinalIgnoreCase) &&
                    (row.DurationSeconds <= 0 || row.ElapsedMs + row.DurationSeconds * 1_000 >= endMs))
                .OrderBy(row => row.StatusId).ToArray();

            ImGui.PushID($"mechanism-target-{targetId}");
            ImGui.PushStyleColor(ImGuiCol.ChildBg, BattlefieldUiTheme.Panel(configuration.UiTheme));
            ImGui.PushStyleColor(ImGuiCol.Border,
                BattlefieldUiTheme.Border(configuration.UiTheme) with { W = .78f });
            var rowCount = Math.Max(1, targetActions
                .Select(row => (row.SourceOwnerEntityId != 0 ? row.SourceOwnerEntityId : row.SourceEntityId,
                    row.ActionId)).Distinct().Count());
            var statusesPerRow = Math.Max(1, (int)(Math.Max(120, ImGui.GetContentRegionAvail().X - 24) / 42));
            var statusRows = Math.Max(1, (int)Math.Ceiling(activeStatuses.Length / (double)statusesPerRow));
            var interactionCount = sliceInteractions.Count(row => row.ObjectId == targetId);
            var cardHeight = 132 + Math.Min(10, rowCount) * 27 +
                             Math.Max(0, statusRows - 1) * 28 + interactionCount * 25;
            ImGui.BeginChild("target-card", new Vector2(0, cardHeight), true,
                ImGuiWindowFlags.NoScrollbar);

            ImGui.TextColored(Cyan, targetName);
            var totalDamage = targetActions.Sum(row => row.Damage);
            var lastAction = targetActions.LastOrDefault();
            var maxHp = targetActions.Select(row => row.TargetMaxHp).LastOrDefault(value => value > 0);
            if (maxHp == 0) maxHp = lastTimeline?.MaxHp ?? 0;
            var before = allObjectiveActions.Where(row => row.TargetEntityId == targetId &&
                    row.ElapsedMs <= startMs && row.TargetMaxHp > 0)
                .OrderByDescending(row => row.ElapsedMs).FirstOrDefault();
            var firstAction = targetActions.FirstOrDefault();
            var startHp = before?.TargetCurrentHp ??
                (firstAction != null && maxHp > 0
                    ? (uint)Math.Min(maxHp, firstAction.TargetCurrentHp + Math.Max(0, firstAction.Damage))
                    : lastTimeline?.CurrentHp ?? 0);
            var endHp = lastAction?.TargetCurrentHp ?? lastTimeline?.CurrentHp ?? startHp;
            var observedLoss = startHp > endHp ? startHp - endHp : 0;
            var lossPercent = maxHp > 0 ? 100d * observedLoss / maxHp : (double?)null;
            ImGui.SameLine(0, 14);
            ImGui.TextUnformatted($"{T("damage")} {totalDamage:N0}");
            if (lossPercent.HasValue)
            {
                ImGui.SameLine(0, 14);
                ImGui.TextUnformatted($"{T("sliceHpLoss")} {lossPercent.Value:0.0}%");
            }
            if (maxHp > 0)
            {
                ImGui.SameLine(0, 14);
                DrawHpShieldBar(endHp, maxHp, lastAction?.TargetShieldPercent ?? 0,
                    new Vector2(236, 17));
            }

            ImGui.TextDisabled(T("objectiveBuffs"));
            if (activeStatuses.Length == 0) ImGui.SameLine(0, 8);
            if (activeStatuses.Length == 0) ImGui.TextDisabled("—");
            for (var index = 0; index < activeStatuses.Length; index++)
            {
                if (index > 0 && ImGui.GetCursorPosX() + 30 < ImGui.GetWindowContentRegionMax().X)
                    ImGui.SameLine(0, 5);
                DrawStatusGlyph(activeStatuses[index].StatusId, 22);
            }

            var playerTotals = targetActions.GroupBy(action =>
                    action.SourceOwnerEntityId != 0 ? action.SourceOwnerEntityId : action.SourceEntityId)
                .ToDictionary(group => group.Key, group => group.Sum(action => action.Damage));
            var rows = targetActions.GroupBy(action => new
                {
                    EntityId = action.SourceOwnerEntityId != 0 ? action.SourceOwnerEntityId : action.SourceEntityId,
                    Name = string.IsNullOrWhiteSpace(action.SourceOwnerName) ? action.SourceName : action.SourceOwnerName,
                    WorldId = action.SourceOwnerWorldId != 0 ? action.SourceOwnerWorldId : action.SourceWorldId,
                    JobId = action.SourceOwnerJobId != 0 ? action.SourceOwnerJobId : action.SourceJobId,
                    action.SourceTeam, action.ActionId, action.ActionName
                })
                .Select(group => new MechanismDamageRow(group.Key.Name, group.Key.WorldId,
                    group.Key.JobId, group.Key.SourceTeam, group.Key.ActionId, group.Key.ActionName,
                    group.Sum(action => action.Damage), playerTotals[group.Key.EntityId],
                    group.Min(action => action.ElapsedMs),
                    group.Max(action => action.ElapsedMs))).ToArray();
            Func<MechanismDamageRow, IComparable> sortKey = mechanismSortColumn switch
            {
                0 => row => row.PlayerName,
                1 => row => row.JobId,
                2 => row => row.Team,
                3 => row => ActionName(row.ActionId, row.ActionName),
                4 => row => row.PlayerTotalDamage,
                5 => row => row.Damage,
                6 => row => maxHp > 0 ? row.Damage / (double)maxHp : 0,
                _ => row => row.LastMs
            };
            rows = (mechanismSortDescending ? rows.OrderByDescending(sortKey) : rows.OrderBy(sortKey)).ToArray();

            if (rows.Length > 0 && ImGui.BeginTable("target-damage", 8,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
                    new Vector2(0, Math.Min(278, 31 + rows.Length * 27))))
            {
                var headers = new[] { T("player"), T("job"), T("team"), T("action"),
                    T("playerTotalDamage"), T("damage"), T("targetHpPercent"), T("time") };
                ImGui.TableSetupColumn(headers[0], ImGuiTableColumnFlags.WidthStretch, 1.4f);
                ImGui.TableSetupColumn(headers[1], ImGuiTableColumnFlags.WidthFixed, 34);
                ImGui.TableSetupColumn(headers[2], ImGuiTableColumnFlags.WidthFixed, 82);
                ImGui.TableSetupColumn(headers[3], ImGuiTableColumnFlags.WidthStretch, 1.3f);
                ImGui.TableSetupColumn(headers[4], ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn(headers[5], ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn(headers[6], ImGuiTableColumnFlags.WidthFixed, 92);
                ImGui.TableSetupColumn(headers[7], ImGuiTableColumnFlags.WidthFixed, 105);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                for (var index = 0; index < headers.Length; index++)
                {
                    ImGui.TableSetColumnIndex(index);
                    ImGui.TableHeader(headers[index] + (index == mechanismSortColumn
                        ? mechanismSortDescending ? " ▼" : " ▲" : string.Empty));
                    if (ImGui.IsItemClicked())
                    {
                        if (mechanismSortColumn == index) mechanismSortDescending = !mechanismSortDescending;
                        else { mechanismSortColumn = index; mechanismSortDescending = true; }
                    }
                }
                foreach (var row in rows)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    DrawCopyableText($"{row.PlayerName}@{WorldName(row.WorldId)}", TextColor);
                    ImGui.TableNextColumn(); DrawJobIcon(row.JobId);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(row.Team < 3 ? TeamColors[row.Team] : Muted,
                        row.Team < 3 ? TeamName(row.Team) : T("unknown"));
                    ImGui.TableNextColumn(); DrawActionGlyph(row.ActionId, 20); ImGui.SameLine(0, 5);
                    ImGui.TextUnformatted(ActionName(row.ActionId, row.ActionName));
                    Cell(row.PlayerTotalDamage.ToString("N0"));
                    Cell(row.Damage.ToString("N0"));
                    Cell(maxHp > 0 ? $"{100d * row.Damage / maxHp:0.00}%" : "—");
                    Cell(row.FirstMs == row.LastMs ? Clock(row.FirstMs) :
                        $"{Clock(row.FirstMs)}–{Clock(row.LastMs)}");
                }
                ImGui.EndTable();
            }

            foreach (var interaction in sliceInteractions.Where(row => row.ObjectId == targetId))
            {
                ImGui.TextDisabled($"{Clock(interaction.ElapsedMs)} · {T("captureInteractionDetails")}");
                ImGui.SameLine(0, 8);
                if (!string.IsNullOrWhiteSpace(interaction.ActorName))
                    DrawCopyableText($"{interaction.ActorName}@{WorldName(interaction.ActorWorldId)}", TextColor);
                if (interaction.InteractionDurationMs is long duration)
                {
                    ImGui.SameLine(0, 8); ImGui.TextUnformatted($"{duration / 1000d:0.00} s");
                }
                if (!string.IsNullOrWhiteSpace(interaction.InterrupterName))
                {
                    ImGui.SameLine(0, 8); ImGui.TextUnformatted($"{T("interrupter")}:");
                    ImGui.SameLine(0, 4);
                    DrawCopyableText($"{interaction.InterrupterName}@{WorldName(interaction.InterrupterWorldId)}", TextColor);
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleColor(2);
            ImGui.PopID();
            ImGui.Spacing();
        }

        if (mechanismSourceActions.Length > 0)
        {
            ImGui.TextColored(Cyan, T("mechanismIncomingActions"));
            ImGui.TextWrapped(T("mechanismIncomingEvidence"));
            if (ImGui.BeginTable("mechanism-source-actions", 5,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingFixedFit,
                    new Vector2(0, Math.Min(260, 31 + mechanismSourceActions.Length * 27))))
            {
                foreach (var header in new[] { T("time"), T("mechanismObject"),
                             T("action"), T("target"), T("amount") })
                    ImGui.TableSetupColumn(header);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();
                foreach (var row in mechanismSourceActions)
                {
                    ImGui.TableNextRow(); Cell(Clock(row.ElapsedMs));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(ObjectiveInstanceDisplayName(match.DutyId, detail,
                        row.SourceEntityId, string.IsNullOrWhiteSpace(row.SourceName)
                            ? T("mechanismObject") : row.SourceName, endMs));
                    ImGui.TableNextColumn(); DrawActionGlyph(row.ActionId, 20);
                    ImGui.SameLine(0, 5);
                    ImGui.TextUnformatted(ActionName(row.ActionId, row.ActionName));
                    ImGui.TableNextColumn();
                    DrawActorIdentity(row.TargetJobId, row.TargetName, row.TargetWorldId,
                        row.TargetTeam, row.TargetAlliance, row.TargetOwnerName,
                        row.TargetOwnerWorldId, row.TargetOwnerJobId, row.TargetEntityId);
                    ImGui.TableNextColumn();
                    var amount = row.Damage > 0 ? row.Damage : row.Healing;
                    ImGui.TextUnformatted(amount > 0 ? amount.ToString("N0") : row.EffectKind);
                }
                ImGui.EndTable();
            }
        }
        if (match.TerritoryId is 1138 or 1139)
            DrawRedSandsEnvironmentStatuses(detail, startMs, endMs);
    }

    private void DrawRedSandsEnvironmentStatuses(DetailedAnalysis detail, long startMs, long endMs)
    {
        var rows = (detail.MovementTimeline ?? [])
            .Where(frame => frame.ElapsedMs > startMs && frame.ElapsedMs <= endMs &&
                            (frame.Actors ?? []).Any(actor => (actor.Statuses ?? [])
                                .Any(status => status.StatusId == RedSandsMechanics.HeatWaveStatusId)))
            .OrderBy(frame => frame.ElapsedMs).ToArray();
        if (rows.Length == 0) return;
        ImGui.Spacing();
        ImGui.TextColored(Cyan, T("mechanismStatusDetails"));
        if (!ImGui.BeginTable("red-sands-environment-statuses", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn(T("time"), ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(T("status"), ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var row in rows.GroupBy(frame => frame.ElapsedMs / 3_000).Select(group => group.First()))
        {
            ImGui.TableNextRow(); Cell(Clock(row.ElapsedMs));
            ImGui.TableNextColumn(); DrawStatusGlyph(RedSandsMechanics.HeatWaveStatusId, 20);
            ImGui.SameLine(0, 5); ImGui.TextUnformatted(T("ccRedSandsHeatWaveActive"));
        }
        ImGui.EndTable();
    }

    /// <summary>
    /// Separates the two score evidence layers available for Secure. The result
    /// packet gives authoritative match totals for occupation and drone points,
    /// but does not contain a per-round breakdown. Per-object action effects can
    /// reconstruct observed damage shares; those rows are estimates and are
    /// deliberately prefixed with an approximation sign.
    /// </summary>
    private void DrawSecureMechanismScoreEvidence(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        ImGui.Spacing();
        ImGui.TextColored(Cyan, T("scoreChanges"));
        if (ImGui.BeginTable("secure-result-score-evidence", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(T("mechanismObject"), ImGuiTableColumnFlags.WidthStretch, 1.5f);
            for (byte team = 0; team < 3; team++)
                ImGui.TableSetupColumn(TeamName(team), ImGuiTableColumnFlags.WidthFixed, 92);
            ImGui.TableSetupColumn(T("evidenceBoundary"), ImGuiTableColumnFlags.WidthStretch, 1.8f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            DrawSecureResultScoreRow(T("secureNodeOwnership"), match.Teams,
                team => team.OccupationPoints, T("secureResultPacketCategoryTotal"));
            DrawSecureResultScoreRow($"{T("secureInterceptorDrone")} / {T("secureInterceptorNode")}",
                match.Teams, team => team.DronePoints, T("secureResultPacketCategoryTotal"));
            ImGui.EndTable();
        }

        var rounds = BuildSecureMechanismRounds(detail);
        if (rounds.Count == 0)
        {
            ImGui.TextDisabled(T("notObserved"));
            return;
        }

        ImGui.TextDisabled(T("secureDamageShareFormula"));
        if (!ImGui.BeginTable("secure-mechanism-round-score-evidence", 7,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Min(292, 31 + rounds.Count * 25)))) return;
        ImGui.TableSetupColumn(T("time"), ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(T("mechanismObject"), ImGuiTableColumnFlags.WidthStretch, 1.25f);
        for (byte team = 0; team < 3; team++)
            ImGui.TableSetupColumn(TeamName(team), ImGuiTableColumnFlags.WidthFixed, 82);
        ImGui.TableSetupColumn(T("evidenceBoundary"), ImGuiTableColumnFlags.WidthStretch, 1.55f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var round in rounds.OrderBy(row => row.ElapsedMs).ThenBy(row => row.Id))
        {
            ImGui.TableNextRow(); Cell(Clock(round.ElapsedMs)); Cell(round.Id); Cell(round.Label);
            var observedTotal = round.ObservedDamage.Sum();
            for (var team = 0; team < 3; team++)
            {
                ImGui.TableNextColumn();
                if (round.OfficialPool <= 0 || observedTotal <= 0)
                    ImGui.TextDisabled("—");
                else
                    ImGui.TextUnformatted($"≈{round.OfficialPool * round.ObservedDamage[team] / (double)observedTotal:0.0}");
            }
            Cell(round.Evidence);
        }
        ImGui.EndTable();
    }

    private void DrawSecureResultScoreRow(string label, IReadOnlyList<TeamResult> teams,
        Func<TeamResult, ushort> value, string evidence)
    {
        ImGui.TableNextRow(); Cell(label);
        for (var team = 0; team < 3; team++)
        {
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(team < teams.Count ? value(teams[team]).ToString("N0") : "—");
        }
        Cell(evidence);
    }

    private IReadOnlyList<SecureMechanismRound> BuildSecureMechanismRounds(DetailedAnalysis detail)
    {
        var result = new List<SecureMechanismRound>();
        var discovery = (detail.ObjectiveDiscovery ?? []).GroupBy(row => row.ObjectId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.ElapsedMs).First());
        var actions = (detail.Actions ?? []).Where(row => row.Damage > 0).OrderBy(row => row.ElapsedMs).ToArray();

        var droneGroups = actions.Where(row =>
                (discovery.TryGetValue(row.TargetEntityId, out var observed) &&
                 SecureObjectiveCatalog.IsDrone(observed.BaseId, observed.Name)) ||
                (row.TargetName ?? string.Empty).Contains("Interceptor Drone", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.TargetEntityId)
            .Select(group => new { TargetId = group.Key, Actions = group.OrderBy(row => row.ElapsedMs).ToArray() })
            .Where(group => (detail.ObjectiveDiscovery ?? []).Any(row =>
                row.ObjectId == group.TargetId && row.MaxHp > 0 && row.CurrentHp == 0))
            .OrderBy(group => group.Actions[0].ElapsedMs).ToArray();
        var droneGeneration = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in droneGroups)
        {
            discovery.TryGetValue(group.TargetId, out var observed);
            var targetObservations = (detail.ObjectiveDiscovery ?? [])
                .Where(row => row.ObjectId == group.TargetId).OrderBy(row => row.ElapsedMs).ToArray();
            var isGold = observed != null
                ? SecureObjectiveCatalog.IsGoldDrone(observed.BaseId, observed.Name)
                : SecureObjectiveCatalog.IsGoldDrone(0, group.Actions[0].TargetName);
            var x = observed?.X ?? group.Actions.Select(row => row.TargetX).FirstOrDefault(value => value.HasValue) ?? 0;
            var z = observed?.Z ?? group.Actions.Select(row => row.TargetZ).FirstOrDefault(value => value.HasValue) ?? 0;
            var slot = SecureObjectiveCatalog.TowerSlot(x, z);
            var generationKey = $"{(isGold ? "G" : "D")}-{slot}";
            droneGeneration.TryGetValue(generationKey, out var generation);
            generation++;
            droneGeneration[generationKey] = generation;
            // A zero-HP observation is the scoring boundary. The first damage
            // event may precede it by many seconds and is not a round time.
            var destroyedAt = targetObservations.First(row => row.MaxHp > 0 && row.CurrentHp == 0).ElapsedMs;
            result.Add(new SecureMechanismRound(destroyedAt,
                $"{generationKey}-{generation:00}",
                T(isGold ? "secureGoldInterceptorDrone" : "secureInterceptorDrone"),
                isGold ? SecureObjectiveCatalog.GoldDroneScorePool : SecureObjectiveCatalog.NormalDroneScorePool,
                SecureObservedTeamDamage(group.Actions), T("secureDamageShareEstimate")));
        }

        var nodeObservations = (detail.ObjectiveDiscovery ?? [])
            .Where(row => row.BaseId == SecureObjectiveCatalog.InterceptorNodeBaseId && row.MaxHp > 0)
            .GroupBy(row => row.ObjectId);
        foreach (var observationGroup in nodeObservations)
        {
            var observations = observationGroup.OrderBy(row => row.ElapsedMs).ToArray();
            if (observations.Length == 0) continue;
            var boundaries = new List<long>();
            for (var index = 1; index < observations.Length; index++)
            {
                var previous = observations[index - 1];
                var current = observations[index];
                if (current.CurrentHp > previous.CurrentHp + current.MaxHp / 2)
                    boundaries.Add(current.ElapsedMs);
            }
            var finalZero = observations.FirstOrDefault(row => row.CurrentHp == 0);
            if (finalZero != null) boundaries.Add(finalZero.ElapsedMs);
            boundaries = boundaries.Distinct().OrderBy(value => value)
                .Take(SecureObjectiveCatalog.InterceptorNodePhaseCount).ToList();
            var phaseStart = observations[0].ElapsedMs;
            for (var phase = 0; phase < boundaries.Count; phase++)
            {
                var phaseEnd = boundaries[phase];
                var phaseActions = actions.Where(row => row.TargetEntityId == observationGroup.Key &&
                    row.ElapsedMs >= phaseStart && row.ElapsedMs < phaseEnd).ToArray();
                result.Add(new SecureMechanismRound(phaseEnd,
                    $"N-{phase + 1:00}", T("secureInterceptorNode"),
                    SecureObjectiveCatalog.InterceptorNodePhaseScorePool,
                    SecureObservedTeamDamage(phaseActions), T("secureDamageShareEstimate")));
                phaseStart = phaseEnd;
            }
        }

        // UI ownership markers establish a transition but do not reveal the
        // points granted by that one transition (Secure also awards points over
        // time). Keep every transition visible with blank score cells.
        var timeline = detail.ObjectiveTimeline ?? [];
        foreach (var node in SecureObjectiveCatalog.Nodes)
        {
            int? previousOwner = null;
            foreach (var state in timeline.Where(row =>
                         Math.Pow(row.X - node.X, 2) + Math.Pow(row.Z - node.Z, 2) <= 12 * 12 &&
                         SecureObjectiveCatalog.TryGetMapIcon(row.State, out var iconId) &&
                         SecureObjectiveCatalog.IsOccupationIcon(iconId))
                     .OrderBy(row => row.ElapsedMs))
            {
                SecureObjectiveCatalog.TryGetMapIcon(state.State, out var iconId);
                var owner = state.OwnerTeam is byte ownerTeam ? ownerTeam :
                    SecureObjectiveCatalog.OwnerFromIcon(iconId) is byte iconOwner ? iconOwner : 3;
                if (previousOwner == owner) continue;
                previousOwner = owner;
                result.Add(new SecureMechanismRound(state.ElapsedMs, node.Code,
                    node.Code, 0, [0, 0, 0],
                    T("secureUiStateRoundScoreUnknown")));
            }
        }

        // 63987 is a persistent mechanism-family marker, not a new round.
        // 63979 is the actual countdown and 63980 is its resolution boundary.
        var commandTimes = timeline.Where(row =>
                SecureObjectiveCatalog.TryGetMapIcon(row.State, out var iconId) &&
                SecureObjectiveCatalog.IsCommandCountdownIcon(iconId))
            .OrderBy(row => row.ElapsedMs).Select(row => row.ElapsedMs).ToArray();
        long previousCommandObservation = long.MinValue;
        var commandRound = 0;
        foreach (var elapsed in commandTimes)
        {
            var startsNewRound = previousCommandObservation == long.MinValue ||
                                 elapsed - previousCommandObservation > 10_000;
            previousCommandObservation = elapsed;
            if (startsNewRound)
            {
                commandRound++;
                var resolution = timeline.Where(row => row.ElapsedMs >= elapsed &&
                        row.ElapsedMs <= elapsed + 45_000 &&
                        SecureObjectiveCatalog.TryGetMapIcon(row.State, out var iconId) &&
                        SecureObjectiveCatalog.IsCommandResolutionIcon(iconId))
                    .OrderBy(row => row.ElapsedMs).FirstOrDefault();
                result.Add(new SecureMechanismRound(resolution?.ElapsedMs ?? elapsed,
                    $"CS-{commandRound:00}",
                    T("secureCommandSystem"), SecureObjectiveCatalog.CommandSystemScorePool,
                    [0, 0, 0], T("secureUiRoundParticipantsScoreUnknown")));
            }
        }
        return result;
    }

    private static long[] SecureObservedTeamDamage(IEnumerable<ActionSliceEvent> actions)
    {
        var damage = new long[3];
        foreach (var action in actions)
            if (action.SourceTeam < 3) damage[action.SourceTeam] += Math.Max(0, action.Damage);
        return damage;
    }

    private void DrawSecureNodeSnapshot(DetailedAnalysis detail, long endMs)
    {
        var frame = detail.MovementTimeline
            .Where(row => row.ElapsedMs <= endMs && endMs - row.ElapsedMs <= 2_500)
            .OrderByDescending(row => row.ElapsedMs).FirstOrDefault();
        var timeline = detail.ObjectiveTimeline ?? [];
        ImGui.Spacing();
        ImGui.TextColored(Cyan, T("secureNodeOwnership"));
        if (!ImGui.BeginTable("secure-node-snapshot", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn(T("mechanismObject"), ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn(T("state"), ImGuiTableColumnFlags.WidthFixed, 118);
        ImGui.TableSetupColumn(T("secureStandingPlayers"), ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var node in SecureObjectiveCatalog.Nodes)
        {
            var states = timeline.Where(row => row.ElapsedMs <= endMs)
                .GroupBy(row => row.ObjectId)
                .Select(group => group.OrderByDescending(row => row.ElapsedMs).First())
                .Where(row => !string.Equals(row.State, "inactive", StringComparison.OrdinalIgnoreCase) &&
                              !(row.State ?? string.Empty).Contains("destroy", StringComparison.OrdinalIgnoreCase) &&
                              !(row.State ?? string.Empty).Contains("unload", StringComparison.OrdinalIgnoreCase) &&
                              Math.Pow(row.X - node.X, 2) + Math.Pow(row.Z - node.Z, 2) <= 12 * 12)
                .OrderByDescending(row => row.ElapsedMs).ToArray();
            // Ownership is a state machine, not an "ever seen" aggregate. A
            // newer neutral marker must replace an older faction owner.
            var latestOwnership = states.FirstOrDefault(row =>
                !SecureObjectiveCatalog.IsFixedNodeId(row.ObjectId) &&
                (row.OwnerTeam.HasValue ||
                 SecureObjectiveCatalog.TryGetMapIcon(row.State, out var iconId) &&
                 SecureObjectiveCatalog.IsOccupationIcon(iconId)));
            var owner = latestOwnership?.OwnerTeam;
            var explicitlyNeutral = latestOwnership != null &&
                SecureObjectiveCatalog.TryGetMapIcon(latestOwnership.State, out var latestIconId) &&
                SecureObjectiveCatalog.IsNeutralOccupationIcon(latestIconId);
            var standing = (frame?.Actors ?? []).Where(actor =>
                    Math.Abs(actor.Y - node.Y) <= 7 &&
                    Math.Pow(actor.X - node.X, 2) + Math.Pow(actor.Z - node.Z, 2) <= 12 * 12)
                .OrderBy(actor => actor.Team).ThenBy(actor => actor.Name).ToArray();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(SecureNodeDisplayName(node));
            ImGui.TableNextColumn();
            if (owner is byte team && team < 3)
                ImGui.TextColored(TeamColors[team], TeamName(team));
            else if (explicitlyNeutral)
                ImGui.TextColored(Muted, T("secureNeutralNode"));
            else
                ImGui.TextDisabled(T("notObserved"));
            ImGui.TableNextColumn();
            if (standing.Length == 0)
                ImGui.TextDisabled(T("notObserved"));
            else
                DrawCopyableText(string.Join(" · ", standing.Select(actor =>
                    $"{actor.Name}@{WorldName(actor.WorldId)}")), TextColor);
        }
        ImGui.EndTable();
    }

    private void DrawSecureTowerEvents(DetailedAnalysis detail, long startMs, long endMs)
    {
        // The global marker proves the round, while action 42940 (Explosion)
        // directly identifies a successful receiver and 42941 (Unmitigated
        // Explosion) identifies a missed-tower blast. In the real duty-127
        // archive, BaseId 9020 is named "Interceptor Node" at the centre and
        // must not be treated as six tower actors.
        var events = (detail.Actions ?? [])
            .Where(row => (row.ActionId is SecureObjectiveCatalog.TowerExplosionActionId or
                               SecureObjectiveCatalog.TowerUnmitigatedExplosionActionId) &&
                          row.ElapsedMs > startMs && row.ElapsedMs <= endMs)
            .GroupBy(row => (row.SourceEntityId, Bucket: row.ElapsedMs / 250))
            .OrderBy(group => group.Min(row => row.ElapsedMs)).ToArray();
        ImGui.Spacing();
        ImGui.TextColored(Cyan, T("secureTowerEvents"));
        ImGui.TextWrapped(T("secureTowerEvidence"));
        if (events.Length == 0)
        {
            // The global map marker proves only that the command-system round
            // was announced/active. It does not identify the six local circles,
            // their occupants, or the faction award. Never turn a countdown
            // into invented participants.
            var markerObserved = (detail.ObjectiveTimeline ?? []).Any(row =>
                row.ElapsedMs > startMs && row.ElapsedMs <= endMs &&
                SecureObjectiveCatalog.TryGetMapIcon(row.State, out var iconId) &&
                iconId is 63_979 or 63_980 or 63_987);
            ImGui.TextDisabled(markerObserved
                ? $"{T("secureCommandSystem")} · {T("secureTowerUnresolved")}"
                : T("notObserved"));
            return;
        }
        if (!ImGui.BeginTable("secure-tower-events", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn(T("time"), ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(T("mechanismObject"), ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn(T("result"), ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn(T("secureTowerParticipants"), ImGuiTableColumnFlags.WidthStretch, 1.7f);
        ImGui.TableSetupColumn(T("secureTowerAffected"), ImGuiTableColumnFlags.WidthStretch, 1.7f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var group in events)
        {
            var elapsed = group.Min(row => row.ElapsedMs);
            var participants = group
                .Where(row => row.ActionId == SecureObjectiveCatalog.TowerExplosionActionId)
                .GroupBy(row => row.TargetEntityId)
                .Select(rows => rows.First()).Where(row => !string.IsNullOrWhiteSpace(row.TargetName))
                .OrderBy(row => row.TargetTeam).ThenBy(row => row.TargetName).ToArray();
            var affected = group
                .Where(row => row.ActionId == SecureObjectiveCatalog.TowerUnmitigatedExplosionActionId)
                .GroupBy(row => row.TargetEntityId)
                .Select(rows => rows.First()).Where(row => !string.IsNullOrWhiteSpace(row.TargetName))
                .OrderBy(row => row.TargetTeam).ThenBy(row => row.TargetName).ToArray();
            var classification = affected.Length > 0
                ? T("secureTowerMissCandidate")
                : participants.Length > 0 ? T("secureTowerSoaked") : T("secureTowerUnresolved");

            ImGui.TableNextRow();
            Cell(Clock(elapsed));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(T("secureCommandSystem"));
            ImGui.TableNextColumn();
            ImGui.TextColored(affected.Length > 0 ? new Vector4(.95f, .63f, .25f, 1f) :
                participants.Length > 0 ? new Vector4(.35f, .86f, .52f, 1f) : Muted,
                classification);
            ImGui.TableNextColumn();
            if (participants.Length == 0) ImGui.TextDisabled(T("notObserved"));
            else DrawCopyableText(string.Join(" · ", participants.Select(row =>
                $"{row.TargetName}@{WorldName(row.TargetWorldId)}")), TextColor);
            ImGui.TableNextColumn();
            if (affected.Length == 0) ImGui.TextDisabled(T("notObserved"));
            else DrawCopyableText(string.Join(" · ", affected.Select(row =>
                $"{row.TargetName}@{WorldName(row.TargetWorldId)} ({row.Damage:N0})")), TextColor);
        }
        ImGui.EndTable();
    }

    private string ObjectiveInstanceDisplayName(uint dutyId, DetailedAnalysis detail,
        uint objectId, string fallback, long endMs)
    {
        if (currentMatchIsCrystalline)
        {
            var observedCc = (detail.ObjectiveDiscovery ?? [])
                .Where(row => row.ObjectId == objectId && row.ElapsedMs <= endMs)
                .OrderByDescending(row => row.ElapsedMs).FirstOrDefault();
            if (observedCc != null && CrystallineMechanismCatalog.TryResolve(currentTerritoryId,
                    observedCc.BaseId, observedCc.Name, out var mechanism))
                return ObjectiveDisplayName(dutyId, mechanism.Label);
        }
        if (dutyId != 127) return ObjectiveDisplayName(dutyId, fallback);
        var fixedNode = SecureObjectiveCatalog.Nodes.FirstOrDefault(node => node.Id == objectId);
        if (fixedNode.Id != 0) return SecureNodeDisplayName(fixedNode);
        var mapState = (detail.ObjectiveTimeline ?? [])
            .Where(row => row.ObjectId == objectId && row.ElapsedMs <= endMs)
            .OrderByDescending(row => row.ElapsedMs).FirstOrDefault();
        if (mapState != null && SecureObjectiveCatalog.TryGetMapIcon(mapState.State, out var mapIcon))
        {
            if (SecureObjectiveCatalog.IsOccupationIcon(mapIcon) &&
                SecureObjectiveCatalog.TryResolveNode(mapState.Label, mapState.X, mapState.Z, out var mappedNode))
                return SecureNodeDisplayName(mappedNode);
            if (SecureObjectiveCatalog.IsDroneMapIcon(mapIcon))
                return T("secureInterceptorDrone");
            if (SecureObjectiveCatalog.IsCommandSystemIcon(mapIcon))
                return T("secureCommandSystem");
            if (mapIcon is 63_982 or 63_983 or 63_984)
                return T("secureDisplacementPanel");
        }
        var observed = (detail.ObjectiveDiscovery ?? [])
            .Where(row => row.ObjectId == objectId && row.ElapsedMs <= endMs)
            .OrderByDescending(row => row.ElapsedMs).FirstOrDefault();
        if (observed == null) return ObjectiveDisplayName(dutyId, fallback);
        if (SecureObjectiveCatalog.TryResolveNode(observed.Name, observed.X, observed.Z, out var observedNode))
            return SecureNodeDisplayName(observedNode);
        if (SecureObjectiveCatalog.IsDrone(observed.BaseId, observed.Name))
        {
            var slot = SecureObjectiveCatalog.TowerSlot(observed.X, observed.Z);
            var generations = (detail.ObjectiveDiscovery ?? [])
                .Where(row => SecureObjectiveCatalog.IsDrone(row.BaseId, row.Name))
                .GroupBy(row => row.ObjectId)
                .Select(group => group.OrderBy(row => row.ElapsedMs).First())
                .Where(row => SecureObjectiveCatalog.TowerSlot(row.X, row.Z) == slot)
                .OrderBy(row => row.ElapsedMs).ThenBy(row => row.ObjectId).ToArray();
            var generation = Array.FindIndex(generations, row => row.ObjectId == observed.ObjectId) + 1;
            var suffix = generation > 0 ? $"D-{slot}-{generation:00}" : $"D-{slot}";
            return $"{T(SecureObjectiveCatalog.IsGoldDrone(observed.BaseId, observed.Name)
                ? "secureGoldInterceptorDrone" : "secureInterceptorDrone")} · {suffix}";
        }
        if (observed.BaseId == SecureObjectiveCatalog.TowerCircleBaseId &&
            (observed.Name ?? string.Empty).Contains("Command System", StringComparison.OrdinalIgnoreCase))
            return $"{T("secureCommandSystem")} · {SecureObjectiveCatalog.TowerCode(observed.X, observed.Z)}";
        return ObjectiveDisplayName(dutyId, fallback);
    }

    /// <summary>
    /// Converts one fixed Secure node into its compact human-facing label.
    /// Every node outputs the same unique two-character code used by the lower
    /// mechanism table. Ownership is a separate, time-varying value and is
    /// intentionally not embedded in this stable geometry label.
    /// </summary>
    private static string SecureNodeDisplayName(SecureObjectiveCatalog.Node node) => node.Code;

    private void DrawMechanismAnalysisLegacy(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        var timeline = detail.ObjectiveTimeline ?? [];
        var discovery = detail.ObjectiveDiscovery ?? [];
        var objectiveIds = timeline.Select(row => row.ObjectId)
            .Concat(discovery.Select(row => row.ObjectId)).Where(id => id != 0).ToHashSet();
        var objectiveActions = (detail.Actions ?? [])
            .Where(row => objectiveIds.Contains(row.TargetEntityId) && row.Damage > 0).ToArray();
        var objectiveStatuses = (detail.StatusTimeline ?? [])
            .Where(row => objectiveIds.Contains(row.TargetEntityId)).ToArray();

        ImGui.TextColored(Cyan, T("mechanismAnalysis"));
        ImGui.TextWrapped(T("mechanismEvidenceBoundary"));
        ImGui.Separator();
        DrawHeroMetric(T("objectivesObserved"), objectiveIds.Count.ToString("N0"), Cyan);
        ImGui.SameLine(); DrawHeroMetric(T("mechanismActions"), objectiveActions.Length.ToString("N0"),
            new Vector4(.95f, .72f, .28f, 1));
        ImGui.SameLine(); DrawHeroMetric(T("objectiveStatusChanges"), objectiveStatuses.Length.ToString("N0"),
            new Vector4(.55f, .78f, 1f, 1));

        ImGui.Spacing();
        ImGui.TextColored(Cyan, T("objectiveTimeline"));
        if (timeline.Count == 0) ImGui.TextDisabled(T("noObjectiveEvidence"));
        else if (ImGui.BeginTable("mechanism-events", 7,
                     ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
                     ImGuiTableFlags.Resizable,
                     new Vector2(0, Math.Min(290, 32 + timeline.Count * 25))))
        {
            foreach (var key in new[] { "time", "objective", "state", "team", "position", "status", "observations" })
                ImGui.TableSetupColumn(T(key));
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var row in timeline.OrderBy(value => value.ElapsedMs))
            {
                ImGui.TableNextRow(); Cell(Clock(row.ElapsedMs)); Cell(row.Label);
                Cell(row.State); Cell(row.OwnerTeam is byte team && team < 3 ? TeamName(team) : T("unknown"));
                Cell($"{row.X:0.0}, {row.Z:0.0}");
                var statusCount = objectiveStatuses.Count(value => value.TargetEntityId == row.ObjectId &&
                    Math.Abs(value.ElapsedMs - row.ElapsedMs) <= 10_000);
                Cell(statusCount.ToString("N0")); Cell(row.EvidenceScope);
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextColored(Cyan, T("mechanismParticipation"));
        if (objectiveActions.Length == 0) ImGui.TextDisabled(T("noMechanismActionEvidence"));
        else if (ImGui.BeginTable("mechanism-participants", 9,
                     ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, 340)))
        {
            foreach (var key in new[] { "player", "job", "team", "objective", "damage", "actions",
                         "objectiveBuffs", "firstSeen", "lastSeen" })
                ImGui.TableSetupColumn(T(key));
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            var groups = objectiveActions.GroupBy(row => new
                { row.SourceName, row.SourceWorldId, row.SourceJobId, row.SourceTeam, row.TargetEntityId, row.TargetName })
                .Select(group => new { group.Key, Damage = group.Sum(row => row.Damage), Count = group.Count(),
                    First = group.Min(row => row.ElapsedMs), Last = group.Max(row => row.ElapsedMs) })
                .OrderByDescending(row => row.Damage).ToArray();
            foreach (var row in groups)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); DrawCopyableText($"{row.Key.SourceName}@{WorldName(row.Key.SourceWorldId)}", TextColor);
                ImGui.TableNextColumn(); DrawJobIcon(row.Key.SourceJobId);
                ImGui.TableNextColumn(); ImGui.TextColored(row.Key.SourceTeam < 3 ? TeamColors[row.Key.SourceTeam] : Muted,
                    row.Key.SourceTeam < 3 ? TeamName(row.Key.SourceTeam) : T("unknown"));
                Cell(row.Key.TargetName); Cell(row.Damage.ToString("N0")); Cell(row.Count.ToString("N0"));
                ImGui.TableNextColumn();
                var buffIds = objectiveStatuses.Where(value => value.TargetEntityId == row.Key.TargetEntityId &&
                        value.Change != "remove" && value.ElapsedMs >= row.First - 5_000 &&
                        value.ElapsedMs <= row.Last + 5_000)
                    .Select(value => value.StatusId).Distinct().Take(8).ToArray();
                if (buffIds.Length == 0) ImGui.TextDisabled("—");
                for (var index = 0; index < buffIds.Length; index++)
                {
                    if (index > 0) ImGui.SameLine(0, 4);
                    DrawStatusGlyph(buffIds[index], 20);
                }
                Cell(Clock(row.First)); Cell(Clock(row.Last));
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextColored(Cyan, T("mechanismActionDetails"));
        if (objectiveActions.Length == 0) ImGui.TextDisabled(T("noMechanismActionEvidence"));
        else if (ImGui.BeginTable("mechanism-action-events", 8,
                     ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, 320)))
        {
            foreach (var key in new[] { "time", "player", "job", "team", "action", "objective", "damage", "hp" })
                ImGui.TableSetupColumn(T(key));
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var row in objectiveActions.OrderBy(value => value.ElapsedMs).TakeLast(800))
            {
                ImGui.TableNextRow();
                Cell(Clock(row.ElapsedMs));
                ImGui.TableNextColumn();
                DrawCopyableText($"{row.SourceName}@{WorldName(row.SourceWorldId)}", TextColor);
                ImGui.TableNextColumn(); DrawJobIcon(row.SourceJobId);
                ImGui.TableNextColumn();
                ImGui.TextColored(row.SourceTeam < 3 ? TeamColors[row.SourceTeam] : Muted,
                    row.SourceTeam < 3 ? TeamName(row.SourceTeam) : T("unknown"));
                ImGui.TableNextColumn(); DrawActionGlyph(row.ActionId, 20); ImGui.SameLine(0, 5);
                ImGui.TextUnformatted(ActionName(row.ActionId, row.ActionName));
                Cell(row.TargetName); Cell(row.Damage.ToString("N0"));
                ImGui.TableNextColumn();
                DrawHpShieldBar(row.TargetCurrentHp, row.TargetMaxHp, 0, new Vector2(136, 16));
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextColored(Cyan, T("mechanismStatusDetails"));
        if (objectiveStatuses.Length == 0) ImGui.TextDisabled(T("noMechanismStatusEvidence"));
        else if (ImGui.BeginTable("mechanism-status-events", 6,
                     ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, 250)))
        {
            foreach (var key in new[] { "time", "status", "change", "source", "objective", "duration" })
                ImGui.TableSetupColumn(T(key));
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            var actorLookup = detail.MovementTimeline.SelectMany(point => point.Actors ?? [])
                .GroupBy(actor => actor.EntityId).ToDictionary(group => group.Key, group => group.Last());
            var objectiveLookup = timeline.GroupBy(row => row.ObjectId)
                .ToDictionary(group => group.Key, group => group.Last().Label);
            foreach (var row in objectiveStatuses.OrderBy(value => value.ElapsedMs).TakeLast(500))
            {
                ImGui.TableNextRow(); Cell(Clock(row.ElapsedMs)); Cell(StatusName(row.StatusId)); Cell(row.Change);
                var source = actorLookup.GetValueOrDefault(row.SourceEntityId);
                ImGui.TableNextColumn();
                if (source == null) ImGui.TextDisabled(T("unknown"));
                else DrawCopyableText($"{source.Name}@{WorldName(source.WorldId)}", TextColor);
                Cell(objectiveLookup.GetValueOrDefault(row.TargetEntityId, $"#{row.TargetEntityId:X8}"));
                Cell($"{row.DurationSeconds:0.0} s");
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextColored(Cyan, T("captureInteractionDetails"));
        var interactions = timeline.Where(row => row.ActorEntityId != 0 ||
            !string.IsNullOrWhiteSpace(row.ActorName) || row.InteractionDurationMs.HasValue ||
            !string.IsNullOrWhiteSpace(row.InteractionResult) || row.InterrupterEntityId != 0 ||
            !string.IsNullOrWhiteSpace(row.InterrupterName)).OrderBy(row => row.ElapsedMs).ToArray();
        if (interactions.Length == 0)
        {
            ImGui.TextDisabled(T("captureInteractionNotObserved"));
        }
        else if (ImGui.BeginTable("mechanism-capture-interactions", 7,
                     ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
                     ImGuiTableFlags.Resizable, new Vector2(0, Math.Min(280, 34 + interactions.Length * 27))))
        {
            foreach (var key in new[] { "time", "objective", "captureActor", "duration", "result",
                         "interrupter", "observations" }) ImGui.TableSetupColumn(T(key));
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var row in interactions)
            {
                ImGui.TableNextRow(); Cell(Clock(row.ElapsedMs)); Cell(row.Label);
                ImGui.TableNextColumn();
                if (string.IsNullOrWhiteSpace(row.ActorName)) ImGui.TextDisabled(T("notObserved"));
                else
                {
                    DrawJobIcon(row.ActorJobId); ImGui.SameLine(0, 5);
                    DrawCopyableText($"{row.ActorName}@{WorldName(row.ActorWorldId)}", TextColor);
                }
                Cell(row.InteractionDurationMs is long duration ? $"{duration / 1000d:0.00} s" : T("notObserved"));
                Cell(string.IsNullOrWhiteSpace(row.InteractionResult) ? T("notObserved") : row.InteractionResult);
                ImGui.TableNextColumn();
                if (string.IsNullOrWhiteSpace(row.InterrupterName)) ImGui.TextDisabled(T("notObserved"));
                else
                {
                    DrawJobIcon(row.InterrupterJobId); ImGui.SameLine(0, 5);
                    DrawCopyableText($"{row.InterrupterName}@{WorldName(row.InterrupterWorldId)}", TextColor);
                }
                Cell(row.EvidenceScope);
            }
            ImGui.EndTable();
        }

        if (match.DutyId == 180)
        {
            ImGui.Spacing(); ImGui.TextColored(Cyan, T("shatterRespawnIntervals"));
            foreach (var group in timeline.GroupBy(row => row.Label).OrderBy(group => group.Key))
            {
                var ordered = group.OrderBy(row => row.ElapsedMs).ToArray();
                for (var index = 0; index < ordered.Length; index++)
                {
                    if (!(ordered[index].State ?? string.Empty).Contains("destroy", StringComparison.OrdinalIgnoreCase)) continue;
                    var next = ordered.Skip(index + 1).FirstOrDefault(row =>
                        string.Equals(row.State, "active", StringComparison.OrdinalIgnoreCase));
                    ImGui.TextUnformatted(next == null
                        ? $"{ordered[index].Label}  {Clock(ordered[index].ElapsedMs)}  →  {T("noLaterSpawnObserved")}"
                        : $"{ordered[index].Label}  {Clock(ordered[index].ElapsedMs)}  →  {Clock(next.ElapsedMs)}  " +
                          $"({TimeSpan.FromMilliseconds(next.ElapsedMs - ordered[index].ElapsedMs):mm\\:ss})");
                }
            }
        }
    }

    private void DrawHero(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BattlefieldUiTheme.Panel(configuration.UiTheme));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(Cyan.X, Cyan.Y, Cyan.Z, .38f));
        ImGui.BeginChild("detail-hero", new Vector2(0, 105), true, ImGuiWindowFlags.NoScrollbar);
        ImGui.TextColored(Cyan, T("matchReplay") + " · " + match.Realm);
        ImGui.SetWindowFontScale(1.28f);
        ImGui.TextUnformatted($"{T("match")} {match.MatchUid[..Math.Min(12, match.MatchUid.Length)]}");
        ImGui.SetWindowFontScale(1f);
        ImGui.TextDisabled($"{match.EndedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {match.MatchLengthSeconds / 60}:{match.MatchLengthSeconds % 60:00}  ·  {match.Players.Count} {T("players")}");
        ImGui.SameLine(ImGui.GetWindowWidth() - 470);
        DrawHeroMetric(T("scoreChanges"), detail.Coverage.ScoreChanges.ToString(), Cyan);
        ImGui.SameLine(); DrawHeroMetric(T("vitalsFrames"), detail.Coverage.VitalsFrames.ToString(), new Vector4(.4f, .72f, 1f, 1));
        ImGui.SameLine(); DrawHeroMetric("Logs", match.MatchLogs?.ToString("0.0") ?? "—",
            match.MatchLogs is double value ? LogsColor(value) : Muted);
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private static void DrawHeroMetric(string label, string value, Vector4 color)
    {
        ImGui.BeginGroup();
        ImGui.TextDisabled(label);
        ImGui.SetWindowFontScale(1.15f); ImGui.TextColored(color, value); ImGui.SetWindowFontScale(1f);
        ImGui.EndGroup();
    }

    private void DrawTimeline(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        var maxMs = Math.Max(1, Math.Max(detail.ScoreTimeline.LastOrDefault()?.ElapsedMs ?? 0,
            detail.MovementTimeline.LastOrDefault()?.ElapsedMs ?? 0));
        selectedMs = Math.Clamp(selectedMs, 0, maxMs);
        ImGui.TextColored(Cyan, T("adaptiveTimeline"));
        ImGui.SameLine(); ImGui.TextDisabled(Clock((long)selectedMs));
        ImGui.SetNextItemWidth(-1);
        ImGui.SliderFloat("##detail-time", ref selectedMs, 0, maxMs, string.Empty,
            ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoInput);
        var score = NearestScore(detail, (long)selectedMs);
        var movement = NearestMovement(detail, (long)selectedMs);
        if (match.Schema == LocalMatchAnalyzer.CrystallineSchema)
        {
            DrawCrystallineTeamIdentity(match, detail, score, movement);
            return;
        }
        if (score != null)
        {
            var localTeam = ResolveLocalTeam(match);
            DrawScoreChip(match, 0, score.Maelstrom, localTeam == 0);
            ImGui.SameLine();
            DrawScoreChip(match, 1, score.Adders, localTeam == 1);
            ImGui.SameLine();
            DrawScoreChip(match, 2, score.Flames, localTeam == 2);
        }
        if (movement != null)
        {
            ImGui.SameLine(520);
            ImGui.TextDisabled($"{T("visible")} {movement.VisibleActors} · {T("frameTime")} {Clock(movement.ElapsedMs)}");
        }
    }

    private void DrawCrystallineTeamIdentity(LocalMatchAnalysis match, DetailedAnalysis detail,
        ScorePoint? score, MovementPoint? movement)
    {
        var localTeam = CrystallineTeamIdentity.Resolve(match, detail);
        if (!ImGui.BeginTable("cc-team-identity", 3,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings)) return;
        ImGui.TableSetupColumn("cc-astra", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableSetupColumn("cc-umbra", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableSetupColumn("cc-observation", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        DrawCrystallineTeamIdentityCell(match, 0, score?.Maelstrom, localTeam == 0);
        DrawCrystallineTeamIdentityCell(match, 1, score?.Adders, localTeam == 1);
        ImGui.TableNextColumn();
        if (movement != null)
            ImGui.TextDisabled($"{T("visible")} {movement.VisibleActors} · {T("frameTime")} {Clock(movement.ElapsedMs)}");
        ImGui.EndTable();
    }

    private void DrawCrystallineTeamIdentityCell(LocalMatchAnalysis match, byte team,
        short? score, bool isLocalTeam)
    {
        ImGui.TableNextColumn();
        var color = MapTeamColor(match, team);
        ImGui.SetWindowFontScale(1.05f);
        ImGui.TextColored(color, $"● {MapTeamName(match, team)}");
        ImGui.SetWindowFontScale(1f);
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        var nameHovered = ImGui.IsItemHovered();
        if (score.HasValue)
        {
            ImGui.SameLine(0, 8);
            ImGui.TextDisabled($"{score.Value}%");
        }
        if (!isLocalTeam) return;
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(minimum.X, maximum.Y + 2),
            new Vector2(maximum.X, maximum.Y + 7),
            ImGui.ColorConvertFloat4ToU32(color with { W = .98f }), 1.5f);
        if (nameHovered) ImGui.SetTooltip(T("localPlayer"));
    }

    private void DrawScoreChip(LocalMatchAnalysis match, byte team, short score, bool isLocalTeam)
    {
        var teamColor = MapTeamColor(match, team);
        var scoreText = match.Schema == LocalMatchAnalyzer.CrystallineSchema ? $"{score}%" : score.ToString();
        ImGui.TextColored(teamColor, $"● {MapTeamName(match, team)} {scoreText}");
        if (!isLocalTeam) return;
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        var color = ImGui.ColorConvertFloat4ToU32(teamColor with { W = .98f });
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(minimum.X, maximum.Y + 2),
            new Vector2(maximum.X, maximum.Y + 7), color, 2f);
    }

    private static byte? ResolveLocalTeam(LocalMatchAnalysis match)
    {
        if (string.IsNullOrWhiteSpace(match.LocalPlayerName)) return null;
        var localName = match.LocalPlayerName.Split('@', 2)[0];
        var candidates = match.Players.Where(x => x.Name == localName && x.Team < 3)
            .Select(x => x.Team).Distinct().ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private void DrawOverview(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        if (!ImGui.BeginTable("overview-columns", 2,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.Resizable)) return;
        ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthFixed, 760);
        ImGui.TableSetupColumn("right", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow(); ImGui.TableNextColumn();
        // FL and CC share the same stable-slice contract: variable action rows
        // must not move the full-width combat flow underneath the two columns.
        ImGui.BeginChild("fl-map-stable", new Vector2(0, 790), false,
            ImGuiWindowFlags.NoScrollbar);
        DrawPanelTitle(T("battlefieldFlow"), T("movementEvidence"));
        DrawMovementMap(match, detail, new Vector2(0, 470));
        ImGui.Spacing();
        DrawPanelTitle(T("scoreTimeline"), T("scoreEvidence"));
        DrawScoreTimeline(detail, new Vector2(0, 205));
        ImGui.EndChild();
        ImGui.TableNextColumn();
        ImGui.BeginChild("fl-slice-stable", new Vector2(0, 790), false,
            ImGuiWindowFlags.AlwaysVerticalScrollbar);
        DrawPanelTitle(T("currentSlice"), T("sliceEvidence"));
        DrawCombatSlice(match, detail);
        ImGui.EndChild();
        ImGui.EndTable();
        ImGui.Spacing();
        DrawPanelTitle(T("observedCombatFlow"), T("hpFlowEvidence"));
        DrawHealthFlow(match, detail, new Vector2(0, 250));
        ImGui.Spacing();
        DrawMechanismAnalysis(match, detail);
        DrawEvidenceBoundary(detail);
    }

    private void DrawCrystallineProgressTimeline(LocalMatchAnalysis match, DetailedAnalysis detail,
        Vector2 requested)
    {
        var rows = (detail.ObjectiveTimeline ?? [])
            .Where(IsCrystallineProgressGauge).OrderBy(row => row.ElapsedMs).ToArray();
        var size = new Vector2(Math.Max(360, requested.X <= 0 ? ImGui.GetContentRegionAvail().X : requested.X),
            requested.Y);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("cc-progress-timeline", size);
        var draw = ImGui.GetWindowDrawList();
        var background = BattlefieldUiTheme.ChartBackground(configuration.UiTheme);
        var border = BattlefieldUiTheme.Border(configuration.UiTheme);
        var grid = BattlefieldUiTheme.Grid(configuration.UiTheme);
        draw.AddRectFilled(origin, origin + size, ImGui.ColorConvertFloat4ToU32(background), 2);
        draw.AddRect(origin, origin + size, ImGui.ColorConvertFloat4ToU32(border), 2);
        if (rows.Length == 0)
        {
            DrawCentered(draw, origin, size, T("ccNoProgressTimeline"), Muted);
            ImGui.Dummy(Vector2.Zero);
            return;
        }

        const float left = 48, right = 14, top = 38, bottom = 28;
        var plotMin = origin + new Vector2(left, top);
        var plotMax = origin + size - new Vector2(right, bottom);
        var maxMs = Math.Max(30_000L, Math.Max((long)match.MatchLengthSeconds * 1_000L,
            rows.Max(row => row.ElapsedMs)));
        Vector2 Plot(long elapsed, double signedPercent) => new(
            plotMin.X + (float)Math.Clamp(elapsed / (double)maxMs, 0, 1) * (plotMax.X - plotMin.X),
            plotMin.Y + (float)(1 - (Math.Clamp(signedPercent, -100, 100) + 100) / 200d) *
            (plotMax.Y - plotMin.Y));

        // The checkpoint series used to be a bare yellow line, making it easy
        // to mistake for the crystal trail or selection cursor. Keep a visible
        // in-chart legend next to the same stroke style and colour.
        var checkpointGold = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .76f, .20f, 1f));
        var legendAt = origin + new Vector2(12, 13);
        draw.AddLine(legendAt, legendAt + new Vector2(24, 0), checkpointGold, 2f);
        draw.AddText(legendAt + new Vector2(31, -7), checkpointGold, T("ccCheckpointLegend"));

        // The current official rule has a 30-second preparation countdown.
        // This region is presentation-only; the captured crystal-unbind gauge
        // remains separate because it is not the same clock.
        var prepEnd = Plot(Math.Min(30_000, maxMs), 0).X;
        draw.AddRectFilled(plotMin, new Vector2(prepEnd, plotMax.Y),
            ImGui.ColorConvertFloat4ToU32(new Vector4(.48f, .50f, .53f, .18f)));
        draw.AddText(plotMin + new Vector2(5, 4), ImGui.ColorConvertFloat4ToU32(Muted),
            T("ccPreparationPeriod"));
        for (var value = -100; value <= 100; value += 25)
        {
            var y = Plot(0, value).Y;
            draw.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y),
                ImGui.ColorConvertFloat4ToU32(grid), value == 0 ? 1.8f : 1f);
            var label = value == 0 ? "0" : $"{Math.Abs(value)}%";
            draw.AddText(new Vector2(plotMin.X - ImGui.CalcTextSize(label).X - 5, y - 7),
                ImGui.ColorConvertFloat4ToU32(value == 0 ? TextColor : Muted), label);
        }
        var timeStep = maxMs <= 300_000 ? 60_000L : 120_000L;
        for (var elapsed = 0L; elapsed <= maxMs; elapsed += timeStep)
        {
            var x = Plot(elapsed, 0).X;
            draw.AddLine(new Vector2(x, plotMin.Y), new Vector2(x, plotMax.Y),
                ImGui.ColorConvertFloat4ToU32(grid));
            draw.AddText(new Vector2(x + 3, plotMax.Y + 5), ImGui.ColorConvertFloat4ToU32(Muted),
                Clock(elapsed));
        }

        var localTeam = match.Players.FirstOrDefault(player =>
            string.Equals(player.Name, match.LocalPlayerName, StringComparison.OrdinalIgnoreCase))?.Team ?? (byte)0;
        var ownAstra = localTeam == 0;
        DrawCrystalPosition(ownAstra ? -1d : 1d);
        // TeamProgress is the furthest progress retained by the HUD. Keep it as
        // a thin context line; CrystalPosition above is the actual cart path
        // that can retreat through zero as possession changes.
        DrawGauge("cc_astra_progress", ownAstra ? 1d : -1d,
            MapTeamColor(match, 0) with { W = .48f }, true);
        DrawGauge("cc_umbra_progress", ownAstra ? -1d : 1d,
            MapTeamColor(match, 1) with { W = .48f }, true);
        DrawGauge("cc_astra_checkpoint", ownAstra ? 1d : -1d,
            new Vector4(1f, .76f, .20f, 1f), true);
        DrawGauge("cc_umbra_checkpoint", ownAstra ? -1d : 1d,
            new Vector4(1f, .76f, .20f, 1f), true);

        var selectedX = Plot((long)selectedMs, 0).X;
        draw.AddLine(new Vector2(selectedX, plotMin.Y), new Vector2(selectedX, plotMax.Y),
            ImGui.ColorConvertFloat4ToU32(Cyan), 2f);
        draw.AddText(plotMin + new Vector2(8, plotMax.Y - plotMin.Y - 18),
            ImGui.ColorConvertFloat4ToU32(MapTeamColor(match, localTeam)), T("ccOwnAttackPositive"));
        var enemyTeam = localTeam == 0 ? (byte)1 : (byte)0;
        var enemy = T("ccEnemyAttackNegative");
        draw.AddText(new Vector2(plotMax.X - ImGui.CalcTextSize(enemy).X - 8, plotMin.Y + 4),
            ImGui.ColorConvertFloat4ToU32(MapTeamColor(match, enemyTeam)), enemy);
        ImGui.Dummy(Vector2.Zero);

        void DrawGauge(string label, double sign, Vector4 color, bool checkpoint)
        {
            var series = rows.Where(row => row.Label == label && row.ProgressPercent.HasValue).ToArray();
            if (series.Length == 0) return;
            var packed = ImGui.ColorConvertFloat4ToU32(color);
            for (var i = 1; i < series.Length; i++)
            {
                var from = Plot(series[i - 1].ElapsedMs, sign * series[i - 1].ProgressPercent!.Value);
                var to = Plot(series[i].ElapsedMs, sign * series[i].ProgressPercent!.Value);
                if (!checkpoint || i % 2 == 0) draw.AddLine(from, to, packed, checkpoint ? 2f : 3f);
            }
        }

        void DrawCrystalPosition(double sign)
        {
            var series = rows.Where(row => row.Label == "cc_crystal_position" &&
                row.ProgressPercent.HasValue).ToArray();
            for (var i = 1; i < series.Length; i++)
            {
                var fromValue = sign * series[i - 1].ProgressPercent!.Value;
                var toValue = sign * series[i].ProgressPercent!.Value;
                var color = (fromValue + toValue) / 2d >= 0
                    ? MapTeamColor(match, localTeam)
                    : MapTeamColor(match, localTeam == 0 ? (byte)1 : (byte)0);
                draw.AddLine(Plot(series[i - 1].ElapsedMs, fromValue),
                    Plot(series[i].ElapsedMs, toValue), ImGui.ColorConvertFloat4ToU32(color), 4f);
            }
        }
    }

    private static bool IsCrystallineProgressGauge(ObjectiveStateEvent row) =>
        row.EvidenceScope == "cc_content_director_progress_v1" ||
        row.Label is "cc_crystal_position" or "cc_astra_progress" or "cc_umbra_progress" or
            "cc_astra_checkpoint" or "cc_umbra_checkpoint" or "cc_preparation";

    private void DrawPanelTitle(string title, string subtitle)
    {
        ImGui.TextColored(Cyan, title);
        ImGui.TextWrapped(subtitle);
    }

    private void DrawMovementMap(LocalMatchAnalysis match, DetailedAnalysis detail, Vector2 requested,
        AnomalyCandidate? anomaly = null)
    {
        var size = new Vector2(Math.Max(300, requested.X <= 0 ? ImGui.GetContentRegionAvail().X : requested.X), requested.Y);
        var origin = ImGui.GetCursorScreenPos();
        var viewport = MarkerUiPrimitives.BeginMapCanvas("movement-map", size,
            ref mapZoom, ref mapCenterUv, 1.75f, 30f);
        var afterCanvas = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size,
            ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.ChartBackground(configuration.UiTheme)), 2);
        draw.AddRect(origin, origin + size,
            ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Border(configuration.UiTheme)), 2);

        var actors = detail.MovementTimeline.SelectMany(x => x.Actors ?? []).ToArray();
        var all = detail.MovementTimeline.SelectMany(x => x.Factions).ToArray();
        var hasObjectiveEvidence = (detail.ObjectiveTimeline?.Count ?? 0) > 0 ||
                                   (detail.ObjectiveDiscovery?.Count ?? 0) > 0;
        if (actors.Length == 0 && all.Length == 0 && !hasObjectiveEvidence)
        {
            DrawCentered(draw, origin, size, T("noMovement"), Muted);
            DrawMapZoomSlider(origin, size, afterCanvas);
            return;
        }
        var spanX = viewport.SpanX; var spanY = viewport.SpanY;
        var uvMin = viewport.UvMin; var uvMax = viewport.UvMax;
        var mapFactor = 100;
        var mapOffsetX = 0;
        var mapOffsetY = 0;
        var mapBackgroundLoaded = false;
        if (MapAssets.TryGetValue(match.DutyId, out var mapAsset))
        {
            try
            {
                if (!mapTextures.TryGetValue(match.DutyId, out var texture))
                    mapTextures[match.DutyId] = texture = textureProvider.GetFromFile(
                        Path.Combine(assetRoot, "assets", "maps", mapAsset.FileName));
                var wrap = texture.GetWrapOrDefault();
                if (wrap != null)
                {
                    draw.AddImage(wrap.Handle, origin, origin + size, uvMin, uvMax,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, .9f)));
                    mapBackgroundLoaded = true;
                }
            }
            catch { /* Fall back to the coordinate grid when an optional map asset cannot load. */ }
        }
        else if (match.Schema == LocalMatchAnalyzer.CrystallineSchema)
        {
            try
            {
                var territory = dataManager.GetExcelSheet<TerritoryTypeSheet>().GetRow(match.TerritoryId);
                var map = territory.Map.Value;
                mapFactor = Math.Max(1, (int)map.SizeFactor);
                mapOffsetX = map.OffsetX;
                mapOffsetY = map.OffsetY;
                var id = map.Id.ToString();
                var texturePath = $"ui/map/{id}/{id.Replace("/", string.Empty)}_m.tex";
                if (!mapTextures.TryGetValue(match.DutyId, out var texture))
                    mapTextures[match.DutyId] = texture = textureProvider.GetFromGame(texturePath);
                var wrap = texture.GetWrapOrDefault();
                if (wrap != null)
                {
                    draw.AddImage(wrap.Handle, origin, origin + size, uvMin, uvMax,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, .9f)));
                    mapBackgroundLoaded = true;
                }
            }
            catch { /* Keep the coordinate-grid replay when the game map texture is unavailable. */ }
        }
        // FFXIV map-space conversion: the map sheet supplies its scale and world offsets.
        Vector2 Point(double x, double z)
        {
            var scale = mapFactor / 100f;
            var uv = new Vector2(((float)x + mapOffsetX) * scale / 2048f + .5f,
                ((float)z + mapOffsetY) * scale / 2048f + .5f);
            return origin + new Vector2((uv.X - uvMin.X) / spanX * size.X,
                (uv.Y - uvMin.Y) / spanY * size.Y);
        }
        draw.PushClipRect(origin, origin + size, true);
        for (var i = 1; i < 8; i++)
        {
            var x = origin.X + i * size.X / 8; var y = origin.Y + i * size.Y / 8;
            var grid = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Grid(configuration.UiTheme));
            draw.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y), grid);
            draw.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + size.X, y), grid);
        }
        var now = (long)selectedMs;
        DrawCrystallineCrystalTrail(draw, detail, now, Point);
        if (match.Schema == LocalMatchAnalyzer.CrystallineSchema)
        {
            DrawObservedCrystallineSpeedZones(draw, match, detail, now, Point);
            DrawObservedCloudNineJumpRoutes(draw, match, detail, now, Point);
            DrawCrystallineMechanismTelegraphs(draw, match, detail, now, Point);
        }
        var history = detail.MovementTimeline.Where(x => x.ElapsedMs <= now && x.ElapsedMs >= now - 10_000).ToArray();
        if (history.Length == 0)
        {
            var nearest = NearestMovement(detail, now);
            if (nearest != null) history = [nearest];
        }
        // Crystalline Conflict has two teams and no useful Frontline-style
        // three-faction blob. Keep this inferred aggregate layer exclusive to FL.
        if (match.Schema != LocalMatchAnalyzer.CrystallineSchema)
        for (byte team = 0; team < 3; team++)
        {
            var path = history.SelectMany(x => x.Factions.Select(f => (Frame: x, Faction: f)))
                .Where(x => x.Faction.Team == team).ToArray();
            for (var i = 1; i < path.Length; i++)
            {
                var alpha = .18f + .72f * i / Math.Max(1, path.Length - 1);
                var color = MapTeamColor(match, team) with { W = alpha };
                draw.AddLine(Point(path[i - 1].Faction.X, path[i - 1].Faction.Z),
                    Point(path[i].Faction.X, path[i].Faction.Z), ImGui.ColorConvertFloat4ToU32(color), 2.5f);
            }
            var last = path.LastOrDefault();
            if (last.Faction == null) continue;
            var p = Point(last.Faction.X, last.Faction.Z);
            var radius = 7 + MathF.Sqrt(last.Faction.Count) * 2.2f;
            var teamColor = MapTeamColor(match, team);
            draw.AddCircleFilled(p, radius, ImGui.ColorConvertFloat4ToU32(teamColor with { W = .75f }));
            var countText = last.Faction.Count.ToString(CultureInfo.InvariantCulture);
            var countAt = p + new Vector2(radius + 4, -8);
            var packedCount = ImGui.ColorConvertFloat4ToU32(teamColor);
            draw.AddText(countAt, packedCount, countText);
            draw.AddText(countAt + new Vector2(.7f, 0), packedCount, countText);
        }
        if (history.Any(x => x.Actors is { Count: > 0 }))
        {
            foreach (var actorPath in history
                         .SelectMany(frame => (frame.Actors ?? []).Select(actor => (frame, actor)))
                         .GroupBy(x => x.actor.EntityId))
            {
                var path = actorPath.OrderBy(x => x.frame.ElapsedMs).ToArray();
                for (var i = 1; i < path.Length; i++)
                {
                    var previous = path[i - 1];
                    var current = path[i];
                    if (!MapViewportMath.CanConnectActorTrail(previous.frame.ElapsedMs,
                            current.frame.ElapsedMs, previous.actor.CurrentHp, current.actor.CurrentHp,
                            previous.actor.FellToZero, current.actor.FellToZero,
                            previous.actor.X, previous.actor.Y, previous.actor.Z,
                            current.actor.X, current.actor.Y, current.actor.Z)) continue;
                    var alpha = .10f + .54f * i / Math.Max(1, path.Length - 1);
                    draw.AddLine(Point(previous.actor.X, previous.actor.Z),
                        Point(current.actor.X, current.actor.Z),
                        ImGui.ColorConvertFloat4ToU32(MapTeamColor(match, current.actor.Team) with { W = alpha }),
                        IsPlayerFocused(current.actor) ? 3f : 1.35f);
                }
            }
            var frame = history[^1];
            foreach (var actor in frame.Actors ?? [])
            {
                var point = Point(actor.X, actor.Z);
                var selected = IsPlayerFocused(actor);
                if (selected) draw.AddCircle(point, 8.5f, ImGui.ColorConvertFloat4ToU32(Vector4.One), 0, 2.2f);
                draw.AddCircleFilled(point, selected ? 5.5f : 3.5f,
                    ImGui.ColorConvertFloat4ToU32(MapTeamColor(match, actor.Team) with { W = .94f }));
                if (selected)
                {
                    draw.AddText(point + new Vector2(10, -9), ImGui.ColorConvertFloat4ToU32(Vector4.One),
                        actor.Name);
                    DrawHpShieldBar(draw, point + new Vector2(10, 8), new Vector2(116, 8),
                        actor.CurrentHp, actor.MaxHp, actor.ShieldPercent, false);
                }
            }
            if (ImGui.IsItemHovered())
            {
                var mouse = ImGui.GetMousePos();
                var nearest = (frame.Actors ?? [])
                    .Select(actor => (actor, distance: Vector2.Distance(mouse, Point(actor.X, actor.Z))))
                    .Where(x => x.distance <= 12).OrderBy(x => x.distance).FirstOrDefault();
                if (nearest.actor != null)
                {
                    var teamLine = match.Schema == LocalMatchAnalyzer.CrystallineSchema
                        ? MapTeamName(match, nearest.actor.Team)
                        : $"{TeamName(nearest.actor.Team)} {(char)('A' + nearest.actor.Alliance % 3)}";
                    ImGui.SetTooltip($"{nearest.actor.Name}@{WorldName(nearest.actor.WorldId)}\n{teamLine}");
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) SetFocus(nearest.actor);
                }
            }
        }
        var lastObjectiveActions = (detail.Actions ?? [])
            .Where(x => x.ElapsedMs <= now && now - x.ElapsedMs <= 1_500 && x.TargetMaxHp > 0)
            .GroupBy(x => x.TargetEntityId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.ElapsedMs).First());
        var lastObjectiveObservations = (detail.ObjectiveDiscovery ?? [])
            .Where(x => x.ElapsedMs <= now && now - x.ElapsedMs <= 1_500 && x.ObjectId != 0 &&
                        x.Loaded && x.RealtimeHpEligible == true)
            .GroupBy(x => x.ObjectId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.ElapsedMs).First());
        // Re-decode archived CC object observations with the current,
        // territory-scoped catalogue. This lets old captures gain a verified
        // Cloud Nine feather/twister label without rewriting the source file.
        // Unknown BaseIds remain invisible and unguessed. Cloud Nine's
        // BaseId 2014410 is now a territory-scoped, evidence-backed exception.
        var redecodedCrystalline = new List<ObjectiveStateEvent>();
        if (match.Schema == LocalMatchAnalyzer.CrystallineSchema)
        {
            foreach (var observation in (detail.ObjectiveDiscovery ?? [])
                         .Where(row => row.ElapsedMs <= now)
                         .GroupBy(row => row.ObjectId)
                         .Select(group => group.OrderByDescending(row => row.ElapsedMs).First()))
            {
                if (!CrystallineMechanismCatalog.TryResolve(match.TerritoryId,
                        observation.BaseId, observation.Name, out var mechanism)) continue;
                var hasHp = mechanism.HasMeaningfulHp && observation.RealtimeHpEligible == true;
                redecodedCrystalline.Add(new ObjectiveStateEvent(observation.ElapsedMs,
                    observation.ObjectId, mechanism.Label,
                    observation.Loaded ? "active" : "inactive", null,
                    observation.X, observation.Y, observation.Z,
                    $"{CrystallineMechanismCatalog.Version}:redecoded_object_observation_v1",
                    hasHp ? observation.CurrentHp : 0, hasHp ? observation.MaxHp : 0));
            }
        }
        var objectiveAtTime = (detail.ObjectiveTimeline ?? [])
            .Concat(redecodedCrystalline)
            .Where(x => x.ElapsedMs <= now)
            .GroupBy(x => x.ObjectId)
            .Select(x => x.OrderByDescending(y => y.ElapsedMs).First())
            // Catalogue v4 briefly promoted every Cloud Nine BaseId 9020,
            // including an empty-name central object loaded before the actual
            // Turbulence event. Old cached objective_state rows are accepted
            // only when the original object observation proves the name.
            .Where(x => match.TerritoryId is not (1034 or 1060) ||
                        x.Label != "cc_cloudnine_twister" ||
                        (detail.ObjectiveDiscovery ?? []).Any(observation =>
                            observation.ObjectId == x.ObjectId && observation.ElapsedMs <= now &&
                            CrystallineMechanismCatalog.IsVerifiedCloudNineTurbulence(
                                observation.TerritoryId, observation.BaseId, observation.Name)))
            .Where(x => !string.Equals(x.State, "inactive", StringComparison.OrdinalIgnoreCase) &&
                        !(x.State ?? string.Empty).Contains("destroy", StringComparison.OrdinalIgnoreCase) &&
                        !(x.State ?? string.Empty).Contains("unload", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var objectiveMetadata = objectiveAtTime.Where(x => IsObjectiveMetadataLabel(x.Label)).ToArray();
        var objectiveRowsRaw = objectiveAtTime
            // A raw map icon/data ID is discovery evidence, not a decoded tactical objective.
            // Do not turn it into a large anonymous circle on the replay map.
            // Secure is the one bounded exception: the client EventMarker is
            // the authoritative source for drone/tower countdowns, so its raw
            // data label remains drawable when the encoded icon proves the
            // mechanism type. Fixed nodes are rendered from the catalogue below.
            .Where(x => (!IsRawObjectiveIdentifier(x.Label) ||
                         match.DutyId == 127 && SecureObjectiveCatalog.IsTransientMechanicState(x.State)) &&
                        !IsObjectiveMetadataLabel(x.Label) && !IsCrystallineProgressGauge(x) &&
                        !(match.DutyId == 127 && (SecureObjectiveCatalog.IsFixedNodeId(x.ObjectId) ||
                          SecureObjectiveCatalog.TryGetMapIcon(x.State, out var secureMapIcon) &&
                          SecureObjectiveCatalog.IsOccupationIcon(secureMapIcon))))
            .Where(x => !ClockworkCastletownMechanics.IsTerritory(match.TerritoryId) ||
                        ClockworkCastletownMechanics.UseGenericMapGlyph(x.Label))
            // Red Sands monsters/geysers can remain loaded in the archive long
            // after their animation ends. Their short action windows are drawn
            // by DrawRedSandsMechanics instead of this persistent object layer.
            .Where(x => !RedSandsMechanics.IsTransientLabel(match.TerritoryId, x.Label))
            .Where(x =>
            {
                var current = x.CurrentHp;
                var maximum = x.MaxHp;
                if (maximum == 0 && lastObjectiveActions.TryGetValue(x.ObjectId, out var action))
                { current = action.TargetCurrentHp; maximum = action.TargetMaxHp; }
                if (maximum == 0 && lastObjectiveObservations.TryGetValue(x.ObjectId, out var observation))
                { current = observation.CurrentHp; maximum = observation.MaxHp; }
                return maximum == 0 || current > 1;
            })
            .ToArray();
        // One point may arrive from both the global map UI and a nearby loaded
        // object. Merge those evidence rows spatially before drawing it.
        var objectiveRows = objectiveRowsRaw
            .GroupBy(row => ((int)Math.Round(row.X / 3d), (int)Math.Round(row.Z / 3d)))
            .Select(group => new
            {
                Event = group.OrderByDescending(row =>
                        (row.Label ?? string.Empty).Contains("Rank ", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(row => row.ProgressPercent.HasValue)
                    .ThenByDescending(row => row.OwnerTeam.HasValue)
                    .ThenByDescending(row => row.ElapsedMs).First(),
                Owner = group.Where(row => row.OwnerTeam.HasValue)
                    .OrderByDescending(row => row.ElapsedMs).Select(row => row.OwnerTeam).FirstOrDefault(),
                Neutral = group.Any(row => (row.State ?? string.Empty).Contains("icon=60575",
                    StringComparison.OrdinalIgnoreCase)),
                Progress = group.Where(row => row.ProgressPercent.HasValue)
                    .OrderByDescending(row => row.ElapsedMs).Select(row => row.ProgressPercent).FirstOrDefault()
            }).ToArray();
        var objectiveActorFrame = detail.MovementTimeline
            .Where(frame => frame.ElapsedMs <= now && now - frame.ElapsedMs <= 1_500)
            .OrderByDescending(frame => frame.ElapsedMs).FirstOrDefault();
        if (match.DutyId == 127)
            DrawSecureFixedNodes(draw, match, objectiveAtTime, objectiveActorFrame, Point);
        foreach (var rendered in objectiveRows)
        {
            var objective = rendered.Event;
            var point = Point(objective.X, objective.Z);
            var color = rendered.Owner is byte owner && owner < 3
                ? MapTeamColor(match, owner)
                : rendered.Neutral ? new Vector4(.92f, .92f, .82f, 1f)
                : new Vector4(.48f, .52f, .55f, .88f);
            var currentHp = objective.CurrentHp;
            var maxHp = objective.MaxHp;
            if (maxHp == 0 && lastObjectiveActions.TryGetValue(objective.ObjectId, out var actionHp))
            { currentHp = actionHp.TargetCurrentHp; maxHp = actionHp.TargetMaxHp; }
            if (maxHp == 0 && lastObjectiveObservations.TryGetValue(objective.ObjectId, out var observedHp))
            { currentHp = observedHp.CurrentHp; maxHp = observedHp.MaxHp; }
            if (ClockworkCastletownMechanics.IsTerritory(match.TerritoryId) &&
                !ClockworkCastletownMechanics.HasMeaningfulHealth(objective.Label))
            { currentHp = 0; maxHp = 0; }
            DrawObjectiveGlyph(draw, point, match.DutyId, objective.Label ?? string.Empty, maxHp, color,
                (objective.State ?? string.Empty).Contains("captur", StringComparison.OrdinalIgnoreCase));
            var objectiveName = ObjectiveInstanceDisplayName(match.DutyId, detail,
                objective.ObjectId, objective.Label ?? string.Empty, now);
            draw.AddText(point + new Vector2(15, -9), ImGui.ColorConvertFloat4ToU32(TextColor), objectiveName);
            var nearbyMetadata = objectiveMetadata
                .Where(row => Math.Abs(row.X - objective.X) <= 3 && Math.Abs(row.Z - objective.Z) <= 3)
                .OrderByDescending(row => row.ElapsedMs).FirstOrDefault();
            var isCrystal = IsCrystallineCrystal(objective);
            if (isCrystal && match.TerritoryId is 1034 or 1060)
                DrawCloudNineCheckpointPie(draw, match, detail, now, point);
            // Tactical crystal has no HP. Only a percentage decoded from the
            // PvP map UI is allowed to describe push/checkpoint progress.
            var displayedPercent = rendered.Progress ?? nearbyMetadata?.ProgressPercent ??
                ObjectivePercentFromLabel(objective.Label ?? string.Empty) ??
                ObjectivePercentFromLabel(nearbyMetadata?.Label ?? string.Empty) ??
                (!isCrystal && maxHp > 0 ? Math.Clamp(100d * currentHp / maxHp, 0, 100) : null);
            var displayedCountdown = objective.RemainingSeconds;
            if (match.DutyId == 127 && !displayedCountdown.HasValue)
                displayedCountdown = SecureObjectiveCatalog.ParseDisplayedCountdownSeconds(
                                         objective.DisplayText ?? string.Empty) ??
                                     SecureObjectiveCatalog.ParseDisplayedCountdownSeconds(
                                         objective.Label ?? string.Empty);
            if (displayedPercent.HasValue)
            {
                DrawObjectiveProgressBar(draw, point + new Vector2(-18, 14), displayedPercent.Value, color);
            }
            else if (displayedCountdown.HasValue)
            {
                var countdown = $"T-{displayedCountdown.Value}s";
                var countdownSize = ImGui.CalcTextSize(countdown);
                var countdownAt = point + new Vector2(-countdownSize.X / 2, 13);
                draw.AddRectFilled(countdownAt - new Vector2(2, 1),
                    countdownAt + countdownSize + new Vector2(2, 1),
                    ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.ChartBackground(configuration.UiTheme) with { W = .82f }), 2);
                draw.AddText(countdownAt, ImGui.ColorConvertFloat4ToU32(TextColor), countdown);
            }
            if (match.DutyId == 127 && objectiveActorFrame?.Actors is { Count: > 0 } visibleActors &&
                IsSecureOccupationPoint(objective.Label ?? string.Empty))
            {
                // This is deliberately an observer-bounded list: global map UI
                // establishes the node and ownership, while only loaded player
                // coordinates can establish who was visibly standing nearby.
                var standing = visibleActors.Where(actor =>
                        Math.Abs(actor.Y - objective.Y) <= 7 &&
                        Math.Pow(actor.X - objective.X, 2) + Math.Pow(actor.Z - objective.Z, 2) <= 12 * 12)
                    .OrderBy(actor => actor.Team).ThenBy(actor => actor.Name).ToArray();
                if (standing.Length > 0)
                {
                    var offset = 15f;
                    foreach (var teamGroup in standing.GroupBy(actor => actor.Team).OrderBy(group => group.Key))
                    {
                        var countText = teamGroup.Count().ToString();
                        var teamColor = teamGroup.Key < 3 ? MapTeamColor(match, teamGroup.Key) : Muted;
                        draw.AddText(point + new Vector2(offset, 9),
                            ImGui.ColorConvertFloat4ToU32(teamColor), countText);
                        offset += ImGui.CalcTextSize(countText).X + 7;
                    }
                    if (Vector2.Distance(ImGui.GetMousePos(), point) <= 20)
                        ImGui.SetTooltip(string.Join("\n", standing.Select(actor =>
                            $"{actor.Name}@{WorldName(actor.WorldId)}")));
                }
            }
        }
        if (match.DutyId == 127)
        {
            // Some Secure drone/tower rows exist only in the loaded-object
            // stream and never receive a separate objective-state event. Draw
            // those active observations with the exact same canonical ID used
            // by the mechanism tables.
            var renderedIds = objectiveRows.Select(row => row.Event.ObjectId).ToHashSet();
            var loadedMechanisms = (detail.ObjectiveDiscovery ?? [])
                .Where(row => row.ElapsedMs <= now && row.ObjectId != 0)
                .GroupBy(row => row.ObjectId)
                .Select(group => group.OrderByDescending(row => row.ElapsedMs).First())
                .Where(row => row.Loaded && !renderedIds.Contains(row.ObjectId) &&
                    (SecureObjectiveCatalog.IsDrone(row.BaseId, row.Name) ||
                     row.BaseId == SecureObjectiveCatalog.TowerCircleBaseId))
                .Where(row => row.MaxHp == 0 || row.CurrentHp > 1)
                .ToArray();
            foreach (var mechanism in loadedMechanisms)
            {
                var point = Point(mechanism.X, mechanism.Z);
                var color = SecureObjectiveCatalog.IsGoldDrone(mechanism.BaseId, mechanism.Name)
                    ? new Vector4(1f, .78f, .20f, 1f)
                    : new Vector4(.38f, .82f, 1f, 1f);
                DrawObjectiveGlyph(draw, point, match.DutyId, mechanism.Name ?? string.Empty,
                    mechanism.MaxHp, color, false);
                var display = ObjectiveInstanceDisplayName(match.DutyId, detail,
                    mechanism.ObjectId, mechanism.Name ?? string.Empty, now);
                draw.AddText(point + new Vector2(15, -9),
                    ImGui.ColorConvertFloat4ToU32(TextColor), display);
            }
        }
        else if (match.Schema == LocalMatchAnalyzer.CrystallineSchema)
        {
            // Historical CC details may contain the loaded mechanism object but
            // predate its objective-state catalogue. Promote only objects that
            // the current territory-scoped catalogue can prove.
            var renderedIds = objectiveRows.Select(row => row.Event.ObjectId).ToHashSet();
            var observedMechanisms = (detail.ObjectiveDiscovery ?? [])
                .Where(row => row.ElapsedMs <= now && row.ObjectId != 0)
                .GroupBy(row => row.ObjectId)
                .Select(group => group.OrderByDescending(row => row.ElapsedMs).First())
                .Where(row =>
                {
                    if (!row.Loaded || renderedIds.Contains(row.ObjectId) ||
                        !CrystallineMechanismCatalog.TryResolve(match.TerritoryId,
                            row.BaseId, row.Name, out var entry)) return false;
                    if (RedSandsMechanics.IsTransientBaseId(match.TerritoryId, row.BaseId) ||
                        RedSandsMechanics.IsTransientLabel(match.TerritoryId, entry.Label)) return false;
                    return !ClockworkCastletownMechanics.IsTerritory(match.TerritoryId) ||
                           ClockworkCastletownMechanics.UseGenericMapGlyph(entry.Label);
                })
                .Where(row => row.MaxHp == 0 || row.CurrentHp > 1)
                .ToArray();
            foreach (var mechanism in observedMechanisms)
            {
                var point = Point(mechanism.X, mechanism.Z);
                CrystallineMechanismCatalog.TryResolve(match.TerritoryId,
                    mechanism.BaseId, mechanism.Name, out var resolvedMechanism);
                var color = resolvedMechanism.Label == ArcheiaHarmoniasMechanics.HealingGlyphLabel
                    ? new Vector4(.20f, .94f, .46f, 1f)
                    : mechanism.BaseId == 2012832
                    ? new Vector4(.22f, .86f, .42f, 1f)
                    : new Vector4(.96f, .67f, .24f, 1f);
                var meaningfulHp = resolvedMechanism.HasMeaningfulHp ? mechanism.MaxHp : 0;
                DrawObjectiveGlyph(draw, point, match.DutyId, resolvedMechanism.Label,
                    meaningfulHp, color, false);
                var display = ObjectiveInstanceDisplayName(match.DutyId, detail,
                    mechanism.ObjectId, mechanism.Name ?? string.Empty, now);
                draw.AddText(point + new Vector2(15, -9),
                    ImGui.ColorConvertFloat4ToU32(TextColor), display);
            }
        }
        // Field markers and target markers are independent state machines.  Render
        // their latest state at the selected replay time without guessing who issued
        // the command. 场景标点和人头标点分别回放，不用邻近角色冒充命令者。
        var activeFieldMarkers = (detail.FieldMarkers ?? [])
            .Where(x => x.ElapsedMs <= now)
            .GroupBy(x => x.MarkerIndex)
            .Select(x => x.OrderByDescending(y => y.ElapsedMs).First())
            .Where(x => x.Change != "remove")
            .ToArray();
        foreach (var marker in activeFieldMarkers)
            DrawMarkerIcon(draw, Point(marker.X, marker.Z), marker.MarkerKey, true, 13f,
                marker.MarkerIndex);

        var activeTargetMarkers = (detail.TargetMarkers ?? [])
            .Where(x => x.ElapsedMs <= now && IsRecordableMarkerTarget(x.TargetEntityId))
            .GroupBy(x => x.MarkerIndex)
            .Select(x => x.OrderByDescending(y => y.ElapsedMs).First())
            .Where(x => x.Change != "remove")
            .GroupBy(x => x.TargetEntityId);
        foreach (var targetGroup in activeTargetMarkers)
        {
            // Target markers follow only an actor present in the current observed
            // frame. The general FindActor helper intentionally searches history
            // for death recap, so using it here would leave markers frozen in place.
            var currentFrame = detail.MovementTimeline.LastOrDefault(x => x.ElapsedMs <= now);
            var actor = currentFrame?.Actors?.FirstOrDefault(x => x.EntityId == targetGroup.Key);
            if (actor == null) continue;
            var anchor = Point(actor.X, actor.Z) + new Vector2(0, -15);
            var order = 0;
            foreach (var marker in targetGroup)
                DrawMarkerIcon(draw, anchor + new Vector2(order++ * 20, 0),
                    marker.MarkerKey, false, 9f, marker.MarkerIndex);
        }
        var evidenceSample = anomaly?.Samples?
            .Where(x => x.FromX.HasValue && x.FromZ.HasValue && x.ToX.HasValue && x.ToZ.HasValue)
            .OrderBy(x => Math.Abs(x.ElapsedMs - now)).FirstOrDefault();
        if (evidenceSample != null)
        {
            var from = Point(evidenceSample.FromX!.Value, evidenceSample.FromZ!.Value);
            var to = Point(evidenceSample.ToX!.Value, evidenceSample.ToZ!.Value);
            var evidenceColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .28f, .16f, 1f));
            var limitColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .72f, .18f, .88f));
            draw.AddLine(from, to, evidenceColor, 4f);
            draw.AddCircleFilled(from, 5f, limitColor);
            draw.AddCircleFilled(to, 6f, evidenceColor);
            var permitted = evidenceSample.NominalRange is { } nominal
                ? nominal + (evidenceSample.Tolerance ?? 0)
                : evidenceSample.Dt is { } dt ? 18d * dt : 0;
            if (permitted > 0)
            {
                var edge = Point(evidenceSample.FromX.Value + permitted, evidenceSample.FromZ.Value);
                draw.AddCircle(from, Vector2.Distance(from, edge), limitColor, 48, 2f);
            }
            var vertical = evidenceSample.FromY.HasValue && evidenceSample.ToY.HasValue
                ? Math.Abs(evidenceSample.ToY.Value - evidenceSample.FromY.Value) : 0;
            draw.AddText(to + new Vector2(9, -9), evidenceColor,
                vertical > .5 ? $"{T("reviewedSample")} · ΔY {vertical:0.0}" : T("reviewedSample"));
        }
        draw.AddText(origin + new Vector2(12, size.Y - 22), ImGui.ColorConvertFloat4ToU32(Muted),
            $"{T("tenSecondTrail")} · {T("mapControls")}");
        if (MapAssets.TryGetValue(match.DutyId, out var asset))
            draw.AddText(origin + new Vector2(12, 10), ImGui.ColorConvertFloat4ToU32(Vector4.One),
                $"{T(asset.NameKey)} · {T("mapOverlay")}");
        else if (match.Schema == LocalMatchAnalyzer.CrystallineSchema)
            draw.AddText(origin + new Vector2(12, 10), ImGui.ColorConvertFloat4ToU32(Vector4.One),
                $"{T("ccCrystalReplay")} · {(mapBackgroundLoaded ? T("mapOverlay") : T("coordinateGrid"))}");
        if (match.DutyId == 127)
        {
            var droneCountdown = objectiveAtTime.Where(row =>
                    SecureObjectiveCatalog.TryGetMapIcon(row.State, out var iconId) &&
                    SecureObjectiveCatalog.IsDroneMapIcon(iconId))
                .Select(row => (Row: row, Seconds: row.RemainingSeconds ??
                    SecureObjectiveCatalog.ParseDisplayedCountdownSeconds(row.DisplayText ?? string.Empty) ??
                    SecureObjectiveCatalog.ParseDisplayedCountdownSeconds(row.Label ?? string.Empty)))
                .Where(value => value.Seconds.HasValue)
                .OrderByDescending(value => value.Row.ElapsedMs).FirstOrDefault();
            var status = droneCountdown.Seconds is int seconds
                ? $"{T("secureInterceptorDrone")} · T-{seconds}s"
                : $"{T("secureInterceptorDrone")} · {T("notObserved")}";
            draw.AddText(origin + new Vector2(12, 32),
                ImGui.ColorConvertFloat4ToU32(droneCountdown.Row == null ? Muted :
                    new Vector4(.38f, .82f, 1f, 1f)), status);
        }
        else if (ArcheiaHarmoniasMechanics.IsTerritory(match.TerritoryId))
        {
            var astra = ArcheiaHarmoniasMechanics.ResolveAetherometer(
                detail.ObjectiveTimeline, match.TerritoryId, 0, now);
            var umbra = ArcheiaHarmoniasMechanics.ResolveAetherometer(
                detail.ObjectiveTimeline, match.TerritoryId, 1, now);
            var astraCheckpoint = ArcheiaHarmoniasMechanics.ResolveCheckpoint(
                detail.ObjectiveTimeline, ArcheiaHarmoniasMechanics.AstraGaugeLabel, now);
            var umbraCheckpoint = ArcheiaHarmoniasMechanics.ResolveCheckpoint(
                detail.ObjectiveTimeline, ArcheiaHarmoniasMechanics.UmbraGaugeLabel, now);
            draw.AddText(origin + new Vector2(12, 32),
                ImGui.ColorConvertFloat4ToU32(MapTeamColor(match, 0)),
                $"{MapTeamName(match, 0)} · {ArcheiaHarmoniasStatusText(astra, astraCheckpoint)}");
            draw.AddText(origin + new Vector2(12, 49),
                ImGui.ColorConvertFloat4ToU32(MapTeamColor(match, 1)),
                $"{MapTeamName(match, 1)} · {ArcheiaHarmoniasStatusText(umbra, umbraCheckpoint)}");
        }
        else if (match.TerritoryId is 1034 or 1060)
        {
            var turbulenceStatus = CloudNineReplayStatus(detail, now);
            draw.AddText(origin + new Vector2(12, 32),
                ImGui.ColorConvertFloat4ToU32(turbulenceStatus.Active ?
                    new Vector4(1f, .76f, .20f, 1f) : Muted), turbulenceStatus.Text);
            draw.AddText(origin + new Vector2(12, 49), ImGui.ColorConvertFloat4ToU32(Muted),
                T("ccCloudNineSafetyHint"));
        }
        else if (RedSandsMechanics.IsTerritory(match.TerritoryId))
        {
            var heatWave = RedSandsMechanics.ResolveHeatWave(detail.MovementTimeline, now);
            var text = heatWave.Active ? T("ccRedSandsHeatWaveActive") :
                heatWave.CountdownSeconds is int seconds
                    ? string.Format(T("ccRedSandsHeatWaveCountdown"), seconds)
                    : T("ccRedSandsHeatWaveNotActive");
            draw.AddText(origin + new Vector2(12, 32),
                ImGui.ColorConvertFloat4ToU32(heatWave.Active || heatWave.CountdownSeconds.HasValue
                    ? new Vector4(1f, .58f, .16f, 1f) : Muted), text);
            draw.AddText(origin + new Vector2(12, 49), ImGui.ColorConvertFloat4ToU32(Muted),
                T("ccRedSandsHeatWaveSafetyHint"));
        }
        else if (objectiveRows.Length == 0)
            draw.AddText(origin + new Vector2(12, 32), ImGui.ColorConvertFloat4ToU32(Muted),
                T("noObjectiveEvents"));
        draw.PopClipRect();
        DrawMapZoomSlider(origin, size, afterCanvas);
    }

    /// <summary>
    /// Draws the nine Secure occupation points from stable catalogue geometry.
    /// Inputs are the latest replay-time objective states and the latest visible
    /// actor frame; output is map-only rendering. Geometry is never removed by
    /// a transient EventMarker unload, while ownership and occupants remain
    /// explicitly bounded to client UI and loaded-actor evidence respectively.
    /// </summary>
    private void DrawSecureFixedNodes(ImDrawListPtr draw, LocalMatchAnalysis match,
        IReadOnlyList<ObjectiveStateEvent> objectiveAtTime, MovementPoint? actorFrame,
        Func<double, double, Vector2> point)
    {
        foreach (var node in SecureObjectiveCatalog.Nodes)
        {
            var dynamicState = objectiveAtTime
                .Where(row => !SecureObjectiveCatalog.IsFixedNodeId(row.ObjectId) &&
                              Math.Pow(row.X - node.X, 2) + Math.Pow(row.Z - node.Z, 2) <= 12 * 12 &&
                              (row.OwnerTeam.HasValue ||
                               SecureObjectiveCatalog.TryGetMapIcon(row.State, out var stateIcon) &&
                               SecureObjectiveCatalog.IsOccupationIcon(stateIcon)))
                .OrderByDescending(row => row.ElapsedMs).FirstOrDefault();
            var owner = dynamicState?.OwnerTeam;
            var explicitlyNeutral = dynamicState != null &&
                SecureObjectiveCatalog.TryGetMapIcon(dynamicState.State, out var iconId) &&
                SecureObjectiveCatalog.IsNeutralOccupationIcon(iconId);
            var color = owner is byte team && team < 3
                ? MapTeamColor(match, team)
                : explicitlyNeutral ? new Vector4(.92f, .92f, .82f, 1f)
                : new Vector4(.56f, .60f, .62f, .92f);
            var at = point(node.X, node.Z);

            // Home nodes use a faction-coloured diamond as the compact spawn
            // emblem; route nodes use a smaller ring. The adjacent text is the
            // same unique two-character code used by the mechanism table,
            // never "P-"/"Secure node" or a repeated faction label.
            var packed = ImGui.ColorConvertFloat4ToU32(color);
            var labelColor = node.HomeTeam is byte homeTeam
                ? MapTeamColor(match, homeTeam) : color;
            var labelPacked = ImGui.ColorConvertFloat4ToU32(labelColor);
            if (node.HomeTeam.HasValue)
            {
                draw.AddQuadFilled(at + new Vector2(0, -7), at + new Vector2(7, 0),
                    at + new Vector2(0, 7), at + new Vector2(-7, 0), labelPacked);
                draw.AddQuad(at + new Vector2(0, -8), at + new Vector2(8, 0),
                    at + new Vector2(0, 8), at + new Vector2(-8, 0),
                    packed, 2f);
            }
            else
            {
                draw.AddCircleFilled(at, 5.5f,
                    ImGui.ColorConvertFloat4ToU32(color with { W = .32f }));
                draw.AddCircle(at, 6.5f, packed, 20, 2f);
            }
            draw.AddText(at + new Vector2(10, -8), labelPacked, SecureNodeDisplayName(node));

            var standing = (actorFrame?.Actors ?? []).Where(actor =>
                    Math.Abs(actor.Y - node.Y) <= 7 &&
                    Math.Pow(actor.X - node.X, 2) + Math.Pow(actor.Z - node.Z, 2) <= 12 * 12)
                .GroupBy(actor => actor.Team).OrderBy(group => group.Key).ToArray();
            if (standing.Length == 0) continue;
            var offset = 10f;
            foreach (var teamGroup in standing)
            {
                var count = teamGroup.Count().ToString(CultureInfo.InvariantCulture);
                var teamColor = teamGroup.Key < 3 ? MapTeamColor(match, teamGroup.Key) : Muted;
                draw.AddText(at + new Vector2(offset, 8),
                    ImGui.ColorConvertFloat4ToU32(teamColor), count);
                offset += ImGui.CalcTextSize(count).X + 6;
            }
        }
    }

    private void DrawCrystallineCrystalTrail(ImDrawListPtr draw, DetailedAnalysis detail, long now,
        Func<double, double, Vector2> point)
    {
        var timeline = (detail.ObjectiveTimeline ?? [])
            .Where(row => row.ElapsedMs <= now && IsCrystallineCrystal(row))
            .OrderBy(row => row.ElapsedMs).ToArray();
        if (timeline.Length == 0) return;
        ObjectiveStateEvent? previous = null;
        var shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(.08f, .06f, .02f, .78f));
        var gold = ImGui.ColorConvertFloat4ToU32(new Vector4(.96f, .73f, .20f, .88f));
        foreach (var row in timeline)
        {
            if (string.Equals(row.State, "inactive", StringComparison.OrdinalIgnoreCase))
            {
                previous = null;
                continue;
            }
            if (previous != null && row.ElapsedMs - previous.ElapsedMs <= 1_250)
            {
                var from = point(previous.X, previous.Z);
                var to = point(row.X, row.Z);
                draw.AddLine(from, to, shadow, 4f);
                draw.AddLine(from, to, gold, 2f);
            }
            previous = row;
        }
    }

    private void DrawObservedCrystallineSpeedZones(ImDrawListPtr draw, LocalMatchAnalysis match,
        DetailedAnalysis detail,
        long now, Func<double, double, Vector2> point)
    {
        // Swift Sprint has several status IDs across map/game revisions. The
        // replay draws only locations directly evidenced by a status apply and
        // a matching loaded actor position; it does not invent fixed geometry.
        int[] swiftSprintStatuses = [2996, 2997, 3125, 4385, 4709];
        var cloudNine = match.TerritoryId is 1034 or 1060;
        if (cloudNine)
        {
            DrawCloudNineGaleSpherePickups();
            return;
        }
        // Red Sands has connected Shifting Sands lanes in the arena itself.
        // Historical speed-status positions are player observations, not
        // independent field objectives; drawing them as cumulative circles is
        // the source of the bogus cluster seen in old replays.
        if (RedSandsMechanics.IsTerritory(match.TerritoryId)) return;
        var positions = new List<(double X, double Z)>();
        foreach (var status in (detail.StatusTimeline ?? []).Where(value =>
                     value.ElapsedMs <= now && value.Change.Equals("apply",
                         StringComparison.OrdinalIgnoreCase) &&
                     swiftSprintStatuses.Contains((int)value.StatusId)))
        {
            var observed = detail.MovementTimeline
                .Where(frame => Math.Abs(frame.ElapsedMs - status.ElapsedMs) <= 1_500)
                .SelectMany(frame => (frame.Actors ?? []).Select(actor => (frame, actor)))
                .Where(value => value.actor.EntityId == status.TargetEntityId)
                .OrderBy(value => Math.Abs(value.frame.ElapsedMs - status.ElapsedMs))
                .FirstOrDefault();
            if (observed.actor == null) continue;
            if (positions.Any(value => Math.Pow(value.X - observed.actor.X, 2) +
                                       Math.Pow(value.Z - observed.actor.Z, 2) <= 36)) continue;
            positions.Add((observed.actor.X, observed.actor.Z));
        }
        var fill = ImGui.ColorConvertFloat4ToU32(new Vector4(.20f, .82f, .95f, .16f));
        var border = ImGui.ColorConvertFloat4ToU32(new Vector4(.24f, .88f, 1f, .82f));
        foreach (var position in positions)
        {
            var center = point(position.X, position.Z);
            draw.AddCircleFilled(center, 15, fill, 24);
            draw.AddCircle(center, 15, border, 24, 2f);
            draw.AddText(center + new Vector2(18, -8), border, T("ccSwiftSprintZone"));
        }

        void DrawCloudNineGaleSpherePickups()
        {
            const long markerLifetimeMs = 6_000;
            var pickups = new List<(long ElapsedMs, double X, double Z, bool ObjectEvidence)>();
            // BaseId 2014410 is the direct field-object source. Its individual
            // unloads are pickups; the simultaneous final unload is only the
            // end of Turbulence and is rejected because no sphere remains.
            foreach (var row in (detail.ObjectiveDiscovery ?? []).Where(row =>
                         row.BaseId == 2014410 && !row.Loaded &&
                         row.ElapsedMs <= now && now - row.ElapsedMs <= markerLifetimeMs &&
                         IsCloudNineTurbulenceActive(detail, Math.Max(0, row.ElapsedMs - 1)) &&
                         IsAnyCloudNineGaleSphereLoaded(detail, row.ElapsedMs + 1)))
                pickups.Add((row.ElapsedMs, row.X, row.Z, true));

            // The real 0.8.93 archive carries status 4385 in actor-vitals
            // snapshots rather than status_change packets. Detect its first
            // observed frame as a fallback for captures where the field object
            // was outside the observer's loaded-object radius.
            foreach (var actorFrames in (detail.MovementTimeline ?? [])
                         .Where(frame => frame.ElapsedMs <= now &&
                                         now - frame.ElapsedMs <= markerLifetimeMs + 3_000 &&
                                         frame.Actors is { Count: > 0 })
                         .SelectMany(frame => frame.Actors!.Select(actor => (frame.ElapsedMs, actor)))
                         .GroupBy(value => value.actor.EntityId))
            {
                var frames = actorFrames.OrderBy(value => value.ElapsedMs).ToArray();
                for (var i = 0; i < frames.Length; i++)
                {
                    var hasSwiftSprint = (frames[i].actor.Statuses ?? [])
                        .Any(status => swiftSprintStatuses.Contains((int)status.StatusId));
                    if (!hasSwiftSprint) continue;
                    var previouslyObserved = i > 0 && (frames[i - 1].actor.Statuses ?? [])
                        .Any(status => swiftSprintStatuses.Contains((int)status.StatusId));
                    if (previouslyObserved || now - frames[i].ElapsedMs > markerLifetimeMs) continue;
                    if (!IsCloudNineTurbulenceActive(detail, frames[i].ElapsedMs)) continue;
                    if (pickups.Any(value => Math.Abs(value.ElapsedMs - frames[i].ElapsedMs) <= 2_500))
                        continue;
                    pickups.Add((frames[i].ElapsedMs, frames[i].actor.X, frames[i].actor.Z, false));
                }
            }

            var fill = ImGui.ColorConvertFloat4ToU32(new Vector4(.12f, .96f, .88f, .92f));
            var edge = ImGui.ColorConvertFloat4ToU32(new Vector4(.26f, 1f, .96f, 1f));
            foreach (var pickup in pickups.OrderBy(value => value.ElapsedMs))
            {
                var center = point(pickup.X, pickup.Z);
                var age = Math.Clamp((now - pickup.ElapsedMs) / (double)markerLifetimeMs, 0, 1);
                var pulseRadius = 12f + (float)age * 9f;
                draw.AddCircleFilled(center, 7f, fill, 24);
                draw.AddCircle(center, pulseRadius, edge, 28, 2.4f);
                draw.AddText(center + new Vector2(13, -11), edge,
                    T(pickup.ObjectEvidence ? "ccCloudNineGaleSpherePickup" :
                        "ccCloudNineGaleSphereObserved"));
            }
        }
    }

    private static bool IsAnyCloudNineGaleSphereLoaded(DetailedAnalysis detail, long elapsedMs) =>
        (detail.ObjectiveDiscovery ?? [])
        .Where(row => row.BaseId == 2014410 && row.ElapsedMs <= elapsedMs)
        .GroupBy(row => row.ObjectId)
        .Select(group => group.OrderByDescending(row => row.ElapsedMs).First())
        .Any(row => row.Loaded);

    /// <summary>
    /// Draws the director-provided checkpoint occupation gauge over the loaded
    /// tactical crystal. This is not HP: only a currently growing 1..99% gauge
    /// is shown, so a previously completed 100% checkpoint cannot mask the
    /// opposing team's later checkpoint attempt.
    /// </summary>
    private void DrawCloudNineCheckpointPie(ImDrawListPtr draw, LocalMatchAnalysis match,
        DetailedAnalysis detail, long now, Vector2 center)
    {
        var progress = CurrentCloudNineCheckpointProgress(detail, now);
        if (progress == null) return;
        const int segments = 32;
        const float radius = 12f;
        var background = ImGui.ColorConvertFloat4ToU32(
            BattlefieldUiTheme.ChartBackground(configuration.UiTheme) with { W = .88f });
        var color = ImGui.ColorConvertFloat4ToU32(MapTeamColor(match, progress.Value.Team));
        draw.AddCircleFilled(center, radius, background, segments);
        var filled = Math.Clamp((int)Math.Ceiling(segments * progress.Value.Percent / 100d), 1, segments);
        for (var i = 0; i < filled; i++)
        {
            var a = -MathF.PI / 2 + i * MathF.Tau / segments;
            var b = -MathF.PI / 2 + (i + 1) * MathF.Tau / segments;
            draw.AddTriangleFilled(center, center + Polar(radius, a), center + Polar(radius, b), color);
        }
        draw.AddCircle(center, radius, color, segments, 1.8f);
        var text = $"{progress.Value.Percent:0}%";
        var size = ImGui.CalcTextSize(text);
        var at = center - size / 2;
        draw.AddText(at + new Vector2(1, 1), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, .9f)), text);
        draw.AddText(at, ImGui.ColorConvertFloat4ToU32(Vector4.One), text);
    }

    private static (byte Team, double Percent)? CurrentCloudNineCheckpointProgress(
        DetailedAnalysis detail, long now)
    {
        var candidates = new List<(byte Team, double Percent, long ChangedAt)>();
        AddCandidate(0, "cc_astra_checkpoint");
        AddCandidate(1, "cc_umbra_checkpoint");
        if (candidates.Count == 0) return null;
        var selected = candidates.OrderByDescending(value => value.ChangedAt).First();
        return (selected.Team, selected.Percent);

        void AddCandidate(byte team, string label)
        {
            var values = (detail.ObjectiveTimeline ?? [])
                .Where(row => row.ElapsedMs <= now && row.Label == label && row.ProgressPercent.HasValue)
                .OrderBy(row => row.ElapsedMs).ToArray();
            if (values.Length == 0) return;
            var latest = values[^1];
            var percent = Math.Clamp(latest.ProgressPercent!.Value, 0, 100);
            if (percent <= 0 || percent >= 100) return;
            var changedAt = latest.ElapsedMs;
            for (var i = values.Length - 2; i >= 0; i--)
            {
                if (Math.Abs(values[i].ProgressPercent!.Value - percent) > .001) break;
                changedAt = values[i].ElapsedMs;
            }
            candidates.Add((team, percent, changedAt));
        }
    }

    /// <summary>
    /// Reconstructs only repeated, directly observed airborne routes.  It does
    /// not claim a fixed Jump Glyph position or current availability because
    /// neither is present in the archived objective packet.
    /// </summary>
    private void DrawObservedCloudNineJumpRoutes(ImDrawListPtr draw, LocalMatchAnalysis match,
        DetailedAnalysis detail, long now, Func<double, double, Vector2> point)
    {
        if (match.TerritoryId is not (1034 or 1060)) return;
        var samples = new List<(uint ActorId, double FromX, double FromZ, double ToX, double ToZ)>();
        foreach (var actorFrames in (detail.MovementTimeline ?? [])
                     .Where(frame => frame.ElapsedMs <= now && frame.Actors is { Count: > 0 })
                     .SelectMany(frame => frame.Actors!.Select(actor => (frame.ElapsedMs, actor)))
                     .GroupBy(value => value.actor.EntityId))
        {
            var frames = actorFrames.OrderBy(value => value.ElapsedMs).ToArray();
            for (var i = 0; i + 2 < frames.Length; i++)
            {
                var start = frames[i]; var apex = frames[i + 1]; var end = frames[i + 2];
                if (end.ElapsedMs - start.ElapsedMs is <= 0 or > 5_500) continue;
                if (start.actor.CurrentHp == 0 || apex.actor.CurrentHp == 0 || end.actor.CurrentHp == 0)
                    continue;
                var distance = Math.Sqrt(Math.Pow(end.actor.X - start.actor.X, 2) +
                                         Math.Pow(end.actor.Z - start.actor.Z, 2));
                if (distance < 35 || apex.actor.Y < Math.Max(start.actor.Y, end.actor.Y) + 6) continue;
                // Turbulence is also an airborne displacement. Excluding its
                // observed window avoids presenting it as a shortcut route.
                if (IsCloudNineTurbulenceActive(detail, apex.ElapsedMs)) continue;
                samples.Add((start.actor.EntityId, start.actor.X, start.actor.Z,
                    end.actor.X, end.actor.Z));
                i += 2;
            }
        }

        var routes = samples.GroupBy(value => (
                Fx: (int)Math.Round(value.FromX / 15d), Fz: (int)Math.Round(value.FromZ / 15d),
                Tx: (int)Math.Round(value.ToX / 15d), Tz: (int)Math.Round(value.ToZ / 15d)))
            .Where(group => group.Select(value => value.ActorId).Distinct().Count() >= 2)
            .Select(group => new
            {
                FromX = group.Average(value => value.FromX), FromZ = group.Average(value => value.FromZ),
                ToX = group.Average(value => value.ToX), ToZ = group.Average(value => value.ToZ)
            }).Take(4).ToArray();
        var color = ImGui.ColorConvertFloat4ToU32(new Vector4(.73f, .52f, 1f, .88f));
        foreach (var route in routes)
        {
            var from = point(route.FromX, route.FromZ); var to = point(route.ToX, route.ToZ);
            for (var segment = 0; segment < 12; segment += 2)
            {
                var a = Vector2.Lerp(from, to, segment / 12f);
                var b = Vector2.Lerp(from, to, (segment + 1) / 12f);
                draw.AddLine(a, b, color, 2f);
            }
            var direction = Vector2.Normalize(to - from);
            var normal = new Vector2(-direction.Y, direction.X);
            draw.AddTriangleFilled(to, to - direction * 9 + normal * 4,
                to - direction * 9 - normal * 4, color);
            draw.AddText((from + to) / 2 + new Vector2(5, -15), color, T("ccObservedJumpRoute"));
        }
    }

    private (bool Active, string Text) CloudNineReplayStatus(DetailedAnalysis detail, long now)
    {
        var active = IsCloudNineTurbulenceActive(detail, now);
        if (active) return (true, T("ccCloudNineTurbulenceActive"));
        var next = (detail.ObjectiveDiscovery ?? [])
            .Where(IsCloudNineTurbulenceObservation)
            .Where(row => row.ElapsedMs > now && row.Loaded)
            .OrderBy(row => row.ElapsedMs).FirstOrDefault();
        if (next != null && next.ElapsedMs - now <= 30_000)
        {
            var seconds = (int)Math.Ceiling((next.ElapsedMs - now) / 1000d);
            return (false, string.Format(CultureInfo.InvariantCulture,
                T("ccCloudNineTurbulenceCountdown"), seconds));
        }
        return (false, T("ccCloudNineMechanismNotActive"));
    }

    private static bool IsCloudNineTurbulenceActive(DetailedAnalysis detail, long elapsedMs) =>
        (detail.ObjectiveDiscovery ?? [])
        .Where(IsCloudNineTurbulenceObservation)
        .Where(row => row.ElapsedMs <= elapsedMs)
        .GroupBy(row => row.ObjectId)
        .Select(group => group.OrderByDescending(row => row.ElapsedMs).First())
        .Any(row => row.Loaded);

    private static long CloudNineActiveWaveStart(DetailedAnalysis detail, long elapsedMs)
    {
        var starts = (detail.ObjectiveDiscovery ?? [])
            .Where(IsCloudNineTurbulenceObservation)
            .Where(row => row.ElapsedMs <= elapsedMs)
            .GroupBy(row => row.ObjectId)
            .Where(group => group.OrderByDescending(row => row.ElapsedMs).First().Loaded)
            .Select(group =>
            {
                var rows = group.OrderBy(row => row.ElapsedMs).ToArray();
                var lastInactive = Array.FindLastIndex(rows, row => !row.Loaded);
                return rows.Skip(lastInactive + 1).Where(row => row.Loaded)
                    .Select(row => row.ElapsedMs).DefaultIfEmpty(elapsedMs).Min();
            }).ToArray();
        return starts.Length == 0 ? elapsedMs : starts.Min();
    }

    private static bool IsCloudNineTurbulenceObservation(ObjectiveObjectObservation row) =>
        CrystallineMechanismCatalog.IsVerifiedCloudNineTurbulence(
            row.TerritoryId, row.BaseId, row.Name);

    private void DrawCrystallineMechanismTelegraphs(ImDrawListPtr draw, LocalMatchAnalysis match,
        DetailedAnalysis detail, long now, Func<double, double, Vector2> point)
    {
        if (ArcheiaHarmoniasMechanics.IsTerritory(match.TerritoryId))
        {
            DrawArcheiaHarmoniasMechanics(draw, match, detail, now, point);
            return;
        }
        if (ClockworkCastletownMechanics.IsTerritory(match.TerritoryId))
        {
            DrawClockworkCastletownMechanics(draw, detail, now, point);
            return;
        }
        if (match.TerritoryId is 1034 or 1060)
        {
            var pulse = ImGui.ColorConvertFloat4ToU32(new Vector4(.38f, .86f, 1f, .92f));
            var impact = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .28f, .18f, .92f));
            foreach (var action in (detail.Actions ?? []).Where(row =>
                         row.ActionId == CrystallineMechanismCatalog.CloudNineTwisterActionId &&
                         row.SourceX.HasValue && row.SourceZ.HasValue &&
                         Math.Abs(row.ElapsedMs - now) <= 1_200))
            {
                var source = point(action.SourceX!.Value, action.SourceZ!.Value);
                // Screen-space pulses locate the directly observed source; they
                // intentionally do not assert an unverified world-space radius.
                draw.AddCircle(source, 7, pulse, 20, 2f);
                draw.AddCircle(source, 12, pulse, 24, 1.5f);
                if (action.TargetX.HasValue && action.TargetZ.HasValue)
                {
                    var target = point(action.TargetX.Value, action.TargetZ.Value);
                    draw.AddLine(source, target, impact, 1.5f);
                    draw.AddCircle(target, 7, impact, 20, 2f);
                    var amount = action.Damage > 0 ? $" {action.Damage:N0}" : string.Empty;
                    draw.AddText(target + new Vector2(9, -16), impact,
                        T("ccCloudNineTwisterHit") + amount);
                }
            }
            return;
        }
        if (RedSandsMechanics.IsTerritory(match.TerritoryId))
        {
            DrawRedSandsMechanics(draw, detail, now, point);
            return;
        }
        if (match.TerritoryId is not (1033 or 1059)) return;
        foreach (var action in (detail.Actions ?? []).Where(row =>
                     row.ActionId == CrystallineMechanismCatalog.VolcanicBombExplosionActionId &&
                     row.SourceX.HasValue && row.SourceZ.HasValue &&
                     Math.Abs(row.ElapsedMs - now) <= 1_200))
        {
            var x = action.SourceX!.Value;
            var z = action.SourceZ!.Value;
            var arm = CrystallineMechanismCatalog.VolcanicBombCrossArmYalms;
            var half = CrystallineMechanismCatalog.VolcanicBombCrossHalfWidthYalms;
            var fill = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .18f, .06f, .22f));
            var edge = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .32f, .12f, .92f));
            DrawWorldRect(x - arm, z - half, x + arm, z + half);
            DrawWorldRect(x - half, z - arm, x + half, z + arm);
            var center = point(x, z);
            draw.AddText(center + new Vector2(9, -17), edge, T("ccBombCrossExplosion"));

            void DrawWorldRect(double minX, double minZ, double maxX, double maxZ)
            {
                var a = point(minX, minZ); var b = point(maxX, minZ);
                var c = point(maxX, maxZ); var d = point(minX, maxZ);
                draw.AddQuadFilled(a, b, c, d, fill);
                draw.AddQuad(a, b, c, d, edge, 2f);
            }
        }
    }

    private void DrawRedSandsMechanics(ImDrawListPtr draw, DetailedAnalysis detail, long now,
        Func<double, double, Vector2> point)
    {
        foreach (var action in (detail.Actions ?? []).Where(row =>
                     RedSandsMechanics.IsMechanismAction(row.ActionId) &&
                     row.SourceX.HasValue && row.SourceZ.HasValue &&
                     RedSandsMechanics.IsActionVisible(row.ActionId, row.ElapsedMs, now))
                 .GroupBy(row => (row.ElapsedMs, row.SourceEntityId, row.ActionId))
                 .Select(group => group.First()))
        {
            var sourceX = action.SourceX!.Value;
            var sourceZ = action.SourceZ!.Value;
            var center = point(sourceX, sourceZ);
            var beforeImpact = now < action.ElapsedMs;
            var color = action.ActionId == RedSandsMechanics.TherapeuticNeedlesActionId
                ? new Vector4(.26f, .94f, .50f, .95f)
                : action.ActionId == RedSandsMechanics.SandpearlShroudActionId
                    ? new Vector4(.96f, .96f, 1f, .95f)
                    : new Vector4(1f, .32f, .18f, .95f);
            var packed = ImGui.ColorConvertFloat4ToU32(color);
            var fill = ImGui.ColorConvertFloat4ToU32(color with { W = beforeImpact ? .14f : .25f });
            // EffectRange is a world-space radius in yalms.  Old captures have
            // null metadata for these NPC actions, so the reviewed Red Sands
            // catalogue supplies the fallback.  Project all 32 world points;
            // a fixed-pixel circle would become too large when zoomed out and
            // too small when zoomed in.
            var radius = RedSandsMechanics.ResolveEffectRadius(action.ActionId,
                action.EffectRange);
            if (radius is double radiusYalms)
            {
                var worldCircle = RedSandsMechanics.BuildWorldCircle(sourceX, sourceZ,
                    radiusYalms);
                var screenCircle = worldCircle.Select(value => point(value.X, value.Z)).ToArray();
                for (var index = 0; index < screenCircle.Length; index++)
                {
                    var next = (index + 1) % screenCircle.Length;
                    draw.AddTriangleFilled(center, screenCircle[index], screenCircle[next], fill);
                    draw.AddLine(screenCircle[index], screenCircle[next], packed,
                        beforeImpact ? 2f : 2.5f);
                }
            }
            // This small screen-space dot is only a source locator.  It does
            // not represent the action's effective range.
            draw.AddCircleFilled(center, 3f, packed, 12);
            var label = action.ActionId switch
            {
                RedSandsMechanics.NeedlesActionId => T("ccRedSandsNeedles"),
                RedSandsMechanics.TherapeuticNeedlesActionId => T("ccRedSandsTherapeuticNeedles"),
                RedSandsMechanics.LandslipActionId => T("ccRedSandsLandslip"),
                RedSandsMechanics.SandpearlShroudActionId => T("ccRedSandsSandpearlShroud"),
                _ => T("ccRedSandsGeyserHazard")
            };
            draw.AddText(center + new Vector2(13, -8), packed, label);
        }
    }

    /// <summary>
    /// Draws Archeia Harmonias evidence at the selected replay time.
    /// Healing glyph coordinates come from loaded BaseId 8062 objects. The
    /// ordinary checkpoint gauge is drawn at the blue tactical checkpoint; it
    /// is never reused as an aether index. Old-cache bridge activation is
    /// derived from the tactical crystal crossing the fixed aetherometer.
    /// </summary>
    private void DrawArcheiaHarmoniasMechanics(ImDrawListPtr draw, LocalMatchAnalysis match,
        DetailedAnalysis detail, long now, Func<double, double, Vector2> point)
    {
        foreach (var (gauge, team) in new[]
                 {
                     (ArcheiaHarmoniasMechanics.AstraGaugeLabel, (byte)0),
                     (ArcheiaHarmoniasMechanics.UmbraGaugeLabel, (byte)1)
                 })
        {
            var aetherometer = ArcheiaHarmoniasMechanics.ResolveAetherometer(
                detail.ObjectiveTimeline, match.TerritoryId, team, now);
            var checkpoint = ArcheiaHarmoniasMechanics.ResolveCheckpoint(
                detail.ObjectiveTimeline, gauge, now);
            if (!ArcheiaHarmoniasMechanics.TryGetRouteGeometry(
                    match.TerritoryId, team, out var geometry)) continue;
            var at = point(geometry.AetherometerX, geometry.AetherometerZ);
            var color = MapTeamColor(match, team);
            var packed = ImGui.ColorConvertFloat4ToU32(color);
            var muted = ImGui.ColorConvertFloat4ToU32(color with { W = .34f });
            var routeColor = aetherometer.BridgeUnlocked ? packed : muted;
            var stateLabel = aetherometer.State ==
                             ArcheiaHarmoniasMechanics.AetherometerState.Unobserved
                ? T("notObserved")
                : aetherometer.BridgeUnlocked
                    ? T("ccHarmoniasUnlocked")
                    : T("ccHarmoniasLocked");

            var checkpointAt = point(geometry.CheckpointX, geometry.CheckpointZ);
            DrawHarmoniasRouteSegment(draw, at, checkpointAt, routeColor,
                aetherometer.BridgeUnlocked);
            var bridgeLabelAt = (at + checkpointAt) * .5f + new Vector2(0, -17);
            draw.AddText(bridgeLabelAt, routeColor,
                $"{T("ccHarmoniasBridge")} · {stateLabel}");

            var jumpGlyph = point(geometry.JumpGlyphX, geometry.JumpGlyphZ);
            var jumpAirborne = point(geometry.JumpAirborneX, geometry.JumpAirborneZ);
            var jumpLanding = point(geometry.JumpLandingX, geometry.JumpLandingZ);
            DrawHarmoniasRouteSegment(draw, jumpGlyph, jumpAirborne, routeColor,
                aetherometer.JumpGlyphUnlocked);
            DrawHarmoniasRouteSegment(draw, jumpAirborne, jumpLanding, routeColor,
                aetherometer.JumpGlyphUnlocked);
            draw.AddCircleFilled(jumpGlyph, 8f,
                ImGui.ColorConvertFloat4ToU32(color with
                    { W = aetherometer.JumpGlyphUnlocked ? .28f : .12f }), 24);
            draw.AddCircle(jumpGlyph, 9f, routeColor, 24, 2.2f);
            draw.AddCircle(jumpGlyph, 4f, routeColor, 18, 1.5f);
            draw.AddCircle(jumpLanding, 5f, routeColor, 18, 1.8f);
            var arrow = Vector2.Normalize(jumpLanding - jumpAirborne);
            var normal = new Vector2(-arrow.Y, arrow.X);
            draw.AddTriangleFilled(jumpLanding, jumpLanding - arrow * 9 + normal * 4,
                jumpLanding - arrow * 9 - normal * 4, routeColor);
            draw.AddText(jumpGlyph + new Vector2(12, -10), routeColor,
                $"{T("ccHarmoniasJumpGlyph")} · {stateLabel}");

            draw.AddCircleFilled(at, 13f, muted, 28);
            draw.AddCircle(at, 14f, packed, 28, 2f);
            if (aetherometer.State == ArcheiaHarmoniasMechanics.AetherometerState.Unlocked)
                draw.AddQuadFilled(at + new Vector2(0, -5), at + new Vector2(5, 0),
                    at + new Vector2(0, 5), at + new Vector2(-5, 0), packed);
            var label = aetherometer.State switch
            {
                ArcheiaHarmoniasMechanics.AetherometerState.Unlocked =>
                    T("ccHarmoniasRoutesUnlocked"),
                ArcheiaHarmoniasMechanics.AetherometerState.Reset =>
                    T("ccHarmoniasAetherReset"),
                ArcheiaHarmoniasMechanics.AetherometerState.AttemptObserved =>
                    T("ccHarmoniasAetherAttemptObserved"),
                ArcheiaHarmoniasMechanics.AetherometerState.Idle =>
                    T("ccHarmoniasAetherIdle"),
                _ => T("notObserved")
            };
            draw.AddText(at + new Vector2(18, -10), packed,
                $"{T("ccHarmoniasAetherometer")} · {label}");

            // The normal tactical checkpoint is a separate, always-present
            // blue objective. Its director percentage is direct evidence.
            var checkpointColor = new Vector4(.22f, .67f, 1f, 1f);
            var checkpointPacked = ImGui.ColorConvertFloat4ToU32(checkpointColor);
            var checkpointMuted = ImGui.ColorConvertFloat4ToU32(checkpointColor with { W = .22f });
            draw.AddCircleFilled(checkpointAt, 14f, checkpointMuted, 28);
            draw.AddCircle(checkpointAt, 15f, checkpointPacked, 28, 2.4f);
            var checkpointFilled = checkpoint.State ==
                                   ArcheiaHarmoniasMechanics.CheckpointState.Cleared
                ? 24
                : (int)Math.Round(24 * checkpoint.ProgressPercent / 100d);
            for (var segment = 0; segment < checkpointFilled; segment++)
            {
                var from = -MathF.PI / 2 + segment * MathF.Tau / 24;
                var to = -MathF.PI / 2 + (segment + .72f) * MathF.Tau / 24;
                draw.AddLine(checkpointAt + Polar(10, from), checkpointAt + Polar(10, to),
                    checkpointPacked, 3f);
            }
            draw.AddText(checkpointAt + new Vector2(18, 8), checkpointPacked,
                $"{T("ccHarmoniasCheckpoint")} · {ArcheiaHarmoniasCheckpointText(checkpoint)}");
        }

        // The 7.51 healing glyph emits Action 49768 for 10,000 healing.  A
        // short screen-space pulse explains the observed packet without
        // claiming an unverified world-space radius.
        foreach (var action in (detail.Actions ?? []).Where(row =>
                     row.ActionId == ArcheiaHarmoniasMechanics.HealingPulseActionId &&
                     row.Healing > 0 && row.SourceX.HasValue && row.SourceZ.HasValue &&
                     ArcheiaHarmoniasMechanics.EvidenceAlpha(row.ElapsedMs, now) > 0))
        {
            var at = point(action.SourceX!.Value, action.SourceZ!.Value);
            var alpha = ArcheiaHarmoniasMechanics.EvidenceAlpha(action.ElapsedMs, now);
            var color = ImGui.ColorConvertFloat4ToU32(new Vector4(.22f, .94f, .48f,
                .38f + .58f * alpha));
            draw.AddCircle(at, 8f, color, 20, 2.2f);
            draw.AddCircle(at, 13f, color, 24, 1.4f);
            draw.AddText(at + new Vector2(16, -9), color,
                string.Format(CultureInfo.InvariantCulture, T("ccHarmoniasHealingPulse"),
                    action.Healing));
        }
    }

    private static void DrawHarmoniasRouteSegment(ImDrawListPtr draw, Vector2 from,
        Vector2 to, uint color, bool unlocked)
    {
        if (unlocked)
        {
            draw.AddLine(from, to, 0xB0000000, 7f);
            draw.AddLine(from, to, color, 4f);
            return;
        }

        const int segments = 12;
        for (var index = 0; index < segments; index += 2)
        {
            var a = Vector2.Lerp(from, to, index / (float)segments);
            var b = Vector2.Lerp(from, to, (index + 1) / (float)segments);
            draw.AddLine(a, b, color, 2.2f);
        }
    }

    private string ArcheiaHarmoniasStatusText(
        ArcheiaHarmoniasMechanics.AetherometerSnapshot aetherometer,
        ArcheiaHarmoniasMechanics.CheckpointSnapshot checkpoint)
    {
        var state = aetherometer.State switch
        {
            ArcheiaHarmoniasMechanics.AetherometerState.Unobserved => T("notObserved"),
            ArcheiaHarmoniasMechanics.AetherometerState.Idle => T("ccHarmoniasAetherIdle"),
            ArcheiaHarmoniasMechanics.AetherometerState.Reset => T("ccHarmoniasAetherReset"),
            ArcheiaHarmoniasMechanics.AetherometerState.Unlocked => T("ccHarmoniasRoutesUnlocked"),
            _ => T("ccHarmoniasAetherAttemptObserved")
        };
        return $"{T("ccHarmoniasAetherometer")} {state} · " +
               $"{T("ccHarmoniasCheckpoint")} {ArcheiaHarmoniasCheckpointText(checkpoint)}";
    }

    private string ArcheiaHarmoniasCheckpointText(
        ArcheiaHarmoniasMechanics.CheckpointSnapshot snapshot) => snapshot.State switch
    {
        ArcheiaHarmoniasMechanics.CheckpointState.Cleared => T("ccHarmoniasCheckpointCleared"),
        ArcheiaHarmoniasMechanics.CheckpointState.Clearing => string.Format(
            CultureInfo.InvariantCulture, T("ccHarmoniasCheckpointClearing"),
            snapshot.ProgressPercent),
        ArcheiaHarmoniasMechanics.CheckpointState.Reset => T("ccHarmoniasCheckpointReset"),
        ArcheiaHarmoniasMechanics.CheckpointState.Idle => T("ccHarmoniasCheckpointIdle"),
        _ => T("notObserved")
    };

    /// <summary>
    /// Draws only directly observed Clockwork Castletown evidence. The archive
    /// currently contains effect packets rather than cast-start packets, so
    /// these overlays explain an observed result and never claim to be a
    /// predictive telegraph or a legal-range adjudicator.
    /// </summary>
    private void DrawClockworkCastletownMechanics(ImDrawListPtr draw, DetailedAnalysis detail,
        long now, Func<double, double, Vector2> point)
    {
        var floorCells = (detail.ObjectiveDiscovery ?? [])
            .Where(row => row.ElapsedMs <= now && row.Loaded &&
                          row.BaseId == ClockworkCastletownMechanics.TrickFloorBaseId)
            .GroupBy(row => row.ObjectId)
            .Select(group => group.OrderByDescending(row => row.ElapsedMs).First())
            .Select(row => new ClockworkCastletownMechanics.TatamiCell(row.ObjectId, row.X, row.Z))
            .ToArray();
        var activeTatami = (detail.Actions ?? [])
            .Where(row => row.ActionId == ClockworkCastletownMechanics.TatamiTwistActionId &&
                          row.SourceX.HasValue && row.SourceZ.HasValue &&
                          ClockworkCastletownMechanics.EvidenceAlpha(row.ElapsedMs, now) > 0)
            .Select(row => (X: row.SourceX!.Value, Z: row.SourceZ!.Value, row.ElapsedMs))
            .ToArray();
        var neutralFill = ImGui.ColorConvertFloat4ToU32(new Vector4(.23f, .27f, .29f, .16f));
        var neutralEdge = ImGui.ColorConvertFloat4ToU32(new Vector4(.72f, .76f, .78f, .66f));
        foreach (var cross in ClockworkCastletownMechanics.FindCompleteTatamiCrosses(floorCells))
        foreach (var cell in cross.Cells)
        {
            var half = ClockworkCastletownMechanics.TatamiTileSizeYalms / 2d;
            var a = point(cell.X - half, cell.Z - half);
            var b = point(cell.X + half, cell.Z - half);
            var c = point(cell.X + half, cell.Z + half);
            var d = point(cell.X - half, cell.Z + half);
            var activations = activeTatami
                .OrderBy(value => Math.Abs(value.ElapsedMs - now))
                .Where(value => Math.Pow(value.X - cell.X, 2) +
                                Math.Pow(value.Z - cell.Z, 2) <= 2.25)
                .ToArray();
            var active = activations.Length > 0;
            var activation = active ? activations[0] : default;
            var alpha = active
                ? ClockworkCastletownMechanics.EvidenceAlpha(activation.ElapsedMs, now)
                : 0f;
            var fill = active
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(.18f, .88f, .43f, .18f + .30f * alpha))
                : neutralFill;
            var edge = active
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(.20f, 1f, .49f, .62f + .34f * alpha))
                : neutralEdge;
            draw.AddQuadFilled(a, b, c, d, fill);
            draw.AddQuad(a, b, c, d, edge, active ? 2.4f : 1.1f);
            if (!active) continue;
            var center = point(cell.X, cell.Z);
            const float marker = 6f;
            draw.AddQuadFilled(center + new Vector2(0, -marker), center + new Vector2(marker, 0),
                center + new Vector2(0, marker), center + new Vector2(-marker, 0), edge);
            draw.AddText(center + new Vector2(9, -15), edge, T("ccClockworkTatamiActive"));
        }

        var effectGroups = (detail.Actions ?? [])
            .Where(row => (ClockworkCastletownMechanics.ClassifyAction(row.ActionId) ==
                               ClockworkCastletownMechanics.HazardKind.YojimboStrike ||
                           ClockworkCastletownMechanics.ClassifyAction(row.ActionId) ==
                               ClockworkCastletownMechanics.HazardKind.OnmyojiGaze) &&
                          row.SourceX.HasValue && row.SourceZ.HasValue &&
                          ClockworkCastletownMechanics.EvidenceAlpha(row.ElapsedMs, now) > 0)
            .GroupBy(row => (row.ActionId, row.ElapsedMs, row.SourceEntityId));
        foreach (var group in effectGroups)
        {
            var first = group.First();
            var sourceX = first.SourceX!.Value;
            var sourceZ = first.SourceZ!.Value;
            var source = point(sourceX, sourceZ);
            var targets = group.Where(row => row.TargetX.HasValue && row.TargetZ.HasValue)
                .Select(row => (X: row.TargetX!.Value, Z: row.TargetZ!.Value)).ToArray();
            var radius = ClockworkCastletownMechanics.ObservedHitRadius(sourceX, sourceZ, targets);
            var alpha = ClockworkCastletownMechanics.EvidenceAlpha(first.ElapsedMs, now);
            var gaze = first.ActionId == ClockworkCastletownMechanics.OnmyojiGazeActionId;
            var color = ImGui.ColorConvertFloat4ToU32(gaze
                ? new Vector4(.72f, .42f, 1f, .28f + .68f * alpha)
                : new Vector4(1f, .34f, .18f, .28f + .68f * alpha));
            draw.AddCircle(source, 7f, color, 20, 2f);
            foreach (var targetValue in targets)
            {
                var target = point(targetValue.X, targetValue.Z);
                draw.AddLine(source, target, color, 1.5f);
                draw.AddCircle(target, 5f, color, 16, 1.7f);
            }
            if (radius > .5)
            {
                var screenRadius = Vector2.Distance(source, point(sourceX + radius, sourceZ));
                DrawDashedCircle(source, screenRadius, color);
            }
            var remaining = Math.Max(0,
                ClockworkCastletownMechanics.ReplayEvidenceWindowMs - Math.Abs(now - first.ElapsedMs)) / 1000d;
            var title = T(gaze ? "ccClockworkOnmyojiGaze" : "ccClockworkYojimboStrike");
            draw.AddText(source + new Vector2(10, -18), color, title);
            draw.AddText(source + new Vector2(10, -3), color,
                string.Format(CultureInfo.InvariantCulture, T("ccClockworkObservedWindow"),
                    remaining, radius));
        }

        void DrawDashedCircle(Vector2 center, float radius, uint color)
        {
            const int segments = 32;
            for (var index = 0; index < segments; index += 2)
            {
                var a = MathF.Tau * index / segments;
                var b = MathF.Tau * (index + 1) / segments;
                draw.AddLine(center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius,
                    center + new Vector2(MathF.Cos(b), MathF.Sin(b)) * radius, color, 1.5f);
            }
        }
    }

    private static bool IsCrystallineCrystal(ObjectiveStateEvent row) =>
        row.EvidenceScope == "cc_loaded_tactical_crystal_position_v1" ||
        row.Label == "cc_tactical_crystal";

    private string ObjectiveDisplayName(uint dutyId, string label)
    {
        label ??= string.Empty;
        var normalized = label.Trim();
        var ccKey = normalized switch
        {
            "cc_tactical_crystal" => "ccTacticalCrystal",
            "cc_medicine_kit" => "ccMedicineKit",
            "cc_swift_sprint_zone" => "ccSwiftSprintZone",
            "cc_cloudnine_black_feather" => "ccCloudNineBlackFeather",
            "cc_cloudnine_twister" => "ccCloudNineTwister",
            "cc_cloudnine_gale_sphere" => "ccCloudNineGaleSphere",
            "cc_bomb_core" => "ccBombCore",
            "cc_trick_floor" => "ccTrickFloor",
            "cc_redsands_geyser" => "ccRedSandsGeyser",
            "cc_redsands_antlion" => "ccRedSandsAntlion",
            "cc_redsands_sabotender" => "ccRedSandsSabotender",
            "cc_redsands_acutender" => "ccRedSandsAcutender",
            "cc_kageyama" => "ccKageyama",
            "cc_clockwork_yojimbo" => "ccClockworkYojimbo",
            "cc_clockwork_onmyoji" => "ccClockworkOnmyoji",
            "cc_harmonias_healing_glyph" => "ccHarmoniasHealingGlyph",
            _ => string.Empty
        };
        // Old cached Red Sands captures used the former global BaseId 8062
        // mapping. Territory evidence repairs the display without rewriting
        // the archived observation.
        if (normalized == "cc_trick_floor" && currentTerritoryId is 1138 or 1139)
            ccKey = "ccRedSandsGeyser";
        if (ccKey.Length > 0) return T(ccKey);
        if (dutyId == 127)
        {
            if (label.Contains("Command System", StringComparison.OrdinalIgnoreCase))
                return T("secureCommandSystem");
            if (label.Contains("Personnel Displacement", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("Displacement Panel", StringComparison.OrdinalIgnoreCase))
                return T("secureDisplacementPanel");
            if (label.Contains("Interceptor Drone", StringComparison.OrdinalIgnoreCase))
                return T(label.Contains('\u03B1') || label.Contains("alpha", StringComparison.OrdinalIgnoreCase)
                    ? "secureGoldInterceptorDrone" : "secureInterceptorDrone");
            if (label.Contains("Interceptor Node", StringComparison.OrdinalIgnoreCase))
                return T("secureInterceptorNode");
            if (SecureObjectiveCatalog.TryResolveNode(label, out var secureNode))
                return secureNode.Code;
        }
        var location = ObjectiveLocationCode(normalized);
        if (dutyId is 130 or 701 or 1080 && location.Length > 0)
            return location;
        return normalized;
    }

    private static bool IsSecureOccupationPoint(string label) =>
        label.Contains("Outpost", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("Allagan Manors", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("Allagan Markets", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("Allagan Sun Temple", StringComparison.OrdinalIgnoreCase);

    private static string ObjectiveLocationCode(string label)
    {
        foreach (var token in label.Split([' ', '·', ':', '：', '\r', '\n', '\t'],
                     StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var trimmed = token.Trim('(', ')', '[', ']', '{', '}', ',', '.', '。');
            if (trimmed.Length is < 2 or > 4 || !char.IsLetter(trimmed[0])) continue;
            if (trimmed.Skip(1).All(char.IsDigit)) return trimmed.ToUpperInvariant();
        }
        return string.Empty;
    }

    private static bool IsRawObjectiveIdentifier(string? label) =>
        string.IsNullOrWhiteSpace(label) || label.StartsWith("icon:", StringComparison.OrdinalIgnoreCase) ||
        label[0] == '#';

    private static bool IsObjectiveMetadataLabel(string? label)
    {
        label ??= string.Empty;
        return ObjectiveLocationCode(label).Length == 0 &&
               (label.Contains("Occupied", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("Data Remaining", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("\u5360\u9886", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("\u4f54\u9818", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("\u6b8b\u91cf", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("\u6b98\u91cf", StringComparison.OrdinalIgnoreCase));
    }

    private static double? ObjectivePercentFromLabel(string label)
    {
        var percent = label.IndexOf('%');
        if (percent <= 0) return null;
        var start = percent - 1;
        while (start >= 0 && (char.IsDigit(label[start]) || label[start] == '.')) start--;
        var token = label[(start + 1)..percent];
        return double.TryParse(token, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 100) : null;
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
        for (var i = 0; i < label.Length; i++)
            if ((label[i] is 'S' or 'A' or 'B') &&
                (i == 0 || !char.IsLetter(label[i - 1])) &&
                (i + 1 == label.Length || !char.IsLetter(label[i + 1]))) return label[i];
        return '\0';
    }

    private static string ObjectiveOrdinal(string label)
    {
        var rank = ObjectiveRank(label);
        if (rank == '\0') return string.Empty;
        var start = label.LastIndexOf(rank);
        var digits = new string(label.Skip(start + 1).TakeWhile(char.IsDigit).ToArray());
        return digits;
    }

    private static void DrawRegularPolygon(ImDrawListPtr draw, Vector2 center, float radius, int sides,
        uint color, float thickness, float rotation = -MathF.PI / 2)
    {
        for (var i = 0; i < sides; i++)
        {
            var from = center + Polar(radius, rotation + i * MathF.Tau / sides);
            var to = center + Polar(radius, rotation + (i + 1) * MathF.Tau / sides);
            draw.AddLine(from, to, color, thickness);
        }
    }

    private static void DrawObjectiveGlyph(ImDrawListPtr draw, Vector2 point, uint dutyId, string label,
        uint maxHp, Vector4 color, bool capturing)
    {
        if (label.Equals("cc_medicine_kit", StringComparison.OrdinalIgnoreCase))
        {
            var green = ImGui.ColorConvertFloat4ToU32(new Vector4(.24f, .88f, .46f, 1f));
            draw.AddLine(point + new Vector2(-5, 0), point + new Vector2(5, 0), green, 3f);
            draw.AddLine(point + new Vector2(0, -5), point + new Vector2(0, 5), green, 3f);
            return;
        }
        if (label.Equals(ArcheiaHarmoniasMechanics.HealingGlyphLabel,
                StringComparison.OrdinalIgnoreCase))
        {
            var green = ImGui.ColorConvertFloat4ToU32(new Vector4(.20f, .94f, .46f, 1f));
            draw.AddCircle(point, 8f, green, 24, 2f);
            draw.AddLine(point + new Vector2(-5, 0), point + new Vector2(5, 0), green, 2.5f);
            draw.AddLine(point + new Vector2(0, -5), point + new Vector2(0, 5), green, 2.5f);
            return;
        }
        if (label.Equals("cc_cloudnine_black_feather", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Black Chocobo Feather", StringComparison.OrdinalIgnoreCase))
        {
            var gold = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .78f, .24f, 1f));
            draw.AddLine(point + new Vector2(-6, 7), point + new Vector2(6, -7), gold, 2.5f);
            draw.AddLine(point + new Vector2(-4, 3), point + new Vector2(-8, -1), gold, 1.6f);
            draw.AddLine(point + new Vector2(0, -1), point + new Vector2(-3, -6), gold, 1.6f);
            draw.AddLine(point + new Vector2(3, -4), point + new Vector2(8, -2), gold, 1.6f);
            return;
        }
        if (label.Equals("cc_cloudnine_twister", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Turbulence", StringComparison.OrdinalIgnoreCase))
        {
            var cyan = ImGui.ColorConvertFloat4ToU32(new Vector4(.36f, .86f, 1f, 1f));
            draw.AddCircle(point + new Vector2(0, -4), 7, cyan, 18, 2f);
            draw.AddCircle(point + new Vector2(0, 2), 5, cyan, 16, 2f);
            draw.AddLine(point + new Vector2(-3, 8), point + new Vector2(3, 8), cyan, 2f);
            return;
        }
        if (label.Equals("cc_cloudnine_gale_sphere", StringComparison.OrdinalIgnoreCase))
        {
            var turquoise = ImGui.ColorConvertFloat4ToU32(new Vector4(.12f, .96f, .88f, 1f));
            draw.AddCircleFilled(point, 6f, turquoise, 20);
            draw.AddCircle(point, 9f, turquoise, 24, 2f);
            draw.AddCircle(point, 12f, ImGui.ColorConvertFloat4ToU32(
                new Vector4(.12f, .96f, .88f, .36f)), 24, 1.5f);
            return;
        }
        var packed = ImGui.ColorConvertFloat4ToU32(color);
        var rank = ObjectiveRank(label);
        var sides = dutyId is 130 or 701 or 1080
            ? rank switch { 'S' => 5, 'A' => 4, 'B' => 3, _ => 0 }
            : 0;
        if (sides > 0) DrawRegularPolygon(draw, point, 11, sides, packed, 3f,
            sides == 4 ? MathF.PI / 4 : -MathF.PI / 2);
        else
        {
            // Shatter objectives remain circular, but their visual size reflects the
            // observed ice tier. Prefer decoded names; HP is only a conservative
            // fallback because patch-specific HP values must not be hard-coded.
            var radius = dutyId == 180 ? IceObjectiveRadius(label, maxHp) : 11f;
            draw.AddCircle(point, radius, packed, 24, 3f);
        }
        if (capturing)
            for (var segment = 0; segment < 16; segment += 2)
                draw.AddLine(point + Polar(14, segment * MathF.PI / 8),
                    point + Polar(14, (segment + 1) * MathF.PI / 8), packed, 2f);
    }

    private static float IceObjectiveRadius(string label, uint maxHp)
    {
        if (label.Contains("large", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("\u5927\u51b0", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("\u5927\u578b", StringComparison.OrdinalIgnoreCase)) return 15f;
        if (label.Contains("small", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("\u5c0f\u51b0", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("\u5c0f\u578b", StringComparison.OrdinalIgnoreCase)) return 7f;
        // A non-zero HP confirms an attackable ice target, but cannot by itself
        // prove the tier across client versions; use the neutral middle radius.
        return maxHp > 0 ? 11f : 9f;
    }

    private void DrawObjectiveProgressBar(ImDrawListPtr draw, Vector2 at, double percent, Vector4 color)
    {
        const float width = 36, height = 5;
        var background = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.ChartBackground(configuration.UiTheme));
        draw.AddRectFilled(at, at + new Vector2(width, height), background, 2);
        draw.AddRectFilled(at, at + new Vector2(width * (float)Math.Clamp(percent / 100d, 0, 1), height),
            ImGui.ColorConvertFloat4ToU32(color), 2);
        var text = $"{percent:0}%";
        draw.AddText(at + new Vector2(width + 3, -6), ImGui.ColorConvertFloat4ToU32(TextColor), text);
    }

    private void DrawMapZoomSlider(Vector2 origin, Vector2 size, Vector2 restoreCursor)
    {
        // Render after the canvas so the narrow overlay owns mouse input. Forty percent
        // of the map height keeps the drag distance short without obscuring the replay.
        var sliderHeight = Math.Max(90, size.Y * .40f);
        ImGui.SetCursorScreenPos(origin + new Vector2(size.X - 22, (size.Y - sliderHeight) * .5f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, BattlefieldUiTheme.Panel(configuration.UiTheme) with { W = .88f });
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Cyan);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Cyan with { W = .92f });
        ImGui.VSliderFloat("##movement-map-zoom", new Vector2(15, sliderHeight), ref mapZoom, 1f, 8f, "");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{T("mapZoom")} {mapZoom:0.0}×");
        ImGui.PopStyleColor(3);
        ImGui.SetCursorScreenPos(restoreCursor);
    }

    private static Vector4 MapTeamColor(LocalMatchAnalysis match, byte team)
    {
        if (match.Schema == LocalMatchAnalyzer.CrystallineSchema)
            return team switch
            {
                0 => new Vector4(.24f, .62f, 1f, 1f), // Astra / 星极
                1 => new Vector4(.95f, .27f, .30f, 1f), // Umbra / 灵极
                _ => new Vector4(.62f, .66f, .70f, 1f)
            };
        return team < TeamColors.Length ? TeamColors[team] : new Vector4(.62f, .66f, .70f, 1f);
    }

    private string MapTeamName(LocalMatchAnalysis match, byte team) =>
        match.Schema == LocalMatchAnalyzer.CrystallineSchema
            ? team switch { 0 => T("ccAstra"), 1 => T("ccUmbra"), _ => T("unknownTeam") }
            : TeamName(team);

    private void DrawMarkerHistory(DetailedAnalysis detail)
    {
        DrawPanelTitle(T("markerHistory"), T("markerEvidenceV2"));
        ImGui.TextWrapped(T("markerNearbyDisclaimer"));
        ImGui.Spacing();
        if (!ImGui.BeginTable("marker-history-columns", 2,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.Resizable)) return;
        ImGui.TableSetupColumn("field-marker-column", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("target-marker-column", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(Cyan, T("fieldMarkers"));
        DrawFieldMarkerTable(detail.FieldMarkers ?? []);
        ImGui.TableNextColumn();
        ImGui.TextColored(Cyan, T("targetMarkers"));
        DrawTargetMarkerTable(detail.TargetMarkers ?? []);
        ImGui.EndTable();
    }

    private void DrawFieldMarkerTable(IReadOnlyList<FieldMarkerTimelineEvent> rows)
    {
        if (rows.Count == 0) { ImGui.TextDisabled(T("noMarkerEvents")); return; }
        if (!ImGui.BeginTable("field-marker-events", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 510))) return;
        ImGui.TableSetupColumn(T("time"), ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn(T("markerType"), ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn(T("markerOperation"), ImGuiTableColumnFlags.WidthFixed, 94);
        ImGui.TableSetupColumn(T("markerPosition"), ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn(T("nearActorCandidate"), ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn(T("markerOperator"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        foreach (var row in rows.OrderByDescending(x => x.ElapsedMs))
        {
            ImGui.TableNextRow();
            if (IsPlayerFocused(row.NearbyActorName, row.NearbyActorWorldId,
                    row.NearbyActorEntityId))
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(.16f, .62f, .58f, .24f)));
            ImGui.TableNextColumn(); DrawCopyableText(Clock(row.ElapsedMs));
            ImGui.TableNextColumn(); DrawMarkerBadge(row.MarkerKey, true, row.MarkerIndex);
            ImGui.TableNextColumn(); DrawCopyableText(MarkerOperation(row.Change));
            ImGui.TableNextColumn(); DrawCopyableText($"X {row.X:0.0} · Y {row.Y:0.0} · Z {row.Z:0.0}");
            ImGui.TableNextColumn();
            if (row.PlacementRelation == "near_actor_candidate" && !string.IsNullOrWhiteSpace(row.NearbyActorName))
            {
                if (DrawCompactFocusButton($"field-marker-focus-{row.MarkerIndex}-{row.ElapsedMs}"))
                {
                    ToggleFocus(row.NearbyActorName, row.NearbyActorWorldId, row.NearbyActorEntityId);
                    selectedMs = row.ElapsedMs;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("focusMarkedPlayer"));
                ImGui.SameLine(0, 4);
                DrawJobIcon(row.NearbyActorJobId); ImGui.SameLine(0, 4);
                var world = row.NearbyActorWorldId == 0 ? string.Empty : "@" + WorldName(row.NearbyActorWorldId);
                DrawCopyableText($"{row.NearbyActorName}{world} · {row.NearbyActorDistance:0.00}m");
            }
            else ImGui.TextDisabled("—");
            ImGui.TableNextColumn(); DrawMarkerOperator(row.OperatorEntityId, row.OperatorName,
                row.OperatorWorldId, row.OperatorJobId, row.OperatorTeam, row.OperatorAlliance,
                row.OperatorEvidence, row.ElapsedMs);
        }
        ImGui.EndTable();
    }

    private void DrawTargetMarkerTable(IReadOnlyList<TargetMarkerTimelineEvent> rows)
    {
        var visibleRows = rows.Where(x => IsRecordableMarkerTarget(x.TargetEntityId)).ToArray();
        if (visibleRows.Length == 0) { ImGui.TextDisabled(T("noMarkerEvents")); return; }
        if (!ImGui.BeginTable("target-marker-events", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 510))) return;
        ImGui.TableSetupColumn(T("time"), ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn(T("markerType"), ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn(T("markerOperation"), ImGuiTableColumnFlags.WidthFixed, 94);
        ImGui.TableSetupColumn(T("target"), ImGuiTableColumnFlags.WidthStretch, 1.8f);
        ImGui.TableSetupColumn(T("markerOperator"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        foreach (var row in visibleRows.OrderByDescending(x => x.ElapsedMs))
        {
            ImGui.TableNextRow();
            if (IsPlayerFocused(row.TargetName, row.TargetWorldId, row.TargetEntityId))
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(.16f, .62f, .58f, .24f)));
            ImGui.TableNextColumn(); DrawCopyableText(Clock(row.ElapsedMs));
            ImGui.TableNextColumn(); DrawMarkerBadge(row.MarkerKey, false, row.MarkerIndex);
            ImGui.TableNextColumn(); DrawCopyableText(MarkerOperation(row.Change));
            ImGui.TableNextColumn();
            if (row.TargetJobId != 0 || row.TargetWorldId != 0)
                DrawActorIdentity(row.TargetJobId, row.TargetName, row.TargetWorldId,
                    row.TargetTeam, row.TargetAlliance, entityId: row.TargetEntityId,
                    focusElapsedMs: row.ElapsedMs);
            else
            {
                var label = !string.IsNullOrWhiteSpace(row.TargetName) ? row.TargetName
                    : row.TargetBaseId != 0 ? $"{T("mechanismObject")} #{row.TargetBaseId}"
                    : $"{T("loadedObject")} 0x{row.TargetEntityId:X8}";
                DrawCopyableText(label);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{row.TargetObjectKind} · {T(row.TargetNameEvidence)}");
            }
            ImGui.TableNextColumn(); DrawMarkerOperator(row.OperatorEntityId, row.OperatorName,
                row.OperatorWorldId, row.OperatorJobId, row.OperatorTeam, row.OperatorAlliance,
                row.OperatorEvidence, row.ElapsedMs);
        }
        ImGui.EndTable();
    }

    private static bool IsRecordableMarkerTarget(uint targetEntityId) =>
        targetEntityId != 0 && targetEntityId != 0xE0000000u;

    private void DrawMarkerOperator(uint entityId, string name, ushort worldId, byte jobId,
        byte team, byte alliance, string evidence, long elapsedMs)
    {
        // Only a local request or a versioned, controlled-trial-verified network
        // source may expose an operator. Historical packet-shaped candidates
        // remain intentionally hidden.
        if (evidence is not ("local_command_verified" or "network_target_marker_verified" or
            "network_field_marker_operator_verified_v1") ||
            entityId == 0 || string.IsNullOrWhiteSpace(name))
        {
            ImGui.TextDisabled(T("operatorUnavailable"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T(evidence));
            return;
        }
        DrawActorIdentity(jobId, name, worldId, team, alliance, entityId: entityId,
            focusElapsedMs: elapsedMs);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T(evidence));
    }

    private string MarkerOperation(string change) => change switch
    {
        "snapshot" => T("markerSnapshot"), "apply" => T("markerApply"),
        "move" => T("markerMove"), "remove" => T("markerRemove"), _ => change
    };

    private void DrawMarkerBadge(string markerKey, bool field, int markerIndex)
    {
        var size = new Vector2(32, 24); var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##marker-badge-{field}-{markerKey}-{copyFieldSequence++}", size);
        var normalized = MarkerUiPrimitives.NormalizeKey(markerKey, field, markerIndex);
        DrawMarkerIcon(ImGui.GetWindowDrawList(), origin + size / 2, normalized, field, 10f,
            markerIndex);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(normalized);
    }

    private void DrawMarkerIcon(ImDrawListPtr draw, Vector2 center, string markerKey,
        bool field, float radius, int markerIndex = -1)
    {
        var iconId = MarkerUiPrimitives.IconId(markerKey, field, markerIndex);
        if (iconId != 0)
        {
            try
            {
                if (!markerIcons.TryGetValue(iconId, out var texture))
                {
                    var bundled = Path.Combine(assetRoot, "assets", "markers",
                        MarkerUiPrimitives.AssetFileName(iconId));
                    markerIcons[iconId] = texture = File.Exists(bundled)
                        ? textureProvider.GetFromFile(bundled)
                        : textureProvider.GetFromGameIcon(new GameIconLookup(iconId, hiRes: true));
                }
                var wrap = texture.GetWrapOrDefault();
                if (wrap != null)
                {
                    var extent = new Vector2(radius * 2.35f);
                    draw.AddImage(wrap.Handle, center - extent / 2, center + extent / 2);
                    return;
                }
            }
            catch { /* Unknown/corrupt assets are shown as missing, never as a different marker. */ }
            return;
        }
        draw.AddCircle(center, radius, ImGui.ColorConvertFloat4ToU32(Muted), 16, 1.5f);
        draw.AddText(center - ImGui.CalcTextSize("?") / 2,
            ImGui.ColorConvertFloat4ToU32(Muted), "?");
    }

    private static Vector2 Polar(float radius, float angle) =>
        new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);

    private void DrawScoreTimeline(DetailedAnalysis detail, Vector2 requested)
    {
        var size = new Vector2(Math.Max(300, requested.X <= 0 ? ImGui.GetContentRegionAvail().X : requested.X), requested.Y);
        var origin = ImGui.GetCursorScreenPos(); ImGui.InvisibleButton("score-timeline", size);
        var draw = ImGui.GetWindowDrawList(); DrawChartBackground(draw, origin, size);
        if (detail.ScoreTimeline.Count == 0) { DrawCentered(draw, origin, size, T("noScore"), Muted); return; }
        var end = Math.Max(1, detail.ScoreTimeline[^1].ElapsedMs);
        var maximum = Math.Max(1, detail.ScoreTimeline.Max(x =>
            Math.Max((int)x.Maelstrom, Math.Max((int)x.Adders, x.Flames))));
        var tickStep = NiceScoreStep(maximum / 4f);
        var axisMaximum = Math.Max(tickStep, (int)MathF.Ceiling(maximum / (float)tickStep) * tickStep);
        const float left = 45, right = 20, top = 31, bottom = 22;
        var plotHeight = size.Y - top - bottom;
        var gridColor = ImGui.ColorConvertFloat4ToU32(new Vector4(.55f, .62f, .66f, .18f));
        var labelColor = ImGui.ColorConvertFloat4ToU32(new Vector4(.66f, .71f, .74f, .72f));
        for (var score = 0; score <= axisMaximum; score += tickStep)
        {
            var y = origin.Y + size.Y - bottom - score / (float)axisMaximum * plotHeight;
            draw.AddLine(new Vector2(origin.X + left, y), new Vector2(origin.X + size.X - right, y), gridColor);
            var label = score.ToString(CultureInfo.InvariantCulture);
            var labelSize = ImGui.CalcTextSize(label);
            draw.AddText(new Vector2(origin.X + left - labelSize.X - 7, y - labelSize.Y / 2), labelColor, label);
        }
        Vector2 Point(long ms, short score) => new(origin.X + left + ms / (float)end * (size.X - left - right),
            origin.Y + size.Y - bottom - score / (float)axisMaximum * plotHeight);
        for (byte team = 0; team < 3; team++)
        {
            Vector2? previous = null;
            foreach (var row in detail.ScoreTimeline)
            {
                var value = team == 0 ? row.Maelstrom : team == 1 ? row.Adders : row.Flames;
                var point = Point(row.ElapsedMs, value);
                if (previous != null) draw.AddLine(previous.Value, point, ImGui.ColorConvertFloat4ToU32(TeamColors[team]), 2.2f);
                previous = point;
            }
            var last = detail.ScoreTimeline[^1];
            var score = team == 0 ? last.Maelstrom : team == 1 ? last.Adders : last.Flames;
            draw.AddText(origin + new Vector2(12 + team * 145, 7), ImGui.ColorConvertFloat4ToU32(TeamColors[team]),
                $"{TeamName(team)} {score}");
        }
        var cursor = origin.X + left + Math.Clamp(selectedMs / end, 0, 1) * (size.X - left - right);
        draw.AddLine(new Vector2(cursor, origin.Y + top), new Vector2(cursor, origin.Y + size.Y - bottom),
            ImGui.ColorConvertFloat4ToU32(Cyan), 2.4f);
    }

    private static int NiceScoreStep(float raw)
    {
        if (raw <= 1) return 1;
        var magnitude = MathF.Pow(10, MathF.Floor(MathF.Log10(raw)));
        var normalized = raw / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return Math.Max(1, (int)(nice * magnitude));
    }

    private void DrawHealthFlow(LocalMatchAnalysis match, DetailedAnalysis detail, Vector2 requested)
    {
        var size = new Vector2(Math.Max(300, requested.X <= 0 ? ImGui.GetContentRegionAvail().X : requested.X), requested.Y);
        var origin = ImGui.GetCursorScreenPos(); ImGui.InvisibleButton("health-flow", size);
        var draw = ImGui.GetWindowDrawList(); DrawChartBackground(draw, origin, size);
        var localCandidates = match.Players.Where(x => x.Name == match.LocalPlayerName).ToArray();
        if (localCandidates.Length != 1) { DrawCentered(draw, origin, size, T("noHealthFlow"), Muted); return; }
        var own = localCandidates[0].Team;
        var objectiveIds = (detail.ObjectiveTimeline ?? []).Select(x => x.ObjectId)
            .Concat((detail.ObjectiveDiscovery ?? []).Select(x => x.ObjectId))
            .Where(x => x != 0).ToHashSet();
        var actionRows = (detail.Actions ?? []).Where(x => x.Damage > 0 &&
            !objectiveIds.Contains(x.TargetEntityId)).ToArray();
        var buckets = new SortedDictionary<long, (long[] Outgoing, long[] Incoming)>();
        foreach (var action in actionRows)
        {
            var bucket = action.ElapsedMs / 2000;
            if (!buckets.TryGetValue(bucket, out var values))
                buckets[bucket] = values = (new long[3], new long[3]);
            if (action.SourceTeam == own && action.TargetTeam < 3 && action.TargetTeam != own)
                values.Outgoing[action.TargetTeam] += action.Damage;
            if (action.TargetTeam == own && action.SourceTeam < 3 && action.SourceTeam != own)
                values.Incoming[action.SourceTeam] += action.Damage;
        }
        if (buckets.Count == 0) { DrawCentered(draw, origin, size, T("noHealthFlow"), Muted); return; }
        var end = Math.Max(1, Math.Max(detail.ScoreTimeline.LastOrDefault()?.ElapsedMs ?? 0,
            actionRows.Max(x => x.ElapsedMs))); var mid = origin.Y + size.Y / 2;
        draw.AddLine(new Vector2(origin.X + 22, mid), new Vector2(origin.X + size.X - 12, mid),
            ImGui.ColorConvertFloat4ToU32(new Vector4(.45f, .52f, .56f, .65f)));
        var maximum = Math.Max(1L, buckets.Values.Select(x => Math.Max(x.Outgoing.Sum(), x.Incoming.Sum())).Max());
        var barWidth = Math.Max(1f, 2000f / end * (size.X - 34) - 1);
        foreach (var (bucket, values) in buckets)
        {
            var x = origin.X + 22 + bucket * 2000 / (float)end * (size.X - 34);
            float outgoingStack = 0, incomingStack = 0;
            for (byte team = 0; team < 3; team++)
            {
                var outgoing = values.Outgoing[team] / (float)maximum * (size.Y / 2 - 28);
                var incoming = values.Incoming[team] / (float)maximum * (size.Y / 2 - 28);
                var color = ImGui.ColorConvertFloat4ToU32(TeamColors[team] with { W = .78f });
                draw.AddRectFilled(new Vector2(x, mid - outgoingStack - outgoing), new Vector2(x + barWidth, mid - outgoingStack), color);
                draw.AddRectFilled(new Vector2(x, mid + incomingStack), new Vector2(x + barWidth, mid + incomingStack + incoming), color);
                outgoingStack += outgoing; incomingStack += incoming;
            }
        }
        if (focusedPlayerName != null)
        {
            var focusBuckets = new Dictionary<long, (long Outgoing, long Incoming)>();
            foreach (var action in actionRows)
            {
                var bucket = action.ElapsedMs / 2000;
                focusBuckets.TryGetValue(bucket, out var value);
                if (IsPlayerFocused(action.SourceOwnerName, action.SourceOwnerWorldId, action.SourceOwnerEntityId) ||
                    IsPlayerFocused(action.SourceName, action.SourceWorldId, action.SourceEntityId)) value.Outgoing += action.Damage;
                if (IsPlayerFocused(action.TargetOwnerName, action.TargetOwnerWorldId, action.TargetOwnerEntityId) ||
                    IsPlayerFocused(action.TargetName, action.TargetWorldId, action.TargetEntityId)) value.Incoming += action.Damage;
                focusBuckets[bucket] = value;
            }
            foreach (var (bucket, value) in focusBuckets)
            {
                var x = origin.X + 22 + bucket * 2000 / (float)end * (size.X - 34);
                var outgoing = value.Outgoing / (float)maximum * (size.Y / 2 - 28);
                var incoming = value.Incoming / (float)maximum * (size.Y / 2 - 28);
                draw.AddRectFilled(new Vector2(x, mid - outgoing), new Vector2(x + Math.Max(2, barWidth), mid),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .94f, .4f, .96f)));
                draw.AddRectFilled(new Vector2(x, mid), new Vector2(x + Math.Max(2, barWidth), mid + incoming),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .38f, .45f, .92f)));
            }
        }
        draw.AddText(origin + new Vector2(10, 8), ImGui.ColorConvertFloat4ToU32(TextColor), T("hpLossUpper"));
        draw.AddText(origin + new Vector2(10, size.Y - 23), ImGui.ColorConvertFloat4ToU32(TextColor), T("hpGainLower"));
        var cursor = origin.X + 22 + Math.Clamp(selectedMs / end, 0, 1) * (size.X - 34);
        draw.AddLine(new Vector2(cursor, origin.Y + 20), new Vector2(cursor, origin.Y + size.Y - 20),
            ImGui.ColorConvertFloat4ToU32(Cyan), 2.4f);
    }

    private void DrawCombatSlice(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        const long windowMs = 5_000;
        var endMs = (long)selectedMs;
        var startMs = Math.Max(0, endMs - windowMs);
        var frames = detail.MovementTimeline
            .Where(x => x.ElapsedMs > startMs && x.ElapsedMs <= endMs).ToArray();
        var startScore = NearestScore(detail, startMs);
        var endScore = NearestScore(detail, endMs);

        ImGui.TextColored(Cyan, $"{T("combatSlice")} · {Clock(startMs)}–{Clock(endMs)}");
        var objectiveIds = (detail.ObjectiveTimeline ?? []).Select(x => x.ObjectId)
            .Concat((detail.ObjectiveDiscovery ?? []).Select(x => x.ObjectId))
            .Where(x => x != 0).ToHashSet();
        var actions = (detail.Actions ?? []).Where(x => x.ElapsedMs > startMs && x.ElapsedMs <= endMs &&
            !objectiveIds.Contains(x.TargetEntityId)).ToArray();
        var localCandidates = match.Players.Where(x => x.Name == match.LocalPlayerName).ToArray();
        byte? localTeam = localCandidates.Length == 1 ? localCandidates[0].Team : null;
        var outgoing = localTeam is byte own
            ? actions.Where(x => x.SourceTeam == own && x.TargetTeam != own && x.Damage > 0).ToArray()
            : actions.Where(x => x.Damage > 0).ToArray();
        var incoming = localTeam is byte ownIncoming
            ? actions.Where(x => x.TargetTeam == ownIncoming && x.SourceTeam != ownIncoming && x.Damage > 0).ToArray()
            : actions.Where(x => x.Damage > 0).ToArray();
        var healing = localTeam is byte ownHealing
            ? actions.Where(x => x.SourceTeam == ownHealing && x.Healing > 0).ToArray()
            : actions.Where(x => x.Healing > 0).ToArray();
        var objectiveEvents = (detail.ObjectiveTimeline ?? []).Where(x => x.ElapsedMs > startMs && x.ElapsedMs <= endMs).ToArray();
        if (ImGui.BeginTable("slice-summary", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
            ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var title in new[] { T("output"), T("damageTaken"), T("healing"), T("downs"), T("objectivesAndScore") })
                ImGui.TableSetupColumn(title);
            ImGui.TableHeadersRow(); ImGui.TableNextRow();
            ImGui.TableNextColumn(); MetricOrUnavailable(outgoing.Sum(x => x.Damage));
            ImGui.TableNextColumn(); MetricOrUnavailable(incoming.Sum(x => x.Damage));
            ImGui.TableNextColumn(); MetricOrUnavailable(healing.Sum(x => x.Healing));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(frames.SelectMany(x => x.Actors ?? []).Count(x => x.FellToZero).ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(objectiveEvents.Length > 0
                ? $"{objectiveEvents.Length} · {ScoreDeltaText(startScore, endScore)}" : ScoreDeltaText(startScore, endScore));
            ImGui.EndTable();
        }

        DrawActionSliceTable("slice-output", T("output"), outgoing);
        DrawActionSliceTable("slice-incoming", T("damageTaken"), incoming);
        DrawActionSliceTable("slice-healing", T("healing"), healing);
        DrawActionSliceTable("slice-control", T("statusesAndActions"),
            actions.Where(x => x.Damage == 0 && x.Healing == 0));

        var actorRows = match.Players.Select(player => new
            {
                Player = player,
                Output = actions.Where(x => ActorMatches(x.SourceName, x.SourceWorldId,
                    x.SourceOwnerName, x.SourceOwnerWorldId, player)).Sum(x => x.Damage),
                Taken = actions.Where(x => ActorMatches(x.TargetName, x.TargetWorldId,
                    x.TargetOwnerName, x.TargetOwnerWorldId, player)).Sum(x => x.Damage),
                Healing = actions.Where(x => ActorMatches(x.SourceName, x.SourceWorldId,
                    x.SourceOwnerName, x.SourceOwnerWorldId, player)).Sum(x => x.Healing),
                Downs = frames.SelectMany(x => x.Actors ?? []).Count(x => x.Name == player.Name &&
                    x.WorldId == player.WorldId && x.FellToZero)
            })
            .Where(x => x.Output > 0 || x.Taken > 0 || x.Healing > 0 || x.Downs > 0)
            .ToArray();
        actorRows = (sliceSortColumn switch
        {
            0 => sliceSortDescending ? actorRows.OrderByDescending(x => x.Player.Team) : actorRows.OrderBy(x => x.Player.Team),
            1 => sliceSortDescending ? actorRows.OrderByDescending(x => x.Player.Name) : actorRows.OrderBy(x => x.Player.Name),
            2 => sliceSortDescending ? actorRows.OrderByDescending(x => x.Player.ClassJobId) : actorRows.OrderBy(x => x.Player.ClassJobId),
            3 => sliceSortDescending ? actorRows.OrderByDescending(x => x.Output) : actorRows.OrderBy(x => x.Output),
            4 => sliceSortDescending ? actorRows.OrderByDescending(x => x.Taken) : actorRows.OrderBy(x => x.Taken),
            5 => sliceSortDescending ? actorRows.OrderByDescending(x => x.Healing) : actorRows.OrderBy(x => x.Healing),
            _ => sliceSortDescending ? actorRows.OrderByDescending(x => x.Downs) : actorRows.OrderBy(x => x.Downs)
        }).ToArray();
        if (actorRows.Length == 0) { ImGui.TextDisabled(T("noVitalsChange")); return; }
        if (ImGui.BeginTable("slice-actors", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, 185)))
        {
            var headers = new[] { T("team"), T("player"), T("job"), T("output"), T("damageTaken"), T("healing"), T("downs") };
            ImGui.TableSetupColumn(headers[0], ImGuiTableColumnFlags.WidthFixed, 82);
            ImGui.TableSetupColumn(headers[1], ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn(headers[2], ImGuiTableColumnFlags.WidthFixed, 34);
            for (var i = 3; i < headers.Length; i++) ImGui.TableSetupColumn(headers[i], ImGuiTableColumnFlags.WidthFixed, 92);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var i = 0; i < headers.Length; i++)
            {
                ImGui.TableSetColumnIndex(i); ImGui.TableHeader(headers[i] + (i == sliceSortColumn ? sliceSortDescending ? " ▼" : " ▲" : ""));
                if (ImGui.IsItemClicked()) { if (i == sliceSortColumn) sliceSortDescending = !sliceSortDescending; else { sliceSortColumn = i; sliceSortDescending = true; } }
            }
            foreach (var row in actorRows)
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (localTeam is byte ownTeam && ownTeam < TeamColors.Length && row.Player.Team == ownTeam)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                        ImGui.ColorConvertFloat4ToU32(TeamColors[ownTeam] with { W = .13f }));
                if (IsPlayerFocused(row.Player))
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(.42f, .34f, .08f, .62f)));
                if (match.Schema == LocalMatchAnalyzer.CrystallineSchema)
                    ImGui.TextColored(MapTeamColor(match, row.Player.Team),
                        T(row.Player.Team == 0 ? "ccAstra" : "ccUmbra"));
                else DrawTeamSquad(row.Player.Team, row.Player.Alliance);
                ImGui.TableNextColumn();
                var focusClicked = DrawCompactFocusButton($"slice-{row.Player.WorldId}-{row.Player.Name}");
                if (focusClicked)
                    SetFocus(row.Player, detail);
                ImGui.SameLine(0, 5); DrawCopyableText($"{row.Player.Name}@{WorldName(row.Player.WorldId)}");
                ImGui.TableNextColumn(); DrawJobIcon(row.Player.ClassJobId);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Output.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Taken.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Healing.ToString("N0"));
                ImGui.TableNextColumn();
                if (row.Downs > 0 && ImGui.SmallButton($"{row.Downs}##downs-{row.Player.WorldId}-{row.Player.Name}"))
                {
                    var death = frames.SelectMany(frame => (frame.Actors ?? [])
                            .Where(actor => actor.Name == row.Player.Name && actor.WorldId == row.Player.WorldId && actor.FellToZero)
                            .Select(actor => (frame, actor)))
                        .OrderByDescending(value => value.frame.ElapsedMs).FirstOrDefault();
                    if (death.actor != null)
                    {
                        selectedDeathEntityId = death.actor.EntityId;
                        selectedDeathMs = death.frame.ElapsedMs;
                        selectDeathTab = true;
                    }
                }
                else if (row.Downs == 0) ImGui.TextUnformatted("0");
            }
            ImGui.EndTable();
        }
    }

    private static bool ActorMatches(string name, ushort worldId, string ownerName,
        ushort ownerWorldId, LocalPlayerAnalysis player) =>
        (name == player.Name && (worldId == 0 || worldId == player.WorldId)) ||
        (ownerName == player.Name && (ownerWorldId == 0 || ownerWorldId == player.WorldId));

    private void MetricOrUnavailable(long value)
    {
        if (value > 0) ImGui.TextUnformatted(value.ToString("N0")); else ImGui.TextDisabled(T("notCaptured"));
    }

    private void DrawActionSliceTable(string id, string title, IEnumerable<ActionSliceEvent> source)
    {
        var rows = source.ToArray();
        Func<ActionSliceEvent, IComparable> key = actionSortColumn switch
        {
            0 => x => x.ElapsedMs,
            1 => x => x.SourceName,
            2 => x => x.ActionId,
            3 => x => x.TargetName,
            4 => x => x.Damage + x.Healing,
            _ => x => x.TargetCurrentHp + ShieldAmount(x.TargetMaxHp, x.TargetShieldPercent)
        };
        rows = (actionSortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key)).ToArray();
        ImGui.TextColored(Cyan, title);
        if (rows.Length == 0) { ImGui.TextDisabled(T("actionsNotCaptured")); return; }
        if (!ImGui.BeginTable(id, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, 150))) return;
        var headers = new[] { T("time"), T("source"), T("action"), T("target"), T("amount"), T("hpAndShield") };
        ImGui.TableSetupColumn(headers[0], ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn(headers[1], ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn(headers[2], ImGuiTableColumnFlags.WidthStretch, 1.15f);
        ImGui.TableSetupColumn(headers[3], ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn(headers[4], ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn(headers[5], ImGuiTableColumnFlags.WidthFixed, 145);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var i = 0; i < headers.Length; i++)
        {
            ImGui.TableSetColumnIndex(i);
            ImGui.TableHeader(headers[i] + (i == actionSortColumn ? actionSortDescending ? " ▼" : " ▲" : string.Empty));
            if (ImGui.IsItemClicked())
            {
                if (i == actionSortColumn) actionSortDescending = !actionSortDescending;
                else { actionSortColumn = i; actionSortDescending = true; }
            }
        }
        foreach (var row in rows)
        {
            ImGui.TableNextRow(); Cell(Clock(row.ElapsedMs));
            if (IsPlayerFocused(row))
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(.42f, .34f, .08f, .62f)));
            ImGui.TableNextColumn(); DrawActionSource(row);
            ImGui.TableNextColumn(); DrawActionIcon(row.ActionId, row.ActionName, row.ActionType, row.EffectKind);
            ImGui.TableNextColumn(); DrawActorIdentity(row.TargetJobId, row.TargetName, row.TargetWorldId,
                row.TargetTeam, row.TargetAlliance, row.TargetOwnerName, row.TargetOwnerWorldId,
                row.TargetOwnerJobId, row.TargetEntityId);
            Cell((row.Damage > 0 ? row.Damage : row.Healing).ToString("N0"));
            ImGui.TableNextColumn();
            DrawHpShieldBar(row.TargetCurrentHp, row.TargetMaxHp, row.TargetShieldPercent,
                new Vector2(136, 16));
        }
        ImGui.EndTable();
    }

    private static uint ShieldAmount(uint maxHp, byte shieldPercent) =>
        CombatUiPrimitives.ShieldAmount(maxHp, shieldPercent);

    private static string FormatHpShield(uint currentHp, uint maxHp, byte shieldPercent)
    {
        return CombatUiPrimitives.FormatHpShield(currentHp, maxHp, shieldPercent);
    }

    private static void DrawHpShieldBar(ImDrawListPtr draw, Vector2 origin, Vector2 size,
        uint currentHp, uint maxHp, byte shieldPercent, bool showText)
    {
        CombatUiPrimitives.DrawHpShieldBar(draw, origin, size, currentHp, maxHp, shieldPercent, showText);
    }

    private static void DrawHpShieldBar(uint currentHp, uint maxHp, byte shieldPercent, Vector2 size)
    {
        var origin = ImGui.GetCursorScreenPos();
        CombatUiPrimitives.DrawHpShieldBar(currentHp, maxHp, shieldPercent, size,
            $"detail-{origin.X:0}-{origin.Y:0}");
    }

    private string ScoreDeltaText(ScorePoint? start, ScorePoint? end)
    {
        if (start == null || end == null) return T("noScoreSample");
        return $"{TeamName(0)} {end.Maelstrom - start.Maelstrom:+#;-#;0} · " +
               $"{TeamName(1)} {end.Adders - start.Adders:+#;-#;0} · " +
               $"{TeamName(2)} {end.Flames - start.Flames:+#;-#;0}";
    }

    private void DrawEvidenceBoundary(DetailedAnalysis detail)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BattlefieldUiTheme.WarningPanel(configuration.UiTheme));
        ImGui.BeginChild("evidence-boundary", new Vector2(0, 78), true);
        ImGui.TextColored(new Vector4(1f, .74f, .36f, 1f), T("evidenceBoundary"));
        ImGui.TextWrapped(T("detailEvidenceNotice"));
        ImGui.EndChild(); ImGui.PopStyleColor();
    }

    private void DrawBehaviors(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        ImGui.TextWrapped(T("behaviorEvidence"));
        var catalog = new (string Title, string Source, bool Ascending, bool CoordinationNote)[]
        {
            ("four_small", "four_small", false, true), ("commander", "commander", false, false),
            ("macro_sender", "macro_sender", false, false), ("solo", "solo", false, false),
            ("disruptor", "disruptor", false, false), ("follow_fire", "follow_fire", false, false),
            ("single_target", "single_target", false, false),
            ("melee_firestarter", "melee_firestarter", false, false),
            ("ranged_firestarter", "ranged_firestarter", false, false),
            // These are deliberately separate from positive playstyle labels.
            // A lower score is worse, so the weakest players appear first.
            ("fragility", "fragility", true, false),
            ("navigation", "navigation", true, false)
        }.ToList();
        if (match.DutyId == 180)
            catalog.Add(("enemy_ice_raider", "enemy_ice_raider", false, false));
        if (match.DutyId == 127)
            catalog.Add(("lower_node_controller", "lower_node_controller", false, false));
        catalog.Add(("edge_displacement_fisher", "edge_displacement_fisher", false, false));
        var width = Math.Max(360, (ImGui.GetContentRegionAvail().X - 10) / 2);
        for (var index = 0; index < catalog.Count; index++)
        {
            if (index % 2 != 0) ImGui.SameLine();
            var item = catalog[index];
            ImGui.BeginChild($"behavior-{item.Title}", new Vector2(width, 225), true);
            ImGui.TextColored(Cyan, T(item.Title));
            if (item.Title == "enemy_ice_raider") ImGui.TextWrapped(T("enemyIceEvidence"));
            if (item.Title == "lower_node_controller") ImGui.TextWrapped(T("lowerNodeEvidence"));
            if (item.Title == "edge_displacement_fisher")
                ImGui.TextWrapped(T("edgeDisplacementFisherEvidence"));
            if (item.Title == "four_small")
            {
                ImGui.TextWrapped(T("coordinationDisclaimer"));
                var groups = (detail.BehaviorGroups ?? []).Where(x => x.ModelScore >= detail.FourSmallDisplayThreshold).ToArray();
                if (groups.Length == 0) ImGui.TextDisabled($"{T("needsEventEvidence")} ({detail.FourSmallDisplayThreshold:0})");
                foreach (var group in groups)
                {
                    ImGui.TextColored(group.Team < TeamColors.Length ? TeamColors[group.Team] : Muted,
                        $"{TeamName(group.Team)} {(char)('A' + group.Alliance % 3)} · {T(string.IsNullOrWhiteSpace(group.CandidateKind) ? "four_core" : group.CandidateKind)} · {T("fourSmallStructure")} {group.ModelScore:0} · {T("burstSync")} {group.BurstSync:0}" +
                        (group.AdvanceSupportLinks > 0
                            ? $" · {T("advanceSupport")} {group.AdvanceSupport:0} ({group.AdvanceSupportLinks})"
                            : string.Empty));
                    if (ImGui.BeginTable($"group-members-{group.Team}-{group.Alliance}", 5,
                        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
                    {
                        foreach (var title in new[] { T("job"), T("player"), "KDA", T("damage"), T("focus") })
                            ImGui.TableSetupColumn(title);
                        ImGui.TableHeadersRow();
                        for (var memberIndex = 0; memberIndex < group.Members.Count; memberIndex++)
                        {
                            var member = group.Members[memberIndex];
                            var player = match.Players.FirstOrDefault(x => x.Name == member.Name && x.WorldId == member.WorldId);
                            var kills = member.Kills != 0 || member.Deaths != 0 || member.Assists != 0 ? member.Kills : player?.Kills ?? 0;
                            var deaths = member.Kills != 0 || member.Deaths != 0 || member.Assists != 0 ? member.Deaths : player?.Deaths ?? 0;
                            var assists = member.Kills != 0 || member.Deaths != 0 || member.Assists != 0 ? member.Assists : player?.Assists ?? 0;
                            var damage = member.Damage != 0 ? member.Damage : player?.DamageDealt ?? 0;
                            ImGui.TableNextRow(); ImGui.TableNextColumn(); DrawJobIcon(member.ClassJobId);
                            ImGui.TableNextColumn(); DrawCopyableText($"{member.Name}@{WorldName(member.WorldId)}");
                            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{kills}/{deaths}/{assists}");
                            ImGui.TableNextColumn(); ImGui.TextUnformatted(damage.ToString("N0"));
                            ImGui.TableNextColumn();
                            if (DrawCompactFocusButton($"group-{group.Team}-{group.Alliance}-{memberIndex}") && player != null)
                                SetFocus(player, detail);
                        }
                        ImGui.EndTable();
                    }
                }
                ImGui.EndChild();
                continue;
            }
            var ranking = match.Players.Select(player =>
                (Player: player, Value: TendenciesFor(match, player).FirstOrDefault(x => x.Key == item.Source)))
                .Where(x => x.Value != null);
            var ranked = (item.Ascending ? ranking.OrderBy(x => x.Value!.Score) : ranking.OrderByDescending(x => x.Value!.Score)).ToArray();
            if (item.CoordinationNote) ImGui.TextWrapped(T("coordinationDisclaimer"));
            if (ranked.Length == 0)
            {
                ImGui.TextDisabled(T("needsEventEvidence"));
                ImGui.EndChild();
                continue;
            }
            if (ImGui.BeginTable($"behavior-table-{item.Title}", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH,
                new Vector2(0, item.CoordinationNote ? 135 : 165)))
            {
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 28);
                ImGui.TableSetupColumn(T("job"), ImGuiTableColumnFlags.WidthFixed, 34);
                ImGui.TableSetupColumn(T("teamAndSquad"), ImGuiTableColumnFlags.WidthFixed, 92);
                ImGui.TableSetupColumn(T("player"));
                ImGui.TableSetupColumn("Logs", ImGuiTableColumnFlags.WidthFixed, 54);
                ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
                for (var i = 0; i < ranked.Length; i++)
                {
                    var row = ranked[i]; ImGui.TableNextRow(); Cell((i + 1).ToString());
                    if (IsPlayerFocused(row.Player))
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                            ImGui.ColorConvertFloat4ToU32(new Vector4(.42f, .34f, .08f, .62f)));
                    ImGui.TableNextColumn(); DrawJobIcon(row.Player.ClassJobId);
                    ImGui.TableNextColumn(); DrawTeamSquad(row.Player.Team, row.Player.Alliance);
                    ImGui.TableNextColumn();
                    if (DrawCompactFocusButton($"{item.Title}-{i}"))
                        SetFocus(row.Player, detail);
                    ImGui.SameLine(0, 5); DrawCopyableText($"{row.Player.Name}@{WorldName(row.Player.WorldId)}");
                    ImGui.TableNextColumn(); ImGui.TextColored(LogsColor(row.Value!.Score), row.Value.Score.ToString("0.0"));
                }
                ImGui.EndTable();
            }
            ImGui.EndChild();
        }
    }

    private void DrawAnomalyCandidates(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        // v7 and earlier movement evidence used an incomplete positive-speed
        // catalog. Keep other historical evidence, but never re-publish those
        // legacy movement rows in the UI.
        var rows = (detail.AnomalyCandidates ?? []).Where(row =>
            (row.Kind != "animation_lock_triple_weave" ||
             row.EvidenceScope.StartsWith("local_request_verified", StringComparison.Ordinal)) &&
            (row.Kind != "movement_envelope_exceeded" ||
             detail.AnomalyModel == UploadService.CurrentAnomalyModel))
            .OrderByDescending(row => row.RiskScore)
            .ThenBy(row => row.PlayerName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        DrawIntegrityStatusBand(detail, rows);
        DrawIntegrityReferenceSnapshotBand();
        if (rows.Length == 0)
        {
            DrawIntegrityOverview(match, detail);
            DrawIntegrityMethodDisclosure(detail);
            return;
        }
        if (selectedAnomalyKey == null || rows.All(row => AnomalyKey(row) != selectedAnomalyKey))
            SelectAnomaly(rows[0], detail);
        DrawIntegrityReviewSummary(rows, detail);
        ImGui.Spacing();
        if (ImGui.TreeNode($"{T("candidateReviewQueue")} ({rows.Length})##candidate-review-queue"))
        {
        ImGui.TextWrapped(T("candidateReviewQueueHelp"));
        if (ImGui.BeginTable("anomaly-table", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(T("player"), ImGuiTableColumnFlags.WidthStretch, 2.1f);
            ImGui.TableSetupColumn(T("anomalyType"), ImGuiTableColumnFlags.WidthStretch, 1.8f);
            ImGui.TableSetupColumn(T("independentEpisodes"), ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn(T("reviewPriority"), ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn(T("dataCompleteness"), ImGuiTableColumnFlags.WidthFixed, 105);
            ImGui.TableSetupScrollFreeze(0,1); ImGui.TableHeadersRow();
            foreach (var row in rows)
            {
                var selected = selectedAnomalyKey == AnomalyKey(row);
                ImGui.TableNextRow();
                if (selected)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                        ImGui.ColorConvertFloat4ToU32(WithAlpha(Cyan, .24f)));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"{(selected ? "●" : "○")}##anomaly-select-{AnomalyKey(row)}"))
                    SelectAnomaly(row, detail);
                ImGui.SameLine(0, 4); DrawJobIcon(row.ClassJobId);
                ImGui.SameLine(0, 5); DrawCopyableText($"{row.PlayerName}@{WorldName(row.WorldId)}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(T(row.Kind));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Occurrences.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(new Vector4(1f, .66f, .25f, 1f), row.RiskScore.ToString("0.0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ConfidenceLabel(row.Confidence));
            }
            ImGui.EndTable();
        }
        ImGui.TreePop();
        }

        var chosen = rows.FirstOrDefault(row => AnomalyKey(row) == selectedAnomalyKey) ?? rows[0];
        DrawSelectedAnomalyEvidence(match, detail, chosen);
        DrawIntegrityOverview(match, detail);
        DrawIntegrityMethodDisclosure(detail);
    }

    private void DrawIntegrityReviewSummary(IReadOnlyList<AnomalyCandidate> rows,
        DetailedAnalysis detail)
    {
        var players = rows.Select(row => (row.PlayerName, row.WorldId)).Distinct().Count();
        var episodes = rows.Sum(row => Math.Max(0, row.Occurrences));
        DrawPanelTitle(T("integrityReviewSummary"), T("integrityReviewSummaryHelp"));
        ImGui.TextColored(Cyan, string.Format(T("integrityReviewSummaryCounts"),
            players, rows.Count, episodes));
        ImGui.TextDisabled(T("integrityReferenceLazyHelp"));

        var columns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 2);
        if (!ImGui.BeginTable("integrity-review-summary-grid", columns,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
            return;
        foreach (var row in rows)
        {
            ImGui.TableNextColumn();
            var selected = selectedAnomalyKey == AnomalyKey(row);
            ImGui.PushID($"integrity-summary-{AnomalyKey(row)}");
            ImGui.PushStyleColor(ImGuiCol.ChildBg, selected
                ? WithAlpha(Cyan, .16f)
                : WithAlpha(BattlefieldUiTheme.ChartBackground(configuration.UiTheme), .86f));
            ImGui.PushStyleColor(ImGuiCol.Border, selected
                ? WithAlpha(Cyan, .72f)
                : WithAlpha(Muted, .42f));
            ImGui.BeginChild("candidate-summary-card", new Vector2(0, 108), true,
                ImGuiWindowFlags.NoScrollbar);
            DrawJobIcon(row.ClassJobId); ImGui.SameLine(0, 6);
            DrawCopyableText($"{row.PlayerName}@{WorldName(row.WorldId)}", selected ? Cyan : Vector4.One);
            ImGui.SameLine(0, 10);
            ImGui.TextColored(new Vector4(1f, .66f, .25f, 1f), T(row.Kind));
            ImGui.SameLine(ImGui.GetWindowWidth() - 132);
            if (ImGui.Button($"{T("inspectCandidateEvidence")}##open", new Vector2(112, 0)))
                SelectAnomaly(row, detail, true);
            ImGui.TextWrapped(AnomalySummary(row));
            ImGui.TextDisabled(string.Format(T("integrityCandidateFacts"), row.Occurrences,
                row.RiskScore.ToString("0.0", CultureInfo.InvariantCulture),
                ConfidenceLabel(row.Confidence)));
            if (row.Samples is { Count: > 0 })
            {
                ImGui.TextDisabled(T("evidenceAnchors")); ImGui.SameLine(0, 6);
                foreach (var sample in row.Samples.OrderBy(x => x.ElapsedMs).Take(4))
                {
                    if (ImGui.SmallButton($"{Clock(sample.ElapsedMs)}##summary-anchor-{sample.ElapsedMs}"))
                    {
                        SelectAnomaly(row, detail, true);
                        selectedMs = sample.ElapsedMs;
                    }
                    ImGui.SameLine(0, 5);
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleColor(2);
            ImGui.PopID();
        }
        ImGui.EndTable();
        ImGui.Spacing();
    }

    private void DrawIntegrityStatusBand(DetailedAnalysis detail, IReadOnlyList<AnomalyCandidate> rows)
    {
        var warning = rows.Count > 0;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, warning
            ? new Vector4(.14f, .075f, .035f, .78f)
            : WithAlpha(BattlefieldUiTheme.ChartBackground(configuration.UiTheme), .84f));
        ImGui.PushStyleColor(ImGuiCol.Border, warning
            ? new Vector4(.72f, .40f, .16f, .55f)
            : WithAlpha(Cyan, .45f));
        ImGui.BeginChild("integrity-status-band", new Vector2(0, 64), true,
            ImGuiWindowFlags.NoScrollbar);
        ImGui.TextColored(warning ? new Vector4(1f,.68f,.28f,1) : Cyan,
            warning ? $"{T("candidateReviewQueue")} · {rows.Count}" : T("noPublishedCandidates"));
        ImGui.SameLine(); ImGui.TextDisabled(T("anomalyNotVerdict"));
        ImGui.TextWrapped(T("anomalyEvidenceBoundary"));
        if (detail.AnomalyCoverage is { } coverage)
            ImGui.TextDisabled($"{T("movementIntervals")} {coverage.MovementIntervals:N0}  ·  " +
                $"{T("exactRangeUses")} {coverage.RangeUsesWithExactPositions:N0}  ·  " +
                $"{T("gcdWindows")} {coverage.GcdWindows:N0}  ·  {detail.AnomalyModel ?? "—"}");
        ImGui.EndChild(); ImGui.PopStyleColor(2); ImGui.Spacing();
    }

    private void DrawIntegrityReferenceSnapshotBand()
    {
        var snapshot = uploads.GetIntegrityReferenceSnapshotState();
        ImGui.PushStyleColor(ImGuiCol.ChildBg,
            WithAlpha(BattlefieldUiTheme.ChartBackground(configuration.UiTheme), .90f));
        ImGui.PushStyleColor(ImGuiCol.Border, WithAlpha(Cyan, .38f));
        ImGui.BeginChild("integrity-reference-snapshot", new Vector2(0, 78), true,
            ImGuiWindowFlags.NoScrollbar);
        ImGui.TextColored(Cyan, T("integrityReferenceSnapshot"));
        ImGui.SameLine();
        if (snapshot.Loading) ImGui.BeginDisabled();
        if (ImGui.SmallButton(T("refreshIntegrityReferenceSnapshot")))
            uploads.RefreshIntegrityReferenceSnapshot();
        if (snapshot.Loading) ImGui.EndDisabled();
        ImGui.SameLine(0, 8);
        if (snapshot.Loading) ImGui.TextDisabled(T("integrityReferenceSnapshotUpdating"));
        else if (snapshot.Available)
            ImGui.TextDisabled(string.Format(T("integrityReferenceSnapshotReady"),
                snapshot.GeneratedAtUtc?.ToLocalTime().ToString("g") ?? "—",
                snapshot.ModelVersion ?? "—", snapshot.SeriesCount,
                snapshot.RemainingDownloadsToday?.ToString() ?? "?", snapshot.DailyDownloadLimit));
        else ImGui.TextDisabled(T("integrityReferenceSnapshotMissing"));
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            ImGui.TextColored(new Vector4(1f, .48f, .32f, 1f), snapshot.Error);
        else ImGui.TextDisabled(T("integrityReferenceSnapshotInclusive"));
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.Spacing();
    }

    private void DrawSelectedAnomalyEvidence(LocalMatchAnalysis match, DetailedAnalysis detail,
        AnomalyCandidate chosen)
    {
        ImGui.Spacing();
        DrawPanelTitle(T("anomalyEvidenceReplay"), T("caseNotVerdict"));
        if (scrollToSelectedAnomaly)
        {
            ImGui.SetScrollHereY(.04f);
            scrollToSelectedAnomaly = false;
        }
        var evidenceColumns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 2);
        var evidenceMapHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * .52f, 380f, 560f);
        if (ImGui.BeginTable("anomaly-evidence-layout", evidenceColumns, ImGuiTableFlags.Resizable |
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("anomaly-map", ImGuiTableColumnFlags.WidthStretch, 1.25f);
            if (evidenceColumns > 1)
                ImGui.TableSetupColumn("anomaly-distribution", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            DrawMovementMap(match, detail, new Vector2(0, evidenceMapHeight), chosen);
            if (evidenceColumns == 1) { ImGui.TableNextRow(); ImGui.TableNextColumn(); }
            else ImGui.TableNextColumn();
            DrawJobIcon(chosen.ClassJobId); ImGui.SameLine(0, 5);
            DrawCopyableText($"{chosen.PlayerName}@{WorldName(chosen.WorldId)}", Cyan);
            ImGui.SameLine(0, 7); ImGui.TextColored(Cyan, T(chosen.Kind));
            ImGui.TextWrapped(AnomalySummary(chosen));
            ImGui.TextDisabled(T("selectedCaseHelp"));
            ImGui.Spacing();
            if (chosen.Samples is { Count: > 0 })
            {
                foreach (var sample in chosen.Samples)
                {
                    DrawAnomalySampleGlyphs(chosen, sample);
                    if (ImGui.SmallButton($"{Clock(sample.ElapsedMs)}##anomaly-time-{sample.ElapsedMs}-{chosen.Kind}"))
                        selectedMs = sample.ElapsedMs;
                    ImGui.SameLine(); ImGui.TextWrapped(AnomalySampleText(sample));
                }
            }
            var distribution = FindAnomalyDistribution(detail, chosen) ??
                               BuildCandidateDistribution(chosen, ExpectedDistributionKind(chosen.Kind));
            var player = match.Players.FirstOrDefault(x => x.Name == chosen.PlayerName &&
                (chosen.WorldId == 0 || x.WorldId == 0 || x.WorldId == chosen.WorldId));
            var fragility = player == null ? null : TendenciesFor(match, player)
                .FirstOrDefault(x => x.Key == "fragility");
            // Default comparison always uses the single downloaded full snapshot.
            // A filtered server cohort would reintroduce one-click-per-card latency.
            var useFragilityCohort = false;
            var cohort = IntegrityCohortFor(match, chosen, distribution?.EvidenceScope ?? string.Empty);
            if (distribution != null)
                DrawAnomalyHistogram(distribution, chosen, cohort, useFragilityCohort);
            else ImGui.TextDisabled(T("noDistributionData"));
            if (chosen.Kind == "animation_lock_triple_weave")
            {
                var intervalDistribution = (detail.AnomalyDistributions ?? []).FirstOrDefault(row =>
                    row.Kind == "ogcd_interval" && row.PlayerName == chosen.PlayerName &&
                    (chosen.WorldId == 0 || row.WorldId == 0 || row.WorldId == chosen.WorldId)) ??
                    BuildCandidateDistribution(chosen, "ogcd_interval");
                if (intervalDistribution != null)
                {
                    var intervalCandidate = chosen with { Kind = "ogcd_interval" };
                    var intervalCohort = IntegrityCohortFor(match, intervalCandidate,
                        intervalDistribution.EvidenceScope,
                        useFragilityCohort ? "fragility" : null, useFragilityCohort ? 25 : null);
                    DrawAnomalyHistogram(intervalDistribution, chosen, intervalCohort,
                        useFragilityCohort);
                }
            }
            ImGui.EndTable();
        }
        var sampleTime = chosen.Samples?.OrderBy(x => Math.Abs(x.ElapsedMs - selectedMs)).FirstOrDefault()?.ElapsedMs
                         ?? (long)selectedMs;
        var synchronized = (detail.Actions ?? []).Where(x => x.ElapsedMs >= sampleTime - 5_000 &&
            x.ElapsedMs <= sampleTime + 3_000 && ActionBelongsToCandidate(x, chosen));
        DrawActionSliceTable("anomaly-synchronized-events", T("synchronizedEvents"), synchronized);
        ImGui.TextDisabled(T("onlyCandidateEvents"));
    }

    private void DrawIntegrityMethodDisclosure(DetailedAnalysis detail)
    {
        ImGui.Spacing();
        if (!ImGui.TreeNode($"{T("reviewMethodAndCoverage")}##integrity-method")) return;
        ImGui.TextWrapped(T("integrityDashboardBoundary"));
        if (detail.AnomalyCoverage is { } coverage)
            DrawMechanismCoverage(detail, coverage);
        ImGui.TreePop();
    }

    private static bool ActionBelongsToCandidate(ActionSliceEvent action, AnomalyCandidate candidate)
    {
        static bool Identity(string name, ushort world, AnomalyCandidate value) =>
            name == value.PlayerName && (value.WorldId == 0 || world == 0 || world == value.WorldId);
        return Identity(action.SourceName, action.SourceWorldId, candidate) ||
               Identity(action.TargetName, action.TargetWorldId, candidate) ||
               Identity(action.SourceOwnerName, action.SourceOwnerWorldId, candidate) ||
               Identity(action.TargetOwnerName, action.TargetOwnerWorldId, candidate);
    }

    private static string AnomalyKey(AnomalyCandidate row) =>
        $"{row.Kind}|{row.PlayerName}|{row.WorldId}|{row.ActionId}";

    private void SelectAnomaly(AnomalyCandidate row, DetailedAnalysis detail, bool navigate = false)
    {
        selectedAnomalyKey = AnomalyKey(row);
        selectedIntegritySection = IntegritySectionFor(row.Kind);
        if (row.ClassJobId != 0) selectedIntegrityJobId = row.ClassJobId;
        if (navigate) scrollToSelectedAnomaly = true;
        if (row.Samples is { Count: > 0 }) selectedMs = row.Samples[0].ElapsedMs;
        focusedPlayerName = row.PlayerName; focusedPlayerWorldId = row.WorldId;
        selectedActorId = detail.MovementTimeline.SelectMany(x => x.Actors ?? [])
            .FirstOrDefault(x => x.Name == row.PlayerName &&
                (row.WorldId == 0 || x.WorldId == 0 || x.WorldId == row.WorldId))?.EntityId;
    }

    private static string IntegritySectionFor(string kind) => kind switch
    {
        "movement_envelope_exceeded" => "movement",
        "action_range_outlier" => "skills",
        "minimum_latency_zantetsuken" or "minimum_latency_tenchu" => "finishers",
        _ when kind.StartsWith("danger_response_", StringComparison.Ordinal) => "defense",
        _ => "locks"
    };

    private static AnomalyDistribution? FindAnomalyDistribution(DetailedAnalysis detail,
        AnomalyCandidate candidate)
    {
        var expectedKind = ExpectedDistributionKind(candidate.Kind);
        return (detail.AnomalyDistributions ?? []).FirstOrDefault(row => row.Kind == expectedKind &&
            row.PlayerName == candidate.PlayerName &&
            (candidate.WorldId == 0 || row.WorldId == 0 || row.WorldId == candidate.WorldId) &&
            (candidate.ActionId == 0 || row.ActionId == candidate.ActionId));
    }

    private static string ExpectedDistributionKind(string candidateKind) => candidateKind switch
    {
        "movement_envelope_exceeded" => "movement_speed_active_unmounted_normalized",
        "action_range_outlier" => "action_range",
        "animation_lock_triple_weave" => "animation_lock_deficit",
        "local_animation_lock_bypass" => "local_request_lock_remaining",
        "local_cast_time_shortening" => "local_cast_completion_ratio",
        "local_lb_aftercast_cancel" => "local_lb_aftercast_lock_deficit",
        _ => candidateKind
    };

    private void DrawMechanismCoverage(DetailedAnalysis detail, AnomalyCoverage coverage)
    {
        DrawPanelTitle(T("mechanismCoverageTitle"), T("mechanismCoverageBoundary"));
        var columns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 3);
        if (!ImGui.BeginTable("mechanism-coverage-grid", columns,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
            return;

        void Cell(string title, string level, Vector4 color, string body)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(color, title);
            ImGui.SameLine();
            ImGui.TextDisabled(level);
            ImGui.TextWrapped(body);
        }

        Cell(T("recastCoverage"), T("evidenceE1"), new Vector4(.35f,.78f,1f,1f),
            string.Format(T("recastCoverageSummary"), coverage.RecastMetadataUses,
                coverage.RecastOpportunities, coverage.RecastRejectedMissingMetadata,
                coverage.RecastAmbiguousChargeWindows, coverage.StrictRecastOpportunities,
                coverage.StrictRecastEarlyEffects, coverage.StrictRecastRejectedLegacyMetadata,
                coverage.StrictRecastRejectedMultiCharge));
        Cell(T("castCoverage"), T("evidenceE0"), new Vector4(.70f,.70f,.70f,1f),
            string.Format(T("castCoverageSummary"), coverage.CastEffectUses,
                coverage.CastAdvertisedEffectUses, coverage.CastStartEventsCaptured,
                coverage.CastEligibleStarts, coverage.CastRejectedLateObservation,
                coverage.CastTimingNotIdentifiable));
        Cell(T("forcedControlCoverage"),
            coverage.ForcedControlShortWindowCaptured > 0 ? T("evidenceE2") : T("evidenceE1"),
            new Vector4(.55f,.86f,.58f,1f),
            string.Format(T("forcedControlCoverageSummary"), coverage.ForcedControlOpportunities,
                coverage.ForcedControlMovementPairs, coverage.ForcedControlRejectedVisibility,
                coverage.ForcedControlShortWindowCaptured,
                coverage.ForcedControlShortWindowRejected));
        ImGui.EndTable();
        var forcedState = EvidenceAvailability.ForcedMovementState(detail, coverage);
        var forcedStateColor = forcedState switch
        {
            "active_observed" => new Vector4(.40f, .86f, .58f, 1f),
            "active_all_rejected" or "declared_missing_events" => new Vector4(1f, .68f, .28f, 1f),
            _ => new Vector4(.65f, .70f, .74f, 1f)
        };
        ImGui.TextColored(forcedStateColor, T($"forcedMovementState_{forcedState}"));
        ImGui.Spacing();
    }

    private void DrawIntegrityOverview(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        var rows = (detail.AnomalyDistributions ?? [])
            .Where(row => row.UpperBounds.Count > 0 && row.Counts.Count > 0 && row.Observations > 0)
            .ToArray();
        ImGui.TextColored(Cyan, T("integrityDistributionDashboard"));
        ImGui.TextWrapped(T("integrityDashboardBoundary"));
        if (detail.AnomalyModel != UploadService.CurrentAnomalyModel)
        {
            ImGui.TextColored(new Vector4(1f, .72f, .3f, 1f),
                string.Format(T("integrityDetailRefreshing"),
                    detail.AnomalyModel ?? "unknown", UploadService.CurrentAnomalyModel));
            ImGui.TextDisabled(T("integrityDetailRefreshingBoundary"));
        }
        if (rows.Length == 0)
        {
            ImGui.TextDisabled(T("noDistributionData"));
            ImGui.Separator();
            return;
        }

        DrawIntegritySectionSelector(rows);
        ImGui.Separator();

        if (selectedIntegritySection == "skills")
        {
        DrawPanelTitle(T("jobSkillEvidence"), T("integritySkillLayoutHelp"));
        var jobs = rows.Where(row => row.Kind is "action_interval" or "action_range" or
                "strict_recast_effect_gap" or "observed_cast_completion_ratio" or
                "forced_movement_displacement")
            .GroupBy(row => (row.ClassJobId, row.ActionId))
            .Where(group => group.Key.ClassJobId != 0 && group.Key.ActionId != 0)
            .GroupBy(group => group.Key.ClassJobId)
            .OrderBy(group => group.Key)
            .ToArray();
        if (jobs.Length == 0) ImGui.TextDisabled(T("noDistributionData"));
        else
        {
            if (!selectedIntegrityJobId.HasValue || jobs.All(job => job.Key != selectedIntegrityJobId.Value))
                selectedIntegrityJobId = jobs[0].Key;
            ImGui.BeginChild("integrity-job-buttons", new Vector2(0, 38), false,
                ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            foreach (var jobButton in jobs)
            {
                var selected = jobButton.Key == selectedIntegrityJobId;
                if (selected)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, WithAlpha(Cyan, .62f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, WithAlpha(Cyan, .82f));
                }
                if (ImGui.Button($"{JobName(jobButton.Key)}##integrity-job-button-{jobButton.Key}"))
                    selectedIntegrityJobId = jobButton.Key;
                if (selected) ImGui.PopStyleColor(2);
                ImGui.SameLine(0, 5);
            }
            ImGui.EndChild();
            var job = jobs.First(group => group.Key == selectedIntegrityJobId);
            DrawJobIcon(job.Key); ImGui.SameLine(0, 6); ImGui.TextColored(Cyan, JobName(job.Key));
            foreach (var action in job.OrderByDescending(group => group.Sum(x => x.Observations))
                         .ThenBy(group => group.Key.ActionId).Take(8))
            {
                var actionId = action.Key.ActionId;
                ImGui.PushID($"integrity-skill-card-{job.Key}-{actionId}");
                ImGui.Separator();
                DrawActionIcon(actionId, $"#{actionId}");
                var range = action.FirstOrDefault(row => row.Kind == "action_range")?.ReferenceValue;
                if (range.HasValue)
                {
                    ImGui.SameLine(0, 10);
                    ImGui.TextDisabled($"{T("nominalSkillRange")} {range:0.#} yalm");
                }
                var skillColumns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 2);
                if (ImGui.BeginTable($"integrity-skill-pair-{job.Key}-{actionId}", skillColumns,
                    ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
                {
                    foreach (var kind in new[] { "action_interval", "action_range",
                                 "strict_recast_effect_gap", "observed_cast_completion_ratio",
                                 "local_cast_completion_ratio", "forced_movement_displacement" })
                    {
                        ImGui.TableNextColumn();
                        DrawAggregateDistributionCompact(match,
                            action.Where(row => row.Kind == kind).ToArray());
                    }
                    ImGui.EndTable();
                }
                DrawForcedMovementInstances(detail, job.Key, actionId);
                ImGui.Spacing();
                ImGui.PopID();
            }
        }

        var unresolvedJobRows = rows.Count(row => row.ClassJobId == 0 &&
            (row.Kind is "action_interval" or "action_range" or
             "strict_recast_effect_gap" or "observed_cast_completion_ratio" or
             "local_cast_completion_ratio" or
             "forced_movement_displacement"));
        if (unresolvedJobRows > 0)
            ImGui.TextDisabled($"{T("unresolvedJobEvidenceOmitted")}: {unresolvedJobRows}");
        }

        if (selectedIntegritySection == "locks")
        {
        DrawPanelTitle(T("animationLockEvidence"), T("effectOrderOnlyBoundary"));
        var anomalyCoverage = detail.AnomalyCoverage;
        if (anomalyCoverage != null)
        {
            ImGui.TextDisabled(string.Format(T("animationLockCoverage"),
                anomalyCoverage.EffectOrderTripleWeaveWindows,
                anomalyCoverage.LocalRequestVerifiedWindows,
                anomalyCoverage.LbEffectOrderWindows,
                anomalyCoverage.LocalRequestVerifiedLbWindows));
        }
        ImGui.TextWrapped(T("tripleWeaveBoundaryHelp"));
        var weaveColumns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 3);
        if (ImGui.BeginTable("integrity-weave-grid", weaveColumns,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            foreach (var kind in new[] { "gcd_weave_count", "ogcd_interval", "animation_lock_deficit",
                         "local_request_lock_remaining" })
            {
                ImGui.TableNextColumn();
                DrawAggregateDistributionCompact(match, rows.Where(row => row.Kind == kind).ToArray());
            }
            ImGui.EndTable();
        }

        var actionLockGroups = rows.Where(row => row.Kind == "local_action_aftercast_lock_deficit" &&
                row.ClassJobId != 0 && row.ActionId != 0)
            .GroupBy(row => (row.ClassJobId, row.ActionId))
            .OrderBy(group => group.Key.ClassJobId).ThenBy(group => group.Key.ActionId)
            .ToArray();
        if (actionLockGroups.Length > 0)
        {
            ImGui.TextColored(Cyan, T("localActionAftercastEvidence"));
            ImGui.TextWrapped(T("localActionAftercastBoundary"));
            var actionLockColumns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 2);
            if (ImGui.BeginTable("local-action-aftercast-grid", actionLockColumns,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
            {
                foreach (var group in actionLockGroups)
                {
                    ImGui.TableNextColumn();
                    ImGui.PushID($"local-aftercast-{group.Key.ClassJobId}-{group.Key.ActionId}");
                    DrawJobIcon(group.Key.ClassJobId); ImGui.SameLine(0, 5);
                    DrawActionIcon(group.Key.ActionId, $"#{group.Key.ActionId}");
                    DrawAggregateDistributionCompact(match, group.ToArray());
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
        }

        var lbGroups = rows.Where(row => row.ClassJobId != 0 && row.ActionId != 0 &&
                row.Kind is "lb_followup_gap_effect_order" or "lb_lock_deficit_effect_order" or
                    "local_lb_movement_onset" or "local_lb_aftercast_lock_deficit")
            .GroupBy(row => (row.ClassJobId, row.ActionId))
            .OrderBy(group => group.Key.ClassJobId).ThenBy(group => group.Key.ActionId)
            .ToArray();
        foreach (var lb in lbGroups)
        {
            ImGui.PushID($"integrity-lb-{lb.Key.ClassJobId}-{lb.Key.ActionId}");
            DrawJobIcon(lb.Key.ClassJobId); ImGui.SameLine(0, 5);
            DrawActionIcon(lb.Key.ActionId, $"#{lb.Key.ActionId}");
            var columns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 2);
            if (ImGui.BeginTable("lb-effect-order-pair", columns,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
            {
                foreach (var kind in new[] { "lb_followup_gap_effect_order", "lb_lock_deficit_effect_order",
                             "local_lb_movement_onset", "local_lb_aftercast_lock_deficit" })
                {
                    ImGui.TableNextColumn();
                    DrawAggregateDistributionCompact(match, lb.Where(row => row.Kind == kind).ToArray());
                }
                ImGui.EndTable();
            }
            ImGui.PopID();
        }
        if (lbGroups.Length == 0) ImGui.TextDisabled(T("noLbEffectOrderData"));
        }

        if (selectedIntegritySection == "movement")
        {
        DrawPanelTitle(T("allPlayerMovementEvidence"), T("movementDistributionBoundary"));
        var movementRows = rows.Where(row => row.Kind.StartsWith("movement_speed",
            StringComparison.Ordinal)).ToArray();
        var movementColumns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 3);
        if (movementRows.Length > 0 && ImGui.BeginTable("integrity-movement-layers",
            movementColumns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            foreach (var kind in new[] { "movement_speed_active_unmounted_normalized",
                         "movement_speed_active_mounted_normalized" })
            {
                ImGui.TableNextColumn();
                DrawAggregateDistributionCompact(match,
                    movementRows.Where(row => row.Kind == kind).ToArray());
            }
            ImGui.EndTable();
        }
        if (movementRows.Length == 0) ImGui.TextWrapped(T("movementDataVersionUnavailable"));
        }

        if (selectedIntegritySection == "defense")
        {
        DrawPanelTitle(T("defensiveReactionEvidence"), T("dangerResponseBoundary"));
        var defenseColumns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 3);
        if (ImGui.BeginTable("integrity-defense-grid", defenseColumns,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            foreach (var kind in new[] { "danger_response_recuperate", "danger_response_purify", "danger_response_guard" })
            {
                ImGui.TableNextColumn();
                DrawAggregateDistributionCompact(match, rows.Where(row => row.Kind == kind).ToArray());
            }
            ImGui.EndTable();
        }
        }

        if (selectedIntegritySection == "finishers")
        {
        DrawPanelTitle(T("finisherReactionEvidence"), T("finisherResponseBoundary"));
        var finisherColumns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 2);
        if (ImGui.BeginTable("integrity-finisher-grid", finisherColumns,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            foreach (var kind in new[] { "minimum_latency_zantetsuken", "minimum_latency_tenchu" })
            {
                ImGui.TableNextColumn();
                DrawAggregateDistributionCompact(match, rows.Where(row => row.Kind == kind).ToArray());
            }
            ImGui.EndTable();
        }
        }
        ImGui.Separator();
    }

    private void DrawIntegritySectionSelector(IReadOnlyList<AnomalyDistribution> rows)
    {
        var skillCount = rows.Where(row => row.ClassJobId != 0 && row.ActionId != 0 &&
                row.Kind is "action_interval" or "action_range" or "strict_recast_effect_gap" or
                    "observed_cast_completion_ratio" or "local_cast_completion_ratio" or
                    "forced_movement_displacement")
            .Select(row => (row.ClassJobId, row.ActionId)).Distinct().Count();
        var lockCount = rows.Where(row => row.Kind is "gcd_weave_count" or "ogcd_interval" or
                "animation_lock_deficit" or "lb_followup_gap_effect_order" or
                "lb_lock_deficit_effect_order" or "local_request_lock_remaining" or
                "local_action_aftercast_lock_deficit" or
                "local_lb_movement_onset" or "local_lb_aftercast_lock_deficit")
            .Select(row => (row.Kind, row.ClassJobId, row.ActionId)).Distinct().Count();
        var movementCount = rows.Where(row => row.Kind.StartsWith("movement_speed",
                StringComparison.Ordinal)).Select(row => row.Kind).Distinct().Count();
        var defenseCount = rows.Where(row => row.Kind is "danger_response_recuperate" or
                "danger_response_purify" or "danger_response_guard")
            .Select(row => row.Kind).Distinct().Count();
        var finisherCount = rows.Where(row => row.Kind is "minimum_latency_zantetsuken" or
                "minimum_latency_tenchu").Select(row => row.Kind).Distinct().Count();
        var sections = new (string Key, string Label, int Count)[]
        {
            ("skills", T("integritySectionSkills"), skillCount),
            ("locks", T("integritySectionLocks"), lockCount),
            ("movement", T("integritySectionMovement"), movementCount),
            ("defense", T("integritySectionDefense"), defenseCount),
            ("finishers", T("integritySectionFinishers"), finisherCount)
        };
        ImGui.BeginChild("integrity-section-buttons", new Vector2(0, 38), false,
            ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        foreach (var section in sections)
        {
            var selected = selectedIntegritySection == section.Key;
            if (selected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, WithAlpha(Cyan, .62f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, WithAlpha(Cyan, .82f));
            }
            if (ImGui.Button($"{section.Label}  {section.Count}##integrity-section-{section.Key}"))
                selectedIntegritySection = section.Key;
            if (selected) ImGui.PopStyleColor(2);
            ImGui.SameLine(0, 5);
        }
        ImGui.EndChild();
    }

    private void DrawForcedMovementInstances(DetailedAnalysis detail, byte sourceJobId, uint actionId)
    {
        var rows = (detail.ForcedMovementObservations ?? [])
            .Where(row => row.ContinuousVisible && row.SourceClassJobId == sourceJobId &&
                          row.ActionId == actionId && row.Samples.Count >= 4)
            .OrderBy(row => row.ElapsedMs)
            .ToArray();
        if (rows.Length == 0) return;
        if (!ImGui.TreeNode($"{T("forcedMovementInstances")} ({rows.Length})##forced-{sourceJobId}-{actionId}"))
            return;
        ImGui.TextDisabled(T("forcedMovementInstanceBoundary"));
        var columns = ResponsiveColumns(ImGui.GetContentRegionAvail().X, 2);
        if (ImGui.BeginTable($"forced-movement-instances-{sourceJobId}-{actionId}", columns,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            foreach (var row in rows)
            {
                var origin = row.Samples[0];
                var distance = row.Samples.Skip(1)
                    .Select(sample => Math.Sqrt(Math.Pow(sample.X-origin.X,2)+
                                                Math.Pow(sample.Z-origin.Z,2)))
                    .DefaultIfEmpty(0).Max();
                ImGui.TableNextColumn();
                ImGui.TextColored(Cyan, Clock(row.ElapsedMs));
                ImGui.SameLine(0, 8);
                ImGui.TextDisabled($"{distance:0.00} yalm · {row.Samples.Count} {T("positionSamples")}");
                DrawJobIcon(row.SourceClassJobId); ImGui.SameLine(0, 5);
                DrawCopyableText($"{row.SourceName}@{WorldName(row.SourceWorldId)}");
                ImGui.TextDisabled("→"); ImGui.SameLine(0, 5);
                DrawJobIcon(row.TargetClassJobId); ImGui.SameLine(0, 5);
                DrawCopyableText($"{row.TargetName}@{WorldName(row.TargetWorldId)}");
            }
            ImGui.EndTable();
        }
        ImGui.TreePop();
    }

    private void DrawAnomalySampleGlyphs(AnomalyCandidate candidate, AnomalySample sample)
    {
        var ids = candidate.ActionId != 0 ? new[] { candidate.ActionId }
            : sample.ActionIds is { Count: > 0 } ? sample.ActionIds.Where(id => id != 0).Distinct().Take(4).ToArray()
            : candidate.Kind switch
            {
                "minimum_latency_purify" or "danger_response_purify" => new uint[] { 29056 },
                "danger_response_recuperate" => new uint[] { 29711 },
                "danger_response_guard" => new uint[] { 29054 },
                "minimum_latency_tenchu" => new uint[] { 29515 },
                "minimum_latency_zantetsuken" => new uint[] { 29537 },
                _ => Array.Empty<uint>()
            };
        foreach (var id in ids)
        {
            DrawActionGlyph(id, 18);
            ImGui.SameLine(0, 3);
        }
    }

    private static int ResponsiveColumns(float availableWidth, int preferred) =>
        Math.Clamp((int)MathF.Floor(Math.Max(1, availableWidth) / 260f), 1, preferred);

    private void DrawAggregateDistributionCompact(LocalMatchAnalysis match,
        IReadOnlyList<AnomalyDistribution> players)
    {
        if (players.Count == 0) { ImGui.TextDisabled(T("noDistributionData")); return; }
        var first = players[0];
        var compatible = players.Where(row => row.Unit == first.Unit &&
            row.UpperBounds.SequenceEqual(first.UpperBounds)).ToArray();
        if (compatible.Length == 0) { ImGui.TextDisabled(T("noDistributionData")); return; }
        var counts = new int[first.Counts.Count];
        var highlights = new int[first.Counts.Count];
        foreach (var row in compatible)
            for (var i = 0; i < counts.Length; i++)
            {
                if (i < row.Counts.Count) counts[i] += row.Counts[i];
                if (i < row.HighlightCounts.Count) highlights[i] += row.HighlightCounts[i];
            }
        var aggregate = new AnomalyDistribution(first.Kind, T("currentMatchAllPlayers"), 0,
            first.ClassJobId, first.Unit, first.UpperBounds, counts, highlights,
            compatible.Sum(row => row.Observations), first.ReferenceValue, first.Tolerance,
            first.ActionId, first.EvidenceScope);
        var synthetic = new AnomalyCandidate(first.Kind, string.Empty, 0, first.ClassJobId,
            0, 0, "observed", aggregate.EvidenceScope, string.Empty, ActionId: first.ActionId);
        var cohort = IntegrityCohortFor(match, synthetic, aggregate.EvidenceScope);
        DrawAnomalyHistogram(aggregate, synthetic, cohort, false, compatible, true);
    }

    private IntegrityCohortSnapshot IntegrityCohortFor(LocalMatchAnalysis match,
        AnomalyCandidate candidate, string evidenceScope, string? behaviorKey = null,
        double? behaviorMax = null)
    {
        if (evidenceScope.StartsWith(
                "continuous_loaded_unmounted_100_500ms_histogram_pilot_v1:",
                StringComparison.Ordinal))
            return new IntegrityCohortSnapshot("movement-pilot", false, Error: T("movementPilotNoCohort"));
        var matchKey = match.RemoteMatchUid ?? match.MatchUid;
        return uploads.GetIntegrityCohort(match.Realm, matchKey,
            candidate with { EvidenceScope = evidenceScope },
            behaviorKey, behaviorMax, requestIfMissing: false);
    }

    private static AnomalyDistribution? BuildCandidateDistribution(AnomalyCandidate candidate,
        string kind)
    {
        (double[] Bounds, string Unit) definition = kind switch
        {
            "movement_speed_active_unmounted_normalized" => (new double[] { 2, 4, 6, 8, 10, 12, 14, 16, 18, 22 }, "base-equivalent yalm/s"),
            "movement_speed_active_mounted_normalized" => (new double[] { 2, 4, 6, 8, 10, 12, 14, 16, 18, 22 }, "base-equivalent yalm/s"),
            "action_range" => (Enumerable.Range(1, 20).Select(index => (double)(index * 3)).ToArray(), "yalm"),
            "action_interval" => (LogSecondBounds(), "s"),
            "strict_recast_effect_gap" => (LogSecondBounds(), "s"),
            "observed_cast_completion_ratio" => (new double[] { .1, .25, .5, .75, .85, .9, .95, 1, 1.05, 1.1, 1.25, 1.5, 2 }, "ratio"),
            "local_cast_completion_ratio" => (new double[] { .1, .25, .5, .65, .75, .85, .9, .95, 1, 1.05, 1.1, 1.25, 1.5 }, "ratio"),
            "forced_movement_displacement" => (new double[] { .25, .5, 1, 2, 3, 5, 8, 10, 15, 20, 30 }, "yalm"),
            "gcd_weave_count" => (new double[] { 0, 1, 2, 3, 4 }, "abilities/window"),
            "ogcd_interval" => (FineMillisecondBounds(3000), "ms"),
            "animation_lock_deficit" => (SignedMillisecondBounds(-600, 600), "ms"),
            "local_request_lock_remaining" => (new double[] { 0 }.Concat(FineMillisecondBounds(3000)).ToArray(), "ms"),
            "local_action_aftercast_lock_deficit" => (SignedMillisecondBounds(-2010, 2010), "ms"),
            "local_lb_movement_onset" => (new double[] { 0 }.Concat(FineMillisecondBounds(5000)).ToArray(), "ms"),
            "local_lb_aftercast_lock_deficit" => (SignedMillisecondBounds(-2010, 2010), "ms"),
            _ when kind.StartsWith("minimum_latency_", StringComparison.Ordinal) =>
                (FineMillisecondBounds(), "ms"),
            _ when kind.StartsWith("danger_response_", StringComparison.Ordinal) =>
                (FineMillisecondBounds(), "ms"),
            _ => (Array.Empty<double>(), string.Empty)
        };
        if (definition.Bounds.Length == 0) return null;
        var values = (candidate.Samples ?? []).Select(sample => CandidateValue(sample, kind))
            .Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        if (values.Length == 0) return null;
        var counts = new int[definition.Bounds.Length + 1];
        foreach (var value in values)
        {
            var index = Array.FindIndex(definition.Bounds, bound => value <= bound);
            counts[index < 0 ? counts.Length - 1 : index]++;
        }
        var first = candidate.Samples?.FirstOrDefault();
        return new AnomalyDistribution(kind, candidate.PlayerName, candidate.WorldId,
            candidate.ClassJobId, definition.Unit, definition.Bounds, counts, counts.ToArray(),
            values.Length, first?.NominalRange, first?.Tolerance, candidate.ActionId,
            "candidate_samples_only");
    }

    private static double[] FineMillisecondBounds(int maximum = 5000) =>
        Enumerable.Range(1, maximum / 30).Select(index => (double)(index * 30)).ToArray();

    private static double[] SignedMillisecondBounds(int minimum, int maximum) =>
        Enumerable.Range(0, (maximum - minimum) / 30 + 1)
            .Select(index => (double)(minimum + index * 30)).ToArray();

    private static double[] LogSecondBounds() =>
        new[] { .03, .06, .12, .24, .48, .96, 1.92, 3.84, 7.68, 15.36, 30.72, 61.44, 122.88 };

    private static double? CandidateValue(AnomalySample sample, string kind) => kind switch
    {
        "movement_speed_active_unmounted_normalized" => sample.Speed,
        "action_range" => sample.Distance,
            "action_interval" or "strict_recast_effect_gap" or "observed_cast_completion_ratio" or
                "local_cast_completion_ratio" => sample.Dt,
        "gcd_weave_count" => sample.WeaveCount,
        "ogcd_interval" => sample.MinimumIntervalMs,
        "animation_lock_deficit" => sample.MaximumLockDeficitMs,
        _ when kind.StartsWith("minimum_latency_", StringComparison.Ordinal) => sample.LatencyMs,
        _ when kind.StartsWith("danger_response_", StringComparison.Ordinal) => sample.LatencyMs,
        _ => null
    };

    private void DrawAnomalyHistogram(AnomalyDistribution distribution, AnomalyCandidate candidate,
        IntegrityCohortSnapshot cohort, bool fragilityFiltered,
        IReadOnlyList<AnomalyDistribution>? playerOverlays = null, bool compact = false)
    {
        ImGui.Spacing();
        var distributionLabel = T(distribution.Kind);
        ImGui.TextColored(Cyan, distributionLabel == distribution.Kind
            ? T("anomalyHistogram") : $"{T("anomalyHistogram")} · {distributionLabel}");
        if (cohort.Error == "not_requested")
            ImGui.TextDisabled(T("referenceSnapshotAwaitingFullDownload"));
        else if (cohort.Loading) ImGui.TextDisabled(T("referenceCohortLoading"));
        else if (cohort.Series is { } reference)
        {
            ImGui.TextDisabled($"{T("referenceCohort")}: {reference.PlayerMatchCount:N0} " +
                $"{T("playerMatches")} · {reference.Observations:N0} {T("observations")}" +
                (fragilityFiltered ? $" · {T("fragilityCohortFilter")}" : string.Empty));
            if (!compact && cohort.RealmCoverage is { Count: > 0 })
                ImGui.TextDisabled(string.Join(" · ", cohort.RealmCoverage
                    .OrderBy(x => x.Key)
                    .Select(x => $"{x.Key} {x.Value.Observations:N0}")));
            if (reference.EvidenceStatus == "insufficient_cohort")
                ImGui.TextColored(new Vector4(1f, .72f, .3f, 1f), T("insufficientCohort"));
        }
        else if (!string.IsNullOrWhiteSpace(cohort.Error))
        {
            ImGui.TextDisabled($"{T("referenceCohortUnavailable")}: {cohort.Error}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"{T("retryReferenceCohort")}##retry-{cohort.Key}"))
            {
                uploads.InvalidateIntegrityCohort(cohort.Key);
                requestedIntegrityCohorts.Add(cohort.Key);
            }
        }
        else ImGui.TextDisabled(T("referenceCohortEmpty"));
        if (!compact)
            ImGui.TextDisabled(T(distribution.Kind is "movement_speed_active_unmounted_normalized" or "minimum_latency_purify" or
                "danger_response_recuperate" or "danger_response_purify" or "danger_response_guard"
                ? "cohortScopeShared" : "cohortScopeJobAction"));
        ImGui.TextDisabled($"{T("currentMatchDistribution")}: {distribution.Observations:N0} · {distribution.Unit}");

        // Respect the table cell's clip region. The surrounding grids reduce their column count
        // responsively, while this final clamp keeps even very narrow user-resized windows usable.
        var size = new Vector2(Math.Max(160, ImGui.GetContentRegionAvail().X), compact ? 164 : 232);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"anomaly-histogram##{distribution.Kind}-{distribution.ClassJobId}-{distribution.ActionId}", size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size,
            ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.ChartBackground(configuration.UiTheme)), 2);
        var series = cohort.Series;
        var compatibleReference = series is not null &&
            series.UpperBounds.SequenceEqual(distribution.UpperBounds) &&
            string.Equals(series.EvidenceScope, distribution.EvidenceScope,
                StringComparison.Ordinal);
        var useDisplayBins = compatibleReference &&
            series!.DisplayUpperBounds is { Count: > 0 } displayBounds &&
            series.DisplayCounts is { Count: > 0 } displayCounts &&
            displayCounts.Count == displayBounds.Count + 1;
        IReadOnlyList<double> chartBounds = useDisplayBins
            ? series!.DisplayUpperBounds! : distribution.UpperBounds;
        IReadOnlyList<int> referenceCounts = compatibleReference
            ? (useDisplayBins ? series!.DisplayCounts! : series!.Counts) : Array.Empty<int>();
        IReadOnlyList<int> statisticalReferenceCounts = compatibleReference
            ? series!.Counts : Array.Empty<int>();
        var currentCounts = useDisplayBins
            ? RebinHistogram(distribution.UpperBounds, distribution.Counts, chartBounds)
            : distribution.Counts;
        var highlightCounts = useDisplayBins
            ? RebinHistogram(distribution.UpperBounds, distribution.HighlightCounts, chartBounds)
            : distribution.HighlightCounts;
        var count = Math.Min(currentCounts.Count, chartBounds.Count + 1);
        if (count <= 0) return;
        static double Share(IReadOnlyList<int> values, int index, int bins)
        {
            var total = values.Take(bins).Sum();
            return total <= 0 || index >= values.Count ? 0 : (double)values[index] / total;
        }
        var maximum = Enumerable.Range(0, count).SelectMany(i => new[]
        {
            Share(referenceCounts, i, count), Share(currentCounts, i, count),
            Share(highlightCounts, i, count)
        }).Append(.01).Max();
        var plotLeft = origin.X + 48;
        var plotRight = origin.X + size.X - 10;
        var chartTop = origin.Y + 14;
        var chartBottom = origin.Y + size.Y - 43;
        var plotWidth = Math.Max(1, plotRight - plotLeft);
        var gridColor = ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Grid(configuration.UiTheme));
        var axisColor = ImGui.ColorConvertFloat4ToU32(Muted);
        for (var tick = 0; tick <= 10; tick++)
        {
            var y = chartBottom - (chartBottom - chartTop) * tick / 10f;
            draw.AddLine(new Vector2(plotLeft, y), new Vector2(plotRight, y), gridColor,
                tick % 2 == 0 ? 1f : .5f);
            if (tick % 2 != 0) continue;
            var yLabel = $"{maximum * tick / 10d * 100:0.#}%";
            var extent = ImGui.CalcTextSize(yLabel);
            draw.AddText(new Vector2(plotLeft - extent.X - 5, y - extent.Y / 2), axisColor, yLabel);
        }
        var barWidth = plotWidth / count;
        var labelStride = Math.Max(1, (int)Math.Ceiling(count * 42f / plotWidth));
        for (var i = 0; i < count; i++)
        {
            var height = (float)((chartBottom - chartTop) * Share(referenceCounts, i, count) / maximum);
            var gap = Math.Min(2f, barWidth * .12f);
            var x0 = plotLeft + i * barWidth + gap;
            var x1 = plotLeft + (i + 1) * barWidth - gap;
            if (height > 0)
                draw.AddRectFilled(new Vector2(x0, chartBottom - height), new Vector2(x1, chartBottom),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(.42f, .5f, .55f, .62f)), 2);
            var currentHeight = (float)((chartBottom - chartTop) * Share(currentCounts, i, count) / maximum);
            if (currentHeight > 0)
                draw.AddRectFilled(new Vector2(x0 + barWidth * .18f, chartBottom - currentHeight),
                    new Vector2(x1 - barWidth * .18f, chartBottom),
                    ImGui.ColorConvertFloat4ToU32(WithAlpha(Cyan, .82f)), 2);
            var highlightHeight = (float)((chartBottom - chartTop) *
                Share(highlightCounts, i, count) / maximum);
            if (highlightHeight > 0)
                draw.AddRectFilled(new Vector2(x0 + barWidth * .34f, chartBottom - highlightHeight),
                    new Vector2(x1 - barWidth * .34f, chartBottom),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .27f, .14f, .94f)), 2);
            if (i == count - 1 || (i % labelStride == 0 && count - 1 - i >= labelStride))
            {
                draw.AddLine(new Vector2(x0 - gap, chartTop), new Vector2(x0 - gap, chartBottom),
                    gridColor, .6f);
                var label = i < chartBounds.Count
                    ? $"≤{chartBounds[i]:0.#}" : $">{chartBounds[^1]:0.#}";
                var labelSize = ImGui.CalcTextSize(label);
                draw.AddText(new Vector2((x0 + x1 - labelSize.X) / 2, chartBottom + 5),
                    ImGui.ColorConvertFloat4ToU32(Muted), label);
            }
        }

        draw.AddLine(new Vector2(plotLeft, chartTop), new Vector2(plotLeft, chartBottom), axisColor, 1.2f);
        draw.AddLine(new Vector2(plotLeft, chartBottom), new Vector2(plotRight, chartBottom), axisColor, 1.2f);
        var xTitle = distribution.Unit == "ms" ? T("millisecondsAxis") : distribution.Unit;
        var xTitleSize = ImGui.CalcTextSize(xTitle);
        draw.AddText(new Vector2(plotRight - xTitleSize.X, origin.Y + size.Y - 18), axisColor, xTitle);

        var candidateValues = (candidate.Samples ?? []).Select(sample => distribution.Kind switch
        {
            "movement_speed_active_unmounted_normalized" => sample.Speed,
            "action_range" => sample.Distance,
            "gcd_weave_count" => sample.WeaveCount is { } count ? (double?)count : null,
            "ogcd_interval" => sample.MinimumIntervalMs is { } interval ? (double?)interval : null,
            "animation_lock_deficit" => sample.MaximumLockDeficitMs is { } deficit ? (double?)deficit : null,
            _ => sample.LatencyMs is { } latency ? (double?)latency : null
        }).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        foreach (var group in candidateValues.GroupBy(value =>
        {
            var bin = chartBounds.ToList().FindIndex(bound => value <= bound);
            return bin < 0 ? count - 1 : Math.Min(bin, count - 1);
        }))
        {
            var markerX = plotLeft + (group.Key + .5f) * barWidth;
            var color = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .18f, .12f, 1f));
            draw.AddTriangleFilled(new Vector2(markerX, chartTop),
                new Vector2(markerX - 6, chartTop + 10), new Vector2(markerX + 6, chartTop + 10), color);
            draw.AddText(new Vector2(markerX + 5, chartTop + 1), color, $"×{group.Count()}");
        }

        var outlierLabels = new List<(AnomalyDistribution Row, int Bin, double Value, Vector4 Color)>();
        if (playerOverlays is { Count: > 0 })
        {
            var overlayIndex = 0;
            foreach (var playerDistribution in playerOverlays)
            {
                var displayPlayerCounts = useDisplayBins
                    ? RebinHistogram(playerDistribution.UpperBounds,
                        playerDistribution.Counts, chartBounds)
                    : playerDistribution.Counts;
                var total = displayPlayerCounts.Take(count).Sum();
                if (total <= 0) continue;
                var halfway = (total + 1) / 2;
                var running = 0; var bin = 0;
                for (; bin < count; bin++)
                {
                    running += bin < displayPlayerCounts.Count ? displayPlayerCounts[bin] : 0;
                    if (running >= halfway) break;
                }
                bin = Math.Min(bin, count - 1);
                var hue = (overlayIndex * .61803398875) % 1.0;
                var playerColor = HsvColor((float)hue, .72f, .98f);
                if (TryGetOutlierProbe(distribution.Kind, statisticalReferenceCounts,
                    distribution.UpperBounds,
                    playerDistribution.Counts, out var outlierBin, out var outlierValue))
                {
                    var displayBin=chartBounds.ToList().FindIndex(bound => outlierValue<=bound);
                    if (displayBin<0) displayBin=count-1;
                    outlierLabels.Add((playerDistribution, Math.Min(displayBin, count - 1),
                        outlierValue, playerColor));
                }
                overlayIndex++;
            }
            var stacks = new Dictionary<int, int>();
            foreach (var outlier in outlierLabels)
            {
                var level = stacks.TryGetValue(outlier.Bin, out var used) ? used : 0;
                stacks[outlier.Bin] = level + 1;
                if (level >= 4) continue;
                var markerX = plotLeft + (outlier.Bin + .5f) * barWidth;
                var markerY = chartTop + 4 + level * 13;
                var display = $"{outlier.Row.PlayerName}@{WorldName(outlier.Row.WorldId)}";
                var textSize = ImGui.CalcTextSize(display);
                var textX = Math.Clamp(markerX + 5, origin.X + 4,
                    Math.Max(origin.X + 4, origin.X + size.X - textSize.X - 4));
                var color = ImGui.ColorConvertFloat4ToU32(outlier.Color);
                draw.AddTriangleFilled(new Vector2(markerX, markerY),
                    new Vector2(markerX - 4, markerY + 7), new Vector2(markerX + 4, markerY + 7), color);
                draw.AddText(new Vector2(textX, markerY), color, display);
            }
            foreach (var stack in stacks.Where(pair => pair.Value > 4))
            {
                var markerX = plotLeft + (stack.Key + .5f) * barWidth;
                draw.AddText(new Vector2(markerX + 5, chartTop + 4 + 4 * 13),
                    ImGui.ColorConvertFloat4ToU32(Muted), $"+{stack.Value - 4}");
            }
        }
        if (outlierLabels.Count > 0)
        {
            ImGui.TextColored(new Vector4(1f,.55f,.28f,1f), T("robustTailMarkers"));
            foreach (var outlier in outlierLabels)
            {
                DrawJobIcon(outlier.Row.ClassJobId); ImGui.SameLine(0, 4);
                DrawCopyableText($"{outlier.Row.PlayerName}@{WorldName(outlier.Row.WorldId)}", outlier.Color);
            }
        }

        if (distribution.ReferenceValue is { } referenceValue)
        {
            var referenceBin = chartBounds.ToList().FindIndex(value => value >= referenceValue);
            if (referenceBin < 0) referenceBin = count - 1;
            var markerX = plotLeft + (referenceBin + .5f) * barWidth;
            draw.AddLine(new Vector2(markerX, chartTop), new Vector2(markerX, chartBottom),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .78f, .26f, .92f)), 2f);
            draw.AddText(new Vector2(markerX + 3, chartTop),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .78f, .26f, 1f)),
                distribution.Tolerance is { } tolerance
                    ? $"{referenceValue:0.#}+{tolerance:0.#}" : $"{referenceValue:0.#}");
        }
        draw.AddLine(new Vector2(origin.X + 10, chartBottom),
            new Vector2(origin.X + size.X - 10, chartBottom),
            ImGui.ColorConvertFloat4ToU32(new Vector4(.55f, .65f, .68f, .7f)));
        ImGui.TextDisabled($"{T("referenceHistogramLegend")} · {T("currentHistogramLegend")} · {T("candidateOperationLegend")}");
        if (distribution.Kind is "action_interval" or "strict_recast_effect_gap")
            ImGui.TextDisabled(T("logBinFormula"));
        if (distribution.Kind == "movement_speed_active_unmounted_normalized" &&
            distribution.SpeedStatusCounts is { Count: > 0 })
            ImGui.TextWrapped($"{T("movementAttribution")}: {T("speedBuffExplained")} " +
                $"{distribution.SpeedStatusCounts.Sum():N0} · {T("displacementExplained")} " +
                $"{(distribution.DisplacementCounts?.Sum() ?? 0):N0} · {T("unexplained")} " +
            $"{(distribution.UnexplainedCounts?.Sum() ?? 0):N0}");
    }

    private static Vector4 HsvColor(float h, float s, float v)
    {
        var i = (int)MathF.Floor(h * 6f);
        var f = h * 6f - i;
        var p = v * (1f - s); var q = v * (1f - f * s); var t = v * (1f - (1f - f) * s);
        return (i % 6) switch
        {
            0 => new Vector4(v, t, p, 1), 1 => new Vector4(q, v, p, 1),
            2 => new Vector4(p, v, t, 1), 3 => new Vector4(p, q, v, 1),
            4 => new Vector4(t, p, v, 1), _ => new Vector4(v, p, q, 1)
        };
    }

    private static Vector4 WithAlpha(Vector4 color, float alpha) =>
        new(color.X, color.Y, color.Z, alpha);

    private static IReadOnlyList<int> RebinHistogram(IReadOnlyList<double> sourceBounds,
        IReadOnlyList<int> sourceCounts, IReadOnlyList<double> targetBounds)
    {
        var result = new int[targetBounds.Count + 1];
        if (sourceCounts.Count != sourceBounds.Count + 1) return result;
        for (var sourceIndex = 0; sourceIndex < sourceCounts.Count; sourceIndex++)
        {
            if (sourceIndex >= sourceBounds.Count)
            {
                result[^1] += sourceCounts[sourceIndex];
                continue;
            }
            var targetIndex = targetBounds.ToList().FindIndex(bound =>
                sourceBounds[sourceIndex] <= bound);
            result[targetIndex < 0 ? result.Length - 1 : targetIndex] += sourceCounts[sourceIndex];
        }
        return result;
    }

    private static bool TryGetOutlierProbe(string kind, IReadOnlyList<int> referenceCounts,
        IReadOnlyList<double> bounds, IReadOnlyList<int> playerCounts, out int playerBin,
        out double playerValue)
    {
        playerBin = -1; playerValue = 0;
        // Strict recast and observed-cast rows are reference-building only
        // until legal reset/instant-cast semantics are version-complete.  Do
        // not turn their low tails into player labels yet.
        // Packet-order weave histograms are context, not an accusation. An
        // ordinary double weave must never become a named/red outlier merely
        // because the cohort contains many 0/1 windows. Verified triple
        // requests are surfaced through the candidate table instead.
        if (kind == "gcd_weave_count") return false;
        var lowTail = kind == "action_interval" ||
                      kind == "ogcd_interval" ||
                      kind == "lb_followup_gap_effect_order" ||
                      kind.StartsWith("danger_response_", StringComparison.Ordinal) ||
                      kind.StartsWith("minimum_latency_", StringComparison.Ordinal);
        var highTail = kind is "action_range" or "movement_speed_active_unmounted_normalized" or
            "animation_lock_deficit" or "lb_lock_deficit_effect_order" or
            "local_request_lock_remaining" or "local_action_aftercast_lock_deficit" or
            "local_lb_aftercast_lock_deficit";
        if ((!lowTail && !highTail) || bounds.Count == 0 || referenceCounts.Sum() < 20 ||
            playerCounts.Sum() <= 0) return false;
        var q1 = BinnedQuantile(referenceCounts, bounds, .25, out _);
        var q3 = BinnedQuantile(referenceCounts, bounds, .75, out _);
        var iqr = q3 - q1;
        if (iqr <= 0) return false;
        playerValue = BinnedQuantile(playerCounts, bounds, lowTail ? .10 : .90, out playerBin);
        var fence = lowTail ? q1 - 1.5 * iqr : q3 + 1.5 * iqr;
        return lowTail ? playerValue < fence : playerValue > fence;
    }

    private static double BinnedQuantile(IReadOnlyList<int> counts, IReadOnlyList<double> bounds,
        double probability, out int bin)
    {
        var total = counts.Sum();
        if (total <= 0) { bin = 0; return 0; }
        var target = Math.Max(1, (int)Math.Ceiling(total * probability));
        var running = 0; bin = 0;
        for (; bin < counts.Count; bin++)
        {
            running += Math.Max(0, counts[bin]);
            if (running >= target) break;
        }
        bin = Math.Min(bin, Math.Max(0, counts.Count - 1));
        if (bin == 0) return bounds[0] / 2;
        if (bin < bounds.Count) return (bounds[bin - 1] + bounds[bin]) / 2;
        var width = bounds.Count > 1 ? bounds[^1] - bounds[^2] : Math.Max(1, bounds[^1]);
        return bounds[^1] + width / 2;
    }

    private string AnomalySummary(AnomalyCandidate row)
    {
        var localized = T(row.Kind + "Summary");
        return localized == row.Kind + "Summary" ? row.Summary : localized;
    }

    private string AnomalySampleText(AnomalySample sample)
    {
        var parts = new List<string> { Clock(sample.ElapsedMs) };
        if (sample.Speed is { } speed) parts.Add($"{speed:0.00} yalm/s");
        if (sample.Distance is { } distance)
        {
            var range = sample.NominalRange is { } nominal
                ? $" / {nominal:0.0}+{sample.Tolerance ?? 0:0.0} yalm"
                : sample.Dt is { } dt ? $" / {dt:0.000}s" : string.Empty;
            parts.Add($"{distance:0.00} yalm{range}");
        }
        if (sample.LatencyMs is { } latency) parts.Add($"{latency}ms");
        if (sample.WeaveCount is { } weaveCount)
            parts.Add($"{T("weaveCount")} {weaveCount}");
        if (sample.MinimumIntervalMs is { } minimumInterval)
            parts.Add($"{T("minimumAbilityInterval")} {minimumInterval}ms");
        if (sample.WindowDurationMs is { } windowDuration)
            parts.Add($"{T("gcdWindowDuration")} {windowDuration}ms");
        if (sample.ActionIds is { Count: > 0 })
            parts.Add(string.Join(" → ", sample.ActionIds.Select(id => ActionName(id, $"#{id}"))));
        if (sample.SpeedStatusIds is { Count: > 0 })
            parts.Add($"{T("activeSpeedStatuses")}: {string.Join(", ", sample.SpeedStatusIds.Select(id => StatusName(id)))}");
        return string.Join(" · ", parts);
    }

    private void DrawDeathRecords(LocalMatchAnalysis match, DetailedAnalysis detail)
    {
        ImGui.TextWrapped(T("deathRecordHelp"));
        var deathRows = new List<(MovementPoint frame, ObservedActor actor)>();
        foreach (var marker in detail.DeathTimeline ?? [])
        {
            var hit = detail.MovementTimeline.Where(x => x.ElapsedMs <= marker.ElapsedMs)
                .SelectMany(frame => (frame.Actors ?? []).Where(actor => actor.EntityId == marker.TargetEntityId)
                    .Select(actor => (frame, actor))).LastOrDefault();
            if (hit.actor != null) deathRows.Add(hit);
        }
        if (deathRows.Count == 0)
            deathRows.AddRange(detail.MovementTimeline.SelectMany(frame =>
                (frame.Actors ?? []).Where(actor => actor.FellToZero).Select(actor => (frame, actor))));
        var deaths = deathRows.OrderBy(x => x.frame.ElapsedMs).ToArray();
        if (deaths.Length == 0) { ImGui.TextDisabled(T("noDeathRecords")); return; }
        if (!ImGui.BeginTable("death-layout", 2, ImGuiTableFlags.SizingFixedFit |
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable)) return;
        ImGui.TableSetupColumn("list", ImGuiTableColumnFlags.WidthFixed, 420);
        ImGui.TableSetupColumn("detail", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow(); ImGui.TableNextColumn();
        if (ImGui.BeginTable("death-list", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable, new Vector2(0, 520)))
        {
            foreach (var title in new[] { T("time"), T("player"), T("job"), T("team") }) ImGui.TableSetupColumn(title);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var death in deaths)
            {
                ImGui.TableNextRow(); Cell(Clock(death.frame.ElapsedMs));
                ImGui.TableNextColumn();
                if (DrawCompactFocusButton($"death-{death.frame.ElapsedMs}-{death.actor.EntityId}"))
                { selectedDeathEntityId = death.actor.EntityId; selectedDeathMs = death.frame.ElapsedMs; }
                ImGui.SameLine(0, 5); DrawCopyableText($"{death.actor.Name}@{WorldName(death.actor.WorldId)}");
                ImGui.TableNextColumn(); DrawJobIcon(death.actor.ClassJobId);
                ImGui.TableNextColumn(); DrawCurrentModeTeam(death.actor.Team, death.actor.Alliance);
            }
            ImGui.EndTable();
        }
        var selected = deaths.FirstOrDefault(x => x.actor.EntityId == selectedDeathEntityId &&
            x.frame.ElapsedMs == selectedDeathMs) is var found && found.actor != null ? found : deaths[0];
        ImGui.TableNextColumn();
        ImGui.TextColored(Cyan, $"{selected.actor.Name}@{WorldName(selected.actor.WorldId)} · {T("lastFifteenSeconds")}");
        DrawDeathTimeline(detail, selected.actor, selected.frame.ElapsedMs);
        ImGui.Spacing(); ImGui.TextColored(Cyan, T("behaviorRankings"));
        DrawPlayerBehaviorRanks(match, selected.actor.Name, selected.actor.WorldId);
        ImGui.EndTable();
    }

    private void DrawDeathTimeline(DetailedAnalysis detail, ObservedActor selected, long deathMs)
    {
        var startMs = deathMs - 15_000;
        var rows = new List<DeathTimelineRow>();
        rows.AddRange(detail.MovementTimeline
            .Where(x => x.ElapsedMs > startMs && x.ElapsedMs <= deathMs)
            .SelectMany(frame => (frame.Actors ?? []).Where(x => x.EntityId == selected.EntityId)
                .Select(actor => new DeathTimelineRow(frame.ElapsedMs,
                    actor.FellToZero ? "down" : "vitals",
                    TargetJobId: actor.ClassJobId, TargetName: actor.Name, TargetWorldId: actor.WorldId,
                    TargetTeam: actor.Team, TargetAlliance: actor.Alliance,
                    CurrentHp: actor.CurrentHp, MaxHp: actor.MaxHp,
                    CurrentMp: actor.CurrentMp, MaxMp: actor.MaxMp))));
        rows.AddRange((detail.Actions ?? [])
            .Where(x => x.ElapsedMs > startMs && x.ElapsedMs <= deathMs &&
                        (x.SourceEntityId == selected.EntityId || x.TargetEntityId == selected.EntityId))
            .Select(x => new DeathTimelineRow(x.ElapsedMs,
                x.Healing > 0 ? "healingEvent" : x.Damage > 0 ? "damageEvent" : "actionEvent",
                x.SourceJobId, x.SourceName, x.SourceWorldId, x.SourceTeam, x.SourceAlliance,
                x.ActionId, 0, x.ActionName,
                x.TargetJobId, x.TargetName, x.TargetWorldId, x.TargetTeam, x.TargetAlliance,
                x.Damage > 0 ? x.Damage : x.Healing,
                x.TargetCurrentHp, x.TargetMaxHp, x.TargetShieldPercent,
                ActionType: x.ActionType,
                SourceOwnerEntityId: x.SourceOwnerEntityId, SourceOwnerName: x.SourceOwnerName,
                SourceOwnerWorldId: x.SourceOwnerWorldId, SourceOwnerJobId: x.SourceOwnerJobId,
                TargetOwnerEntityId: x.TargetOwnerEntityId, TargetOwnerName: x.TargetOwnerName,
                TargetOwnerWorldId: x.TargetOwnerWorldId, TargetOwnerJobId: x.TargetOwnerJobId,
                EffectKind: x.EffectKind)));
        var exactStatuses = (detail.StatusTimeline ?? [])
            .Where(x => x.ElapsedMs > startMs && x.ElapsedMs <= deathMs && x.TargetEntityId == selected.EntityId)
            .ToArray();
        rows.AddRange(exactStatuses.Select(x =>
        {
            var source = FindActor(detail, x.SourceEntityId, x.ElapsedMs);
            var target = FindActor(detail, x.TargetEntityId, x.ElapsedMs) ?? selected;
            return new DeathTimelineRow(x.ElapsedMs, x.Change == "remove" ? "statusRemoved" : "statusApplied",
                source?.ClassJobId ?? 0, source?.Name ?? string.Empty, source?.WorldId ?? 0,
                source?.Team ?? 255, source?.Alliance ?? 255,
                StatusId: x.StatusId,
                TargetJobId: target.ClassJobId, TargetName: target.Name, TargetWorldId: target.WorldId,
                TargetTeam: target.Team, TargetAlliance: target.Alliance);
        }));
        if (exactStatuses.Length == 0)
        {
            rows.AddRange(detail.MovementTimeline
                .Where(x => x.ElapsedMs > startMs && x.ElapsedMs <= deathMs)
                .SelectMany(frame => (frame.Actors ?? []).Where(x => x.EntityId == selected.EntityId)
                    .SelectMany(actor => (actor.Statuses ?? []).Select(status =>
                    {
                        var source = FindActor(detail, status.SourceEntityId, frame.ElapsedMs);
                        return new DeathTimelineRow(frame.ElapsedMs, "statusObserved",
                            source?.ClassJobId ?? 0, source?.Name ?? string.Empty, source?.WorldId ?? 0,
                            source?.Team ?? 255, source?.Alliance ?? 255,
                            StatusId: status.StatusId,
                            TargetJobId: actor.ClassJobId, TargetName: actor.Name, TargetWorldId: actor.WorldId,
                            TargetTeam: actor.Team, TargetAlliance: actor.Alliance);
                    }))));
        }
        rows.Add(new DeathTimelineRow(deathMs, "down",
            TargetJobId: selected.ClassJobId, TargetName: selected.Name, TargetWorldId: selected.WorldId,
            TargetTeam: selected.Team, TargetAlliance: selected.Alliance));

        if (!ImGui.BeginTable("death-unified-timeline", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, 520))) return;
        var deathHeaders = new[] { T("time"), T("eventType"), T("source"), T("actionOrStatus"),
                     T("target"), T("amount"), T("hpAndShield"), "MP" };
        ImGui.TableSetupColumn(deathHeaders[0], ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn(deathHeaders[1], ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn(deathHeaders[2], ImGuiTableColumnFlags.WidthStretch, 1.25f);
        ImGui.TableSetupColumn(deathHeaders[3], ImGuiTableColumnFlags.WidthStretch, 1.15f);
        ImGui.TableSetupColumn(deathHeaders[4], ImGuiTableColumnFlags.WidthStretch, 1.25f);
        ImGui.TableSetupColumn(deathHeaders[5], ImGuiTableColumnFlags.WidthFixed, 82);
        ImGui.TableSetupColumn(deathHeaders[6], ImGuiTableColumnFlags.WidthFixed, 145);
        ImGui.TableSetupColumn(deathHeaders[7], ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        foreach (var row in rows.OrderBy(x => x.ElapsedMs).ThenBy(x => x.Kind))
        {
            ImGui.TableNextRow(); Cell(Clock(row.ElapsedMs)); Cell(T(row.Kind));
            ImGui.TableNextColumn();
            if (string.IsNullOrWhiteSpace(row.SourceName) && row.EffectKind.StartsWith("dot"))
                ImGui.TextDisabled(T("periodicSourceUnresolved"));
            else if (string.IsNullOrWhiteSpace(row.SourceName) && row.EffectKind.StartsWith("hot"))
                ImGui.TextDisabled(T("periodicSourceUnresolved"));
            else if (string.IsNullOrWhiteSpace(row.SourceName) && row.ActionId != 0)
                ImGui.TextDisabled(T("gameMechanic"));
            else if (string.IsNullOrWhiteSpace(row.SourceName)) ImGui.TextDisabled("—");
            else DrawActorIdentity(row.SourceJobId, row.SourceName, row.SourceWorldId, row.SourceTeam,
                row.SourceAlliance, row.SourceOwnerName, row.SourceOwnerWorldId,
                row.SourceOwnerJobId, row.SourceOwnerEntityId, allowFocus: false);
            ImGui.TableNextColumn();
            if (row.StatusId != 0) DrawStatusIcon(row.StatusId);
            else if (row.ActionId != 0 || !string.IsNullOrWhiteSpace(row.FallbackName))
                DrawActionIcon(row.ActionId, row.FallbackName, row.ActionType, row.EffectKind);
            else ImGui.TextDisabled("—");
            ImGui.TableNextColumn();
            if (string.IsNullOrWhiteSpace(row.TargetName)) ImGui.TextDisabled("—");
            else DrawActorIdentity(row.TargetJobId, row.TargetName, row.TargetWorldId, row.TargetTeam,
                row.TargetAlliance, row.TargetOwnerName, row.TargetOwnerWorldId,
                row.TargetOwnerJobId, row.TargetOwnerEntityId, allowFocus: false);
            Cell(row.Amount == 0 ? "—" : row.Amount.ToString("N0"));
            ImGui.TableNextColumn();
            DrawHpShieldBar(row.CurrentHp, row.MaxHp, row.ShieldPercent, new Vector2(136, 16));
            Cell(row.MaxMp == 0 ? "—" : $"{row.CurrentMp:N0}/{row.MaxMp:N0}");
        }
        ImGui.EndTable();
    }

    private void DrawDeathVitals(DetailedAnalysis detail, uint entityId, long deathMs)
    {
        var rows = detail.MovementTimeline
            .Where(x => x.ElapsedMs > deathMs - 15_000 && x.ElapsedMs <= deathMs)
            .SelectMany(frame => (frame.Actors ?? []).Where(x => x.EntityId == entityId).Select(x => (frame.ElapsedMs, Actor: x)))
            .ToArray();
        if (!ImGui.BeginTable("death-vitals", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable, new Vector2(0, 215))) return;
        foreach (var title in new[] { T("time"), "HP", "MP", T("state") }) ImGui.TableSetupColumn(title);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        foreach (var row in rows)
        {
            ImGui.TableNextRow(); Cell(Clock(row.ElapsedMs)); Cell($"{row.Actor.CurrentHp:N0}/{row.Actor.MaxHp:N0}");
            Cell($"{row.Actor.CurrentMp:N0}/{row.Actor.MaxMp:N0}");
            Cell(row.Actor.FellToZero ? T("down") : T("active"));
        }
        ImGui.EndTable();
    }

    private void DrawDeathStatuses(DetailedAnalysis detail, uint entityId, long deathMs)
    {
        var exact = (detail.StatusTimeline ?? []).Where(x => x.TargetEntityId == entityId &&
            x.ElapsedMs > deathMs - 15_000 && x.ElapsedMs <= deathMs).ToArray();
        if (exact.Length > 0)
        {
            if (!ImGui.BeginTable("death-status-events", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.BordersInnerH, new Vector2(0, 145))) return;
            foreach (var title in new[] { T("time"), T("status"), T("change"), T("source"), T("remaining") }) ImGui.TableSetupColumn(title);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var row in exact)
            {
                ImGui.TableNextRow(); Cell(Clock(row.ElapsedMs));
                ImGui.TableNextColumn(); DrawStatusIcon(row.StatusId);
                Cell(row.Change == "remove" ? T("removed") : T("applied"));
                var source = detail.MovementTimeline.Where(x => x.ElapsedMs <= row.ElapsedMs)
                    .SelectMany(x => x.Actors ?? []).LastOrDefault(x => x.EntityId == row.SourceEntityId);
                Cell(source == null ? "—" : $"{source.Name}@{WorldName(source.WorldId)}");
                Cell(row.DurationSeconds.ToString("0.0"));
            }
            ImGui.EndTable(); return;
        }
        var rows = detail.MovementTimeline
            .Where(x => x.ElapsedMs > deathMs - 15_000 && x.ElapsedMs <= deathMs)
            .SelectMany(frame => (frame.Actors ?? []).Where(x => x.EntityId == entityId)
                .SelectMany(actor => (actor.Statuses ?? []).Select(status => (frame.ElapsedMs, status))))
            .ToArray();
        if (rows.Length == 0) { ImGui.TextDisabled(T("statusesNotCaptured")); return; }
        if (!ImGui.BeginTable("death-statuses", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.BordersInnerH, new Vector2(0, 145))) return;
        foreach (var title in new[] { T("time"), T("status"), T("source"), T("remaining") }) ImGui.TableSetupColumn(title);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        foreach (var row in rows)
        {
            ImGui.TableNextRow(); Cell(Clock(row.ElapsedMs)); ImGui.TableNextColumn(); DrawStatusIcon(row.status.StatusId);
            Cell(row.status.SourceEntityId == 0 ? "—" : row.status.SourceEntityId.ToString("X"));
            Cell(row.status.RemainingSeconds.ToString("0.0"));
        }
        ImGui.EndTable();
    }

    private void DrawPlayerBehaviorRanks(LocalMatchAnalysis match, string name, ushort worldId)
    {
        var sources = new[] { "four_small", "commander", "macro_sender", "solo", "disruptor", "follow_fire",
            "single_target", "melee_firestarter", "ranged_firestarter" };
        if (!ImGui.BeginTable("death-behaviors", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.BordersInnerH, new Vector2(0, 180))) return;
        foreach (var title in new[] { T("tendency"), T("rank"), "Logs" }) ImGui.TableSetupColumn(title);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        foreach (var key in sources)
        {
            var all = match.Players.Select(p => (p, v: TendenciesFor(match, p).FirstOrDefault(x => x.Key == key)))
                .Where(x => x.v != null).ToArray();
            var selected = all.FirstOrDefault(x => x.p.Name == name && x.p.WorldId == worldId);
            ImGui.TableNextRow(); Cell(T(key));
            if (selected.v == null) { Cell("—"); Cell("—"); continue; }
            var ordered = all.OrderByDescending(x => x.v!.Score).ToArray();
            Cell($"{Array.FindIndex(ordered, x => x.p.Name == name && x.p.WorldId == worldId) + 1}/{ordered.Length}");
            ImGui.TableNextColumn(); ImGui.TextColored(LogsColor(selected.v.Score), selected.v.Score.ToString("0.0"));
        }
        ImGui.EndTable();
    }

    private void DrawChartBackground(ImDrawListPtr draw, Vector2 origin, Vector2 size)
    {
        draw.AddRectFilled(origin, origin + size,
            ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.ChartBackground(configuration.UiTheme)), 2);
        draw.AddRect(origin, origin + size,
            ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Border(configuration.UiTheme)), 2);
        for (var i = 1; i < 5; i++)
        {
            var y = origin.Y + i * size.Y / 5;
            draw.AddLine(new Vector2(origin.X + 10, y), new Vector2(origin.X + size.X - 10, y),
                ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Grid(configuration.UiTheme)));
        }
    }

    private static void DrawCentered(ImDrawListPtr draw, Vector2 origin, Vector2 size, string text, Vector4 color)
    {
        var extent = ImGui.CalcTextSize(text);
        draw.AddText(origin + (size - extent) / 2, ImGui.ColorConvertFloat4ToU32(color), text);
    }

    private static ScorePoint? NearestScore(DetailedAnalysis detail, long ms) =>
        detail.ScoreTimeline.LastOrDefault(x => x.ElapsedMs <= ms) ?? detail.ScoreTimeline.FirstOrDefault();
    private static MovementPoint? NearestMovement(DetailedAnalysis detail, long ms) =>
        detail.MovementTimeline.OrderBy(x => Math.Abs(x.ElapsedMs - ms)).FirstOrDefault();
    private static ObservedActor? FindActor(DetailedAnalysis detail, uint entityId, long ms) =>
        entityId == 0 ? null : detail.MovementTimeline.Where(x => x.ElapsedMs <= ms)
            .SelectMany(x => x.Actors ?? []).LastOrDefault(x => x.EntityId == entityId);
    private bool IsPlayerFocused(string name, ushort worldId, uint entityId = 0) =>
        focusedPlayerName != null && name == focusedPlayerName &&
        (focusedPlayerWorldId == 0 || worldId == 0 || worldId == focusedPlayerWorldId) ||
        focusedPlayerName == null && selectedActorId != null && entityId == selectedActorId;
    private bool IsPlayerFocused(ObservedActor actor) => IsPlayerFocused(actor.Name, actor.WorldId, actor.EntityId);
    private bool IsPlayerFocused(LocalPlayerAnalysis player) => IsPlayerFocused(player.Name, player.WorldId);
    private bool IsPlayerFocused(ActionSliceEvent row) =>
        IsPlayerFocused(row.SourceOwnerName, row.SourceOwnerWorldId, row.SourceOwnerEntityId) ||
        IsPlayerFocused(row.SourceName, row.SourceWorldId, row.SourceEntityId) ||
        IsPlayerFocused(row.TargetOwnerName, row.TargetOwnerWorldId, row.TargetOwnerEntityId) ||
        IsPlayerFocused(row.TargetName, row.TargetWorldId, row.TargetEntityId);
    private void SetFocus(ObservedActor actor)
    {
        if (IsPlayerFocused(actor)) { focusedPlayerName = null; focusedPlayerWorldId = 0; selectedActorId = null; return; }
        focusedPlayerName = actor.Name; focusedPlayerWorldId = actor.WorldId; selectedActorId = actor.EntityId;
    }
    private void SetFocus(LocalPlayerAnalysis player, DetailedAnalysis detail)
    {
        if (IsPlayerFocused(player)) { focusedPlayerName = null; focusedPlayerWorldId = 0; selectedActorId = null; return; }
        focusedPlayerName = player.Name; focusedPlayerWorldId = player.WorldId;
        selectedActorId = detail.MovementTimeline.SelectMany(x => x.Actors ?? [])
            .FirstOrDefault(x => x.Name == player.Name && (x.WorldId == 0 || x.WorldId == player.WorldId))?.EntityId;
    }
    private void ToggleFocus(string name, ushort worldId, uint entityId = 0)
    {
        if (IsPlayerFocused(name, worldId, entityId))
        {
            focusedPlayerName = null; focusedPlayerWorldId = 0; selectedActorId = null;
            return;
        }
        focusedPlayerName = name; focusedPlayerWorldId = worldId;
        selectedActorId = entityId == 0 ? null : entityId;
    }
    private static string Clock(long ms) => TimeSpan.FromMilliseconds(ms).ToString(@"mm\:ss");
    private static IReadOnlyList<BehaviorTendency> TendenciesFor(LocalMatchAnalysis match, LocalPlayerAnalysis player) =>
        match.BehaviorTable?.TryGetValue($"{player.Name}@{player.WorldId}", out var rows) == true ? rows : [];
    private void Cell(string value) { ImGui.TableNextColumn(); DrawCopyableText(value); }

    private void DrawCopyableText(string value, Vector4? color = null)
    {
        var text = value ?? string.Empty;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(.22f, .30f, .34f, .18f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(.22f, .30f, .34f, .25f));
        if (color.HasValue) ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.SetNextItemWidth(Math.Max(24,
            Math.Min(ImGui.GetContentRegionAvail().X, ImGui.CalcTextSize(text).X + 10)));
        if (!copyTextBuffers.TryGetValue(text, out var buffer))
        {
            buffer = System.Text.Encoding.UTF8.GetBytes(text + "\0");
            copyTextBuffers[text] = buffer;
        }
        ImGui.InputText($"##copyable-{copyFieldSequence++}", buffer.AsSpan(), ImGuiInputTextFlags.ReadOnly);
        ImGui.PopStyleVar(2);
        if (color.HasValue) ImGui.PopStyleColor();
        ImGui.PopStyleColor(3);
    }
    private string TeamName(byte team) => team switch { 0 => T("maelstrom"), 1 => T("adders"), 2 => T("flames"), _ => T("unknownTeam") };
    private string WorldName(ushort id)
    {
        if (worldNames.TryGetValue(id, out var value)) return value;
        try { value = dataManager.GetExcelSheet<WorldSheet>().GetRow(id).Name.ToString(); } catch { value = id.ToString(); }
        return worldNames[id] = string.IsNullOrWhiteSpace(value) ? id.ToString() : value;
    }
    private string JobName(byte id)
    {
        if (jobNames.TryGetValue(id, out var value)) return value;
        try { value = dataManager.GetExcelSheet<ClassJobSheet>().GetRow(id).Abbreviation.ToString(); } catch { value = id.ToString(); }
        return jobNames[id] = string.IsNullOrWhiteSpace(value) ? id.ToString() : value;
    }
    private string StatusName(long id)
    {
        var language = EffectiveLanguage();
        var key = (language, id);
        if (statusNames.TryGetValue(key, out var value)) return value;
        value = statusCatalog.Find(id, language) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) && language is not ("zh-CN" or "zh-TW"))
        {
            try
            {
                var sheetLanguage = language == "ja" ? ClientLanguage.Japanese : ClientLanguage.English;
                value = dataManager.GetExcelSheet<StatusSheet>(sheetLanguage).GetRow((uint)id).Name.ToString();
            }
            catch { /* Keep the explicit localized fallback below. */ }
        }
        return statusNames[key] = string.IsNullOrWhiteSpace(value) ? $"{T("status")} #{id}" : value;
    }
    private void DrawJobIcon(byte classJobId)
    {
        try
        {
            if (!jobIcons.TryGetValue(classJobId, out var shared))
                jobIcons[classJobId] = shared = textureProvider.GetFromGameIcon(
                    new GameIconLookup((uint)(62100 + classJobId), hiRes: true));
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, new Vector2(22, 22));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(JobName(classJobId));
                return;
            }
        }
        catch { /* Fall through to a neutral missing-icon marker. */ }
        ImGui.TextDisabled("?");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(JobName(classJobId));
    }
    private void DrawActorIdentity(byte classJobId, string name, ushort worldId,
        byte team = 255, byte alliance = 255, string ownerName = "", ushort ownerWorldId = 0,
        byte ownerJobId = 0, uint entityId = uint.MaxValue, bool allowFocus = true,
        long? focusElapsedMs = null)
    {
        var isSummon = !string.IsNullOrWhiteSpace(ownerName);
        var focusName = isSummon ? ownerName : name;
        var focusWorld = isSummon ? ownerWorldId : worldId;
        if (allowFocus && !string.IsNullOrWhiteSpace(focusName))
        {
            var focusClicked = DrawCompactFocusButton($"focus-actor-{copyFieldSequence++}");
            if (focusClicked)
            {
                ToggleFocus(focusName, focusWorld, isSummon ? 0 : entityId);
                if (focusElapsedMs.HasValue) selectedMs = focusElapsedMs.Value;
            }
            if (focusElapsedMs.HasValue && ImGui.IsItemHovered()) ImGui.SetTooltip(T("focusMarkedPlayer"));
            ImGui.SameLine(0, 4);
        }
        DrawJobIcon(isSummon && ownerJobId != 0 ? ownerJobId : classJobId); ImGui.SameLine(0, 4);
        var world = worldId == 0 ? string.Empty : "@" + WorldName(worldId);
        if (isSummon)
        {
            var ownerWorld = ownerWorldId == 0 ? string.Empty : "@" + WorldName(ownerWorldId);
            var summonName = string.IsNullOrWhiteSpace(name) ? T("summon") : LocalizedActorName(name);
            DrawCopyableText($"{summonName} ({T("owner")}: {ownerName}{ownerWorld})");
        }
        else if (!string.IsNullOrWhiteSpace(name)) DrawCopyableText(name + world);
        else if (entityId == 0) ImGui.TextDisabled(T("environmentSource"));
        else ImGui.TextDisabled(entityId == uint.MaxValue ? T("unresolvedActor") : $"{T("unresolvedActor")} #{entityId:X8}");
        if (team < 3)
        {
            ImGui.SameLine(0, 5);
            DrawCurrentModeTeam(team, alliance);
        }
    }
    private void DrawActionSource(ActionSliceEvent row)
    {
        if (row.SourceEntityId != 0 || !string.IsNullOrWhiteSpace(row.SourceName) ||
            !string.IsNullOrWhiteSpace(row.SourceOwnerName))
        {
            DrawActorIdentity(row.SourceJobId, row.SourceName, row.SourceWorldId,
                row.SourceTeam, row.SourceAlliance, row.SourceOwnerName, row.SourceOwnerWorldId,
                row.SourceOwnerJobId, row.SourceEntityId);
            return;
        }
        if (row.EffectKind.StartsWith("dot", StringComparison.Ordinal) ||
            row.EffectKind.StartsWith("hot", StringComparison.Ordinal))
            ImGui.TextDisabled(T("periodicSourceUnresolved"));
        else if (row.ActionId != 0) ImGui.TextDisabled(T("gameMechanic"));
        else ImGui.TextDisabled(T("unresolvedActor"));
    }
    private void DrawTeamSquad(byte team, byte alliance)
    {
        var color = team < TeamColors.Length ? TeamColors[team] : Muted;
        if (alliance >= 9) { ImGui.TextColored(color, "?"); return; }
        var label = ((char)('A' + alliance % 3)).ToString();
        var at = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"{label}##squad-{copyFieldSequence++}", new Vector2(15, ImGui.GetTextLineHeight()));
        var packed = ImGui.ColorConvertFloat4ToU32(color);
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(at, packed, label);
        draw.AddText(at + new Vector2(.65f, 0), packed, label);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{TeamName(team)} · {label}");
    }

    private void DrawCurrentModeTeam(byte team, byte alliance)
    {
        if (!currentMatchIsCrystalline)
        {
            DrawTeamSquad(team, alliance);
            return;
        }
        var color = team switch
        {
            0 => new Vector4(.24f, .62f, 1f, 1f),
            1 => new Vector4(.95f, .27f, .30f, 1f),
            _ => Muted
        };
        ImGui.TextColored(color, T(team == 0 ? "ccAstra" : team == 1 ? "ccUmbra" : "unknownTeam"));
    }

    private static bool DrawCompactFocusButton(string id)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(1, 0));
        var clicked = ImGui.SmallButton($"◎##{id}");
        ImGui.PopStyleVar();
        return clicked;
    }

    private string LocalizedActorName(string name)
    {
        if (!name.Equals("Demi-Bahamut", StringComparison.OrdinalIgnoreCase)) return name;
        return T("demiBahamut");
    }
    private ClientLanguage? SheetLanguage() => configuration.Language switch
    {
        "en" => ClientLanguage.English,
        "ja" => ClientLanguage.Japanese,
        // CN data uses the client's native sheet. Official Dalamud exposes only JA/EN/DE/FR overrides.
        _ => null
    };
    private string EffectiveLanguage() => configuration.Language == "auto"
        ? (CultureInfo.CurrentUICulture.Name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
           CultureInfo.CurrentUICulture.Name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
           CultureInfo.CurrentUICulture.Name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ? "zh-TW"
          : CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN"
          : CultureInfo.CurrentUICulture.Name.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "ja" : "en")
        : configuration.Language;
    private string ActionName(uint actionId, string fallbackName)
    {
        if (actionId == 0) return fallbackName;
        var language = EffectiveLanguage();
        var key = (language, actionId);
        if (actionNames.TryGetValue(key, out var cached)) return cached;
        var catalogName = actionCatalog.Find(actionId, language);
        if (!string.IsNullOrWhiteSpace(catalogName)) return actionNames[key] = catalogName;
        // Global game sheets do not contain Chinese action names. Do not silently
        // replace an explicitly selected Chinese label with an English one.
        if (language is "zh-CN" or "zh-TW")
            return actionNames[key] = string.IsNullOrWhiteSpace(fallbackName) ? $"{T("action")} #{actionId}" : fallbackName;
        var name = fallbackName;
        try
        {
            var sheetLanguage = language == "ja" ? ClientLanguage.Japanese : ClientLanguage.English;
            var localized = dataManager.GetExcelSheet<ActionSheet>(sheetLanguage).GetRow(actionId).Name.ToString();
            if (!string.IsNullOrWhiteSpace(localized)) name = localized;
        }
        catch { /* Preserve the captured fallback name or action ID. */ }
        return actionNames[key] = string.IsNullOrWhiteSpace(name) ? $"{T("action")} #{actionId}" : name;
    }
    private void DrawActionGlyph(uint actionId, float size = 22)
    {
        if (actionId == 0) { ImGui.Dummy(new Vector2(size, size)); return; }
        try
        {
            var action = dataManager.GetExcelSheet<ActionSheet>(SheetLanguage()).GetRow(actionId);
            if (!actionIcons.TryGetValue(actionId, out var shared))
                actionIcons[actionId] = shared = textureProvider.GetFromGameIcon(
                    new GameIconLookup(action.Icon, hiRes: true));
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, new Vector2(size, size));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(ActionName(actionId, $"#{actionId}"));
                return;
            }
        }
        catch { /* Keep layout stable when the icon sheet is unavailable. */ }
        ImGui.TextDisabled("?");
    }

    private void DrawActionIcon(uint actionId, string fallbackName, byte actionType = 1,
        string effectKind = "")
    {
        if (actionId == 0 && fallbackName.StartsWith("status:", StringComparison.Ordinal) &&
            long.TryParse(fallbackName.AsSpan(7), out var statusId))
        {
            DrawStatusIcon(statusId);
            if (effectKind.StartsWith("dot", StringComparison.Ordinal))
            { ImGui.SameLine(0, 5); ImGui.TextDisabled(T("periodicDamageSettlement")); }
            else if (effectKind.StartsWith("hot", StringComparison.Ordinal))
            { ImGui.SameLine(0, 5); ImGui.TextDisabled(T("periodicHealingSettlement")); }
            return;
        }
        if (actionId == 0)
        {
            ImGui.TextDisabled(effectKind.StartsWith("dot", StringComparison.Ordinal) ? T("periodicDamageSettlement")
                : effectKind.StartsWith("hot", StringComparison.Ordinal) ? T("periodicHealingSettlement")
                : T("unknownActionEvent"));
            return;
        }
        var name = ActionName(actionId, fallbackName);
        try
        {
            if (actionType != 1) throw new InvalidOperationException("The event is not an Action-sheet action.");
            var action = dataManager.GetExcelSheet<ActionSheet>(SheetLanguage()).GetRow(actionId);
            if (!actionIcons.TryGetValue(actionId, out var shared))
                actionIcons[actionId] = shared = textureProvider.GetFromGameIcon(
                    new GameIconLookup(action.Icon, hiRes: true));
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null) ImGui.Image(wrap.Handle, new Vector2(22, 22));
        }
        catch { /* The action name and ID remain visible when an icon is unavailable. */ }
        ImGui.SameLine(0, 5); DrawCopyableText(string.IsNullOrWhiteSpace(name) ? $"#{actionId}" : name);
    }
    private void DrawStatusIcon(long statusId)
    {
        var name = StatusName(statusId);
        try
        {
            var status = dataManager.GetExcelSheet<StatusSheet>(SheetLanguage()).GetRow((uint)statusId);
            if (!statusIcons.TryGetValue(statusId, out var shared))
                statusIcons[statusId] = shared = textureProvider.GetFromGameIcon(
                    new GameIconLookup(status.Icon, hiRes: true));
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null) ImGui.Image(wrap.Handle, new Vector2(22, 22));
        }
        catch { ImGui.TextDisabled("?"); }
        ImGui.SameLine(0, 5); DrawCopyableText(name);
    }
    private void DrawStatusGlyph(long statusId, float size = 22)
    {
        try
        {
            var status = dataManager.GetExcelSheet<StatusSheet>(SheetLanguage()).GetRow((uint)statusId);
            if (!statusIcons.TryGetValue(statusId, out var shared))
                statusIcons[statusId] = shared = textureProvider.GetFromGameIcon(
                    new GameIconLookup(status.Icon, hiRes: true));
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, new Vector2(size, size));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(StatusName(statusId));
                return;
            }
        }
        catch { /* The status name remains available through the timeline below. */ }
        var fallback = $"#{statusId}";
        ImGui.PushStyleColor(ImGuiCol.Button, BattlefieldUiTheme.Panel(configuration.UiTheme));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BattlefieldUiTheme.SelectedPanel(configuration.UiTheme));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        ImGui.SmallButton(fallback);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(StatusName(statusId));
    }
    private static Vector4 LogsColor(double value) => value >= 99 ? new Vector4(1f, .78f, .2f, 1)
        : value >= 95 ? new Vector4(1f, .35f, .78f, 1) : value >= 75 ? new Vector4(1f, .55f, .2f, 1)
        : value >= 50 ? new Vector4(.3f, .65f, 1f, 1) : value >= 25 ? new Vector4(.35f, .8f, .45f, 1)
        : new Vector4(.6f, .62f, .65f, 1);
    private string T(string key) => I18n.Get(configuration.Language, key);
    private string ConfidenceLabel(string value) => value switch
    {
        "low" => T("confidenceLow"), "medium" => T("confidenceMedium"),
        "high" => T("confidenceHigh"), _ => value
    };
}
