using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ActionSheet = Lumina.Excel.Sheets.Action;
using StatusSheet = Lumina.Excel.Sheets.Status;
using WorldSheet = Lumina.Excel.Sheets.World;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Encounter-relative 15/30/60 second combat buffer. A quiet gap completes the
/// encounter but keeps its final actions available until the next encounter.
/// 以交战结束为锚点的缓冲池；停战后仍保留最后一段技能，直到下一次交战。
/// </summary>
internal sealed class LiveCombatWindow : Window
{
    private enum LiveSide { Unknown, Own, Enemy }
    private static readonly Vector4[] TeamColors =
    [
        new(.96f, .31f, .37f, 1f),
        new(.97f, .76f, .23f, 1f),
        new(.28f, .64f, 1f, 1f)
    ];
    private static readonly Vector4 OwnSideColor = new(.30f, .78f, 1f, 1f);
    private static readonly Vector4 EnemySideColor = new(1f, .48f, .42f, 1f);
    private readonly Configuration configuration;
    private readonly LiveCombatBuffer buffer;
    private readonly LiveMarkerState markers;
    private readonly Func<CollectorUiState> state;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly Func<uint, string, ushort, bool> tryTargetVisiblePlayer;
    private readonly ActionLocalizationCatalog actionCatalog;
    private readonly StatusLocalizationCatalog statusCatalog;
    private readonly string assetRoot;
    private readonly Dictionary<ushort, string> worldNames = new();
    private readonly Dictionary<(string Language, long Id), string> statusNames = new();
    private readonly Dictionary<byte, ISharedImmediateTexture> jobIcons = new();
    private readonly Dictionary<uint, ISharedImmediateTexture> actionIcons = new();
    private readonly Dictionary<uint, ISharedImmediateTexture> mapTextures = new();
    private readonly Dictionary<uint, ISharedImmediateTexture> markerIcons = new();
    private IReadOnlyList<LiveCombatRow> cached = [];
    private IReadOnlyList<LiveCoordinationRow> cachedCoordination = [];
    private IReadOnlyList<LiveCombatRow> cachedMatchTendencyRows = [];
    private IReadOnlyList<LiveCoordinationRow> cachedMatchCoordination = [];
    private IReadOnlyList<LiveObservedKda> cachedObservedKda = [];
    private long cachedMatchEvidenceEndMs = -1;
    private long cachedEndMs = -1;
    private bool cachedEncounterActive;
    private long nextRefreshAt;
    private long copiedUntil;
    private bool frozen;
    private int seconds = 30;
    // The live slice opens on the question players most often ask after an
    // unexpected hit: "what did I just take?"
    // 即时切片默认只看本机承受，减少打开页面后的第一次操作。
    private int direction = 2;
    private string activeMatchUid = string.Empty;
    private int viewMode;
    private float markerMapZoom = 1.45f;
    private Vector2 markerMapCenterUv = new(.5f, .5f);

    private sealed record MapAsset(string FileName, string NameKey);
    private static readonly IReadOnlyDictionary<uint, MapAsset> MapAssets = new Dictionary<uint, MapAsset>
    {
        [127] = new("borderland_ruins.png", "mapBorderland"),
        [130] = new("seal_rock.png", "mapSealRock"),
        [180] = new("fields_of_glory.png", "mapShatter"),
        [701] = new("onsal_hakair.png", "mapOnsal"),
        [1080] = new("worqor_chirteh.png", "mapWorqor")
    };

    public LiveCombatWindow(Configuration configuration, LiveCombatBuffer buffer, LiveMarkerState markers,
        Func<CollectorUiState> state, IDataManager dataManager, ITextureProvider textureProvider,
        Func<uint, string, ushort, bool> tryTargetVisiblePlayer, string assetRoot)
        : base("Battlefield Chronicle · Live combat###BattlefieldLiveCombat")
    {
        this.configuration = configuration; this.buffer = buffer; this.markers = markers; this.state = state;
        this.dataManager = dataManager; this.textureProvider = textureProvider;
        this.tryTargetVisiblePlayer = tryTargetVisiblePlayer;
        actionCatalog = new ActionLocalizationCatalog(assetRoot);
        statusCatalog = new StatusLocalizationCatalog(assetRoot);
        this.assetRoot = assetRoot;
        Size = new Vector2(1180, 540); SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    public override void PreDraw() => BattlefieldUiTheme.Push(configuration.UiTheme);
    public override void PostDraw() => BattlefieldUiTheme.Pop();

    public override void Draw()
    {
        WindowName = T("liveCombatTitle") + "###BattlefieldLiveCombat";
        var ui = state();
        if (ui.MatchUid.Length > 0 && ui.MatchUid != activeMatchUid)
        {
            activeMatchUid = ui.MatchUid; frozen = false; cached = []; cachedCoordination = [];
            cachedMatchTendencyRows = []; cachedMatchCoordination = [];
            cachedObservedKda = [];
            cachedEndMs = -1; cachedMatchEvidenceEndMs = -1; nextRefreshAt = 0;
        }
        var now = Environment.TickCount64;
        if (!frozen && now >= nextRefreshAt)
        {
            Refresh(ui);
            nextRefreshAt = now + 250;
        }
        ImGui.TextColored(ui.Capturing ? new Vector4(.25f,.85f,.62f,1) : new Vector4(.65f,.7f,.76f,1),
            ui.Capturing ? T("recording") : T("waiting"));
        ImGui.SameLine(); ImGui.TextDisabled(T("liveObserverBoundary"));
        if (ui.Capturing)
        {
            DrawLocalObservedKdaHeader(ui);
            DrawLiveScoreHeader(ui);
        }

        if (ImGui.Button(T("liveCombatSlice") + (viewMode == 0 ? "  ●" : string.Empty))) viewMode = 0;
        ImGui.SameLine();
        if (ImGui.Button(T("liveMarkerMap") + (viewMode == 1 ? "  ●" : string.Empty))) viewMode = 1;
        ImGui.SameLine();
        if (ImGui.Button(T("liveTendencyCandidates") + (viewMode == 2 ? "  ●" : string.Empty))) viewMode = 2;
        ImGui.SameLine();
        if (ImGui.Button(T("liveObservedKda") + (viewMode == 3 ? "  ●" : string.Empty))) viewMode = 3;
        ImGui.Separator();
        if (viewMode == 1) { DrawLiveMarkerMap(ui); return; }
        if (viewMode == 2)
        {
            DrawLiveTendencyCandidates(ui);
            return;
        }
        if (viewMode == 3) { DrawObservedKda(); return; }

        ImGui.SetNextItemWidth(155);
        var options = new[] { 15, 30, 60 }; var index = Array.IndexOf(options, seconds);
        if (ImGui.Combo(T("secondsBeforeEncounterEnd"), ref index, "15 s\0 30 s\0 60 s\0")) seconds = options[index];
        ImGui.SameLine(); ImGui.SetNextItemWidth(190);
        ImGui.Combo(T("liveDirection"), ref direction,
            $"{T("liveAll")}\0{T("liveOutgoing")}\0{T("liveIncoming")}\0");
        ImGui.SameLine();
        if (ImGui.Button(frozen ? T("resumeBuffer") : T("freezeBuffer")))
        {
            if (!frozen) { Refresh(ui); frozen = true; }
            else { frozen = false; nextRefreshAt = 0; }
        }

        ImGui.SameLine();
        var bufferState = frozen ? T("bufferFrozen") : cachedEncounterActive ? T("encounterActive") : T("lastEncounterStored");
        ImGui.TextColored(frozen ? new Vector4(1f,.72f,.28f,1) : new Vector4(.45f,.8f,1f,1), bufferState);
        if (copiedUntil > now) { ImGui.SameLine(); ImGui.TextColored(new Vector4(.35f,.9f,.58f,1), T("copied")); }
        ImGui.TextDisabled(T("combatBufferHelp"));
        var cutoff = cachedEndMs - seconds * 1000L;
        if (!ImGui.BeginTable("live-actions", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, -1))) return;
        ImGui.TableSetupColumn(T("beforeEncounterEnd"), ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn(T("source"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(T("action"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(T("target"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(T("amount"), ImGuiTableColumnFlags.WidthFixed, 92);
        ImGui.TableSetupColumn(T("hpAndShield"), ImGuiTableColumnFlags.WidthFixed, 176);
        ImGui.TableSetupColumn(T("operations"), ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        for (var i = cached.Count - 1; i >= 0; i--)
        {
            var row = cached[i];
            if (row.ElapsedMs < cutoff) continue;
            var outgoing = IsLocal(row.SourceEntityId, row.SourceName, row.SourceWorldId, ui);
            var incoming = IsLocal(row.TargetEntityId, row.TargetName, row.TargetWorldId, ui);
            if (direction == 1 && !outgoing || direction == 2 && !incoming) continue;
            var sourceSide = incoming && !outgoing && row.Kind == "damage" ? LiveSide.Enemy
                : outgoing || incoming && row.Kind == "heal" ? LiveSide.Own : LiveSide.Unknown;
            var targetSide = outgoing && !incoming && row.Kind == "damage" ? LiveSide.Enemy
                : incoming || outgoing && row.Kind == "heal" ? LiveSide.Own : LiveSide.Unknown;
            ImGui.TableNextRow(); Cell(RelativeTime(row.ElapsedMs));
            ImGui.TableNextColumn(); DrawActor(row.SourceJobId, row.SourceName, row.SourceWorldId,
                row.SourceTeam, row.SourceBattalion, ui.LocalPlayerBattalion, sourceSide);
            ImGui.TableNextColumn(); DrawAction(row.ActionId, row.ActionType);
            ImGui.TableNextColumn(); DrawActor(row.TargetJobId, row.TargetName, row.TargetWorldId,
                row.TargetTeam, row.TargetBattalion, ui.LocalPlayerBattalion, targetSide);
            ImGui.TableNextColumn(); ImGui.TextColored(row.Kind == "heal" ? new Vector4(.35f,.86f,.58f,1) : new Vector4(1f,.52f,.42f,1),
                $"{(row.Kind == "heal" ? "+" : "-")}{row.Amount:N0}");
            ImGui.TableNextColumn();
            CombatUiPrimitives.DrawHpShieldBar(row.TargetCurrentHp, row.TargetMaxHp,
                row.TargetShieldPercent, new Vector2(168, 17), $"live-{i}-{row.ElapsedMs}-{row.TargetEntityId}");
            ImGui.TableNextColumn(); DrawOperations(row, incoming, outgoing, sourceSide, targetSide,
                ui.LocalPlayerBattalion, i);
        }
        ImGui.EndTable();
    }

    private void DrawLiveMarkerMap(CollectorUiState ui)
    {
        ImGui.TextWrapped(T("liveMarkerMapHelp"));
        ImGui.TextColored(new Vector4(.94f, .62f, .28f, 1f), T("markerEvidence"));
        var snapshot = markers.Snapshot();
        var available = ImGui.GetContentRegionAvail();
        var size = new Vector2(Math.Max(420, available.X), Math.Max(300, available.Y - 58));
        var origin = ImGui.GetCursorScreenPos();
        var viewport = MarkerUiPrimitives.BeginMapCanvas("live-marker-map-canvas", size,
            ref markerMapZoom, ref markerMapCenterUv, 1.45f, 34f);
        var afterCanvas = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size,
            ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.ChartBackground(configuration.UiTheme)), 2);
        draw.AddRect(origin, origin + size,
            ImGui.ColorConvertFloat4ToU32(BattlefieldUiTheme.Border(configuration.UiTheme)), 2);

        var spanX = viewport.SpanX; var spanY = viewport.SpanY;
        var uvMin = viewport.UvMin; var uvMax = viewport.UvMax;

        if (MapAssets.TryGetValue(ui.DutyId, out var mapAsset))
        {
            try
            {
                if (!mapTextures.TryGetValue(ui.DutyId, out var texture))
                    mapTextures[ui.DutyId] = texture = textureProvider.GetFromFile(
                        Path.Combine(assetRoot, "assets", "maps", mapAsset.FileName));
                var wrap = texture.GetWrapOrDefault();
                if (wrap != null) draw.AddImage(wrap.Handle, origin, origin + size, uvMin, uvMax,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, .9f)));
            }
            catch { /* Optional map asset: coordinate grid remains usable. */ }
        }

        Vector2 Point(float x, float z)
        {
            var uv = new Vector2(.5f + x / 2048f, .5f + z / 2048f);
            return origin + new Vector2((uv.X - uvMin.X) / spanX * size.X,
                (uv.Y - uvMin.Y) / spanY * size.Y);
        }

        draw.PushClipRect(origin, origin + size, true);
        for (var i = 1; i < 8; i++)
        {
            var grid = ImGui.ColorConvertFloat4ToU32(new Vector4(.22f, .3f, .32f, .28f));
            var x = origin.X + i * size.X / 8; var y = origin.Y + i * size.Y / 8;
            draw.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y), grid);
            draw.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + size.X, y), grid);
        }
        foreach (var field in snapshot.Fields)
            DrawMarkerIcon(draw, Point(field.X, field.Z), field.Key, true, 14f, field.Index);
        foreach (var objective in snapshot.Objectives.Where(x => x.MaxHp == 0 || x.CurrentHp > 1))
        {
            var point = Point(objective.X, objective.Z);
            var color = objective.OwnerTeam < 3
                ? TeamColors[objective.OwnerTeam]
                : new Vector4(1f, .78f, .24f, 1f);
            var packed = ImGui.ColorConvertFloat4ToU32(color);
            draw.AddCircleFilled(point, 8f, ImGui.ColorConvertFloat4ToU32(
                new Vector4(.035f, .055f, .06f, .94f)));
            draw.AddCircle(point, 8f, packed, 20, 2.5f);
            draw.AddLine(point + new Vector2(-5, 0), point + new Vector2(5, 0), packed, 1.5f);
            draw.AddLine(point + new Vector2(0, -5), point + new Vector2(0, 5), packed, 1.5f);
            var suffix = objective.ProgressPercent.HasValue
                ? $" {objective.ProgressPercent.Value:0}%"
                : objective.MaxHp > 0
                    ? $" {100d * objective.CurrentHp / objective.MaxHp:0}%"
                    : objective.RemainingSeconds.HasValue
                        ? $" T-{objective.RemainingSeconds.Value}s"
                        : string.IsNullOrWhiteSpace(objective.DisplayText)
                            ? string.Empty : $" {objective.DisplayText}";
            draw.AddText(point + new Vector2(11, -9),
                ImGui.ColorConvertFloat4ToU32(Vector4.One), objective.Label + suffix);
        }
        foreach (var target in snapshot.Targets.Where(x => x.PositionVisible))
        {
            var point = Point(target.X, target.Z);
            DrawMarkerIcon(draw, point, target.Key, false, 10f, target.Index);
            if (!string.IsNullOrWhiteSpace(target.Name))
                draw.AddText(point + new Vector2(13, -9), ImGui.ColorConvertFloat4ToU32(Vector4.One),
                    Display(target.Name, target.WorldId));
        }
        if (snapshot.Fields.Count == 0 && snapshot.Targets.Count == 0 &&
            snapshot.Objectives.Count == 0)
        {
            var message = T("noMarkerEvents");
            var extent = ImGui.CalcTextSize(message);
            draw.AddText(origin + (size - extent) / 2,
                ImGui.ColorConvertFloat4ToU32(new Vector4(.53f, .61f, .66f, 1f)), message);
        }
        draw.AddText(origin + new Vector2(12, 10), ImGui.ColorConvertFloat4ToU32(Vector4.One),
            MapAssets.TryGetValue(ui.DutyId, out var asset) ? T(asset.NameKey) : T("mapOverlay"));
        draw.AddText(origin + new Vector2(12, size.Y - 23),
            ImGui.ColorConvertFloat4ToU32(new Vector4(.53f, .61f, .66f, 1f)), T("mapControls"));
        draw.PopClipRect();
        var height = Math.Max(80, size.Y - 88);
        ImGui.SetCursorScreenPos(origin + new Vector2(size.X - 27, 42));
        ImGui.PushStyleColor(ImGuiCol.FrameBg,
            BattlefieldUiTheme.Panel(configuration.UiTheme) with { W = .88f });
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, BattlefieldUiTheme.Accent(configuration.UiTheme));
        ImGui.VSliderFloat("##live-marker-map-zoom", new Vector2(17, height),
            ref markerMapZoom, 1f, 8f, "");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{T("mapZoom")} {markerMapZoom:0.0}×");
        ImGui.PopStyleColor(2);
        ImGui.SetCursorScreenPos(afterCanvas);
    }

    private void DrawLiveScoreHeader(CollectorUiState ui)
    {
        ImGui.NewLine();
        string Label(byte team)
        {
            var current = ui.Score.Current(team);
            var projected = ui.Score.Projected(team);
            return projected is > 0 && projected != current
                ? $"● {TeamName(team)} {current} ({projected})"
                : $"● {TeamName(team)} {current}";
        }
        var labels = Enumerable.Range(0, 3).Select(x => Label((byte)x)).ToArray();
        var spacing = 18f;
        var totalWidth = labels.Sum(x => ImGui.CalcTextSize(x).X) + spacing * 2;
        var right = ImGui.GetWindowContentRegionMax().X;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetWindowContentRegionMin().X,
            right - totalWidth));
        for (byte team = 0; team < 3; team++)
        {
            var isLocal = ui.LocalPlayerBattalion <= 2 && ui.LocalPlayerBattalion == team;
            var begin = ImGui.GetCursorScreenPos();
            ImGui.TextColored(TeamColors[team], labels[team]);
            if (isLocal)
            {
                var end = ImGui.GetItemRectMax();
                ImGui.GetWindowDrawList().AddLine(new Vector2(begin.X, end.Y + 1),
                    new Vector2(end.X, end.Y + 1),
                    ImGui.ColorConvertFloat4ToU32(TeamColors[team]), 2.5f);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(ui.Score.Projected(team) is int projected && projected != ui.Score.Current(team)
                    ? string.Format(T("liveProjectedScoreHelp"), projected - ui.Score.Current(team))
                    : T("liveCurrentScoreHelp"));
            if (team < 2) ImGui.SameLine(0, spacing);
        }
    }

    private void DrawLocalObservedKdaHeader(CollectorUiState ui)
    {
        var row = cachedObservedKda.FirstOrDefault(value =>
            ui.LocalPlayerEntityId != 0 && value.EntityId == ui.LocalPlayerEntityId ||
            ui.LocalPlayerName.Length > 0 && value.Name == ui.LocalPlayerName &&
            (ui.LocalPlayerWorldId == 0 || value.WorldId == 0 ||
             value.WorldId == ui.LocalPlayerWorldId));
        var label = row == null ? "KDA 0/0/0" : $"KDA {row.Kills}/{row.Deaths}/{row.Assists}";
        var right = ImGui.GetWindowContentRegionMax().X;
        var afterBoundary = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + 16;
        ImGui.SameLine(Math.Max(afterBoundary, right - ImGui.CalcTextSize(label).X));
        ImGui.SetWindowFontScale(.86f);
        ImGui.TextDisabled(label);
        ImGui.SetWindowFontScale(1f);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("liveObservedKdaBoundary"));
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
            catch { /* Never replace a missing official icon with a different-looking badge. */ }
            return;
        }
        var muted = ImGui.ColorConvertFloat4ToU32(new Vector4(.53f, .61f, .66f, 1f));
        draw.AddCircle(center, radius, muted, 16, 1.5f);
        draw.AddText(center - ImGui.CalcTextSize("?") / 2, muted, "?");
    }

    private void Refresh(CollectorUiState ui)
    {
        var snapshot = buffer.Snapshot(ui.ElapsedMs);
        cached = snapshot.Rows; cachedCoordination = snapshot.Coordination;
        cachedMatchTendencyRows = snapshot.MatchTendencyRows;
        cachedMatchCoordination = snapshot.MatchCoordination;
        cachedObservedKda = snapshot.ObservedKda;
        cachedEndMs = snapshot.EndElapsedMs;
        cachedMatchEvidenceEndMs = snapshot.MatchEvidenceEndElapsedMs;
        cachedEncounterActive = snapshot.IsActive;
    }

    private void DrawObservedKda()
    {
        ImGui.TextColored(BattlefieldUiTheme.Accent(configuration.UiTheme), T("liveObservedKda"));
        ImGui.SameLine(); ImGui.TextDisabled(T("liveObservedKdaBoundary"));
        var rows = cachedObservedKda.Where(x => x.Kills + x.Deaths + x.Assists > 0).ToArray();
        if (rows.Length == 0) { ImGui.TextDisabled(T("noObservedKda")); return; }
        if (!ImGui.BeginTable("live-observed-kda", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn(T("player"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("K", ImGuiTableColumnFlags.WidthFixed, 42);
        ImGui.TableSetupColumn("D", ImGuiTableColumnFlags.WidthFixed, 42);
        ImGui.TableSetupColumn("A", ImGuiTableColumnFlags.WidthFixed, 42);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        foreach (var row in rows)
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn(); DrawJob(row.JobId); ImGui.SameLine(0, 4);
            ImGui.TextUnformatted(Display(row.Name, row.WorldId));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Kills.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Deaths.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Assists.ToString());
        }
        ImGui.EndTable();
    }

    private void DrawLiveTendencyCandidates(CollectorUiState ui)
    {
        ImGui.TextWrapped(T("liveTendencyBoundary"));
        if (!ui.Capturing || cachedMatchEvidenceEndMs < 0)
        {
            ImGui.TextDisabled(T("noLiveTendencyEvidence"));
            return;
        }
        var result = LiveTendencyAnalyzer.Analyze(cachedMatchTendencyRows, cachedMatchCoordination,
            ui.LocalPlayerBattalion, cachedMatchEvidenceEndMs);
        DrawGroupCandidates(T("coordination_core"), result.Coordination);
        ImGui.Separator();
        DrawActorCandidates(T("commander"), result.Commanders);
        ImGui.Separator();
        DrawActorCandidates(T("disruptor"), result.Disruptors);
    }

    private void DrawGroupCandidates(string title, IReadOnlyList<LiveGroupCandidate> candidates)
    {
        ImGui.TextColored(new Vector4(.35f, .83f, .91f, 1f), title);
        if (candidates.Count == 0) { ImGui.TextDisabled(T("noLiveTendencyEvidence")); return; }
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            ImGui.TextColored(LogsColor(candidate.Score), $"{candidate.Score:0.0}");
            ImGui.SameLine(); ImGui.TextDisabled(string.Format(T("sharedBurstBuckets"), candidate.SharedBurstBuckets));
            foreach (var member in candidate.Members)
            {
                ImGui.SameLine(0, 8); DrawLiveCandidateActor(member, $"group-{i}-{member.EntityId}");
            }
        }
    }

    private void DrawActorCandidates(string title, IReadOnlyList<LiveActorCandidate> candidates)
    {
        ImGui.TextColored(new Vector4(.35f, .83f, .91f, 1f), title);
        if (candidates.Count == 0) { ImGui.TextDisabled(T("noLiveTendencyEvidence")); return; }
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            ImGui.TextColored(LogsColor(candidate.Score), $"{candidate.Score:0.0}");
            ImGui.SameLine(0, 8); DrawLiveCandidateActor(candidate.Actor, $"actor-{title}-{i}");
            ImGui.SameLine(0, 8); ImGui.TextDisabled(string.Format(T("evidenceCount"), candidate.EvidenceCount));
        }
    }

    private void DrawLiveCandidateActor(LiveActorRef actor, string id)
    {
        DrawJob(actor.JobId); ImGui.SameLine(0, 4);
        var display = Display(actor.Name, actor.WorldId);
        ImGui.TextUnformatted(display);
        if (ImGui.BeginPopupContextItem($"candidate-menu-{id}"))
        {
            if (ImGui.MenuItem(T("copyPlayerName"))) ImGui.SetClipboardText(display);
            ImGui.BeginDisabled(actor.EntityId == 0);
            if (ImGui.MenuItem(T("selectVisiblePlayer")))
                tryTargetVisiblePlayer(actor.EntityId, actor.Name, actor.WorldId);
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
        ImGui.SameLine(0, 5);
        if (ImGui.SmallButton($"⧉##copy-{id}")) ImGui.SetClipboardText(display);
        ImGui.SameLine(0, 3);
        ImGui.BeginDisabled(actor.EntityId == 0);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(1, 0));
        if (ImGui.SmallButton($"◎##target-{id}"))
            tryTargetVisiblePlayer(actor.EntityId, actor.Name, actor.WorldId);
        ImGui.PopStyleVar();
        ImGui.EndDisabled();
    }

    private static Vector4 LogsColor(double value) => value >= 95 ? new Vector4(1f, .35f, .78f, 1)
        : value >= 75 ? new Vector4(1f, .55f, .2f, 1) : value >= 50 ? new Vector4(.3f, .65f, 1f, 1)
        : value >= 25 ? new Vector4(.35f, .8f, .45f, 1) : new Vector4(.6f, .62f, .65f, 1);

    private string RelativeTime(long elapsedMs)
    {
        if (cachedEndMs < 0) return "—";
        var delta = Math.Max(0, cachedEndMs - elapsedMs) / 1000.0;
        return delta < .05 ? "0.0s" : $"-{delta:0.0}s";
    }

    private void DrawOperations(LiveCombatRow row, bool incoming, bool outgoing,
        LiveSide sourceSide, LiveSide targetSide, byte localBattalion, int index)
    {
        // The targeting button always means "select the other side of my event":
        // my outgoing event -> target; incoming event -> source.  For a row not
        // involving the local player, source is the deterministic fallback.
        // 选中按钮始终指向与本机交互的另一端：我方输出选目标，我方承受选来源。
        var targetTheTarget = outgoing;
        var entityId = targetTheTarget ? row.TargetEntityId : row.SourceEntityId;
        var name = targetTheTarget ? row.TargetName : row.SourceName;
        var worldId = targetTheTarget ? row.TargetWorldId : row.SourceWorldId;
        var canTarget = entityId != 0 && name.Length > 0;
        ImGui.BeginDisabled(!canTarget);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(1, 0));
        ImGui.SmallButton($"◎##live-target-{index}-{row.ElapsedMs}-{entityId}");
        ImGui.PopStyleVar();
        var held = ImGui.IsItemActive();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canTarget
                ? T(targetTheTarget ? "holdToTargetVisibleTarget" : "holdToTargetVisibleSource")
                : T("targetActorUnavailable"));
        ImGui.EndDisabled();
        if (held && !tryTargetVisiblePlayer(entityId, name, worldId))
        {
            ImGui.SameLine(); ImGui.TextDisabled("×");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("targetActorUnavailable"));
        }
        ImGui.SameLine(0, 6);
        if (ImGui.SmallButton($"⧉##live-copy-{index}-{row.ElapsedMs}-{row.ActionId}"))
        {
            ImGui.SetClipboardText(BuildEvidenceText(row, sourceSide, targetSide, localBattalion));
            copiedUntil = Environment.TickCount64 + 1800;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("copyCombatRecord"));
    }

    private string BuildEvidenceText(LiveCombatRow row, LiveSide sourceSide, LiveSide targetSide,
        byte localBattalion)
    {
        var shield = CombatUiPrimitives.ShieldAmount(row.TargetMaxHp, row.TargetShieldPercent);
        var statuses = row.TargetStatusIds is { Count: > 0 }
            ? string.Join(", ", row.TargetStatusIds.Select(id => StatusName(id))) : T("noCapturedStatuses");
        var amountKind = row.Kind == "heal" ? T("healingEvent") : T("damage");
        return $"[{TimeSpan.FromMilliseconds(row.ElapsedMs):mm\\:ss}] " +
               $"{T("source")}: {ActorText(row.SourceName, row.SourceWorldId, row.SourceTeam, row.SourceBattalion, localBattalion, sourceSide)} | " +
               $"{T("action")}: {ActionName(row.ActionId)} | {amountKind}: {row.Amount:N0} | " +
               $"{T("target")}: {ActorText(row.TargetName, row.TargetWorldId, row.TargetTeam, row.TargetBattalion, localBattalion, targetSide)} | " +
               $"{T("stateAfterEvent")}: HP {row.TargetCurrentHp:N0}/{row.TargetMaxHp:N0} (+{T("shield")} {shield:N0}); " +
               $"{T("statuses")}: {statuses}";
    }

    private bool IsLocal(uint entityId, string name, ushort worldId, CollectorUiState ui)
    {
        if (ui.LocalPlayerEntityId != 0 && entityId == ui.LocalPlayerEntityId) return true;
        return ui.LocalPlayerName.Length > 0 && name == ui.LocalPlayerName &&
               (ui.LocalPlayerWorldId == 0 || worldId == 0 || worldId == ui.LocalPlayerWorldId);
    }

    private void DrawActor(byte jobId, string name, ushort worldId, byte team, byte battalion,
        byte localBattalion, LiveSide side)
    {
        DrawJob(jobId); ImGui.SameLine(0, 5);
        ImGui.TextUnformatted(Display(name, worldId));
        var label = TeamOrSideText(team, battalion, localBattalion, side);
        if (label.Length == 0) return;
        ImGui.SameLine(0, 5);
        var isOwn = localBattalion <= 2 && battalion == localBattalion || side == LiveSide.Own;
        var color = team < TeamColors.Length ? TeamColors[team] : isOwn ? OwnSideColor : EnemySideColor;
        ImGui.TextColored(color, label);
    }

    private string ActorText(string name, ushort worldId, byte team, byte battalion,
        byte localBattalion, LiveSide side)
    {
        var label = TeamOrSideText(team, battalion, localBattalion, side);
        return label.Length == 0 ? Display(name, worldId) : $"{Display(name, worldId)} · {label}";
    }

    private string TeamOrSideText(byte team, byte battalion, byte localBattalion, LiveSide side)
    {
        if (team < 3) return team switch { 0 => T("maelstrom"), 1 => T("adders"), _ => T("flames") };
        if (battalion <= 2)
        {
            if (localBattalion <= 2 && battalion == localBattalion) return T("ownSide");
            if (localBattalion <= 2) return $"{T("enemyBattlefieldGroup")} {battalion + 1}";
            return $"{T("battlefieldGroup")} {battalion + 1}";
        }
        return side == LiveSide.Own ? T("ownSide")
            : side == LiveSide.Enemy ? T("enemySide") : string.Empty;
    }

    private string TeamName(byte team) => team switch
    {
        0 => T("maelstrom"), 1 => T("adders"), 2 => T("flames"), _ => T("unknownTeam")
    };

    private static void Cell(string value) { ImGui.TableNextColumn(); ImGui.TextUnformatted(value); }
    private string Display(string name, ushort world) => string.IsNullOrWhiteSpace(name) ? T("unresolvedActor")
        : world == 0 ? $"{name}@{T("unknownWorld")}" : $"{name}@{WorldName(world)}";
    private string WorldName(ushort id)
    {
        if (id == 0) return T("unknownWorld");
        if (worldNames.TryGetValue(id, out var cachedName)) return cachedName;
        try
        {
            var value = dataManager.GetExcelSheet<WorldSheet>().GetRow(id).Name.ToString();
            return worldNames[id] = string.IsNullOrWhiteSpace(value) ? T("unknownWorld") : value;
        }
        catch { return worldNames[id] = T("unknownWorld"); }
    }
    private void DrawJob(byte id)
    {
        if (id == 0) { ImGui.TextDisabled("?"); return; }
        try
        {
            if (!jobIcons.TryGetValue(id, out var shared))
                jobIcons[id] = shared = textureProvider.GetFromGameIcon(new GameIconLookup((uint)(62100 + id), true));
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null) { ImGui.Image(wrap.Handle, new Vector2(22,22)); return; }
        }
        catch { }
        ImGui.TextDisabled("?");
    }
    private void DrawAction(uint id, byte actionType)
    {
        try
        {
            if (actionType != 1) throw new InvalidOperationException();
            var language = EffectiveLanguage();
            var action = dataManager.GetExcelSheet<ActionSheet>(language == "ja" ? ClientLanguage.Japanese : ClientLanguage.English).GetRow(id);
            if (!actionIcons.TryGetValue(id, out var shared))
                actionIcons[id] = shared = textureProvider.GetFromGameIcon(new GameIconLookup(action.Icon, true));
            var wrap = shared.GetWrapOrDefault(); if (wrap != null) ImGui.Image(wrap.Handle, new Vector2(22,22));
        }
        catch { ImGui.TextDisabled("·"); }
        ImGui.SameLine(0,5); ImGui.TextUnformatted(ActionName(id));
    }
    private string ActionName(uint id) => actionCatalog.Find(id, EffectiveLanguage()) ?? $"{T("action")} #{id}";
    private string StatusName(long id)
    {
        var language = EffectiveLanguage(); var key = (language, id);
        if (statusNames.TryGetValue(key, out var value)) return value;
        value = statusCatalog.Find(id, language) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) && language is not ("zh-CN" or "zh-TW"))
        {
            try
            {
                var sheetLanguage = language == "ja" ? ClientLanguage.Japanese : ClientLanguage.English;
                value = dataManager.GetExcelSheet<StatusSheet>(sheetLanguage).GetRow((uint)id).Name.ToString();
            }
            catch { }
        }
        return statusNames[key] = string.IsNullOrWhiteSpace(value) ? T("unresolvedStatus") : value;
    }
    private string EffectiveLanguage()
    {
        if (configuration.Language != "auto") return configuration.Language;
        var culture = CultureInfo.CurrentUICulture.Name;
        if (culture.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
            culture.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            culture.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)) return "zh-TW";
        if (culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        return culture.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "ja" : "en";
    }
    private string T(string key) => I18n.Get(configuration.Language, key);
}
