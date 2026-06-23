using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Forms;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using OmenTools;
using OmenTools.Dalamud;
using OmenTools.Dalamud.Helpers;
using OmenTools.Extensions;
using OmenTools.Interop.Windows.Helpers;
using OmenTools.OmenService;
using OmenTools.Threading;
using IGameObject = OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.IGameObject;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using static OmenTools.Info.Game.Data.Addons;

namespace DailyRoutines.ModulesPublic;

public unsafe class GoldSaucerGATEsHelper : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "金碟机遇临门辅助" : "Gold Saucer GATEs Helper",
        Description = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified 
            ? "1. 喷风中的幸存者：提示被吹飞概率最小的站位。\n2. 必中一闪快刀斩魔：显示竹子的倒向范围。\n3. 空军装甲驾驶员：自动瞄准并射击目标。\n※ 部分功能移植自 Saucy 插件。" 
            : "1. Any Way the Wind Blows: Shows safest spot.\n2. The Slice Is Right: Shows bamboo fall area.\n3. Air Force One: Automatically shoots targets.\n※ Partially based on Saucy.",
        Category = ModuleCategory.GoldSaucer,
        Author = ["Puni.sh", "nynpsu"],
        ReportURL = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    // --- Any Way the Wind Blows 常量 ---
    private static readonly Vector3 SafeSpot = new(66.96f, -4.48f, -24.69f);
    private const float DotRadius = 6f;

    // --- Slice Is Right 常量 ---
    private static float HalfPi => MathF.PI / 2f;
    private const float MaxDistanceSquared = 30f * 30f;
    
    private const double TelegraphDelaySeconds = 5;
    private const double TelegraphDurationSeconds = 7;

    private const uint GimmickSingleRect = 2010777;
    private const uint GimmickDoubleRect = 2010778;
    private const uint GimmickCircle = 2010779;

    private readonly Dictionary<ulong, DateTime> objectSpawnTimes = [];
    
    // --- 缓存数据 ---
    private readonly List<IGameObject> activeSliceObjects = [];
    private readonly List<ulong> toRemoveList = [];

    // --- 共享颜色 (ABGR Hex) ---
    private const uint ColourWindGreen  = 0xFF00FF00;
    private const uint ColourWindRed    = 0xFF0000FF;
    private const uint ColourSliceBlue  = 0x26FF0000;
    private const uint ColourSliceGreen = 0x2600FF00;
    private const uint ColourSliceRed   = 0x660000FF;

    // --- 预计算数据 ---
    private static readonly float[] CircleSins = new float[40];
    private static readonly float[] CircleCoses = new float[40];

    // --- Air Force One 常量与状态字段 ---
    private const int ShootInterval = 100;
    private const int DedupExpiryMS = 2000;

    private bool wasInDuty;
    private AtkUnitBase* rideShootingAddon;
    private readonly Dictionary<ulong, long> shotBalloons = [];
    private ulong activeShotTargetID;
    private long activeShotTime;

    private readonly List<(Vector2 Pos, float Radius, float Dist)> bombScreenPositions = new(8);
    private readonly List<(IGameObject obj, float dist)> candidates = new(16);

    private static bool IsTelegraphVisible(DateTime firstSeen)
    {
        var now = DateTime.Now;
        var visibleFrom = firstSeen.AddSeconds(TelegraphDelaySeconds);
        var visibleUntil = visibleFrom.AddSeconds(TelegraphDurationSeconds);
        return now >= visibleFrom && now < visibleUntil;
    }

    private static bool IsTelegraphExpired(DateTime firstSeen) =>
        DateTime.Now >= firstSeen.AddSeconds(TelegraphDelaySeconds + TelegraphDurationSeconds);

    private static bool TryGetSliceHelperType(OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.IGameObject gameObject, out uint helperType)
    {
        helperType = 0;
        if (!gameObject.IsValid()) return false;
        
        if (gameObject.ObjectKind == ObjectKind.EventObj)
        {
            if (gameObject.DataID is >= GimmickSingleRect and <= GimmickCircle)
            {
                helperType = gameObject.DataID;
                return true;
            }
        }
        return false;
    }

    protected override void Init()
    {
        for (var i = 0; i < 40; i++)
        {
            var angle = MathF.PI * 2f / 40 * i;
            CircleSins[i] = MathF.Sin(angle);
            CircleCoses[i] = MathF.Cos(angle);
        }
        
        DService.Instance().ClientState.TerritoryChanged += OnTerritoryChanged;
        OnTerritoryChanged(DService.Instance().ClientState.TerritoryType);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RideShooting", OnAddonSetup);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RideShooting", OnAddonFinalize);

        if (RideShooting != null && RideShooting->IsAddonAndNodesReady())
        {
            OnAddonSetup(AddonEvent.PostSetup, null!);
        }
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnTerritoryChanged;
        WindowManager.Instance().PostDraw -= OnDraw;
        objectSpawnTimes.Clear();
        activeSliceObjects.Clear();
        toRemoveList.Clear();

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSetup);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonFinalize);
        
        if (wasInDuty)
        {
            DService.Instance().Framework.Update -= OnFrameworkUpdate;
        }

        wasInDuty = false;
        rideShootingAddon = null;
        shotBalloons.Clear();
        activeShotTargetID = 0;
        activeShotTime = 0;
    }

    private void OnTerritoryChanged(uint territory)
    {
        WindowManager.Instance().PostDraw -= OnDraw;
        objectSpawnTimes.Clear();
        activeSliceObjects.Clear();

        // 仅在金碟娱乐场（144）启用
        if (territory == 144)
            WindowManager.Instance().PostDraw += OnDraw;
    }

    private unsafe void OnDraw()
    {
        var mgr = GoldSaucerManager.Instance();
        if (mgr == null) return;
        var dir = mgr->CurrentGFateDirector;
        if (dir == null)
        {
            if (objectSpawnTimes.Count > 0) objectSpawnTimes.Clear();
            if (activeSliceObjects.Count > 0) activeSliceObjects.Clear();
            return;
        }
        
        var gateType = (byte)dir->GateType;
        if (gateType != 8)
        {
            if (objectSpawnTimes.Count > 0) objectSpawnTimes.Clear();
            if (activeSliceObjects.Count > 0) activeSliceObjects.Clear();
        }

        if (gateType == 5) // AnyWayTheWindBlows
        {
            DrawAnyWayTheWindBlows(dir);
        }
        else if (gateType == 8) // SliceIsRight
        {
            if (Throttler<string>.Shared.Throttle("GoldSaucerGATEsHelper_SliceCheck", 50))
            {
                activeSliceObjects.Clear();
                foreach (var obj in DService.Instance().ObjectTable)
                {
                    if (TryGetSliceHelperType(obj, out _))
                    {
                        activeSliceObjects.Add(obj);
                    }
                }
                PruneDespawnedObjects();
            }
            DrawSliceIsRight();
        }
    }

    private unsafe void DrawAnyWayTheWindBlows(GFateDirector* dir)
    {
        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player == null) return;
        if (!dir->Flags.HasFlag(GFateDirectorFlag.IsJoined) || dir->Flags.HasFlag(GFateDirectorFlag.IsFinished)) return;

        var pos = player.Position;

        var distSq  = Vector3.DistanceSquared(pos, SafeSpot);
        var onSpot  = distSq < 0.00025f * 0.00025f;
        var colour  = onSpot ? ColourWindGreen : ColourWindRed;

        if (!DService.Instance().GameGUI.WorldToScreen(SafeSpot, out var screenPos)) return;
        var drawList = ImGui.GetBackgroundDrawList();
        drawList.AddCircleFilled(screenPos, DotRadius, colour);
    }

    private void PruneDespawnedObjects()
    {
        if (objectSpawnTimes.Count == 0) return;

        toRemoveList.Clear();
        foreach (var (id, firstSeen) in objectSpawnTimes)
        {
            var found = false;
            for (var i = 0; i < activeSliceObjects.Count; i++)
            {
                if (activeSliceObjects[i].EntityID == id)
                {
                    found = true;
                    break;
                }
            }
            if (!found || IsTelegraphExpired(firstSeen)) toRemoveList.Add(id);
        }

        for (var i = 0; i < toRemoveList.Count; i++)
        {
            objectSpawnTimes.Remove(toRemoveList[i]);
        }
    }

    private void DrawSliceIsRight()
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;

        foreach (var obj in activeSliceObjects)
        {
            var distSq = Vector3.DistanceSquared(localPlayer.Position, obj.Position);
            if (distSq > MaxDistanceSquared) continue;

            if (TryGetSliceHelperType(obj, out var helperType))
            {
                RenderSliceObject(obj.EntityID, obj.Position, obj.Rotation, helperType);
            }
        }
    }

    private void RenderSliceObject(ulong objID, Vector3 position, float rotation, uint helperType)
    {
        var now = DateTime.Now;
        if (!objectSpawnTimes.TryGetValue(objID, out var spawnTime))
        {
            objectSpawnTimes[objID] = now;
            return;
        }

        if (!IsTelegraphVisible(spawnTime)) return;

        switch (helperType)
        {
            case GimmickSingleRect: // 单刀 - 蓝色矩形
                DrawRectWorld(position, rotation + HalfPi, 25f, 5f, ColourSliceBlue);
                break;
            case GimmickDoubleRect: // 双刀 - 两侧绿色矩形
                DrawRectWorld(position, rotation + HalfPi, 25f, 5f, ColourSliceGreen);
                DrawRectWorld(position, rotation - HalfPi, 25f, 5f, ColourSliceGreen);
                break;
            case GimmickCircle: // 圆形 AoE - 红色
                DrawFilledCircleWorld(position, 11f, ColourSliceRed);
                break;
        }
    }

    private void DrawRectWorld(Vector3 origin, float rotation, float length, float width, uint colour)
    {
        var gameGUI  = DService.Instance().GameGUI;
        var drawList = ImGui.GetBackgroundDrawList();
        var io       = ImGui.GetIO();
        var displaySize = io.DisplaySize;

        var halfWidth = width / 2f;
        
        var sinRot = MathF.Sin(rotation);
        var cosRot = MathF.Cos(rotation);
        
        var sinRotPerp = MathF.Sin(rotation + HalfPi);
        var cosRotPerp = MathF.Cos(rotation + HalfPi);

        var curRight = new Vector3(
            origin.X + halfWidth * sinRotPerp,
            origin.Y,
            origin.Z + halfWidth * cosRotPerp);
        
        var curLeft = new Vector3(
            origin.X - halfWidth * sinRotPerp,
            origin.Y,
            origin.Z - halfWidth * cosRotPerp);

        var curCenter = origin;

        const int segments = 20;
        var stepLen = length / segments;
        var stepOffset = new Vector3(stepLen * sinRot, 0f, stepLen * cosRot);

        for (var i = 0; i < segments; i++)
        {
            var nextRight  = curRight + stepOffset;
            var nextLeft   = curLeft + stepOffset;
            var nextCenter = curCenter + stepOffset;

            Span<Vector3> points = stackalloc Vector3[] { nextLeft, nextCenter, nextRight, curRight, curCenter, curLeft };
            var anyVisible = false;

            drawList.PathClear();
            foreach (var pt in points)
            {
                if (gameGUI.WorldToScreen(pt, out var sp))
                {
                    if (sp.X >= 0f && sp.X <= displaySize.X && sp.Y >= 0f && sp.Y <= displaySize.Y)
                        anyVisible = true;

                    drawList.PathLineTo(sp);
                }
            }

            if (anyVisible)
                drawList.PathFillConvex(colour);
            else
                drawList.PathClear();

            curRight = nextRight;
            curLeft = nextLeft;
            curCenter = nextCenter;
        }
    }

    private void DrawFilledCircleWorld(Vector3 center, float radius, uint colour)
    {
        var gameGUI  = DService.Instance().GameGUI;
        var drawList = ImGui.GetBackgroundDrawList();

        var anyVisible = false;
        drawList.PathClear();

        for (var i = 0; i < 40; i++)
        {
            var wp = new Vector3(
                center.X + radius * CircleSins[i],
                center.Y,
                center.Z + radius * CircleCoses[i]);

            if (gameGUI.WorldToScreen(wp, out var sp))
            {
                anyVisible = true;
            }
            drawList.PathLineTo(sp);
        }

        if (anyVisible) drawList.PathFillConvex(colour);
        else drawList.PathClear();
    }

    #region Air Force One Events & Loop

    private void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        rideShootingAddon = args != null ? (AtkUnitBase*)args.Addon.Address : RideShooting;
        if (rideShootingAddon == null) return;

        DService.Instance().Framework.Update += OnFrameworkUpdate;
        DService.Instance().Log.Information("[GoldSaucerGATEsHelper] Entered Air Force One GATE Duty! Registered framework update.");
        wasInDuty = true;
    }

    private void OnAddonFinalize(AddonEvent type, AddonArgs args)
    {
        DService.Instance().Framework.Update -= OnFrameworkUpdate;
        rideShootingAddon = null;
        wasInDuty = false;
        shotBalloons.Clear();
        bombScreenPositions.Clear();
        candidates.Clear();
        DService.Instance().Log.Information("[GoldSaucerGATEsHelper] Exited Air Force One Duty. Unregistered framework update and cleaned states.");
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (rideShootingAddon == null || !rideShootingAddon->IsAddonAndNodesReady()) return;

        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player == null) return;

        var gameGUI = DService.Instance().GameGUI;

        if (activeShotTargetID != 0)
        {
            if (Environment.TickCount64 - activeShotTime < 50)
            {
                var target = DService.Instance().ObjectTable.SearchByID(activeShotTargetID);
                if (target != null && gameGUI.WorldToScreen(target.Position, out var screenPos))
                {
                    TrySetScreenAim(screenPos);
                    return;
                }
            }
            activeShotTargetID = 0;
        }

        bombScreenPositions.Clear();
        candidates.Clear();

        foreach (var x in DService.Instance().ObjectTable)
        {
            if (x.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj) continue;

            var dataID = x.DataID;
            var eventObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.EventObject*)x.Address;

            if (dataID is 2015183 or 2009679)
            {
                if (eventObj->SharedTimelineState != 4)
                {
                    if (gameGUI.WorldToScreen(x.Position, out var bombScreen))
                    {
                        var topPos = x.Position + new Vector3(0, 2.0f, 0); 
                        if (gameGUI.WorldToScreen(topPos, out var bombTopScreen))
                        {
                            var radius = Vector2.Distance(bombScreen, bombTopScreen);
                            var dist = Vector3.Distance(player.Position, x.Position);
                            bombScreenPositions.Add((bombScreen, radius + 8f, dist));
                        }
                        else
                        {
                            var dist = Vector3.Distance(player.Position, x.Position);
                            bombScreenPositions.Add((bombScreen, 32f, dist));
                        }
                    }
                }
                continue;
            }

            if (dataID is 2009678 or 2009676 or 2009677 or 2015180 or 2015179 or 2015178)
            {
                if (eventObj->SharedTimelineState == 1)
                {
                    if (shotBalloons.TryGetValue(x.GameObjectID, out var shotTime))
                    {
                        if (Environment.TickCount64 - shotTime < DedupExpiryMS)
                            continue;
                    }
                    var dist = Vector3.Distance(player.Position, x.Position);
                    candidates.Add((x, dist));
                }
            }
        }

        candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

        IGameObject? bestTarget = null;
        var bestScreen = Vector2.Zero;
        var bestDist = 0f;

        foreach (var (obj, dist) in candidates)
        {
            if (!gameGUI.WorldToScreen(obj.Position, out var targetScreen)) continue;

            if (IsNearBombOnScreen(targetScreen, dist, bombScreenPositions))
            {
                if (Throttler.Shared.Throttle($"SkipLog-{obj.GameObjectID}", 1000))
                {
                    DService.Instance().Log.Information($"[GoldSaucerGATEsHelper] SKIP target {obj.Name} (ID={obj.DataID}) - too close to bomb on screen at {targetScreen}");
                }
                continue;
            }

            bestTarget = obj;
            bestScreen = targetScreen;
            bestDist = dist;
            break;
        }

        if (bestTarget != null)
        {
            TrySetScreenAim(bestScreen);

            if (Throttler.Shared.Throttle("AutoAirForceOne-Shoot", ShootInterval))
            {
                activeShotTargetID = bestTarget.GameObjectID;
                activeShotTime = Environment.TickCount64;
                shotBalloons[bestTarget.GameObjectID] = Environment.TickCount64;
                
                DService.Instance().Log.Information($"[GoldSaucerGATEsHelper] SHOOT: {bestTarget.Name} (ID={bestTarget.DataID}, ObjID={bestTarget.GameObjectID:X}) Pos={bestTarget.Position} Screen={bestScreen} Dist={bestDist:F1}y Bombs={bombScreenPositions.Count} Candidates={candidates.Count}");

                DService.Instance().Framework.RunOnTick(() =>
                {
                    KeyEmulationHelper.SendKeypress(Keys.Space);
                }, delayTicks: 1);
            }
        }
    }

    private static bool IsNearBombOnScreen(Vector2 targetScreen, float targetDist, List<(Vector2 Pos, float Radius, float Dist)> bombScreenPositions)
    {
        foreach (var bomb in bombScreenPositions)
        {
            if (bomb.Dist < targetDist || bomb.Dist - targetDist < 15.0f)
            {
                var dx = targetScreen.X - bomb.Pos.X;
                var dy = targetScreen.Y - bomb.Pos.Y;
                if (dx * dx + dy * dy < bomb.Radius * bomb.Radius)
                    return true;
            }
        }

        return false;
    }

    private static bool TrySetScreenAim(Vector2 screen)
    {
        var agent = AgentRideShooting.TryGet();
        var handler = agent != null ? agent->Handler : null;
        if (handler == null || (nint)handler < 0x10000 || ((nint)handler & 7) != 0) return false;

        handler->AimScreenX = screen.X;
        handler->AimScreenY = screen.Y;
        return true;
    }

    #endregion

    #region Air Force One Interop Definitions

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 0x38)]
    private struct AgentRideShooting
    {
        [System.Runtime.InteropServices.FieldOffset(0x00)] public FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInterface AgentInterface;

        [System.Runtime.InteropServices.FieldOffset(0x30)] public RideShootingHandler* Handler;

        public static AgentRideShooting* TryGet()
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
            if (module == null) return null;

            return (AgentRideShooting*)module->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.RideShooting);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 0xC78)]
    private struct RideShootingHandler
    {
        [System.Runtime.InteropServices.FieldOffset(0xC70)] public float AimScreenX;
        [System.Runtime.InteropServices.FieldOffset(0xC74)] public float AimScreenY;
    }

    #endregion
}
