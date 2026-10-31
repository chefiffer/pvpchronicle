using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NativeBattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Frontline capture orchestrator. Collection, storage, upload and UI are deliberately separated.
/// 纷争前线采集总控；采集、存储、上传与界面相互隔离，避免任何网络或磁盘延迟阻塞游戏线程。
/// </summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Schema = "battlefield-frontline-collector/v2";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static ICommandManager Commands { get; set; } = null!;
    [PluginService] private static IGameInteropProvider Interop { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static ITargetManager TargetManager { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private delegate void FrontlineResultDelegate(IntPtr director, IntPtr packet);
    [Signature(GameLayout.ResultSignature, DetourName = nameof(OnFrontlineResult))]
    private readonly Hook<FrontlineResultDelegate> resultHook = null!;

    private readonly Configuration configuration;
    private readonly JsonlMatchWriter output;
    private readonly MatchRepository repository;
    private readonly UploadService uploads;
    private readonly CombatEventCapture combatCapture;
    private readonly LocalActionIntegrityCapture localActionIntegrity;
    private readonly LocalFieldMarkerCommandCapture localFieldMarkerCapture;
    private readonly FieldMarkerNetworkCapture fieldMarkerNetworkCapture;
    private readonly LocalTargetMarkerCommandCapture localTargetMarkerCapture;
    private readonly MarkerAttributionTracker markerAttribution = new();
    private readonly WindowSystem windows = new("BattlefieldFrontlineCollector");
    private readonly IFontHandle? uiFont;
    private readonly MainWindow mainWindow;
    private readonly MatchDetailWindow detailWindow;
    private readonly LiveCombatBuffer liveCombat = new();
    private readonly LiveScoreProjectionTracker liveScoreProjection = new();
    private readonly LiveMarkerState liveMarkers = new();
    private readonly LiveCombatWindow liveCombatWindow;
    private readonly Stopwatch elapsed = new();
    private readonly ConcurrentDictionary<uint, ActionActorIdentity> actorIdentityCache = new();
    private readonly MovementAnomalyTracker movementAnomaly;
    private readonly CrystallineRankHistory crystallineRankHistory;
    private readonly CrystallineMatchHistory crystallineMatchHistory;
    private readonly CrystallineActReplayArchive crystallineActReplayArchive;
    private readonly CrystallineResultCapture crystallineResultCapture;
    private readonly IReadOnlyDictionary<uint, ActionCaptureMetadata> actionMetadata;
    private readonly string captureDirectory;
    private readonly string importDirectory;
    private readonly string assetRoot;
    private string? matchUid;
    private string localPlayerName = string.Empty;
    private string matchRealm = "CN";
    private uint resolvedRealmWorldId;
    private string resolvedRealm = "CN";
    private volatile string gameAccountBindingId = string.Empty;
    private long nextRealmRefreshAt;
    private long nextCrystallineRankSampleAt;
    private DateTime startedAtUtc;
    private uint dutyId;
    private uint territoryId;
    private (short Mael, short Adders, short Flames, byte Bh)? previous;
    private long lastScoreWriteMs = -10_000;
    private long lastVitalsSampleMs = -10_000;
    private long lastVitalsWriteMs = -10_000;
    private long lastIdentitySampleMs = -10_000;
    private long lastMarkerSampleMs = -10_000;
    private long lastCastSampleMs = -10_000;
    private long lastLocalActionStateSampleMs = -10_000;
    private long lastObjectiveObjectSampleMs = -10_000;
    private int lastVitalsFingerprint;
    private int visibleActorCount;
    private readonly uint[] previousMarkerTargets = new uint[17];
    private readonly FieldMarkerSnapshot[] previousFieldMarkers = new FieldMarkerSnapshot[8];
    private bool markerBaselineCaptured;
    private bool waitForDutyExit;
    private bool disposed;
    private readonly Dictionary<uint, uint> activeObservedCasts = new();
    private readonly Dictionary<uint, ObjectiveObjectSnapshot> observedObjectiveObjects = new();
    private readonly Dictionary<uint, ObjectiveMapMarkerSnapshot> observedObjectiveMapMarkers = new();
    private bool secureStaticObjectivesPublished;
    private readonly Dictionary<int, int[]> observedObjectiveUiArrays = new();
    private readonly HashSet<uint> emittedActionObjectives = new();
    private readonly HashSet<uint> observedCrystallineCrystalIds = new();
    private CrystallineProgressSnapshot? previousCrystallineProgress;
    private long lastCrystallineProgressWriteMs = -10_000;

    private readonly record struct FieldMarkerSnapshot(bool Active, float X, float Y, float Z);
    private readonly record struct ObjectiveObjectSnapshot(uint ObjectId, uint BaseId, uint DataId,
        string ObjectKind, byte SubKind, string Name, float X, float Y, float Z,
        bool IsTargetable, uint CurrentHp, uint MaxHp, byte Battalion,
        bool RealtimeHpEligible);
    private readonly record struct ObjectiveMapMarkerSnapshot(uint MarkerId, uint IconId,
        ushort DataId, byte MarkerType, sbyte EventState, string Label,
        float X, float Y, float Z, float Radius, int EndTimestamp,
        int? RemainingSeconds, string DisplayText, double? ProgressPercent);
    private readonly record struct CrystallineProgressSnapshot(int CrystalPosition,
        int AstraProgress, int UmbraProgress, int AstraOnPoint, int UmbraOnPoint,
        int AstraCheckpointProgress, int UmbraCheckpointProgress, int UnbindSeconds);

    private const float ObjectiveRealtimeHpRadius = 190f;

    public Plugin()
    {
        movementAnomaly = new MovementAnomalyTracker(ReadGameVersion());
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Migrate();
        PluginInterface.SavePluginConfig(configuration);
        var baseDirectory = PluginInterface.GetPluginConfigDirectory();
        assetRoot = PluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory;
        captureDirectory = Path.Combine(baseDirectory, "exports");
        importDirectory = Path.Combine(baseDirectory, "imports");
        crystallineRankHistory = new CrystallineRankHistory(
            Path.Combine(baseDirectory, "crystalline-rank-history.json"));
        crystallineMatchHistory = new CrystallineMatchHistory(
            Path.Combine(baseDirectory, "crystalline-match-history.json"));
        crystallineActReplayArchive = new CrystallineActReplayArchive(
            Path.Combine(baseDirectory, "crystalline-act-replays.json"));
        Directory.CreateDirectory(importDirectory);
        var crystallineHistoryImportDirectory = Path.Combine(importDirectory, "crystalline-conflict");
        Directory.CreateDirectory(crystallineHistoryImportDirectory);
        crystallineMatchHistory.ImportLegacySummaries(Path.Combine(
            crystallineHistoryImportDirectory, "cc-history-summary.json"));
        output = new JsonlMatchWriter(captureDirectory, assetRoot);
        repository = new MatchRepository(Path.Combine(baseDirectory, "analysis"), assetRoot);
        uploads = new UploadService(configuration, repository, Log, Path.Combine(baseDirectory, "cache"),
            () => gameAccountBindingId, assetRoot);
        uploads.ResumePending();
        I18n.Configure(assetRoot);
        var rotationCalendar = PvpRotationCalendar.Load(assetRoot);
        uiFont = BuildUiFont(assetRoot);
        actionMetadata = BuildActionMetadataCatalog(assetRoot);
        detailWindow = new MatchDetailWindow(configuration, repository, uploads, DataManager, TextureProvider,
            assetRoot);
        liveCombatWindow = new LiveCombatWindow(configuration, liveCombat, liveMarkers, UiState, DataManager, TextureProvider,
            TryTargetVisiblePlayer, assetRoot);
        mainWindow = new MainWindow(configuration, repository, uploads, DataManager, TextureProvider,
            SaveConfiguration, UiState, OpenDetailedAnalysis, () => liveCombatWindow.IsOpen = true,
            captureDirectory, importDirectory, rotationCalendar, crystallineRankHistory,
            crystallineMatchHistory, crystallineActReplayArchive);
        windows.AddWindow(mainWindow);
        windows.AddWindow(detailWindow);
        windows.AddWindow(liveCombatWindow);

        Interop.InitializeFromAttributes(this);
        resultHook.Enable();
        crystallineResultCapture = new CrystallineResultCapture(Interop, CrystallineContext,
            OnCrystallineResult, crystallineRankHistory.Observe, Log);
        combatCapture = new CombatEventCapture(Interop, ObjectTable, output,
            () => (matchUid, elapsed.ElapsedMilliseconds), ResolveCachedActorIdentity, Log,
            actionId => actionMetadata.TryGetValue(actionId, out var value) ? value : default,
            (value, state) =>
            {
                liveCombat.Add(value, state);
                movementAnomaly.ObserveAction(value);
                ObserveActionObjective(value);
            }, markerAttribution.Observe, liveCombat.AddDeath);
        localActionIntegrity = new LocalActionIntegrityCapture(Interop, ObjectTable, output,
            () => (matchUid, elapsed.ElapsedMilliseconds), Log);
        localFieldMarkerCapture = new LocalFieldMarkerCommandCapture(Interop,
            () => (matchUid, elapsed.ElapsedMilliseconds, ObjectTable.LocalPlayer?.EntityId ?? 0),
            markerAttribution.Observe, Log);
        fieldMarkerNetworkCapture = new FieldMarkerNetworkCapture(Interop,
            () => (matchUid, elapsed.ElapsedMilliseconds), markerAttribution.Observe, Log,
            Path.Combine(baseDirectory, "diagnostics", "field-markers"),
            // Passive marker observations stay fail-closed, but the retired
            // controlled diagnostic mode no longer writes diagnostic summaries.
            static () => false);
        localTargetMarkerCapture = new LocalTargetMarkerCommandCapture(Interop,
            () => (matchUid, elapsed.ElapsedMilliseconds, ObjectTable.LocalPlayer?.EntityId ?? 0),
            markerAttribution.Observe, Log);
        Framework.Update += OnFrameworkUpdate;
        ChatGui.ChatMessage += OnChatMessage;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        PluginInterface.UiBuilder.Draw += DrawWindows;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        Commands.AddHandler("/bflog", new CommandInfo(OnCommand)
        {
            HelpMessage = "打开战场记录与本地复盘窗口 / Open Battlefield Chronicle."
        });
        Commands.AddHandler("/bflive", new CommandInfo((_, _) => liveCombatWindow.IsOpen = true)
        { HelpMessage = "打开最近 15–60 秒实时战斗切片 / Open rolling live combat slice." });
        Commands.AddHandler("/bfslice", new CommandInfo((_, _) => liveCombatWindow.IsOpen = true)
        { HelpMessage = "打开即时战斗切片 / Open the live combat slice." });
        Commands.AddHandler("/bfcache", new CommandInfo((_, _) => OpenDirectory(captureDirectory))
        { HelpMessage = "打开本地战局缓存 / Open the local match cache." });
        Commands.AddHandler("/bfimport", new CommandInfo((_, _) => OpenDirectory(importDirectory))
        { HelpMessage = "打开离线导入目录 / Open the offline-import staging folder." });
        // Dalamud can construct a local plugin on its loader thread. ObjectTable,
        // LocalPlayer and other game-state services are main-thread-only in API 15.
        // The first Framework.Update callback performs initial detection safely.
    }

    private static string ReadGameVersion()
    {
        try
        {
            var executable = Process.GetCurrentProcess().MainModule?.FileName;
            var path = executable is null ? null : Path.Combine(Path.GetDirectoryName(executable)!, "ffxivgame.ver");
            return path is not null && File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch
        {
            // Unknown client version is a hard refusal for movement candidate
            // publication; ordinary histograms are still collected.
            return string.Empty;
        }
    }

    private static IFontHandle? BuildUiFont(string assetRoot)
    {
        var path = Path.Combine(assetRoot, "Assets", "fonts", "NotoSansThai-Variable.ttf");
        if (!File.Exists(path)) path = Path.Combine(assetRoot, "assets", "fonts", "NotoSansThai-Variable.ttf");
        if (!File.Exists(path)) return null;
        return PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(step => step.OnPreBuild(toolkit =>
        {
            var baseFont = toolkit.AddDalamudDefaultFont(-1);
            var config = new SafeFontConfig
            {
                // SafeFontConfig does not accept Dalamud's -1 "use default"
                // sentinel.  Keep the Thai merge font at the atlas base size;
                // UI scaling is applied by Dalamud after the atlas is built.
                SizePx = 17f,
                MergeFont = baseFont,
                GlyphRanges = [0x0E00, 0x0E7F, 0],
            };
            toolkit.AddFontFromFile(path, config);
            toolkit.Font = baseFont;
        }));
    }

    private void DrawWindows()
    {
        if (uiFont == null) { windows.Draw(); return; }
        using (uiFont.Push()) windows.Draw();
    }

    private static IReadOnlyDictionary<uint, ActionCaptureMetadata>
        BuildActionMetadataCatalog(string assetRoot)
    {
        var result = new Dictionary<uint, ActionCaptureMetadata>();
        try
        {
            var path = Path.Combine(assetRoot, "Assets", "localization", "action_names.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            foreach (var property in document.RootElement.GetProperty("actions").EnumerateObject())
            {
                if (!uint.TryParse(property.Name, out var actionId)) continue;
                var row = sheet.GetRow(actionId);
                var range = Convert.ToInt16(row.Range);
                var effectRange = Convert.ToByte(row.EffectRange);
                var category = Convert.ToByte(row.ActionCategory.RowId);
                var cast100Ms = Convert.ToUInt16(row.Cast100ms);
                var recast100Ms = Convert.ToUInt16(row.Recast100ms);
                var cooldownGroup = Convert.ToByte(row.CooldownGroup);
                var maxCharges = Convert.ToByte(row.MaxCharges);
                result[actionId] = new ActionCaptureMetadata(range > 0 ? range : null,
                    effectRange, category, cast100Ms, recast100Ms, cooldownGroup,
                    maxCharges, row.AffectsPosition);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not build the immutable action metadata catalog; range and weave histograms will be unavailable.");
        }
        return result;
    }

    private static uint CurrentDutyId() => GameMain.Instance() == null
        ? 0u : (uint)GameMain.Instance()->CurrentContentFinderConditionId;

    private void OnTerritoryChanged(uint _)
    {
        if (!GameLayout.IsSupportedDuty(CurrentDutyId())) waitForDutyExit = false;
        if (matchUid != null && !GameLayout.IsSupportedDuty(CurrentDutyId())) EndIncompleteMatch("territory_changed");
        TryStartMatch();
    }

    private void TryStartMatch()
    {
        var currentDuty = CurrentDutyId();
        if (matchUid != null || waitForDutyExit || !GameLayout.IsSupportedDuty(currentDuty)) return;
        // TerritoryChanged can fire before the local player object and home world are ready.
        // Wait a few frames instead of writing an identity-less match header that can never
        // be associated with the uploader's personal Logs later.
        // 切图事件早于本机角色加载时，等待后续帧；不要生成无法归属个人 Logs 的空身份战局头。
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null || string.IsNullOrWhiteSpace(localPlayer.Name.TextValue) ||
            localPlayer.HomeWorld.RowId == 0) return;
        dutyId = currentDuty;
        territoryId = (uint)ClientState.TerritoryType;
        matchUid = Guid.NewGuid().ToString("N");
        localPlayerName = localPlayer.Name.TextValue;
        matchRealm = ResolveRealmFromLocalPlayer();
        if (configuration.Realm != matchRealm)
        {
            configuration.Realm = matchRealm;
            PluginInterface.SavePluginConfig(configuration);
        }
        startedAtUtc = DateTime.UtcNow;
        previous = null; visibleActorCount = 0;
        previousCrystallineProgress = null;
        lastCrystallineProgressWriteMs = -10_000;
        actorIdentityCache.Clear();
        markerAttribution.Reset();
        Array.Clear(previousMarkerTargets);
        Array.Clear(previousFieldMarkers);
        markerBaselineCaptured = false;
        lastScoreWriteMs = -10_000; lastVitalsSampleMs = -10_000; lastIdentitySampleMs = -10_000;
        lastMarkerSampleMs = -10_000;
        // Every elapsed-time gate is relative to this match's fresh stopwatch.
        // Leaving the objective gate at the previous match's elapsed value made
        // the next match wait that many minutes before its first crystal/map scan.
        lastObjectiveObjectSampleMs = -10_000;
        lastCastSampleMs = -10_000;
        lastLocalActionStateSampleMs = -10_000;
        lastVitalsWriteMs = -10_000; lastVitalsFingerprint = 0;
        elapsed.Restart();
        liveCombat.Reset();
        liveScoreProjection.Reset();
        liveMarkers.Reset();
        movementAnomaly.Reset();
        var mode = GameLayout.IsCrystallineDuty(dutyId) ? "crystalline_conflict" : "frontline";
        output.Start(matchUid, dutyId, territoryId, localPlayerName, matchRealm, mode);
        Log.Information("{Mode} capture started: {MatchUid}, duty {DutyId}", mode, matchUid, dutyId);
    }

    private string ResolveRealmFromLocalPlayer()
    {
        try
        {
            var worldId = ObjectTable.LocalPlayer?.HomeWorld.RowId ?? 0;
            if (worldId == 0) return configuration.Realm;
            if (resolvedRealmWorldId == worldId) return resolvedRealm;
            var world = DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>().GetRow(worldId);
            var region = world.DataCenter.Value.Region.RowId;
            resolvedRealmWorldId = worldId;
            return resolvedRealm = region switch
            {
                1 => "JP",
                2 => "NA",
                3 => "EU",
                4 or 7 => "OCE",
                _ => worldId >= 1000 ? "CN" : configuration.Realm
            };
        }
        catch { return configuration.Realm; }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var tick = Environment.TickCount64;
        if (tick >= nextRealmRefreshAt)
        {
            nextRealmRefreshAt = tick + 2_000;
            var currentPlayer = ObjectTable.LocalPlayer;
            gameAccountBindingId = MachineIdentity.GetGameAccountId(
                currentPlayer?.Name.TextValue ?? string.Empty,
                (ushort)(currentPlayer?.HomeWorld.RowId ?? 0)) ?? string.Empty;
            var homeRealm = ResolveRealmFromLocalPlayer();
            if (configuration.Realm != homeRealm)
            {
                configuration.Realm = homeRealm;
                PluginInterface.SavePluginConfig(configuration);
            }
        }
        if (tick >= nextCrystallineRankSampleAt)
        {
            nextCrystallineRankSampleAt = tick + 5_000;
            ObserveCrystallineRankProfile();
        }
        if (matchUid == null) { TryStartMatch(); return; }
        if (!GameLayout.IsSupportedDuty(CurrentDutyId())) { EndIncompleteMatch("duty_left"); return; }
        var elapsedMs = elapsed.ElapsedMilliseconds;
        (short, short, short, byte)? frontlineScore = null;
        if (GameLayout.IsFrontlineDuty(dutyId))
        {
            var eventFramework = EventFramework.Instance();
            var baseDirector = eventFramework == null ? null : eventFramework->GetInstanceContentDirector();
            if (baseDirector != null)
            {
                var director = (FrontlineContentDirector*)baseDirector;
                var raw = (director->MaelstromScore, director->AddersScore,
                    director->FlamesScore, director->PlayerBattleHigh);
                if (ValidScore(raw.Item1) && ValidScore(raw.Item2) && ValidScore(raw.Item3))
                {
                    frontlineScore = (raw.Item1, raw.Item2, raw.Item3,
                        raw.Item4 <= 5 ? raw.Item4 : (byte)0);
                    liveScoreProjection.Observe(dutyId, elapsedMs, raw.Item1, raw.Item2, raw.Item3);
                }
            }
        }
        else if (GameLayout.IsCrystallineDuty(dutyId))
        {
            CaptureCrystallineProgress(elapsedMs);
        }

        // One local actor at 20 Hz is sufficient to prove queue/lock/cast
        // continuity while keeping allocations and capture size bounded.
        if (elapsedMs - lastLocalActionStateSampleMs >= 50)
        {
            lastLocalActionStateSampleMs = elapsedMs;
            localActionIntegrity.Sample();
        }

        // Action-effect detours are not guaranteed to run on Dalamud's framework
        // thread. Keep a small persistent identity/owner cache refreshed here so
        // short-lived summons can still be attributed after they despawn.
        if (elapsedMs - lastIdentitySampleMs >= 250)
        {
            lastIdentitySampleMs = elapsedMs;
            RefreshActorIdentityCache();
            foreach (var observation in movementAnomaly.ObserveForcedMovement(
                         Schema, matchUid, elapsedMs, ObjectTable.PlayerObjects))
                output.Write(observation);
            foreach (var candidate in movementAnomaly.Observe(Schema, matchUid, elapsedMs, ObjectTable.PlayerObjects))
                output.Write(candidate);
        }

        // Markers are tiny fixed-size snapshots (17 target markers + 8 field markers).
        // Sampling them independently avoids the former two-second blind spot without
        // adding per-frame allocations or disk writes when nothing changed.
        // 标点是固定长度的小状态；100ms 比对只在变化时落盘，不随帧写盘。
        if (elapsedMs - lastMarkerSampleMs >= 100)
        {
            lastMarkerSampleMs = elapsedMs;
            CaptureMarkerChanges(elapsedMs);
        }

        // Casting is sampled independently from the 2 s replay vitals.  Only a
        // transition first observed near zero cast progress is eligible as an
        // onset; actors entering the observer's loaded area mid-cast are kept
        // as coverage evidence but refused from timing comparisons.
        if (elapsedMs - lastCastSampleMs >= 100)
        {
            lastCastSampleMs = elapsedMs;
            CaptureCastStarts(elapsedMs);
        }

        // Discovery-only stream for building versioned, map-specific objective
        // catalogs. Write only load/state/unload changes; never label a generic
        // EventObj or BattleNpc as a capture point before a map adapter confirms it.
        if (elapsedMs - lastObjectiveObjectSampleMs >= 500)
        {
            lastObjectiveObjectSampleMs = elapsedMs;
            CaptureObjectiveObjectCandidates(elapsedMs);
            CaptureObjectiveMapMarkers(elapsedMs);
            CaptureObjectiveUiNumberArrays(elapsedMs);
        }

        if (frontlineScore is { } value &&
            (value != previous || elapsedMs - lastScoreWriteMs >= 10_000))
        {
            previous = value; lastScoreWriteMs = elapsedMs;
            output.Write(new ScoreEvent(Schema, "score", matchUid, DateTime.UtcNow, elapsedMs,
                value.Item1, value.Item2, value.Item3, value.Item4, dutyId, territoryId, GameLayout.LayoutVersion));
        }

        if (elapsedMs - lastVitalsSampleMs >= 2000)
        {
            lastVitalsSampleMs = elapsedMs;
            var actors = CaptureVisibleActors();
            visibleActorCount = actors.Count;
            var fingerprint = VitalsFingerprint(actors);
            if (fingerprint != lastVitalsFingerprint || elapsedMs - lastVitalsWriteMs >= 10_000)
            {
                lastVitalsFingerprint = fingerprint; lastVitalsWriteMs = elapsedMs;
                output.Write(new VitalsEvent(Schema, "actor_vitals", matchUid, DateTime.UtcNow,
                    elapsedMs, dutyId, territoryId, actors));
            }
        }
    }

    private void ObserveCrystallineRankProfile()
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null) return;
        var profile = PvPProfile.Instance();
        if (profile == null) return;
        var tier = profile->CrystallineConflictCurrentRank;
        // A completely empty profile can be an unloaded server response. Do
        // not turn that transient zero into a false "Unranked" observation.
        if (profile->CrystallineConflictRankedMatches <= 0 && tier == 0) return;
        crystallineRankHistory.Observe(new CrystallineRankObservation(
            DateTime.UtcNow,
            player.Name.TextValue,
            (ushort)player.HomeWorld.RowId,
            tier,
            profile->CrystallineConflictCurrentRiser,
            profile->CrystallineConflictCurrentRisingStars,
            profile->CrystallineConflictCurrentCrystalCredit,
            "pvp_profile"));
    }

    private (string Name, ushort WorldId, byte ClassJobId, uint DutyId, uint TerritoryId)
        CrystallineContext()
    {
        var player = ObjectTable.LocalPlayer;
        return (player?.Name.TextValue ?? string.Empty,
            (ushort)(player?.HomeWorld.RowId ?? 0), (byte)(player?.ClassJob.RowId ?? 0),
            CurrentDutyId(), (uint)ClientState.TerritoryType);
    }

    private ActionActorIdentity? ResolveCachedActorIdentity(uint entityId) =>
        actorIdentityCache.TryGetValue(entityId, out var identity) ? identity : null;

    private bool TryTargetVisiblePlayer(uint entityId, string expectedName, ushort expectedWorldId)
    {
        try
        {
            if (entityId == 0 || ObjectTable.SearchById(entityId) is not IPlayerCharacter player) return false;
            if (!string.Equals(player.Name.TextValue, expectedName, StringComparison.Ordinal)) return false;
            if (expectedWorldId != 0 && player.HomeWorld.RowId != expectedWorldId) return false;
            if (!GameGui.WorldToScreen(player.Position, out _)) return false;
            TargetManager.Target = player;
            return true;
        }
        catch { return false; }
    }

    private void RefreshActorIdentityCache()
    {
        try
        {
            foreach (var actor in ObjectTable)
            {
                if (actor == null || actor.EntityId == 0) continue;
                var player = actor as IPlayerCharacter;
                var battle = actor as Dalamud.Game.ClientState.Objects.Types.IBattleChara;
                var ownerId = actor.OwnerId;
                // 0xE0000000 is the game's invalid object-id sentinel, not a
                // summon owner.  Persisting it created bogus @65535 owners.
                if (ownerId >= 0xE0000000) ownerId = 0;
                var owner = ownerId == 0 ? null : ObjectTable.SearchById(ownerId);
                var ownerPlayer = owner as IPlayerCharacter;
                var ownerBattle = owner as Dalamud.Game.ClientState.Objects.Types.IBattleChara;
                var ownerName = owner?.Name.TextValue ?? string.Empty;
                var ownerWorld = (ushort)(ownerPlayer?.HomeWorld.RowId ?? 0);
                var ownerJob = ownerBattle?.ClassJob.RowId ?? 0;
                var battalion = ReadBattalion(actor.Address);
                var ownerBattalion = owner == null ? (byte)255 : ReadBattalion(owner.Address);
                if (ownerId != 0 && string.IsNullOrWhiteSpace(ownerName) &&
                    actorIdentityCache.TryGetValue(ownerId, out var cachedOwner))
                {
                    ownerName = cachedOwner.OwnerName.Length > 0 ? cachedOwner.OwnerName : cachedOwner.Name;
                    ownerWorld = cachedOwner.OwnerWorldId != 0 ? cachedOwner.OwnerWorldId : cachedOwner.WorldId;
                    ownerJob = cachedOwner.OwnerClassJobId != 0 ? cachedOwner.OwnerClassJobId : cachedOwner.ClassJobId;
                    ownerBattalion = cachedOwner.OwnerBattalion <= 2
                        ? cachedOwner.OwnerBattalion : cachedOwner.Battalion;
                }
                actorIdentityCache[actor.EntityId] = new ActionActorIdentity(actor.EntityId,
                    actor.Name.TextValue, (ushort)(player?.HomeWorld.RowId ?? 0), battle?.ClassJob.RowId ?? 0,
                    ownerId, ownerName, ownerWorld, ownerJob, battalion, ownerBattalion);
            }
        }
        catch (Exception ex) { Log.Verbose(ex, "Skipped an invalid actor identity-cache sample."); }
    }

    private void CaptureCastStarts(long elapsedMs)
    {
        const int samplingIntervalMs = 100;
        var loaded = new HashSet<uint>();
        try
        {
            foreach (var player in ObjectTable.PlayerObjects)
            {
                if (player.EntityId == 0) continue;
                loaded.Add(player.EntityId);
                var battle = (Dalamud.Game.ClientState.Objects.Types.IBattleChara)player;
                var actionId = battle.IsCasting ? battle.CastActionId : 0u;
                if (actionId == 0)
                {
                    activeObservedCasts.Remove(player.EntityId);
                    continue;
                }
                if (activeObservedCasts.TryGetValue(player.EntityId, out var prior) && prior == actionId)
                    continue;
                activeObservedCasts[player.EntityId] = actionId;
                var current = Math.Max(0, battle.CurrentCastTime);
                var total = Math.Max(0, battle.TotalCastTime);
                var metadata = actionMetadata.TryGetValue(actionId, out var value) ? value : default;
                var identity = ResolveCachedActorIdentity(player.EntityId);
                output.Write(new CastStartObservationEvent(Schema, "cast_start_observed", matchUid!,
                    DateTime.UtcNow, elapsedMs, player.EntityId,
                    identity?.Name ?? player.Name.TextValue, identity?.WorldId ?? 0,
                    identity?.ClassJobId ?? player.ClassJob.RowId, actionId,
                    unchecked((uint)battle.CastTargetObjectId), current, total, metadata.Cast100Ms,
                    samplingIntervalMs, current <= .25f));
            }
        }
        catch (Exception ex) { Log.Verbose(ex, "Skipped an invalid cast-start sample."); }
        foreach (var entityId in activeObservedCasts.Keys.Where(id => !loaded.Contains(id)).ToArray())
            activeObservedCasts.Remove(entityId);
    }

    private static byte ReadBattalion(IntPtr address)
    {
        if (address == IntPtr.Zero) return 255;
        var value = ((NativeBattleChara*)address)->Battalion;
        return value <= 2 ? value : (byte)255;
    }

    private void CaptureMarkerChanges(long elapsedMs)
    {
        try
        {
            var controller = MarkingController.Instance();
            if (controller == null) return;
            var baseline = !markerBaselineCaptured;
            for (var index = 0; index < previousMarkerTargets.Length; index++)
            {
                var observedTarget = controller->Markers[index].ObjectId;
                // The client uses 0xE0000000 as an invalid/empty object-id
                // sentinel. It is not a replayable marker target.
                // 客户端空对象哨兵不是可复盘的标点事件。
                var target = IsRecordableMarkerTarget(observedTarget) ? observedTarget : 0u;
                UpdateLiveTargetMarker(index, target);
                if (baseline)
                {
                    previousMarkerTargets[index] = target;
                    if (target != 0) WriteTargetMarker(elapsedMs, index, target, "snapshot");
                    continue;
                }
                if (target == previousMarkerTargets[index]) continue;
                var priorTarget = previousMarkerTargets[index];
                var attribution = markerAttribution.MatchTargetChange(
                    elapsedMs, index, priorTarget, target);
                if (priorTarget != 0)
                    WriteTargetMarker(elapsedMs, index, priorTarget, "remove", attribution);
                previousMarkerTargets[index] = target;
                if (target != 0)
                    WriteTargetMarker(elapsedMs, index, target, "apply", attribution);

            }

            for (var index = 0; index < previousFieldMarkers.Length; index++)
            {
                var marker = controller->FieldMarkers[index];
                var current = new FieldMarkerSnapshot(marker.Active, marker.Position.X,
                    marker.Position.Y, marker.Position.Z);
                if (current.Active)
                    liveMarkers.SetField(new LiveFieldMarker(index, FieldMarkerKey(index),
                        current.X, current.Y, current.Z));
                else liveMarkers.RemoveField(index);
                var prior = previousFieldMarkers[index];
                if (baseline)
                {
                    previousFieldMarkers[index] = current;
                    if (current.Active) WriteFieldMarker(elapsedMs, index, current, "snapshot");
                    continue;
                }
                if (prior.Active && !current.Active)
                    WriteFieldMarker(elapsedMs, index, prior, "remove",
                        markerAttribution.MatchFieldChange(elapsedMs, index, "remove",
                            prior.X, prior.Y, prior.Z));
                else if (!prior.Active && current.Active)
                    WriteFieldMarker(elapsedMs, index, current, "apply",
                        markerAttribution.MatchFieldChange(elapsedMs, index, "apply",
                            current.X, current.Y, current.Z));
                else if (prior.Active && current.Active && FieldMarkerMoved(prior, current))
                    WriteFieldMarker(elapsedMs, index, current, "move",
                        markerAttribution.MatchFieldChange(elapsedMs, index, "apply",
                            current.X, current.Y, current.Z));
                previousFieldMarkers[index] = current;
            }
            markerBaselineCaptured = true;
            fieldMarkerNetworkCapture.FlushDiagnostics(elapsedMs);
        }
        catch (Exception ex) { Log.Verbose(ex, "Skipped an invalid marker-state sample."); }
    }

    private void UpdateLiveTargetMarker(int index, uint targetId)
    {
        if (!IsRecordableMarkerTarget(targetId)) { liveMarkers.RemoveTarget(index); return; }
        actorIdentityCache.TryGetValue(targetId, out var identity);
        var gameObject = ObjectTable.SearchById(targetId);
        // A target marker is spatially meaningful only while the referenced
        // object is loaded. Never retain its last coordinate outside parsing range.
        // 人或物离开加载范围后立即从实时图消失，不把最后坐标伪装成当前位置。
        if (gameObject == null) { liveMarkers.RemoveTarget(index); return; }
        var displayName = !string.IsNullOrWhiteSpace(identity?.Name)
            ? identity!.Name : gameObject.Name.TextValue ?? string.Empty;
        liveMarkers.SetTarget(new LiveTargetMarker(index, TargetMarkerKey(index), targetId,
            displayName, identity?.WorldId ?? 0, identity?.ClassJobId ?? 0,
            identity?.Battalion ?? 255, gameObject.Position.X, gameObject.Position.Y,
            gameObject.Position.Z, true));
    }

    private void WriteTargetMarker(long elapsedMs, int index, uint targetId, string change,
        MarkerOperatorAttribution? attribution = null)
    {
        if (!IsRecordableMarkerTarget(targetId)) return;
        actorIdentityCache.TryGetValue(targetId, out var target);
        var gameObject = ObjectTable.SearchById(targetId);
        var objectName = gameObject?.Name.TextValue ?? string.Empty;
        var capturedName = !string.IsNullOrWhiteSpace(target?.Name) ? target.Name : objectName;
        var ownerId = gameObject?.OwnerId ?? 0;
        if (ownerId >= 0xE0000000) ownerId = 0;
        var (markerOperator, operatorEvidence) = ResolveMarkerOperator(attribution);
        output.Write(new TargetMarkerEvent(Schema, "target_marker", matchUid!, DateTime.UtcNow,
            elapsedMs, index, TargetMarkerKey(index), targetId, capturedName,
            target?.WorldId ?? 0, target?.ClassJobId ?? 0, target?.Battalion ?? 255,
            change, operatorEvidence,
            gameObject?.ObjectKind.ToString() ?? string.Empty, gameObject?.BaseId ?? 0,
            gameObject?.BaseId ?? 0, gameObject?.SubKind ?? 0, ownerId,
            gameObject?.Position.X, gameObject?.Position.Y, gameObject?.Position.Z,
            !string.IsNullOrWhiteSpace(objectName) ? "loaded_game_object_name" : "object_metadata_only",
            markerOperator.EntityId, markerOperator.Name, markerOperator.WorldId,
            markerOperator.ClassJobId, markerOperator.Battalion));
    }

    private static bool IsRecordableMarkerTarget(uint targetId) =>
        targetId != 0 && targetId != 0xE0000000u;

    private void WriteFieldMarker(long elapsedMs, int index, FieldMarkerSnapshot marker, string change,
        MarkerOperatorAttribution? attribution = null)
    {
        if (change != "snapshot" && attribution is null)
            fieldMarkerNetworkCapture.ObserveStateChange(elapsedMs, index, change,
                marker.X, marker.Y, marker.Z);
        var nearby = change == "remove" ? null : NearestLoadedPlayer(marker.X, marker.Y, marker.Z, 1.5f);
        var (markerOperator, operatorEvidence) = ResolveMarkerOperator(attribution);
        output.Write(new FieldMarkerEvent(Schema, "field_marker", matchUid!, DateTime.UtcNow,
            elapsedMs, index, FieldMarkerKey(index), marker.X, marker.Y, marker.Z, change,
            nearby?.Identity.EntityId ?? 0, nearby?.Identity.Name ?? string.Empty,
            nearby?.Identity.WorldId ?? 0, nearby?.Identity.ClassJobId ?? 0,
            nearby?.Identity.Battalion ?? 255, nearby?.Distance,
            nearby == null ? "none" : "near_actor_candidate",
            operatorEvidence, markerOperator.EntityId, markerOperator.Name,
            markerOperator.WorldId, markerOperator.ClassJobId, markerOperator.Battalion));
    }

    private (ActionActorIdentity Identity, string Evidence) ResolveMarkerOperator(
        MarkerOperatorAttribution? attribution)
    {
        if (attribution is not { } value || value.EntityId == 0)
            return (new ActionActorIdentity(0, string.Empty, 0, 0, 0, string.Empty, 0, 0,
                255, 255), "client_marker_state_no_operator");
        if (actorIdentityCache.TryGetValue(value.EntityId, out var identity))
            return (identity, value.Evidence);
        var player = ObjectTable.SearchById(value.EntityId);
        return (new ActionActorIdentity(value.EntityId, player?.Name.TextValue ?? string.Empty,
            player is IPlayerCharacter pc ? (ushort)pc.HomeWorld.RowId : (ushort)0,
            player is IPlayerCharacter job ? job.ClassJob.RowId : 0,
            0, string.Empty, 0, 0, player == null ? (byte)255 : ReadBattalion(player.Address), 255),
            value.Evidence);
    }

    private (ActionActorIdentity Identity, float Distance)? NearestLoadedPlayer(
        float x, float y, float z, float maximumDistance)
    {
        ActionActorIdentity? best = null;
        var bestDistance = maximumDistance;
        foreach (var player in ObjectTable.PlayerObjects)
        {
            var dx = player.Position.X - x; var dy = player.Position.Y - y; var dz = player.Position.Z - z;
            var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > bestDistance) continue;
            if (!actorIdentityCache.TryGetValue(player.EntityId, out var identity)) continue;
            best = identity; bestDistance = distance;
        }
        return best == null ? null : (best, bestDistance);
    }

    private static bool FieldMarkerMoved(FieldMarkerSnapshot left, FieldMarkerSnapshot right)
    {
        const float epsilonSquared = .01f * .01f;
        var dx = left.X - right.X; var dy = left.Y - right.Y; var dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz > epsilonSquared;
    }

    private static string FieldMarkerKey(int index) => index switch
    {
        0 => "A", 1 => "B", 2 => "C", 3 => "D",
        4 => "1", 5 => "2", 6 => "3", 7 => "4", _ => $"field_{index}"
    };

    private static string TargetMarkerKey(int index) => index switch
    {
        >= 0 and <= 4 => $"attack_{index + 1}",
        >= 5 and <= 7 => $"bind_{index - 4}",
        >= 8 and <= 9 => $"ignore_{index - 7}",
        10 => "square", 11 => "circle", 12 => "cross", 13 => "triangle",
        >= 14 and <= 16 => $"attack_{index - 8}",
        _ => $"target_{index}"
    };

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (matchUid == null) return;
        try
        {
            var channel = message.LogKind.ToString();
            // Commander/macro evidence is limited to the uploader's own team-visible
            // tactical channels. Public chat is deliberately not retained.
            if (!channel.Contains("Party", StringComparison.OrdinalIgnoreCase) &&
                !channel.Contains("Alliance", StringComparison.OrdinalIgnoreCase) &&
                !channel.Contains("PvPTeam", StringComparison.OrdinalIgnoreCase)) return;
            var speaker = message.OriginalSender.ExtractText().Trim();
            var text = message.OriginalMessage.ExtractText().Trim();
            if (speaker.Length == 0 || text.Length == 0) return;
            var categories = CoordinationCommandCategories(text);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            liveCombat.AddCoordination(elapsed.ElapsedMilliseconds, speaker, digest, categories);
            output.Write(new CoordinationChatEvent(Schema, "coordination_chat", matchUid,
                DateTime.UtcNow, elapsed.ElapsedMilliseconds, channel, speaker, digest,
                categories, categories.Count > 0));
        }
        catch (Exception ex) { Log.Verbose(ex, "Skipped an invalid coordination chat event."); }
    }

    private static IReadOnlyList<string> CoordinationCommandCategories(string text)
    {
        static bool HasAny(string value, params string[] words) =>
            words.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));
        var result = new List<string>(4);
        if (HasAny(text, "跟随", "跟隨", "集合", "集结", "集結", "跟我", "follow", "gather", "集合して", "ついて"))
            result.Add("follow");
        if (HasAny(text, "前压", "前壓", "进攻", "進攻", "冲", "衝", "开团", "開團", "attack", "push", "攻撃", "突撃"))
            result.Add("attack");
        if (HasAny(text, "撤退", "后退", "後退", "拉走", "散开", "散開", "retreat", "back", "退避", "下が"))
            result.Add("retreat");
        if (HasAny(text, "倒数", "倒數", "ET3", "ET2", "ET1", "3:00", "2:00", "1:00", "countdown", "カウント"))
            result.Add("countdown");
        return result;
    }

    private List<ActorVitals> CaptureVisibleActors()
    {
        var actors = new List<ActorVitals>();
        try
        {
            foreach (var actor in ObjectTable.PlayerObjects)
            {
                try
                {
                    var statuses = actor.StatusList.Select(status => new ActorStatus(
                        status.StatusId, status.SourceId, status.RemainingTime)).ToArray();
                    var worldId = actor is IPlayerCharacter player ? (ushort)player.HomeWorld.RowId : (ushort)0;
                    actors.Add(new ActorVitals(actor.EntityId, actor.Name.TextValue,
                        worldId, actor.ClassJob.RowId,
                        actor.Position.X, actor.Position.Y, actor.Position.Z,
                        actor.CurrentHp, actor.MaxHp, actor.CurrentMp, actor.MaxMp, statuses,
                        actor.ShieldPercentage));
                }
                catch (Exception ex) { Log.Verbose(ex, "Skipped an invalid loaded player object."); }
            }
        }
        catch (Exception ex) { Log.Verbose(ex, "Skipped this optional vitals sample."); }
        return actors;
    }

    private void CaptureCrystallineProgress(long elapsedMs)
    {
        try
        {
            var eventFramework = EventFramework.Instance();
            var baseDirector = eventFramework == null ? null : eventFramework->GetInstanceContentDirector();
            if (baseDirector == null) return;
            var director = (CrystallineConflictContentDirector*)baseDirector;
            var unbind = float.IsFinite(director->CrystalUnbindTimeRemaining)
                ? (int)MathF.Ceiling(Math.Clamp(director->CrystalUnbindTimeRemaining, 0, 120))
                : 0;
            var snapshot = new CrystallineProgressSnapshot(director->CrystalPosition,
                director->AstraProgress, director->UmbraProgress,
                director->AstraOnPoint, director->UmbraOnPoint,
                director->AstraCheckpointProgress, director->UmbraCheckpointProgress, unbind);
            // Reject a stale/shifted layout instead of publishing plausible-looking garbage.
            if (Math.Abs(snapshot.CrystalPosition) > 1_200 ||
                snapshot.AstraProgress is < 0 or > 1_000 ||
                snapshot.UmbraProgress is < 0 or > 1_000 ||
                snapshot.AstraCheckpointProgress is < 0 or > 100 ||
                snapshot.UmbraCheckpointProgress is < 0 or > 100 ||
                snapshot.AstraOnPoint is < 0 or > 10 || snapshot.UmbraOnPoint is < 0 or > 10)
                return;
            if (previousCrystallineProgress == snapshot && elapsedMs - lastCrystallineProgressWriteMs < 5_000)
                return;
            previousCrystallineProgress = snapshot;
            lastCrystallineProgressWriteMs = elapsedMs;
            WriteCrystallineGauge(elapsedMs, 0xFFF10001, "cc_crystal_position",
                snapshot.CrystalPosition / 10d, $"{snapshot.CrystalPosition / 10d:0.0}%");
            WriteCrystallineGauge(elapsedMs, 0xFFF10002, "cc_astra_progress",
                snapshot.AstraProgress / 10d, $"{snapshot.AstraProgress / 10d:0.0}%");
            WriteCrystallineGauge(elapsedMs, 0xFFF10003, "cc_umbra_progress",
                snapshot.UmbraProgress / 10d, $"{snapshot.UmbraProgress / 10d:0.0}%");
            WriteCrystallineGauge(elapsedMs, 0xFFF10004, "cc_astra_checkpoint",
                snapshot.AstraCheckpointProgress, $"{snapshot.AstraCheckpointProgress}%");
            WriteCrystallineGauge(elapsedMs, 0xFFF10005, "cc_umbra_checkpoint",
                snapshot.UmbraCheckpointProgress, $"{snapshot.UmbraCheckpointProgress}%");
            output.Write(new ObjectiveStateCaptureEvent(Schema, "objective_state", matchUid!,
                DateTime.UtcNow, elapsedMs, 0xFFF10006, "cc_preparation",
                snapshot.UnbindSeconds > 0 ? "active" : "complete", null,
                0, 0, 0, 0, 0, "cc_content_director_progress_v1",
                RemainingSeconds: snapshot.UnbindSeconds,
                DisplayText: snapshot.UnbindSeconds > 0 ? $"T-{snapshot.UnbindSeconds}s" : string.Empty));
        }
        catch (Exception ex) { Log.Verbose(ex, "Skipped one invalid Crystalline progress sample."); }
    }

    private void WriteCrystallineGauge(long elapsedMs, uint id, string label,
        double value, string displayText) =>
        output.Write(new ObjectiveStateCaptureEvent(Schema, "objective_state", matchUid!,
            DateTime.UtcNow, elapsedMs, id, label, "active", null,
            0, 0, 0, 0, 0, "cc_content_director_progress_v1",
            ProgressPercent: value, DisplayText: displayText));

    private void CaptureObjectiveObjectCandidates(long elapsedMs)
    {
        var current = new Dictionary<uint, ObjectiveObjectSnapshot>();
        var observer = ObjectTable.LocalPlayer;
        foreach (var actor in ObjectTable)
        {
            try
            {
                if (actor == null || actor.EntityId == 0 || actor is IPlayerCharacter) continue;
                var ownerId = actor.OwnerId;
                if (ownerId != 0 && ownerId < 0xE0000000) continue; // summon/pet, not a map objective
                var kind = actor.ObjectKind.ToString();
                var battle = actor as Dalamud.Game.ClientState.Objects.Types.IBattleChara;
                var isCrystallineCrystal = GameLayout.IsCrystallineDuty(dutyId) &&
                    actor is Dalamud.Game.ClientState.Objects.Types.IBattleNpc battleNpc &&
                    battleNpc.NameId == 11350;
                var mechanismKind = kind.Contains("Event", StringComparison.OrdinalIgnoreCase) ||
                                    kind.Contains("BattleNpc", StringComparison.OrdinalIgnoreCase);
                if (!mechanismKind && !(actor.IsTargetable && (battle?.MaxHp ?? 0) > 0)) continue;
                // Dalamud API 15 aliases the former DataId to BaseId and marks
                // DataId obsolete. Preserve both wire fields for compatibility,
                // but attest only the single API-verified identifier here.
                var realtimeHpEligible = observer != null &&
                    MathF.Sqrt(MathF.Pow(actor.Position.X - observer.Position.X, 2) +
                               MathF.Pow(actor.Position.Z - observer.Position.Z, 2)) <= ObjectiveRealtimeHpRadius;
                var snapshot = new ObjectiveObjectSnapshot(actor.EntityId, actor.BaseId, actor.BaseId,
                    kind, actor.SubKind, actor.Name.TextValue ?? string.Empty,
                    MathF.Round(actor.Position.X, 2), MathF.Round(actor.Position.Y, 2),
                    MathF.Round(actor.Position.Z, 2), actor.IsTargetable,
                    battle?.CurrentHp ?? 0, battle?.MaxHp ?? 0, ReadBattalion(actor.Address),
                    realtimeHpEligible);
                current[actor.EntityId] = snapshot;
                if (!observedObjectiveObjects.TryGetValue(actor.EntityId, out var previous) ||
                    previous != snapshot)
                    WriteObjectiveObjectObservation(elapsedMs, snapshot,
                        observedObjectiveObjects.ContainsKey(actor.EntityId) ? "change" : "load", true);
                if (isCrystallineCrystal)
                {
                    observedCrystallineCrystalIds.Add(actor.EntityId);
                    PublishObjectiveState(elapsedMs, snapshot.ObjectId, "cc_tactical_crystal",
                        "active", 255, snapshot.X, snapshot.Y, snapshot.Z, 0, 0,
                        "cc_loaded_tactical_crystal_position_v1");
                }
                else if (GameLayout.IsCrystallineDuty(dutyId) &&
                          CrystallineMechanismCatalog.TryResolve(territoryId, snapshot.BaseId, snapshot.Name,
                              out var crystallineMechanism))
                {
                    // Red Sands preloads its monster/geyser presentation pool
                    // off-stage and does not reliably unload it. Keep the raw
                    // object observation, but never publish a persistent active
                    // objective from that lifecycle. Replay uses direct actions.
                    if (RedSandsMechanics.IsTransientBaseId(territoryId, snapshot.BaseId) ||
                        RedSandsMechanics.IsTransientLabel(territoryId, crystallineMechanism.Label))
                        continue;
                    var hasHp = crystallineMechanism.HasMeaningfulHp &&
                                snapshot.RealtimeHpEligible && snapshot.MaxHp > 0;
                    PublishObjectiveState(elapsedMs, snapshot.ObjectId, crystallineMechanism.Label,
                        hasHp && snapshot.CurrentHp <= 1 ? "destroyed" : "active", 255,
                        snapshot.X, snapshot.Y, snapshot.Z,
                        hasHp ? snapshot.CurrentHp : 0, hasHp ? snapshot.MaxHp : 0,
                        $"{CrystallineMechanismCatalog.Version}:observer_loaded_object_v1");
                }
                else if (IsTacticalObjectiveName(snapshot.Name))
                    PublishObjectiveState(elapsedMs, snapshot.ObjectId, snapshot.Name,
                        snapshot.RealtimeHpEligible && snapshot.MaxHp > 0 && snapshot.CurrentHp <= 1
                            ? "destroyed" : "active",
                        snapshot.Battalion, snapshot.X, snapshot.Y, snapshot.Z,
                        snapshot.RealtimeHpEligible ? snapshot.CurrentHp : 0,
                        snapshot.RealtimeHpEligible ? snapshot.MaxHp : 0,
                        snapshot.RealtimeHpEligible
                            ? "observer_loaded_object_hp_within_190y_v2"
                            : "observer_loaded_object_position_only_outside_190y_v2");
            }
            catch (Exception ex) { Log.Verbose(ex, "Skipped one invalid objective-object candidate."); }
        }
        foreach (var missing in observedObjectiveObjects.Values
                     .Where(previous => !current.ContainsKey(previous.ObjectId)))
        {
            WriteObjectiveObjectObservation(elapsedMs, missing, "unload", false);
            if (observedCrystallineCrystalIds.Remove(missing.ObjectId))
                PublishObjectiveState(elapsedMs, missing.ObjectId, "cc_tactical_crystal", "inactive",
                    255, missing.X, missing.Y, missing.Z, 0, 0,
                    "cc_loaded_tactical_crystal_position_v1");
            else if (GameLayout.IsCrystallineDuty(dutyId) &&
                     CrystallineMechanismCatalog.TryResolve(territoryId, missing.BaseId, missing.Name,
                         out var crystallineMechanism))
                PublishObjectiveState(elapsedMs, missing.ObjectId, crystallineMechanism.Label,
                    "inactive", 255, missing.X, missing.Y, missing.Z, 0, 0,
                    $"{CrystallineMechanismCatalog.Version}:observer_loaded_object_v1");
            else if (IsTacticalObjectiveName(missing.Name))
                PublishObjectiveState(elapsedMs, missing.ObjectId, missing.Name, "inactive",
                    missing.Battalion, missing.X, missing.Y, missing.Z,
                    0, 0, "observer_loaded_object_unloaded_v2");
        }
        observedObjectiveObjects.Clear();
        foreach (var pair in current) observedObjectiveObjects[pair.Key] = pair.Value;
    }

    private void CaptureObjectiveMapMarkers(long elapsedMs)
    {
        if (dutyId == 127 && !secureStaticObjectivesPublished)
        {
            secureStaticObjectivesPublished = true;
            // Input: fixed duty-127 geometry. Output: one immutable replay
            // anchor per occupation point. These IDs are deliberately separate
            // from hashed EventMarker IDs, so a later marker unload can update
            // ownership evidence without deleting the node itself.
            foreach (var node in SecureObjectiveCatalog.Nodes)
                PublishObjectiveState(elapsedMs, node.Id, node.Name,
                    "position_known:ownership_unobserved", 255,
                    node.X, node.Y, node.Z, 0, 0, SecureObjectiveCatalog.EvidenceScope);
        }
        var current = new Dictionary<uint, ObjectiveMapMarkerSnapshot>();
        try
        {
            var agent = AgentMap.Instance();
            if (agent == null) return;
            foreach (ref readonly var marker in agent->EventMarkers.AsSpan())
            {
                if (marker.IconId == 0 || (marker.Flags & 1) != 0) continue;
                if (marker.TerritoryTypeId != 0 && marker.TerritoryTypeId != territoryId) continue;
                if (dutyId == 127 && !SecureObjectiveCatalog.IsMechanicMapIcon(marker.IconId))
                    continue;
                var label = marker.TooltipString == null
                    ? string.Empty : marker.TooltipString->ToString();
                if (string.IsNullOrWhiteSpace(label))
                    label = marker.DataId != 0 ? $"#{marker.DataId}" : $"icon:{marker.IconId}";
                var displayText = FindRenderedMarkerText(agent, marker.IconId, marker.DataId);
                // EventMarker tooltips already carry values such as
                // "Data Remaining: 32%" even when the rendered map subtext is
                // empty. Prefer the rendered UI, then fall back to the same
                // client-owned tooltip instead of losing the percentage.
                var progressPercent = ParseDisplayedPercent(displayText) ??
                                      ParseDisplayedPercent(label);
                var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int? remainingSeconds = marker.EndTimestamp >= now && marker.EndTimestamp - now <= 86_400
                    ? marker.EndTimestamp - now : null;
                // Secure's drone marker currently exposes "Spawns in: 0:40"
                // in the tooltip while EndTimestamp remains zero. Preserve the
                // client-owned text countdown instead of silently dropping it.
                if (dutyId == 127 && !remainingSeconds.HasValue)
                    remainingSeconds = SecureObjectiveCatalog.ParseDisplayedCountdownSeconds(displayText) ??
                                       SecureObjectiveCatalog.ParseDisplayedCountdownSeconds(label);
                var id = StableObjectiveMarkerId(marker.MarkerType, marker.DataId,
                    marker.Position.X, marker.Position.Z);
                var snapshot = new ObjectiveMapMarkerSnapshot(id, marker.IconId, marker.DataId,
                    marker.MarkerType, marker.EventState, label,
                    MathF.Round(marker.Position.X, 2), MathF.Round(marker.Position.Y, 2),
                    MathF.Round(marker.Position.Z, 2), MathF.Round(marker.Radius, 2),
                    marker.EndTimestamp, remainingSeconds, displayText, progressPercent);
                current[id] = snapshot;
                if (!observedObjectiveMapMarkers.TryGetValue(id, out var previous) || previous != snapshot)
                {
                    WriteObjectiveMapMarkerObservation(elapsedMs, snapshot,
                        observedObjectiveMapMarkers.ContainsKey(id) ? "change" : "load", true);
                    var mapOwner = ObjectiveOwnerFromMapIcon(dutyId, marker.IconId);
                    var state = dutyId == 127 &&
                                SecureObjectiveCatalog.IsInactiveDisplacementPanel(marker.IconId, label)
                        ? "inactive"
                        : $"active:event={marker.EventState}:icon={marker.IconId}";
                    PublishObjectiveState(elapsedMs, id, label,
                        state, mapOwner ?? 255,
                        snapshot.X, snapshot.Y, snapshot.Z, 0, 0,
                        "client_global_event_map_marker_v2", snapshot.RemainingSeconds,
                        snapshot.ProgressPercent, snapshot.DisplayText);
                }
            }
        }
        catch (Exception ex) { Log.Verbose(ex, "Skipped an invalid event-map-marker sample."); }
        foreach (var missing in observedObjectiveMapMarkers.Values
                     .Where(previous => !current.ContainsKey(previous.MarkerId)))
        {
            WriteObjectiveMapMarkerObservation(elapsedMs, missing, "unload", false);
            PublishObjectiveState(elapsedMs, missing.MarkerId, missing.Label, "inactive", 255,
                missing.X, missing.Y, missing.Z, 0, 0, "client_global_event_map_marker_v2");
        }
        observedObjectiveMapMarkers.Clear();
        foreach (var pair in current) observedObjectiveMapMarkers[pair.Key] = pair.Value;
    }

    private void ObserveActionObjective(ActionEffectEvent value)
    {
        if (dutyId != 180 || value.Actors == null) return;
        var identities = new Dictionary<uint, ActionActorIdentity>();
        foreach (var actor in value.Actors) identities[actor.EntityId] = actor;
        foreach (var effect in value.Effects)
        {
            if (!effect.TargetX.HasValue || !effect.TargetY.HasValue || !effect.TargetZ.HasValue ||
                !identities.TryGetValue(effect.TargetEntityId, out var target) ||
                !IsTacticalObjectiveName(target.Name)) continue;
            if (effect.TargetMaxHp > 0 && effect.TargetCurrentHp <= 1)
            {
                liveMarkers.RemoveObjective(effect.TargetEntityId);
                output.Write(new ObjectiveStateCaptureEvent(Schema, "objective_state", matchUid!,
                    DateTime.UtcNow, value.ElapsedMs, effect.TargetEntityId, target.Name, "destroyed",
                    null, effect.TargetX.Value, effect.TargetY.Value, effect.TargetZ.Value,
                    effect.TargetCurrentHp, effect.TargetMaxHp,
                    "action_target_hp_zero_v1"));
                emittedActionObjectives.Remove(effect.TargetEntityId);
                continue;
            }
            liveMarkers.SetObjective(new LiveObjectiveMarker(effect.TargetEntityId, target.Name,
                "active", 255, effect.TargetX.Value, effect.TargetY.Value, effect.TargetZ.Value,
                effect.TargetCurrentHp, effect.TargetMaxHp, "action_target_exact_position_v1"));
            if (!emittedActionObjectives.Add(effect.TargetEntityId)) continue;
            output.Write(new ObjectiveStateCaptureEvent(Schema, "objective_state", matchUid!,
                DateTime.UtcNow, value.ElapsedMs, effect.TargetEntityId, target.Name, "active",
                null, effect.TargetX.Value, effect.TargetY.Value, effect.TargetZ.Value,
                effect.TargetCurrentHp, effect.TargetMaxHp,
                "action_target_exact_position_v1"));
        }
    }

    private void PublishObjectiveState(long elapsedMs, uint id, string label, string state,
        byte battalion, float x, float y, float z, uint currentHp, uint maxHp, string evidenceScope,
        int? remainingSeconds = null, double? progressPercent = null, string displayText = "")
    {
        var owner = battalion <= 2 ? battalion : (byte?)null;
        output.Write(new ObjectiveStateCaptureEvent(Schema, "objective_state", matchUid!,
            DateTime.UtcNow, elapsedMs, id, label, state, owner, x, y, z,
            currentHp, maxHp, evidenceScope, RemainingSeconds: remainingSeconds,
            ProgressPercent: progressPercent, DisplayText: displayText));
        if (state == "inactive" || state.Contains("destroy", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("unload", StringComparison.OrdinalIgnoreCase))
            liveMarkers.RemoveObjective(id);
        else liveMarkers.SetObjective(new LiveObjectiveMarker(id, label, state,
            owner ?? 255, x, y, z, currentHp, maxHp, evidenceScope,
            remainingSeconds, progressPercent, displayText));
    }

    private void WriteObjectiveMapMarkerObservation(long elapsedMs,
        ObjectiveMapMarkerSnapshot value, string change, bool active) =>
        output.Write(new ObjectiveMapMarkerObservationEvent(Schema,
            "objective_map_marker_observation", matchUid!, DateTime.UtcNow, elapsedMs,
            dutyId, territoryId, value.MarkerId, value.IconId, value.DataId,
            value.MarkerType, value.EventState, value.Label, value.X, value.Y, value.Z,
            value.Radius, active, change, "client_global_event_map_marker_v2",
            value.EndTimestamp, value.RemainingSeconds, value.DisplayText,
            value.ProgressPercent));

    private static uint StableObjectiveMarkerId(byte markerType, ushort dataId, float x, float z)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var value in new[] { (uint)markerType, dataId, (uint)BitConverter.SingleToInt32Bits(x),
                         (uint)BitConverter.SingleToInt32Bits(z) })
                hash = (hash ^ value) * 16777619u;
            return hash == 0 ? 1u : hash;
        }
    }

    private static byte? ObjectiveOwnerFromMapIcon(uint currentDutyId, uint iconId)
    {
        if (currentDutyId == 127)
            return SecureObjectiveCatalog.OwnerFromIcon(iconId);
        if (currentDutyId != 130) return null;
        // Seal Rock 7.4 map UI: A/S point icons and their radius overlays use
        // the same faction ordering.  The mapping was verified against the
        // simultaneously loaded EventObj battalion in the 2026-10-29 capture.
        return iconId switch
        {
            60_485 or 60_591 or 60_595 => 0, // Maelstrom / red
            60_484 or 60_590 or 60_594 => 1, // Adders / yellow
            60_486 or 60_592 or 60_596 => 2, // Flames / blue
            _ => null
        };
    }

    private static string FindRenderedMarkerText(AgentMap* agent, uint iconId, ushort dataId)
    {
        try
        {
            var markers = agent->MapMarkers;
            var count = Math.Min((int)agent->MapMarkerCount, markers.Length);
            string fallback = string.Empty;
            for (var i = 0; i < count; i++)
            {
                ref readonly var rendered = ref markers[i];
                if (rendered.MapMarker.IconId != iconId && rendered.MapMarker.SecondaryIconId != iconId)
                    continue;
                var text = rendered.MapMarker.Subtext.ToString();
                if (rendered.DataKey == dataId && !string.IsNullOrWhiteSpace(text)) return text;
                if (string.IsNullOrWhiteSpace(fallback) && !string.IsNullOrWhiteSpace(text)) fallback = text;
            }
            return fallback;
        }
        catch { return string.Empty; }
    }

    private static double? ParseDisplayedPercent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var percent = text.IndexOf('%');
        if (percent < 1) return null;
        var start = percent - 1;
        while (start >= 0 && char.IsDigit(text[start])) start--;
        var digits = text[(start + 1)..percent];
        return double.TryParse(digits, out var value) && value is >= 0 and <= 100 ? value : null;
    }

    private void CaptureObjectiveUiNumberArrays(long elapsedMs)
    {
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null) return;
            CaptureObjectiveUiNumberArray(elapsedMs, stage, NumberArrayType.PvPFrontline,
                "PvPFrontline", "client_ui_number_array_pvp_frontline_probe_v1");
            CaptureObjectiveUiNumberArray(elapsedMs, stage, NumberArrayType.PvPMKS,
                "PvPMKS", "client_ui_number_array_crystalline_hud_probe_v1");
            CaptureObjectiveUiNumberArray(elapsedMs, stage, NumberArrayType.PvPMKSNaviMap,
                "PvPMKSNaviMap", "client_ui_number_array_pvp_map_probe_v1");
        }
        catch (Exception ex) { Log.Verbose(ex, "Skipped one invalid PvP UI-array sample."); }
    }

    private void CaptureObjectiveUiNumberArray(long elapsedMs, AtkStage* stage,
        NumberArrayType type, string name, string evidenceScope)
    {
        var data = stage->GetNumberArrayData(type);
        if (data == null || data->IntArray == null || data->Size is <= 0 or > 4096) return;
        var values = data->Span.ToArray();
        var key = (int)type;
        var first = !observedObjectiveUiArrays.TryGetValue(key, out var previous) ||
                    previous.Length != values.Length;
        var changes = new List<IndexedUiValue>();
        for (var i = 0; i < values.Length; i++)
            if ((first && values[i] != 0) || (!first && previous![i] != values[i]))
                changes.Add(new IndexedUiValue(i, values[i]));
        if (first || changes.Count > 0)
            output.Write(new ObjectiveUiNumberArrayObservationEvent(Schema,
                "objective_ui_number_array_observation", matchUid!, DateTime.UtcNow,
                elapsedMs, dutyId, territoryId, name, key, values.Length, changes, evidenceScope));
        observedObjectiveUiArrays[key] = values;
    }

    private static bool IsTacticalObjectiveName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("冰封", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("氷", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("アイス", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("石文", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("tomelith", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ovo", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("契约", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("契約", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("据点", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("據點", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("战术", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("戰術", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("tactical", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("无人机", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("無人機", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("drone", StringComparison.OrdinalIgnoreCase) ||
               // Keep the high-platform tower and node mechanisms. They have
               // no player owner and were previously discarded solely because
               // their English client names do not contain "drone".
               name.Contains("interceptor", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("command system", StringComparison.OrdinalIgnoreCase);
    }

    private void WriteObjectiveObjectObservation(long elapsedMs, ObjectiveObjectSnapshot value,
        string change, bool loaded) =>
        output.Write(new ObjectiveObjectObservationEvent(Schema, "objective_object_observation",
            matchUid!, DateTime.UtcNow, elapsedMs, dutyId, territoryId, value.ObjectId,
            value.BaseId, value.DataId, value.ObjectKind, value.SubKind, value.Name,
            value.X, value.Y, value.Z, loaded, value.IsTargetable, value.CurrentHp,
            value.MaxHp, value.Battalion, change,
            value.RealtimeHpEligible
                ? "observer_loaded_nonplayer_hp_within_190y_v2"
                : "observer_loaded_nonplayer_position_only_v2",
            value.RealtimeHpEligible));

    private static bool ValidScore(short score) => score is >= 0 and <= 5000;

    private static int VitalsFingerprint(IEnumerable<ActorVitals> actors)
    {
        var fingerprint = 0;
        foreach (var actor in actors)
        {
            // XOR keeps the aggregate independent of ObjectTable iteration order
            // without allocating and sorting a second collection every two seconds.
            fingerprint ^= HashCode.Combine(actor.EntityId, actor.CurrentHp, actor.CurrentMp, actor.ShieldPercent,
                (int)MathF.Round(actor.X * 2), (int)MathF.Round(actor.Y * 2),
                (int)MathF.Round(actor.Z * 2));
        }
        return fingerprint;
    }

    private void OnFrontlineResult(IntPtr director, IntPtr packetAddress)
    {
        try
        {
            if (matchUid == null || packetAddress == IntPtr.Zero || !GameLayout.IsFrontlineDuty(dutyId)) return;
            var packet = (FrontlineResultsPacket*)packetAddress;
            if (packet->PlayerCount is 0 or > 72 || packet->MatchLength is 0 or > 7200)
                throw new InvalidDataException("Result packet failed range validation.");
            var teams = new[] { Team("maelstrom", packet->Maelstrom), Team("adders", packet->Adders), Team("flames", packet->Flames) };
            if (teams.Any(x => x.Placement > 3 || x.TotalPoints > 5000))
                throw new InvalidDataException("Team result failed range validation.");
            var players = new List<PlayerResult>(packet->PlayerCount);
            var span = packet->PlayerSpan;
            for (var i = 0; i < packet->PlayerCount; i++)
            {
                var p = span[i];
                players.Add(new PlayerResult(ReadName(p), p.WorldId, p.ClassJobId, p.Team, p.Alliance,
                    p.Kills, p.Deaths, p.Assists, p.DamageDealt, p.DamageToOther,
                    p.DamageTaken, p.HpRestored, p.Occupations));
            }
            var result = new ResultEvent(Schema, "result", matchUid, DateTime.UtcNow,
                packet->MatchLength, packet->PlayerCount, dutyId, territoryId,
                GameLayout.LayoutVersion, teams, players);
            // The result packet is the first authoritative source for all three
            // Frontline factions and A/B/C squads.  Backfill the retained live
            // buffer only now; never guess enemy faction or squad during play.
            // 结算包才是三方阵营和分队的权威来源；赛中不猜测敌方阵营或 A/B/C。
            liveCombat.ApplyResult(players);
            CompleteMatch(result);
        }
        catch (Exception ex) { Log.Error(ex, "Frontline result capture failed; invalid result was not exported."); }
        finally { resultHook.Original(director, packetAddress); }
    }

    private void CompleteMatch(ResultEvent result)
    {
        var uid = result.MatchUid;
        var path = output.CurrentPath ?? string.Empty;
        var matchStartedAt = startedAtUtc;
        var playerName = localPlayerName;
        var realm = matchRealm;
        foreach (var profile in movementAnomaly.ExportProfiles(Schema, uid, elapsed.ElapsedMilliseconds))
            output.Write(profile);
        waitForDutyExit = true;
        ResetCaptureState();
        FinalizeCompletedMatch(uid, path, result, matchStartedAt, realm, playerName);
    }

    private void OnCrystallineResult(CrystallineMatchRecord captured)
    {
        var currentUid = matchUid;
        var record = captured with { MatchUid = currentUid ?? captured.MatchUid };
        crystallineMatchHistory.Observe(record);
        if (currentUid == null || !GameLayout.IsCrystallineDuty(dutyId)) return;

        var local = record.Players?.FirstOrDefault(player => player.WorldId == record.WorldId &&
            string.Equals(player.Name, record.PlayerName, StringComparison.OrdinalIgnoreCase));
        var localTeam = local?.Team ?? (byte)255;
        var winner = localTeam > 1 || record.EffectiveOutcome == "draw"
            ? (byte)255
            : record.EffectiveOutcome == "win" ? localTeam : (byte)(1 - localTeam);
        var teams = new[]
        {
            new TeamResult("astra", (ushort)(winner == 255 ? 0 : winner == 0 ? 1 : 2),
                (ushort)Math.Min(ushort.MaxValue, record.AstraProgress / 10), 0, 0, 0,
                (ushort)Math.Min(ushort.MaxValue, record.AstraProgress / 10), 0),
            new TeamResult("umbra", (ushort)(winner == 255 ? 0 : winner == 1 ? 1 : 2),
                (ushort)Math.Min(ushort.MaxValue, record.UmbraProgress / 10), 0, 0, 0,
                (ushort)Math.Min(ushort.MaxValue, record.UmbraProgress / 10), 0)
        };
        var players = (record.Players ?? []).Select(player => new PlayerResult(
            player.Name, player.WorldId, player.ClassJobId, player.Team, 0,
            player.Kills, player.Deaths, player.Assists,
            (uint)Math.Max(0, player.DamageDealt), 0,
            (uint)Math.Max(0, player.DamageTaken), (uint)Math.Max(0, player.HpRestored),
            player.TimeOnCrystalSeconds)).ToArray();
        // The result packet is the authoritative ten-player CC roster. Apply it
        // before finalizing so retained combat rows receive the verified side.
        // 水晶冲突结算包是十人名单的权威来源；完成战局前回填已保留事件的阵营。
        liveCombat.ApplyResult(players);
        var result = new ResultEvent(Schema, "result", currentUid, record.EndedAtUtc,
            record.MatchLengthSeconds, (ushort)players.Length, dutyId, territoryId,
            GameLayout.LayoutVersion, teams, players);
        foreach (var profile in movementAnomaly.ExportProfiles(Schema, currentUid,
                     elapsed.ElapsedMilliseconds)) output.Write(profile);
        var path = output.CurrentPath ?? string.Empty;
        var matchStartedAt = startedAtUtc;
        var realm = matchRealm;
        var playerName = localPlayerName;
        waitForDutyExit = true;
        ResetCaptureState();
        FinalizeCrystallineMatch(currentUid, path, result, matchStartedAt, realm, playerName);
    }

    private void FinalizeCrystallineMatch(string uid, string path, ResultEvent result,
        DateTime matchStartedAt, string realm, string playerName)
    {
        _ = output.CompleteAsync(result, uid, "result_packet").ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                Log.Error(task.Exception, "Could not flush Crystalline Conflict capture {MatchUid}", uid);
                return;
            }
            try
            {
                var persistedPath = output.LastCompactPath is { } streamed && File.Exists(streamed)
                    ? streamed : path;
                try
                {
                    if (persistedPath == path) persistedPath = BfcapCodec.EncodeJsonl(path, assetRoot, 3);
                    if (File.Exists(persistedPath)) File.Delete(path);
                }
                catch (Exception conversionError)
                {
                    Log.Warning(conversionError,
                        "BFCAP conversion failed for CC {MatchUid}; retaining recoverable JSONL", uid);
                }
                var analysis = LocalMatchAnalyzer.BuildCrystalline(result, matchStartedAt, realm,
                    playerName, persistedPath);
                repository.Save(analysis);
                if (configuration.AutoUpload) uploads.Enqueue(uid, persistedPath, realm);
                if (configuration.OpenAfterMatch) mainWindow.IsOpen = true;
                Log.Information("Crystalline Conflict capture completed: {MatchUid}", uid);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not finalize Crystalline Conflict capture {MatchUid}", uid);
            }
        }, TaskScheduler.Default);
    }

    private void FinalizeCompletedMatch(string uid, string path, ResultEvent result,
        DateTime matchStartedAt, string realm, string playerName)
    {
        _ = output.CompleteAsync(result, uid, "result_packet").ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                Log.Error(task.Exception, "Could not flush Frontline capture {MatchUid}", uid);
                return;
            }
            try
            {
                // Convert outside Framework.Update. JSONL is deleted only after
                // the compact container passes a complete verification read.
                var persistedPath = output.LastCompactPath is { } streamed && File.Exists(streamed)
                    ? streamed : path;
                try
                {
                    if (persistedPath == path) persistedPath = BfcapCodec.EncodeJsonl(path, assetRoot, 3);
                    if (File.Exists(persistedPath)) File.Delete(path);
                }
                catch (Exception conversionError)
                {
                    Log.Warning(conversionError,
                        "BFCAP conversion failed for {MatchUid}; retaining recoverable JSONL", uid);
                }
                var analysis = LocalMatchAnalyzer.Build(result, matchStartedAt, realm, playerName, persistedPath);
                repository.Save(analysis);
                if (configuration.AutoUpload) uploads.Enqueue(uid, persistedPath, realm);
                if (configuration.OpenAfterMatch) mainWindow.IsOpen = true;
                Log.Information("Frontline capture completed: {MatchUid}", uid);
            }
            catch (Exception ex) { Log.Error(ex, "Could not finalize Frontline capture {MatchUid}", uid); }
        }, TaskScheduler.Default);
    }

    private void EndIncompleteMatch(string reason)
    {
        if (matchUid == null) return;
        var uid = matchUid;
        var path = output.CurrentPath ?? string.Empty;
        foreach (var profile in movementAnomaly.ExportProfiles(Schema, uid, elapsed.ElapsedMilliseconds))
            output.Write(profile);
        ResetCaptureState();
        _ = output.EndAsync(uid, reason).ContinueWith(task =>
        {
            if (task.IsFaulted || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                var persistedPath = output.LastCompactPath is { } streamed && File.Exists(streamed)
                    ? streamed : BfcapCodec.EncodeJsonl(path,assetRoot,3);
                if (File.Exists(persistedPath)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex,"Incomplete capture {MatchUid} remains as recoverable JSONL",uid);
            }
        },TaskScheduler.Default);
        Log.Information("Frontline capture ended without result: {Reason}", reason);
    }

    private void ResetCaptureState()
    {
        matchUid = null; elapsed.Reset(); previous = null; visibleActorCount = 0;
        movementAnomaly.Reset(); liveMarkers.Reset(); liveScoreProjection.Reset();
        localActionIntegrity.Reset(); lastLocalActionStateSampleMs = -10_000;
        lastObjectiveObjectSampleMs = -10_000; lastCastSampleMs = -10_000;
        activeObservedCasts.Clear(); observedObjectiveObjects.Clear();
        observedObjectiveMapMarkers.Clear(); observedObjectiveUiArrays.Clear();
        previousCrystallineProgress = null; lastCrystallineProgressWriteMs = -10_000;
        secureStaticObjectivesPublished = false;
        emittedActionObjectives.Clear(); observedCrystallineCrystalIds.Clear();
    }

    private static TeamResult Team(string name, FrontlineResultsPacket.TeamStat x) => new(name,
        x.Placement, x.OccupationPoints, x.EnemyKillPoints, x.DronePoints,
        x.IcePoints, x.TotalPoints, x.KOPointLosses);

    private static string ReadName(FrontlineResultsPacket.PlayerStat player)
    {
        var bytes = new ReadOnlySpan<byte>(player.PlayerName, 32);
        var end = bytes.IndexOf((byte)0); if (end < 0) end = bytes.Length;
        return Encoding.UTF8.GetString(bytes[..end]);
    }

    private CollectorUiState UiState()
    {
        var local = ObjectTable.LocalPlayer;
        return new CollectorUiState(matchUid != null, matchUid ?? string.Empty,
            elapsed.ElapsedMilliseconds, output.CurrentPath ?? string.Empty, visibleActorCount,
            output.DroppedEvents, local?.Name.TextValue ?? localPlayerName, local?.EntityId ?? 0,
            (ushort)(local?.HomeWorld.RowId ?? 0), ReadBattalion(local?.Address ?? IntPtr.Zero), dutyId,
            liveScoreProjection.Snapshot(elapsed.ElapsedMilliseconds));
    }
    private void ToggleWindow() => mainWindow.Toggle();
    private void OpenDetailedAnalysis(LocalMatchAnalysis match) => detailWindow.Open(match);
    private void OnCommand(string _, string __) => ToggleWindow();
    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private void SaveConfiguration() => PluginInterface.SavePluginConfig(configuration);

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Framework.Update -= OnFrameworkUpdate;
        ChatGui.ChatMessage -= OnChatMessage;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        PluginInterface.UiBuilder.Draw -= DrawWindows;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        uiFont?.Dispose();
        Commands.RemoveHandler("/bflog");
        Commands.RemoveHandler("/bflive");
        Commands.RemoveHandler("/bfslice");
        Commands.RemoveHandler("/bfcache");
        Commands.RemoveHandler("/bfimport");
        resultHook.Dispose();
        crystallineResultCapture.Dispose();
        EndIncompleteMatch("plugin_disposed");
        combatCapture.Dispose();
        localActionIntegrity.Dispose();
        localFieldMarkerCapture.Dispose();
        fieldMarkerNetworkCapture.Dispose();
        localTargetMarkerCapture.Dispose();
        uploads.Dispose(); output.Dispose();
        windows.RemoveAllWindows();
    }
}
