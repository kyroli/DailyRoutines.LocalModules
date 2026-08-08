using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
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
using OmenTools.OmenService;
using OmenTools.Threading;
using IGameObject = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IGameObject;
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

    private const uint GimmickSingleRect = 2010777;
    private const uint GimmickDoubleRect = 2010778;
    private const uint GimmickCircle = 2010779;

    private readonly Dictionary<ulong, long> objectSpawnTimes = [];

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
    private const int NativeShotIntervalMS = 250;
    private const int MaxTargetWalk = 128;
    private const string FireCachedTargetSig = "48 8B C4 53 48 81 EC ?? ?? ?? ?? 0F 29 70 ?? 0F 57 C9 0F 29 78 ?? 48 8B D9 F3 0F 10 B9 ?? ?? ?? ?? BA FF FF 00 00";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint FireCachedTargetDelegate(nint context);

    private bool wasInDuty;
    private AtkUnitBase* rideShootingAddon;
    private FireCachedTargetDelegate? fireCachedTarget;
    private long lastShotAt;

    private static bool IsTelegraphVisible(long firstSeen) =>
        Environment.TickCount64 - firstSeen is >= 5000 and < 12000;

    private static bool IsTelegraphExpired(long firstSeen) =>
        Environment.TickCount64 - firstSeen >= 12000;

    private static bool TryGetSliceHelperType(OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IGameObject gameObject, out uint helperType)
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

        if (DService.Instance().SigScanner.TryScanText(FireCachedTargetSig, out var fireCachedTargetAddr))
        {
            fireCachedTarget = Marshal.GetDelegateForFunctionPointer<FireCachedTargetDelegate>(fireCachedTargetAddr);
        }
        else
        {
            DService.Instance().Log.Warning("[GoldSaucerGATEsHelper] Air Force One: FireCachedTarget signature scan failed. Auto-shooting disabled.");
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
        fireCachedTarget = null;
        lastShotAt = 0;
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
        var onSpot  = distSq < 0.25f * 0.25f;
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
        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        if (localPlayer == null) return;

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
        var now = Environment.TickCount64;
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
        lastShotAt = 0;
        DService.Instance().Log.Information("[GoldSaucerGATEsHelper] Exited Air Force One Duty. Unregistered framework update and cleaned states.");
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (rideShootingAddon == null || !rideShootingAddon->IsAddonAndNodesReady()) return;
        if (fireCachedTarget == null) return;

        var now = Environment.TickCount64;
        if (now - lastShotAt < NativeShotIntervalMS) return;

        var agent = AgentRideShooting.TryGet();
        if (agent == null) return;

        var context = agent->GetContext();
        if (context == 0) return;

        var target = FindBestTarget(context);
        if (target == 0) return;

        *(Vector3*)(context + 0xCA0) = *(Vector3*)(target + 0x00);
        *(int*)(context + 0xCB0) = 1;
        *(ushort*)(context + 0xCB4) = *(ushort*)(target + 0x30);

        fireCachedTarget.Invoke(context);
        lastShotAt = now;
    }

    private static nint FindBestTarget(nint context)
    {
        var sentinel = *(nint*)(context + 0xC58);
        if (sentinel == 0) return 0;

        nint best = 0;
        var node = *(nint*)(sentinel + 0x00);

        for (var i = 0; i < MaxTargetWalk && node != 0 && node != sentinel; i++, node = *(nint*)(node + 0x00))
        {
            var target = *(nint*)(node + 0x10);
            if (target == 0) continue;

            var kind = *(int*)(target + 0x4C);
            var subState = *(int*)(target + 0x50);
            var targetType = *(nint*)(target + 0x40);

            if (kind != 2 || subState != 0 || targetType == 0) continue;

            var score = *(short*)(targetType + 0x04);
            if (score < 0) continue;

            best = target;
        }

        return best;
    }

    #endregion

    #region Air Force One Interop Definitions

    [StructLayout(LayoutKind.Explicit, Size = 0x38)]
    private struct AgentRideShooting
    {
        [FieldOffset(0x00)] public FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInterface AgentInterface;
        [FieldOffset(0x30)] public nint AddonEventInterface;

        public nint GetContext() => AddonEventInterface != 0 ? AddonEventInterface - 0x20 : 0;

        public static AgentRideShooting* TryGet()
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
            if (module == null) return null;

            return (AgentRideShooting*)module->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.RideShooting);
        }
    }

    #endregion
}
