using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using OmenTools;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Dalamud;
using OmenTools.Extensions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static OmenTools.Info.Game.Data.Addons;

namespace DailyRoutines.ModulesPublic;

public class AutoJumboCactpotCustom : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "自动每周仙人仙彩(改)" : "Auto Jumbo Cactpot (Custom)",
        Description = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "基于官方同名模块修改，自动购买并选择每周仙人仙彩号码。\n※ 增加了“一号多买”模式：首张票随机生成，后续票自动沿用该号码。" : "Automatically purchases and selects Jumbo Cactpot numbers.\n※ Added 'Synchronized' mode: first ticket is random, rest copy the first.",
        Category    = ModuleCategory.GoldSaucer,
        Author      = ["AtmoOmen", "nynpsu"],
        ReportURL   = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    private record struct LocStrings(string SelectionMode, string ModeRandom, string ModeFixed, string ModeSync, string InputNumber, string SyncHint);
    private LocStrings Loc;

    private Config config = null!;
    private FrozenDictionary<Mode, string> NumberModeLoc = FrozenDictionary<Mode, string>.Empty;
    
    // 用于实现“随机一次”的临时状态
    private int    sessionRandomNumber = -1;
    private long lastExecuteTime     = 0;

    protected override unsafe void Init()
    {
        config = LoadConfig<Config>() ?? new();

        Loc = DService.Instance().ClientState.ClientLanguage switch
        {
            Dalamud.Game.ClientLanguage.ChineseSimplified => new(
                "选号模式", "完全随机", "固定号码", "一号多买", "指定号码", "(首张随机，后续自动沿用)"),
            _ => new(
                "Selection Mode", "Fully Random", "Fixed Number", "Synchronized", "Input Number", "(Randomize first, then synchronize)")
        };

        NumberModeLoc = new Dictionary<Mode, string>
        {
            [Mode.Random]     = Loc.ModeRandom,
            [Mode.Fixed]      = Loc.ModeFixed,
            [Mode.RandomOnce] = Loc.ModeSync
        }.ToFrozenDictionary();

        TaskHelper ??= new() { TimeoutMS = 5_000 };

        OmenTools.DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LotteryWeeklyInput", OnAddon);
        
        if (LotteryWeeklyInput != null && AtkUnitBaseExtension.IsAddonAndNodesReady(ref *LotteryWeeklyInput))
            OnAddon(AddonEvent.PostSetup, null!);
    }
    
    protected override void Uninit() =>
        OmenTools.DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(180f); 

        using (var combo = ImRaii.Combo(Loc.SelectionMode, NumberModeLoc.GetValueOrDefault(config.NumberMode, string.Empty)))
        {
            if (combo)
            {
                foreach (var modePair in NumberModeLoc)
                {
                    if (ImGui.Selectable(modePair.Value, modePair.Key == config.NumberMode))
                    {
                        config.NumberMode = modePair.Key;
                        SaveConfig(config);
                    }
                }
            }
        }

        if (config.NumberMode == Mode.Fixed)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputInt($"##{Loc.InputNumber}", ref config.FixedNumber))
            {
                config.FixedNumber = Math.Clamp(config.FixedNumber, 0, 9999);
                SaveConfig(config);
            }
        }
        else if (config.NumberMode == Mode.RandomOnce)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(Loc.SyncHint);
        }
    }

    private unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        TaskHelper.Abort();

        TaskHelper.Enqueue
        (() =>
            {
                if (!OmenTools.DService.Instance().Condition.IsOccupiedInEvent)
                {
                    TaskHelper.Abort();
                    return true;
                }

                if (!LotteryWeeklyInput->IsAddonAndNodesReady()) return false;

                var number = 0;
                var currentTime = Environment.TickCount64;

                switch (config.NumberMode)
                {
                    case Mode.Random:
                        number = Random.Shared.Next(0, 10000);
                        break;
                    case Mode.Fixed:
                        number = Math.Clamp(config.FixedNumber, 0, 9999);
                        break;
                    case Mode.RandomOnce:
                        // 如果距离上次执行超过 15 秒 (15000 毫秒)，或者是第一次执行，则重新生成随机数
                        if (currentTime - lastExecuteTime > 15000 || sessionRandomNumber == -1)
                        {
                            sessionRandomNumber = Random.Shared.Next(0, 10000);
                        }
                        number = sessionRandomNumber;
                        lastExecuteTime = currentTime;
                        break;
                }

                LotteryWeeklyInput->Callback(number);
                return true;
            }
        );

        TaskHelper.Enqueue
        (() =>
            {
                AddonSelectYesnoEvent.ClickYes();
                return false;
            }
        );
    }

    private class Config : ModuleConfig
    {
        public int  FixedNumber = 1;
        public Mode NumberMode  = Mode.Random;
    }

    public enum Mode
    {
        Random,
        Fixed,
        RandomOnce
    }
}
