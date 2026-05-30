using System;
using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

using Dalamud.Bindings.ImGui;
using OmenTools;
using OmenTools.Dalamud;
using OmenTools.Dalamud.Helpers;
using OmenTools.Extensions;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public class GoldSaucerGATEsHelper : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "金碟机遇临门辅助" : "Gold Saucer GATEs Helper",
        Description = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "1. 喷风中的幸存者：提示被吹飞概率最小的站位。\n2. 必中一闪快刀斩魔：显示竹子的倒向范围。\n※ 功能移植自 Saucy 插件。" : "1. Any Way the Wind Blows: Shows safest spot.\n2. The Slice Is Right: Shows bamboo fall area.\n※ Features ported from Saucy.",
        Category = ModuleCategory.GoldSaucer,
        Author = ["Puni.sh","nynpsu"],
        ReportURL = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    // --- Any Way the Wind Blows 常量 ---
    private static readonly Vector3 SafeSpot = new(66.96f, -4.48f, -24.69f);
    private const float DotRadius = 6f;

    // --- Slice Is Right 常量 ---
    private static float HalfPi => MathF.PI / 2f;
    private const float MaxDistanceSquared = 30f * 30f;
    private readonly Dictionary<ulong, DateTime> objectSpawnTimes = [];
    
    // --- 缓存数据 ---
    private readonly List<OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.IGameObject> activeSliceObjects = [];
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
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnTerritoryChanged;
        WindowManager.Instance().PostDraw -= OnDraw;
        objectSpawnTimes.Clear();
        activeSliceObjects.Clear();
        toRemoveList.Clear();
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
                    if (obj.ObjectKind != ObjectKind.EventObj) continue;
                    if (obj.DataID is not (>= 2010777 and <= 2010779)) continue;
                    activeSliceObjects.Add(obj);
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
        foreach (var id in objectSpawnTimes.Keys)
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
            if (!found) toRemoveList.Add(id);
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

            RenderSliceObject(obj.EntityID, obj.Position, obj.Rotation, obj.DataID);
        }
    }

    private void RenderSliceObject(ulong objID, Vector3 position, float rotation, uint dataID)
    {
        var now = DateTime.Now;
        if (!objectSpawnTimes.TryGetValue(objID, out var spawnTime))
        {
            objectSpawnTimes[objID] = now;
            return;
        }

        // 按照原版插件 SaucyCN 的逻辑，全部机关统一使用 5 秒延迟，以保证同时显示
        var delay = 5;
        if (spawnTime.AddSeconds(delay) > now) return;

        switch (dataID)
        {
            case 2010777: // 单刀 - 蓝色矩形
                DrawRectWorld(position, rotation + HalfPi, 25f, 5f, ColourSliceBlue);
                break;
            case 2010778: // 双刀 - 两侧绿色矩形
                DrawRectWorld(position, rotation + HalfPi, 25f, 5f, ColourSliceGreen);
                DrawRectWorld(position, rotation - HalfPi, 25f, 5f, ColourSliceGreen);
                break;
            case 2010779: // 圆形 AoE - 红色
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

            var points = new[] { nextLeft, nextCenter, nextRight, curRight, curCenter, curLeft };
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

}
