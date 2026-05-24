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
    private const float StageNorth = -50.76f;
    private const float StageSouth = -21f;
    private const float StageEast  = 85.45f;
    private const float StageWest  = 55.6f;
    private const uint FungahDataID = 1010476;
    private const float DotRadius = 6f;

    // --- Slice Is Right 常量 ---
    private static float HalfPi => MathF.PI / 2f;
    private const float MaxDistanceSquared = 30f * 30f;
    private readonly Dictionary<ulong, DateTime> objectSpawnTimes = [];
    
    // --- 缓存数据 ---
    private bool isFungahPresent;
    private readonly List<OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.IGameObject> activeSliceObjects = [];

    // --- 共享颜色 (ABGR Hex) ---
    private const uint ColourWindGreen  = 0xFF00FF00;
    private const uint ColourWindRed    = 0xFF0000FF;
    private const uint ColourSliceBlue  = 0x26FF0000;
    private const uint ColourSliceGreen = 0x2600FF00;
    private const uint ColourSliceRed   = 0x660000FF;
    private const uint ColourWhite      = 0xFFFFFFFF;

    // --- 预计算数据 ---
    private static readonly float[] CircleSins = new float[40];
    private static readonly float[] CircleCoses = new float[40];

    private (string Left, string Right, string Down, string Up) Loc;

    protected override void Init()
    {
        Loc = DService.Instance().ClientState.ClientLanguage switch
        {
            Dalamud.Game.ClientLanguage.ChineseSimplified => ("向左移动", "向右移动", "向下移动", "向上移动"),
            _ => ("Move Left", "Move Right", "Move Down", "Move Up")
        };

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
        if (dir == null) return;
        
        var gateType = (byte)dir->GateType;

        if (gateType == 5) // AnyWayTheWindBlows
        {
            if (Throttler<string>.Shared.Throttle("GoldSaucerGATEsHelper_FungahCheck", 500))
            {
                isFungahPresent = false;
                foreach (var obj in DService.Instance().ObjectTable)
                {
                    if (obj.ObjectKind == ObjectKind.EventNpc && obj.DataID == FungahDataID)
                    {
                        isFungahPresent = true;
                        break;
                    }
                }
            }
            DrawAnyWayTheWindBlows();
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
            }
            DrawSliceIsRight();
        }
    }

    private void DrawAnyWayTheWindBlows()
    {
        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player == null) return;
        var pos = player.Position;
        if (pos.X <= StageWest || pos.X >= StageEast || pos.Z >= StageSouth || pos.Z <= StageNorth) return;

        if (!isFungahPresent) return;

        var distSq  = Vector3.DistanceSquared(pos, SafeSpot);
        var onSpot  = distSq < 0.00025f * 0.00025f;
        var isNear  = distSq < 0.05f * 0.05f;
        var colour  = onSpot ? ColourWindGreen : ColourWindRed;

        if (!DService.Instance().GameGUI.WorldToScreen(SafeSpot, out var screenPos)) return;
        var drawList = ImGui.GetBackgroundDrawList();
        drawList.AddCircleFilled(screenPos, DotRadius, colour);

        if (!onSpot && isNear)
        {
            var text = "";
            if (pos.X - SafeSpot.X > 0.015f) text = Loc.Left;
            else if (SafeSpot.X - pos.X > 0.015f) text = Loc.Right;
            else if (pos.Z < SafeSpot.Z) text = Loc.Down;
            else if (pos.Z > SafeSpot.Z) text = Loc.Up;

            if (!string.IsNullOrEmpty(text))
            {
                var textSize = ImGui.CalcTextSize(text);
                drawList.AddText(new Vector2(screenPos.X - textSize.X / 2, screenPos.Y + 10), ColourWhite, text);
            }
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

    private void RenderSliceObject(ulong objId, Vector3 position, float rotation, uint dataID)
    {
        var now = DateTime.Now;
        if (!objectSpawnTimes.TryGetValue(objId, out var spawnTime))
        {
            objectSpawnTimes[objId] = now;
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

        var halfWidth = width / 2f;
        
        var sinRot = MathF.Sin(rotation);
        var cosRot = MathF.Cos(rotation);
        
        var sinRotPerp = MathF.Sin(rotation + HalfPi);
        var cosRotPerp = MathF.Cos(rotation + HalfPi);

        var v1 = new Vector3(
            origin.X + halfWidth * sinRotPerp,
            origin.Y,
            origin.Z + halfWidth * cosRotPerp);
        
        var v2 = new Vector3(
            origin.X - halfWidth * sinRotPerp,
            origin.Y,
            origin.Z - halfWidth * cosRotPerp);

        var v3 = new Vector3(
            v2.X + length * sinRot,
            v2.Y,
            v2.Z + length * cosRot);

        var v4 = new Vector3(
            v1.X + length * sinRot,
            v1.Y,
            v1.Z + length * cosRot);

        var anyVisible = false;
        anyVisible |= gameGUI.WorldToScreen(v1, out var sp1);
        anyVisible |= gameGUI.WorldToScreen(v2, out var sp2);
        anyVisible |= gameGUI.WorldToScreen(v3, out var sp3);
        anyVisible |= gameGUI.WorldToScreen(v4, out var sp4);

        if (anyVisible)
        {
            drawList.PathLineTo(sp1);
            drawList.PathLineTo(sp2);
            drawList.PathLineTo(sp3);
            drawList.PathLineTo(sp4);
            drawList.PathFillConvex(colour);
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
