using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Drawing;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Bindings.ImPlot;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using OmenTools;
using OmenTools.ImGuiOm;
using OmenTools.Extensions;
using static OmenTools.Info.Game.Data.Addons;
using static OmenTools.Global.Globals;
using Action = System.Action;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.KamiToolKit.Addons;
using DailyRoutines.Common.KamiToolKit.Nodes;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Network.Structures;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Algorithms;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using OmenTools.Threading;
using System.Collections.Frozen;
using System.Numerics;
using System.Reflection;

namespace DailyRoutines.ModulesPublic;


public unsafe partial class AutoRetainerWorkCustom : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title               = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "自动雇员作业(改)" : "Auto Retainer Work (Custom)",
        Description         = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "基于官方同名模块修改，自动收取并重新派遣雇员。\n※ 增加了与雇员交互期间会自动开启“跳过对话”模块的功能。\n※ 增加了自动改价时的超低价格倒查过滤保护，防止因个例超低价导致改价异常。" : "Automatically collects and dispatches retainers.\n※ Added auto 'Skip Dialogue' when interacting with retainers.\n※ Added fallback protection for unusual low prices when auto adjusting market price.",
        Category            = ModuleCategory.Interface,
        Author              = ["AtmoOmen", "nynpsu"],
        ReportURL           = "https://github.com/kyroli/DailyRoutines.LocalModules/issues",
        ModulesPrerequisite = ["AutoRefreshMarketSearchResult"]
    };

    private          Config            config            = null!;
    private readonly Throttler<string> retainerThrottler = new();
    private readonly HashSet<ulong>    playerRetainers   = [];

    private DRAutoRetainerWork? addon;




    private readonly RetainerWorkerBase[] workers;
    private static bool IsCN => DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;

    public AutoRetainerWorkCustom()
    {
        workers =
        [
            new CollectWorker(this),
            new EntrustDupsWorker(this),
            new GilsShareWorker(this),
            new GilsWithdrawWorker(this),
            new RefreshWorker(this),
            new TownDispatchWorker(this),
            new PriceAdjustWorker(this)
        ];
    }

    private static bool wasTalkSkipEnabledByUs;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        foreach (var worker in workers)
            worker.Init();

        addon ??= new(this);

        DService.Instance().Condition.ConditionChange += OnConditionChanged;

        if (DService.Instance().Condition[ConditionFlag.OccupiedSummoningBell])
        {
            var talkSkipModule = ModuleManager.Instance().GetModuleByName("AutoTalkSkip");
            if (talkSkipModule != null && !(ModuleManager.Instance().IsModuleEnabled("AutoTalkSkip") ?? false))
            {
                wasTalkSkipEnabledByUs = true;
                ModuleManager.Instance().LoadAsync(talkSkipModule);
            }
        }
    }

    protected override void Uninit()
    {
        addon?.Dispose();
        addon = null;

        foreach (var worker in workers)
            worker.Uninit();

        DService.Instance().Condition.ConditionChange -= OnConditionChanged;

        if (wasTalkSkipEnabledByUs)
        {
            wasTalkSkipEnabledByUs = false;
            var talkSkipModule = ModuleManager.Instance().GetModuleByName("AutoTalkSkip");
            if (talkSkipModule != null)
                ModuleManager.Instance().UnloadAsync(talkSkipModule);
        }
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.OccupiedSummoningBell) return;

        var talkSkipModule = ModuleManager.Instance().GetModuleByName("AutoTalkSkip");
        if (talkSkipModule == null) return;

        if (value)
        {
            var isEnabled = ModuleManager.Instance().IsModuleEnabled("AutoTalkSkip") ?? false;
            if (!isEnabled)
            {
                wasTalkSkipEnabledByUs = true;
                ModuleManager.Instance().LoadAsync(talkSkipModule);
            }
        }
        else
        {
            if (wasTalkSkipEnabledByUs)
            {
                wasTalkSkipEnabledByUs = false;
                ModuleManager.Instance().UnloadAsync(talkSkipModule);
            }
        }
    }

    private class TownDispatchWorker
    (
        AutoRetainerWorkCustom module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? TaskHelper;

        public override bool DrawConfigCondition() => true;

        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;

        public override void Init() => TaskHelper ??= new() { TimeoutMS = 15_000 };

        public override void Uninit()
        {
            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override void DrawConfig()
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-Dispatch-Title"));

            var imageState = ImageHelper.Instance().TryGetImage
            (
                "https://gh.atmoomen.top/StaticAssets/main/DailyRoutines/image/AutoRetainersDispatch-1.png",
                out var imageHandle
            );
            ImGui.SameLine();
            ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());

            if (ImGui.IsItemHovered())
            {
                using (ImRaii.Tooltip())
                {
                    ImGui.TextUnformatted(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-Dispatch-Description"));
                    if (imageState)
                        ImGui.Image(imageHandle.Handle, imageHandle.Size * 0.8f);
                }
            }

            using var indent = ImRaii.PushIndent();

            if (ImGui.Button(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Start")))
                EnqueueRetainersDispatch();

            ImGui.SameLine();
            if (ImGui.Button(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Stop")))
                TaskHelper.Abort();
        }

        private void EnqueueRetainersDispatch()
        {
            if (TaskHelper.AbortByConflictKey(ParentModule)) return;
            if (ParentModule.IsAnyOtherWorkerBusy(typeof(TownDispatchWorker))) return;

            var addon = (AddonSelectString*)SelectString;
            if (addon == null) return;

            var entryCount = addon->PopupMenu.PopupMenu.EntryCount;
            if (entryCount - 1 <= 0) return;

            for (var i = 0; i < entryCount - 1; i++)
            {
                var tempI = i;
                TaskHelper.Enqueue
                (
                    () =>
                    {
                        if (TaskHelper.AbortByConflictKey(ParentModule)) return true;
                        return AddonSelectStringEvent.Select(tempI);
                    },
                    IsCN ? $"点击第 {tempI} 位雇员, 拉起市场变更请求" : $"Click {tempI}th retainer, request market change"
                );
                TaskHelper.Enqueue
                (
                    () =>
                    {
                        if (TaskHelper.AbortByConflictKey(ParentModule)) return true;
                        return AddonSelectYesnoEvent.ClickYes();
                    },
                    IsCN ? "确认市场变更" : "Confirm market change"
                );
            }
        }
    }

    private class GilsWithdrawWorker
    (
        AutoRetainerWorkCustom module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? TaskHelper;

        public override bool DrawConfigCondition() => false;

        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;

        public override void Init() => TaskHelper ??= new() { TimeoutMS = 15_000 };

        public override void Uninit()
        {
            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-GilsWithdraw-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersGilWithdraw, () => TaskHelper?.Abort(), width)
            );

        private void EnqueueRetainersGilWithdraw()
        {
            if (TaskHelper.AbortByConflictKey(ParentModule)) return;
            if (ParentModule.IsAnyOtherWorkerBusy(typeof(GilsWithdrawWorker))) return;

            var count = GetValidRetainerCount(x => x.Gil > 0, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach
            (index =>
                {
                    TaskHelper.Enqueue
                    (
                        () =>
                        {
                            if (TaskHelper.AbortByConflictKey(ParentModule)) return true;
                            return ParentModule.EnterRetainer(index);
                        },
                        IsCN ? $"选择进入 {index} 号雇员" : $"Select {index}th retainer"
                    );
                    TaskHelper.Enqueue
                    (
                        () =>
                        {
                            if (TaskHelper.AbortByConflictKey(ParentModule)) return true;
                            return AddonSelectStringEvent.Select(["金币管理", "金幣管理", "Entrust or withdraw gil", "ギルの受け渡し"]);
                        },
                        IsCN ? "选择进入金币管理" : "Select Gil Management"                    );
                    TaskHelper.Enqueue
                    (
                        () =>
                        {
                            if (TaskHelper.AbortByConflictKey(ParentModule)) return true;
                            if (!Bank->IsAddonAndNodesReady()) return false;

                            var gils = AddonBankEvent.RetainerGilAmount;
                            if (gils <= 0)
                                AddonBankEvent.ClickCancel();
                            else
                            {
                                AddonBankEvent.SetNumber((uint)gils);
                                AddonBankEvent.ClickConfirm();
                            }

                            Bank->Close(true);
                            return true;
                        },
                        IsCN ? "取出所有的金币" : "Withdraw all Gil"                    );
                    TaskHelper.Enqueue
                    (
                        () =>
                        {
                            if (TaskHelper.AbortByConflictKey(ParentModule)) return true;
                            return LeaveRetainer();
                        },
                        IsCN ? "回到雇员列表" : "Return to retainer list"
                    );
                }
            );
        }
    }

    private class GilsShareWorker
    (
        AutoRetainerWorkCustom module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

        public override bool DrawConfigCondition() => false;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init() => taskHelper ??= new() { TimeoutMS = 15_000 };

        public override void Uninit()
        {
            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width)
        {
            CheckboxNode? methodOneNode   = null;
            CheckboxNode? methodTwoNode   = null;
            var           methodNodeWidth = width / 2f;

            methodOneNode = CreateOverlayCheckbox
            (
                $"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Method")} 1",
                ParentModule.config.GilsShareMethod == 0,
                isChecked =>
                {
                    if (!isChecked)
                    {
                        methodOneNode!.IsChecked = true;
                        return;
                    }

                    ParentModule.config.GilsShareMethod = 0;
                    ParentModule.config.Save(ParentModule);
                    methodTwoNode!.IsChecked = false;
                },
                methodNodeWidth,
                DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-GilsShare-MethodsHelp")
            );

            methodTwoNode = CreateOverlayCheckbox
            (
                $"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Method")} 2",
                ParentModule.config.GilsShareMethod == 1,
                isChecked =>
                {
                    if (!isChecked)
                    {
                        methodTwoNode!.IsChecked = true;
                        return;
                    }

                    ParentModule.config.GilsShareMethod = 1;
                    ParentModule.config.Save(ParentModule);
                    methodOneNode!.IsChecked = false;
                },
                methodNodeWidth,
                DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-GilsShare-MethodsHelp")
            );

            var methodRow = new HorizontalListNode
            {
                IsVisible          = true,
                Size               = new(width, 24f),
                ItemSpacing        = 4f,
                FitToContentHeight = true
            };
            methodRow.AddNode([methodOneNode, methodTwoNode]);

            return CreateOverlayCategory
            (
                DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-GilsShare-Title"),
                width,
                methodRow,
                CreateOverlayButtonRow(EnqueueRetainersGilShare, () => taskHelper?.Abort(), width)
            );
        }

        private void EnqueueRetainersGilShare()
        {
            if (taskHelper.AbortByConflictKey(ParentModule)) return;
            if (ParentModule.IsAnyOtherWorkerBusy(typeof(GilsShareWorker))) return;

            var retainerManager = RetainerManager.Instance();
            var retainerCount   = retainerManager->GetRetainerCount();

            var totalGilAmount = 0U;
            for (var i = 0U; i < GetValidRetainerCount(_ => true, out _); i++)
                totalGilAmount += retainerManager->GetRetainerBySortedIndex(i)->Gil;

            var avgAmount = (uint)Math.Floor(totalGilAmount / (double)retainerCount);
            if (avgAmount <= 1) return;

            switch (ParentModule.config.GilsShareMethod)
            {
                case 0:
                    for (var i = 0U; i < retainerCount; i++)
                        EnqueueRetainersGilShareMethodFirst(i, avgAmount);

                    break;
                case 1:
                    for (var i = 0U; i < retainerCount; i++)
                        EnqueueRetainersGilShareMethodSecond(i);

                    for (var i = 0U; i < retainerCount; i++)
                        EnqueueRetainersGilShareMethodFirst(i, avgAmount);

                    break;
            }
        }

        private void EnqueueRetainersGilShareMethodFirst(uint index, uint avgAmount)
        {
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                    return ParentModule.EnterRetainer(index);
                },
                $"选择进入 {index} 号雇员"
            );
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                    return AddonSelectStringEvent.Select(["金币管理", "金幣管理", "Entrust or withdraw gil", "ギルの受け渡し"]);
                },
                IsCN ? "选择进入金币管理" : "Select Gil Management"            );
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                    if (!Bank->IsAddonAndNodesReady()) return false;

                    var gils = AddonBankEvent.RetainerGilAmount;
                    if (gils < 0 || gils == avgAmount) // 金币恰好相等
                    {
                        AddonBankEvent.ClickCancel();
                        Bank->Close(true);
                        return true;
                    }

                    if (gils > avgAmount) // 雇员金币多于平均值
                    {
                        AddonBankEvent.SetNumber((uint)(gils - avgAmount));
                        AddonBankEvent.ClickConfirm();
                        Bank->Close(true);
                        return true;
                    }

                    // 雇员金币少于平均值
                    AddonBankEvent.SwitchMode();
                    AddonBankEvent.SetNumber((uint)(avgAmount - gils));
                    AddonBankEvent.ClickConfirm();
                    Bank->Close(true);
                    return true;
                },
                IsCN ? $"使用 1 号方法均分 {index} 号雇员的金币" : $"Share Gil evenly for retainer {index} using Method 1"
            );
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                    return LeaveRetainer();
                },
                "回到雇员列表"
            );
        }

        private void EnqueueRetainersGilShareMethodSecond(uint index)
        {
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                    return ParentModule.EnterRetainer(index);
                },
                $"选择进入 {index} 号雇员"
            );
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                    return AddonSelectStringEvent.Select(["金币管理", "金幣管理", "Entrust or withdraw gil", "ギルの受け渡し"]);
                },
                IsCN ? "选择进入金币管理" : "Select Gil Management"            );
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                    if (!Bank->IsAddonAndNodesReady()) return false;

                    var gils = AddonBankEvent.RetainerGilAmount;

                    if (gils <= 0)
                        AddonBankEvent.ClickCancel();
                    else
                    {
                        AddonBankEvent.SetNumber((uint)gils);
                        AddonBankEvent.ClickConfirm();
                    }

                    Bank->Close(true);
                    return true;
                },
                IsCN ? $"使用 2 号方法取出 {index} 号雇员的金币" : $"Withdraw Gil for retainer {index} using Method 2"
            );

            // 回到雇员列表
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                    return LeaveRetainer();
                },
                "回到雇员列表"
            );
        }
    }

    private class EntrustDupsWorker
    (
        AutoRetainerWorkCustom module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

        public override bool DrawConfigCondition() => false;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init()
        {
            taskHelper ??= new() { TimeoutMS = 15_000 };

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerItemTransferList",     OnEntrustDupsAddons);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerItemTransferProgress", OnEntrustDupsAddons);
        }

        public override void Uninit()
        {
            DService.Instance().AddonLifecycle.UnregisterListener(OnEntrustDupsAddons);

            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-EntrustDups-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersEntrust, () => taskHelper?.Abort(), width)
            );

        private void EnqueueRetainersEntrust()
        {
            if (taskHelper.AbortByConflictKey(ParentModule)) return;
            if (ParentModule.IsAnyOtherWorkerBusy(typeof(EntrustDupsWorker))) return;

            var count = GetValidRetainerCount(x => x.ItemCount > 0, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach
            (index =>
                {
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                            return ParentModule.EnterRetainer(index);
                        },
                        IsCN ? $"选择进入 {index} 号雇员" : $"Select {index}th retainer"
                    );
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                            return AddonSelectStringEvent.Select(["道具管理", "Entrust or withdraw items", "アイテムの受け渡し"]);
                        },
                        IsCN ? "选择道具管理" : "Select Entrust items"                    );
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (!ParentModule.retainerThrottler.Throttle("AutoRetainerEntrustDups", 100)) return false;
                            if (taskHelper.AbortByConflictKey(ParentModule)) return true;

                            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
                            if (agent == null || !agent->IsAgentActive()) return false;
                            AgentId.Retainer.SendEvent(0, 0);
                            return true;
                        },
                        IsCN ? "选择同类道具合并提交" : "Select merge duplicate items"                    );
                    taskHelper.DelayNext(500, "等待同类道具合并提交开始");
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                            return ExitRetainerInventory();
                        },
                        "离开雇员背包界面"
                    );
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                            return LeaveRetainer();
                        },
                        IsCN ? "回到雇员列表" : "Return to retainer list"
                    );
                }
            );
        }

        private void OnEntrustDupsAddons(AddonEvent type, AddonArgs args)
        {
            if (!taskHelper.IsBusy) return;

            switch (args.AddonName)
            {
                case "RetainerItemTransferList":
                    args.Addon.ToStruct()->Callback(1);
                    break;
                case "RetainerItemTransferProgress":
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                            var addon = AddonHelper.GetByName("RetainerItemTransferProgress");
                            if (!addon->IsAddonAndNodesReady()) return false;

                            var progress = addon->AtkValues[2].Float;

                            if (progress == 1)
                            {
                                addon->Callback(-2);
                                addon->Close(true);
                                return true;
                            }

                            return false;
                        },
                        IsCN ? "等待同类道具合并提交开始" : "Wait for duplicate items merge to start",
                        weight: 2
                    );
                    break;
            }
        }
    }

    private class RefreshWorker
    (
        AutoRetainerWorkCustom module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

        public override bool DrawConfigCondition() => false;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init() => taskHelper ??= new() { TimeoutMS = 15_000 };

        public override void Uninit()
        {
            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-Refresh-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersRefresh, () => taskHelper?.Abort(), width)
            );

        private void EnqueueRetainersRefresh()
        {
            if (ParentModule.IsAnyOtherWorkerBusy(typeof(RefreshWorker))) return;

            var count = GetValidRetainerCount(_ => true, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach
            (index =>
                {
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                            return ParentModule.EnterRetainer(index);
                        },
                        IsCN ? $"选择进入 {index} 号雇员" : $"Select {index}th retainer"
                    );
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                            return LeaveRetainer();
                        },
                        IsCN ? "回到雇员列表" : "Return to retainer list"
                    );
                }
            );
        }
    }

    private class CollectWorker
    (
        AutoRetainerWorkCustom module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

        private static readonly string[] VentureCompleteTexts = ["结束", "Complete", "完了"];

        public override bool DrawConfigCondition() => false;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init()
        {
            taskHelper ??= new() { TimeoutMS = 15_000, ShowDebug = true };

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerList);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,  "RetainerList", OnRetainerList);
        }

        public override void Uninit()
        {
            DService.Instance().AddonLifecycle.UnregisterListener(OnRetainerList);

            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-Collect-Title"),
                width,
                CreateOverlayCheckbox
                (
                    DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-Collect-AutoCollect"),
                    ParentModule.config.AutoRetainerCollect,
                    isChecked =>
                    {
                        ParentModule.config.AutoRetainerCollect = isChecked;
                        if (ParentModule.config.AutoRetainerCollect)
                            EnqueueRetainersCollect();
                        ParentModule.config.Save(ParentModule);
                    },
                    width
                ),
                CreateOverlayButtonRow(EnqueueRetainersCollect, () => taskHelper?.Abort(), width)
            );

        private void OnRetainerList(AddonEvent type, AddonArgs args)
        {
            if (ParentModule.IsAnyOtherWorkerBusy(typeof(CollectWorker))) return;

            switch (type)
            {
                case AddonEvent.PostSetup:
                    ParentModule.ObtainPlayerRetainers();
                    if (taskHelper.IsBusy) return;
                    if (!ParentModule.config.AutoRetainerCollect) break;
                    if (taskHelper.AbortByConflictKey(ParentModule)) break;
                    EnqueueRetainersCollect();
                    break;
                case AddonEvent.PostDraw:
                    if (!ParentModule.config.AutoRetainerCollect) break;
                    if (!ParentModule.retainerThrottler.Throttle("AutoRetainerCollect-AFK", 5_000)) return;

                    DService.Instance().Framework.RunOnTick
                    (
                        () =>
                        {
                            if (taskHelper.IsBusy) return;
                            EnqueueRetainersCollect();
                        },
                        TimeSpan.FromSeconds(1)
                    );
                    break;
            }
        }

        private void EnqueueRetainersCollect()
        {
            if (taskHelper.AbortByConflictKey(ParentModule)) return;

            var serverTime = Framework.GetServerTime();
            var count = GetValidRetainerCount
            (
                x => x.VentureId != 0 && x.VentureComplete != 0 && x.VentureComplete + 1 <= serverTime,
                out var validRetainers
            );

            if (count == 0)
            {
                if (taskHelper.IsBusy)
                    taskHelper.Enqueue(LeaveRetainer, IsCN ? "确保所有雇员均已返回" : "Ensure all retainers have returned");                return;
            }

            foreach (var index in validRetainers)
            {
                taskHelper.Enqueue
                (
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                        return ParentModule.EnterRetainer(index);
                    },
                    IsCN ? $"选择进入 {index} 号雇员" : $"Select {index}th retainer"
                );

                taskHelper.Enqueue
                (
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                        if (!SelectString->IsAddonAndNodesReady()) return false;
                        if (RetainerList != null) return false;

                        if (!AddonSelectStringEvent.TryScanSelectStringText(VentureCompleteTexts, out var i))
                        {
                            taskHelper.Abort();
                            taskHelper.Enqueue(LeaveRetainer, "回到雇员列表");
                            return true;
                        }

                        return AddonSelectStringEvent.Select(i);
                    },
                    "确认雇员探险完成"
                );

                taskHelper.Enqueue
                (
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                        if (!RetainerTaskResult->IsAddonAndNodesReady()) return false;

                        RetainerTaskResult->Callback(14);
                        return true;
                    },
                    "重新派遣雇员探险"
                );

                taskHelper.Enqueue
                (
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                        if (!RetainerTaskAsk->IsAddonAndNodesReady()) return false;

                        RetainerTaskAsk->Callback(12);
                        return true;
                    },
                    "确认派遣雇员探险"
                );

                taskHelper.Enqueue
                (
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                        return LeaveRetainer();
                    },
                    IsCN ? "回到雇员列表" : "Return to retainer list"
                );
            }

            taskHelper.Enqueue(EnqueueRetainersCollect, "重新检查是否有其他雇员需要收取");
        }
    }

    private abstract class RetainerWorkerBase
    (
        AutoRetainerWorkCustom module
    )
    {
        protected AutoRetainerWorkCustom ParentModule = module;

        public abstract bool IsWorkerBusy();

        public virtual bool DrawConfigCondition() => true;

        public abstract void Init();

        public virtual TreeListCategoryNode? CreateOverlayCategory(float width) => null;

        public virtual void DrawConfig() { }

        public abstract void Uninit();
        protected static TreeListCategoryNode CreateOverlayCategory(string title, float width, params NodeBase[] nodes)
        {
            var contentNode = new VerticalListNode
            {
                IsVisible        = true,
                Size             = new(width, 0f),
                FitContents      = true,
                FitWidth         = true,
                FirstItemSpacing = 4f,
                ItemSpacing      = 4f
            };
            contentNode.AddNode(nodes);

            var categoryNode = new TreeListCategoryNode
            {
                IsVisible = true,
                Size      = new(width, 28f),
                String    = title
            };
            categoryNode.AddNode(contentNode);
            categoryNode.IsCollapsed = true;

            return categoryNode;
        }

        protected static HorizontalFlexNode CreateOverlayButtonRow(Action startAction, Action stopAction, float width)
        {
            var row = new HorizontalFlexNode
            {
                IsVisible      = true,
                Size           = new(width, 28f),
                AlignmentFlags = FlexFlags.FitContentHeight | FlexFlags.FitWidth,
                ItemSpacing    = 4
            };
            row.AddNode
            (
                [
                    new TextButtonNode
                    {
                        IsVisible = true,
                        IsEnabled = true,
                        Size      = new(100f, 28f),
                        String    = DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Start"),
                        OnClick   = startAction
                    },
                    new TextButtonNode
                    {
                        IsVisible = true,
                        IsEnabled = true,
                        Size      = new(100f, 28f),
                        String    = DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Stop"),
                        OnClick   = stopAction
                    }
                ]
            );

            return row;
        }

        protected static CheckboxNode CreateOverlayCheckbox(string title, bool isChecked, Action<bool> onClick, float width, string? tooltip = null)
        {
            var node = new CheckboxNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(width, 24f),
                IsChecked = isChecked,
                String    = title,
                OnClick   = onClick
            };

            if (!string.IsNullOrWhiteSpace(tooltip))
                node.TextTooltip = tooltip;

            return node;
        }

        protected static TextNode CreateOverlayText(string text, float width)
        {
            var node = new TextNode
            {
                IsVisible     = true,
                Size          = new(width, 24f),
                FontSize      = 14,
                String        = text,
                AlignmentType = AlignmentType.Left,
            };
            node.AutoAdjustTextSize();

            return node;
        }
    }

    private class DRAutoRetainerWork : AttachedAddon
    {
        private readonly AutoRetainerWorkCustom module;
        private readonly bool isFullyConstructed;
        private TreeListNode? treeListNode;

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public DRAutoRetainerWork(AutoRetainerWorkCustom module) : base("RetainerList")
        {
            this.module = module;

            InternalName          = "DRAutoRetainerWorkCustom";
            Title                 = module.Info.Title;
            Size                  = new(260f, 320f);
            RememberClosePosition = true;

            isFullyConstructed = true;

            if (CanOpenAddon)
                Open();
        }

        protected override Vector2 PositionOffset =>
            new(0f, 6f);

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            if (WindowNode is WindowNode windowNode)
                windowNode.CloseButtonNode.IsVisible = false;

            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x4,  true);
            FlagHelper.UpdateFlag(ref addon->Flags1A0, 0x80, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x40, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A3, 0x1,  true);

            var width = ContentSize.X;
            treeListNode = new()
            {
                IsVisible               = true,
                Position                = ContentStartPosition,
                Size                    = new(width, 0f),
                CategoryVerticalSpacing = 4f,
                OnLayoutUpdate = height =>
                {
                    SetWindowSize(Size.X, ContentStartPosition.Y + height + 16f);
                    if (treeListNode == null) return;

                    treeListNode.Position = ContentStartPosition;
                    treeListNode.Height   = height;
                }
            };

            foreach (var worker in module.workers)
            {
                var categoryNode = worker.CreateOverlayCategory(width);
                if (categoryNode == null) continue;

                treeListNode.AddCategoryNode(categoryNode);
            }

            treeListNode.AttachNode(addon);
            
            treeListNode.RefreshLayout();
        }

        protected override bool CanCloseHostAddon(AtkUnitBase* hostAddon) => false;

        protected override bool CanOpenAddon => isFullyConstructed && !module.IsAnyWorkerBusy();
    }

    #region 模块界面

    protected override void ConfigUI()
    {
        foreach (var worker in workers)
        {
            if (!worker.DrawConfigCondition()) continue;

            worker.DrawConfig();

            ImGui.NewLine();
        }
    }

    #endregion

    #region 单独操作

    /// <summary>
    ///     打开指定索引对应的雇员
    /// </summary>
    private bool EnterRetainer(uint index)
    {
        if (!retainerThrottler.Throttle("EnterRetainer", 100)) return false;

        if (!RetainerList->IsAddonAndNodesReady()) return false;

        RetainerList->Callback(2, (int)index, 0, 0);
        return true;
    }

    /// <summary>
    ///     离开雇员界面
    /// </summary>
    private static bool LeaveRetainer()
    {
        // 如果存在
        if (SelectYesno->IsAddonAndNodesReady())
        {
            SelectYesno->Callback(0);
            return false;
        }

        if (SelectString->IsAddonAndNodesReady())
        {
            SelectString->Callback(-1);
            return false;
        }
        return RetainerList->IsAddonAndNodesReady();
    }

    /// <summary>
    ///     根据条件获取符合要求的雇员数量
    /// </summary>
    private static uint GetValidRetainerCount(Func<RetainerManager.Retainer, bool> predicateFunc, out List<uint> validRetainers)
    {
        validRetainers = [];

        var manager = RetainerManager.Instance();
        if (manager == null) return 0;

        var counter = 0U;

        for (var i = 0U; i < manager->GetRetainerCount(); i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null) continue;
            if (!predicateFunc(*retainer)) continue;

            validRetainers.Add(i);
            counter++;
        }

        return counter;
    }

    /// <summary>
    ///     离开雇员背包界面, 防止右键菜单残留
    /// </summary>
    private static bool ExitRetainerInventory()
    {
        var agent  = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        var agent2 = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        if (agent == null || agent2 == null || !agent->IsAgentActive()) return false;

        var addon  = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agent->GetAddonId());
        var addon2 = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agent2->GetAddonId());

        if (addon != null)
            addon->Close(true);
        if (addon2 != null)
            addon2->Callback(-1);

        AgentId.Retainer.SendEvent(0, -1);
        return true;
    }

    /// <summary>
    ///     搜索背包物品
    /// </summary>
    private static bool TrySearchItemInInventory(uint itemID, bool isHQ, out List<InventoryItem> foundItem)
    {
        foundItem = [];
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return false;

        foreach (var type in Inventories.Player)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                if (slot->ItemId == itemID &&
                    (!isHQ || isHQ && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)))
                    foundItem.Add(*slot);
            }
        }

        return foundItem.Count > 0;
    }

    /// <summary>
    ///     将雇员 ID 添加至列表
    /// </summary>
    private void ObtainPlayerRetainers()
    {
        var retainerManager = RetainerManager.Instance();
        if (retainerManager == null) return;

        for (var i = 0U; i < retainerManager->GetRetainerCount(); i++)
        {
            var retainer = retainerManager->GetRetainerBySortedIndex(i);
            if (retainer == null) break;

            playerRetainers.Add(retainer->RetainerId);
        }
    }

    /// <summary>
    ///     是否有其他 Worker 正在运行
    /// </summary>
    private bool IsAnyOtherWorkerBusy(Type current)
    {
        foreach (var worker in workers)
        {
            if (!worker.IsWorkerBusy()) continue;
            if (current == worker.GetType()) continue;
            
            return true;
        }

        return false;
    }
    
    /// <summary>
    ///     是否有 Worker 正在运行
    /// </summary>
    private bool IsAnyWorkerBusy()
    {
        foreach (var worker in workers)
        {
            if (!worker.IsWorkerBusy()) continue;
            
            return true;
        }

        return false;
    }

    #endregion

    #region 预定义

    private enum AdjustBehavior
    {
        固定值,
        百分比
    }

    [Flags]
    private enum AbortCondition
    {
        无        = 1,
        低于最小值    = 2,
        低于预期值    = 4,
        低于收购价    = 8,
        大于可接受降价值 = 16,
        高于预期值    = 32,
        高于最大值    = 64
    }

    private enum AbortBehavior
    {
        无,
        收回至雇员,
        收回至背包,
        出售至系统商店,
        改价至最小值,
        改价至预期值,
        改价至最高值
    }

    private enum SortOrder
    {
        上架顺序,
        物品ID,
        物品类型
    }

    private class PriceCheckCondition
    (
        AbortCondition                           condition,
        Func<ItemConfig, uint, uint, uint, bool> predicate
    )
    {
        public AbortCondition                           Condition { get; } = condition;
        public Func<ItemConfig, uint, uint, uint, bool> Predicate { get; } = predicate;
    }

    private static class PriceCheckConditions
    {
        private static readonly PriceCheckCondition[] Conditions =
        [
            new
            (
                AbortCondition.高于最大值,
                (cfg, _, modified, _) =>
                    modified > cfg.PriceMaximum
            ),

            new
            (
                AbortCondition.高于预期值,
                (cfg, _, modified, _) =>
                    modified > cfg.PriceExpected
            ),

            new
            (
                AbortCondition.大于可接受降价值,
                (cfg, orig, modified, _) =>
                    cfg.PriceMaxReduction != 0         &&
                    orig                  != 999999999 &&
                    orig - modified       > 0          &&
                    orig - modified       > cfg.PriceMaxReduction
            ),

            new
            (
                AbortCondition.低于收购价,
                (cfg, _, modified, _) =>
                    LuminaGetter.TryGetRow<Item>(cfg.itemID, out var itemRow) &&
                    modified <= itemRow.PriceMid
            ),

            new
            (
                AbortCondition.低于最小值,
                (cfg, _, modified, _) =>
                    modified < cfg.PriceMinimum
            ),

            new
            (
                AbortCondition.低于预期值,
                (cfg, _, modified, _) =>
                    modified < cfg.PriceExpected
            )
        ];

        /// <summary>
        ///     获取所有价格检查条件
        /// </summary>
        public static IEnumerable<PriceCheckCondition> GetAll() => Conditions;

        /// <summary>
        ///     根据条件类型获取特定的检查条件
        /// </summary>
        public static PriceCheckCondition Get(AbortCondition condition) =>
            Conditions.FirstOrDefault(x => x.Condition == condition);
    }

    private class Config : ModuleConfig
    {
        public bool AutoPriceAdjustWhenNewOnSale = true;

        public bool AutoRetainerCollect = true;

        public int GilsShareMethod;

        public Dictionary<string, ItemConfig> ItemConfigs = new()
        {
            { new ItemKey(0, false).ToString(), new ItemConfig(0, false) },
            { new ItemKey(0, true).ToString(), new ItemConfig(0,  true) }
        };

        public SortOrder MarketItemsSortOrder       = SortOrder.上架顺序;
        public float     MarketItemsWindowFontScale = 0.8f;

        public bool SendPriceAdjustProcessMessage = true;
    }

    private class ItemKey : IEquatable<ItemKey>
    {
        public ItemKey() { }

        public ItemKey(uint itemID, bool isHQ)
        {
            this.itemID = itemID;
            IsHQ   = isHQ;
        }

        public uint itemID { get; set; }
        public bool IsHQ   { get; set; }

        public bool Equals(ItemKey? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return itemID == other.itemID && IsHQ == other.IsHQ;
        }

        public override string ToString() => $"{itemID}_{(IsHQ ? "HQ" : "NQ")}";

        public override bool Equals(object? obj) => Equals(obj as ItemKey);

        public override int GetHashCode() => HashCode.Combine(itemID, IsHQ);

        public static bool operator ==(ItemKey? lhs, ItemKey? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(ItemKey lhs, ItemKey rhs) => !(lhs == rhs);
    }

    private class ItemConfig : IEquatable<ItemConfig>
    {
        public ItemConfig() { }

        public ItemConfig(uint itemID, bool isHQ)
        {
            this.itemID = itemID;
            IsHQ   = isHQ;
            ItemName = itemID == 0
                           ? DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-CommonItemPreset")
                           : LuminaGetter.GetRow<Item>(itemID)?.Name.ToString() ?? string.Empty;
        }

        public uint   itemID   { get; set; }
        public bool   IsHQ     { get; set; }
        public string ItemName { get; set; } = string.Empty;

        /// <summary>
        ///     改价行为
        /// </summary>
        public AdjustBehavior AdjustBehavior { get; set; } = AdjustBehavior.固定值;

        /// <summary>
        ///     改价具体值
        /// </summary>
        public Dictionary<AdjustBehavior, int> AdjustValues { get; set; } = new()
        {
            { AdjustBehavior.固定值, 1 },
            { AdjustBehavior.百分比, 10 }
        };

        /// <summary>
        ///     最低可接受价格 (最小值: 1)
        /// </summary>
        public int PriceMinimum { get; set; } = 100;

        /// <summary>
        ///     最大可接受价格
        /// </summary>
        public int PriceMaximum { get; set; } = 100000000;

        /// <summary>
        ///     预期价格 (最小值: PriceMinimum + 1)
        /// </summary>
        public int PriceExpected { get; set; } = 200;

        /// <summary>
        ///     最大可接受降价值 (设置为 0 以禁用)
        /// </summary>
        public int PriceMaxReduction { get; set; }

        /// <summary>
        ///     单次上架数量 (设置为 0 以禁用)
        /// </summary>
        public int UpshelfCount { get; set; }

        /// <summary>
        ///     意外情况逻辑
        /// </summary>
        public Dictionary<AbortCondition, AbortBehavior> AbortLogic { get; set; } = [];

        public bool Equals(ItemConfig? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return itemID == other.itemID && IsHQ == other.IsHQ;
        }

        public override bool Equals(object? obj) => Equals(obj as ItemConfig);

        public override int GetHashCode() => HashCode.Combine(itemID, IsHQ);

        public static bool operator ==(ItemConfig? lhs, ItemConfig? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(ItemConfig lhs, ItemConfig rhs) => !(lhs == rhs);
    }

    #endregion
}



public unsafe partial class AutoRetainerWorkCustom
{
    private class PriceAdjustWorker
    (
        AutoRetainerWorkCustom module
    ) : RetainerWorkerBase(module)
    {
        private Hook<MoveToRetainerMarketDelegate>? MoveToRetainerMarketHook;

        private static readonly List<string> SellInventoryItemsText =
        [
            "玩家所持物品",
            "Sell items in your inventory",
            "プレイヤー所持品から"
        ];

        private          TaskHelper?     taskHelper;
        private readonly ItemSelectCombo itemSelectCombo = new("AddNewItem");

        private          ItemConfig?    selectedItemConfig;
        private readonly Vector2        childSizeLeft     = ScaledVector2(200, 400);
        private          Vector2        childSizeRight    = ScaledVector2(450, 400);
        private          string         presetSearchInput = string.Empty;
        private          bool           newConfigItemHQ;
        private          AbortCondition conditionInput = AbortCondition.低于最小值;
        private          AbortBehavior  behaviorInput  = AbortBehavior.无;
        private          uint           itemModifyUnitPriceManual;
        private          uint           itemModifyCountManual;
        private          Vector2        marketDataTableImageSize = new Vector2(32) * GlobalUIScale;
        private          Vector2        manualUnitPriceImageSize = new Vector2(32) * GlobalUIScale;

        private KeyValuePair<uint, List<IMarketBoardHistoryListing>> historyListings;

        private bool          isNeedToDrawMarketListWindow;
        private bool          isNeedToDrawMarketUpshelfWindow;
        private InventoryType sourceUpshelfType;
        private ushort        sourceUpshelfSlot;
        private uint          upshelfUnitPriceInput;
        private uint          upshelfQuantityInput;
        private bool          isDisplayingTooltip;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init()
        {
            MoveToRetainerMarketHook ??= DService.Instance().Hook.HookFromMemberFunction
            (
                typeof(InventoryManager.MemberFunctionPointers),
                "MoveToRetainerMarket",
                (MoveToRetainerMarketDelegate)MoveToRetainerMarketDetour
            );
            MoveToRetainerMarketHook.Enable();
            
            taskHelper ??= new() { TimeoutMS = 30_000, ShowDebug = true };
            taskHelper.TimeoutAction = () =>
            {
                isNeedToDrawMarketListWindow = false;
                isNeedToDrawMarketUpshelfWindow = false;
                DService.Instance().Framework.RunOnTick(() =>
                {
                    var sellList = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("RetainerSellList").Address;
                    if (sellList != null && sellList->IsVisible)
                        sellList->Callback(-1);
                    var sell = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("RetainerSell").Address;
                    if (sell != null && sell->IsVisible)
                        sell->Close(true);
                });
            };

            DService.Instance().MarketBoard.HistoryReceived   += OnHistoryReceived;
            DService.Instance().MarketBoard.OfferingsReceived += OnOfferingReceived;

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "RetainerSell",     OnRetainerSell);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "RetainerSellList", OnRetainerSellList);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerSellList", OnRetainerSellList);

            WindowManager.Instance().PostDraw += DrawMarketListWindow;
            WindowManager.Instance().PostDraw += DrawUpshelfWindow;
        }

        public override void DrawConfig()
        {
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-Title"));

            ItemConfigSelector();

            ImGui.SameLine();
            ItemConfigEditor();
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-Title"),
                width,
                CreateOverlayText(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-AdjustForRetainers"), width),
                CreateOverlayButtonRow
                (
                    () =>
                    {
                        if (taskHelper is not { IsBusy: false }) return;
                        EnqueuePriceAdjustAll();
                    },
                    () => taskHelper?.Abort(),
                    width
                ),
                CreateOverlayCheckbox
                (
                    DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-SendProcessMessage"),
                    ParentModule.config.SendPriceAdjustProcessMessage,
                    isChecked =>
                    {
                        ParentModule.config.SendPriceAdjustProcessMessage = isChecked;
                        ParentModule.config.Save(ParentModule);
                    },
                    width
                ),
                CreateOverlayCheckbox
                (
                    DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale"),
                    ParentModule.config.AutoPriceAdjustWhenNewOnSale,
                    isChecked =>
                    {
                        ParentModule.config.AutoPriceAdjustWhenNewOnSale = isChecked;
                        ParentModule.config.Save(ParentModule);
                    },
                    width
                )
            );

        private void DrawMarketListWindow()
        {
            if (!isNeedToDrawMarketListWindow) return;

            if (!RetainerSellList->IsAddonAndNodesReady())
            {
                isNeedToDrawMarketListWindow = false;
                return;
            }

            var addon = RetainerSellList;
            if (addon == null) return;

            var size      = new Vector2(addon->GetScaledWidth(true), addon->GetScaledHeight(true));
            var windowPos = default(Vector2);

            ImGui.SetNextWindowSize(size);

            if (ImGui.Begin
                (
                    "改价窗口##AutoRetainerWork-PriceAdjustWorker",
                    ImGuiWindowFlags.NoTitleBar  |
                    ImGuiWindowFlags.NoResize    |
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.MenuBar
                ))
            {
                windowPos = ImGui.GetWindowPos();
                using var font = OmenTools.OmenService.FontManager.Instance().GetUIFont(ParentModule.config.MarketItemsWindowFontScale).Push();
                DrawMarketItemsTable();
                ImGui.End();
            }

            if (addon->X != (short)windowPos.X || addon->Y != (short)windowPos.Y)
                addon->SetPosition((short)windowPos.X, (short)windowPos.Y);

            if (InfoProxyItemSearch.Instance()->SearchItemId == 0) return;

            ImGui.SetNextWindowSizeConstraints(new(200, 300), new(float.MaxValue));

            if (ImGui.Begin("市场数据窗口##AutoRetainerWork-PriceAdjustWorker", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar))
            {
                DrawMarketDataTable();

                if (historyListings.Key != 0 && historyListings.Value.Count > 0)
                {
                    ImGui.NewLine();

                    DrawMarketHistoryDataTable();
                }

                ImGui.End();
            }
        }

        private void DrawUpshelfWindow()
        {
            if (!isNeedToDrawMarketUpshelfWindow) return;

            if (!RetainerSellList->IsAddonAndNodesReady())
            {
                isNeedToDrawMarketUpshelfWindow = false;
                return;
            }

            if (ImGui.Begin("上架窗口##AutoRetainerWork-PriceAdjustWorker", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            {
                using var font = OmenTools.OmenService.FontManager.Instance().UIFont120.Push();
                DrawMarketUpshelf();
                ImGui.End();
            }
        }

        public override void Uninit()
        {
            MoveToRetainerMarketHook?.Dispose();
            MoveToRetainerMarketHook = null;

            DService.Instance().AddonLifecycle.UnregisterListener(OnRetainerSell);
            DService.Instance().AddonLifecycle.UnregisterListener(OnRetainerSellList);

            WindowManager.Instance().PostDraw -= DrawMarketListWindow;
            isNeedToDrawMarketListWindow      =  false;

            WindowManager.Instance().PostDraw -= DrawUpshelfWindow;
            isNeedToDrawMarketUpshelfWindow   =  false;

            DService.Instance().MarketBoard.HistoryReceived   -= OnHistoryReceived;
            DService.Instance().MarketBoard.OfferingsReceived -= OnOfferingReceived;

            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;

            PriceCacheManager.ClearCache();
        }

        private delegate void MoveToRetainerMarketDelegate
        (
            InventoryManager* manager,
            InventoryType     srcInv,
            ushort            srcSlot,
            InventoryType     dstInv,
            ushort            dstSlot,
            uint              quantity,
            uint              unitPrice
        );

        public static class PriceCacheManager
        {
            private const           int        CACHE_EXPIRATION_MINUTES = 10;
            private static readonly PriceCache CurrentPriceCache        = new();
            private static readonly PriceCache HistoryPriceCache        = new();
            private static readonly List<uint> EmptyPrices              = [];

            public static void UpdateCache<T>
            (
                AutoRetainerWorkCustom  module,
                PriceCache        cache,
                uint              itemID,
                IEnumerable<T>    listings,
                Func<T, bool>     isHQSelector,
                Func<T, bool>     onMannequinSelector,
                Func<T, uint>     priceSelector,
                Func<T, ulong>    retainerSelector = null
            )
            {
                var filteredListings = listings
                                       .Where(x => !onMannequinSelector(x))
                                       .ToLookup(isHQSelector);

                foreach (var isHQ in new[] { false, true })
                {
                    var items = filteredListings[isHQ];
                    if (retainerSelector != null)
                        items = items.Where(x => !module.playerRetainers.Contains(retainerSelector(x)));

                    var enumerable = items as T[] ?? items.ToArray();
                    if (enumerable.Length == 0) continue;

                    var sortedPrices = enumerable.Select(priceSelector).Where(p => p > 0).OrderBy(p => p).Take(3).ToList();
                    if (sortedPrices.Count == 0) continue;

                    var cacheKey = CacheKeys.Create(itemID, isHQ);
                    if (!cache.TryGetPrice(cacheKey, out var currentPrice) || sortedPrices[0] <= currentPrice)
                        cache.SetPrices(cacheKey, sortedPrices);
                }
            }

            public static void UpdateHistoryCache<T>
            (
                PriceCache     cache,
                uint           itemID,
                IEnumerable<T> listings,
                Func<T, bool>  isHQSelector,
                Func<T, bool>  onMannequinSelector,
                Func<T, uint>  priceSelector
            )
            {
                var filteredListings = listings
                                       .Where(x => !onMannequinSelector(x))
                                       .ToLookup(isHQSelector);

                foreach (var isHQ in new[] { false, true })
                {
                    var items      = filteredListings[isHQ];
                    var enumerable = items as T[] ?? items.ToArray();
                    if (enumerable.Length == 0) continue;

                    var sortedPrices = enumerable.Select(priceSelector).Where(p => p > 0).OrderBy(p => p).Take(3).ToList();
                    if (sortedPrices.Count == 0) continue;

                    var cacheKey = CacheKeys.Create(itemID, isHQ);
                    if (!cache.TryGetPrice(cacheKey, out var currentPrice) || sortedPrices[0] <= currentPrice)
                        cache.SetPrices(cacheKey, sortedPrices);
                }
            }

            public static void OnOfferingReceived(AutoRetainerWorkCustom module, IMarketBoardCurrentOfferings data)
            {
                if (!data.ItemListings.Any()) return;
                UpdateCache
                (
                    module,
                    CurrentPriceCache,
                    data.ItemListings[0].ItemId,
                    data.ItemListings,
                    x => x.IsHq,
                    x => x.OnMannequin,
                    x => x.PricePerUnit,
                    x => x.RetainerId
                );
            }

            public static void OnHistoryReceived(IMarketBoardHistory history)
            {
                if (!history.HistoryListings.Any()) return;
                UpdateHistoryCache
                (
                    HistoryPriceCache,
                    history.ItemId,
                    history.HistoryListings,
                    x => x.IsHq,
                    x => x.OnMannequin,
                    x => x.SalePrice
                );
            }

            public static bool TryGetPriceCache(uint itemID, bool isHQ, out uint price)
            {
                price = 0;
                var cacheKey         = CacheKeys.Create(itemID, isHQ);
                var oppositeCacheKey = CacheKeys.Create(itemID, !isHQ);

                // 清理过期缓存
                CurrentPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));
                HistoryPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));

                // 按优先级尝试获取价格
                return (CurrentPriceCache.TryGetPrice(cacheKey,         out price) ||
                        CurrentPriceCache.TryGetPrice(oppositeCacheKey, out price) ||
                        HistoryPriceCache.TryGetPrice(cacheKey,         out price) ||
                        HistoryPriceCache.TryGetPrice(oppositeCacheKey, out price)) &&
                       price != 0;
            }

            public static bool TryGetPricesCache(uint itemID, bool isHQ, out List<uint> prices)
            {
                prices = EmptyPrices;
                var cacheKey         = CacheKeys.Create(itemID, isHQ);
                var oppositeCacheKey = CacheKeys.Create(itemID, !isHQ);

                // 清理过期缓存
                CurrentPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));
                HistoryPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));

                // 按优先级尝试获取价格列表
                if (CurrentPriceCache.TryGetPrices(cacheKey, out prices) && prices.Count > 0) return true;
                if (CurrentPriceCache.TryGetPrices(oppositeCacheKey, out prices) && prices.Count > 0) return true;
                if (HistoryPriceCache.TryGetPrices(cacheKey, out prices) && prices.Count > 0) return true;
                if (HistoryPriceCache.TryGetPrices(oppositeCacheKey, out prices) && prices.Count > 0) return true;

                prices = EmptyPrices;
                return false;
            }

            public static (DateTime Current, DateTime History) GetCacheTimes() => 
                (CurrentPriceCache.LastUpdateTime, HistoryPriceCache.LastUpdateTime);

            public static void ClearCache(bool clearCurrent = true, bool clearHistory = true)
            {
                if (clearCurrent)
                    CurrentPriceCache.Clear();
                if (clearHistory)
                    HistoryPriceCache.Clear();
            }

            private static class CacheKeys
            {
                public static string Create(uint itemID, bool isHQ) => $"{itemID}_{(isHQ ? "HQ" : "NQ")}";
            }
        }

        public sealed class PriceCache
        {
            private static readonly List<uint> EmptyPrices = [];
            private readonly Dictionary<string, CacheEntry> data = [];

            public DateTime LastUpdateTime { get; private set; } = DateTime.MinValue;

            public void RemoveExpiredEntries(TimeSpan expirationTime)
            {
                var now = StandardTimeManager.Instance().Now;
                var expiredKeys = data
                                  .Where(kvp => now - kvp.Value.LastUpdateTime > expirationTime)
                                  .Select(kvp => kvp.Key)
                                  .ToList();

                foreach (var key in expiredKeys)
                    data.Remove(key);

                if (!data.Any())
                    LastUpdateTime = DateTime.MinValue;
            }

            public bool TryGetPrice(string key, out uint price)
            {
                price = 0;

                if (data.TryGetValue(key, out var entry))
                {
                    price = entry.Price;
                    return true;
                }

                return false;
            }

            public bool TryGetPrices(string key, out List<uint> prices)
            {
                prices = EmptyPrices;

                if (data.TryGetValue(key, out var entry))
                {
                    prices = entry.Prices;
                    return true;
                }

                return false;
            }

            public void SetPrice(string key, uint price)
            {
                data[key] = new CacheEntry
                {
                    Price          = price,
                    Prices         = [price],
                    LastUpdateTime = StandardTimeManager.Instance().Now
                };
                LastUpdateTime = StandardTimeManager.Instance().Now;
            }

            public void SetPrices(string key, List<uint> prices)
            {
                data[key] = new CacheEntry
                {
                    Price          = prices.Count > 0 ? prices[0] : 0,
                    Prices         = prices,
                    LastUpdateTime = StandardTimeManager.Instance().Now
                };
                LastUpdateTime = StandardTimeManager.Instance().Now;
            }

            public void Clear()
            {
                data.Clear();
                LastUpdateTime = DateTime.MinValue;
            }

            private class CacheEntry
            {
                public uint         Price          { get; init; }
                public List<uint>   Prices         { get; init; } = [];
                public DateTime     LastUpdateTime { get; init; }
            }
        }

        #region 配置界面

        private void ItemConfigSelector()
        {
            using var child = ImRaii.Child("ItemConfigSelectorChild", childSizeLeft, true);
            if (!child) return;

            if (ImGuiOm.ButtonIcon("AddNewConfig", FontAwesomeIcon.Plus, DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Add")))
                ImGui.OpenPopup("AddNewPreset");

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("ImportConfig", FontAwesomeIcon.FileImport, DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("ImportFromClipboard")))
            {
                var itemConfig = ImportFromClipboard<ItemConfig>();

                if (itemConfig != null)
                {
                    var itemKey = new ItemKey(itemConfig.itemID, itemConfig.IsHQ).ToString();
                    ParentModule.config.ItemConfigs[itemKey] = itemConfig;
                }
            }

            using (var popup0 = ImRaii.Popup("AddNewPreset"))
            {
                if (popup0)
                {
                    AddNewConfigItemPopup
                    (() =>
                        {
                            var newConfigStr = new ItemKey(itemSelectCombo.SelectedID, newConfigItemHQ).ToString();
                            var newConfig    = new ItemConfig(itemSelectCombo.SelectedID, newConfigItemHQ);

                            if (ParentModule.config.ItemConfigs.TryAdd(newConfigStr, newConfig))
                            {
                                ParentModule.config.Save(ParentModule);
                                ImGui.CloseCurrentPopup();
                            }
                        }
                    );
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("###PresetSearchInput", DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("PleaseSearch"), ref presetSearchInput, 100);

            ImGui.Separator();

            foreach (var itemConfig in ParentModule.config.ItemConfigs.ToList())
            {
                if (!string.IsNullOrWhiteSpace(presetSearchInput) && !itemConfig.Value.ItemName.Contains(presetSearchInput))
                    continue;

                if (ImGui.Selectable
                    (
                        $"{itemConfig.Value.ItemName} {(itemConfig.Value.IsHQ ? "(HQ)" : "")}",
                        itemConfig.Value == selectedItemConfig
                    ))
                    selectedItemConfig = itemConfig.Value;

                var isOpenPopup = false;

                using (var popup1 = ImRaii.ContextPopupItem($"{itemConfig.Value}_{itemConfig.Key}_{itemConfig.Value.itemID}"))
                {
                    if (popup1)
                    {
                        if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("ExportToClipboard")))
                            ExportToClipboard(itemConfig.Value);

                        if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-CreateNewBaseOnExisted")))
                            isOpenPopup = true;

                        if (itemConfig.Value.itemID != 0)
                        {
                            if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Delete")))
                            {
                                ParentModule.config.ItemConfigs.Remove(itemConfig.Key);
                                ParentModule.config.Save(ParentModule);

                                selectedItemConfig = null;
                            }
                        }
                    }
                }

                if (isOpenPopup)
                    ImGui.OpenPopup($"AddNewPresetBasedOnExisted_{itemConfig.Key}");

                using (var popup2 = ImRaii.Popup($"AddNewPresetBasedOnExisted_{itemConfig.Key}"))
                {
                    if (popup2)
                    {
                        AddNewConfigItemPopup
                        (() =>
                            {
                                var newConfigStr = new ItemKey(itemSelectCombo.SelectedID, newConfigItemHQ).ToString();
                                var newConfig = new ItemConfig
                                {
                                    itemID            = itemSelectCombo.SelectedID,
                                    IsHQ              = newConfigItemHQ,
                                    ItemName          = itemSelectCombo.SelectedItem.Name.ToString() ?? string.Empty,
                                    AbortLogic        = itemConfig.Value.AbortLogic,
                                    AdjustBehavior    = itemConfig.Value.AdjustBehavior,
                                    AdjustValues      = itemConfig.Value.AdjustValues,
                                    PriceExpected     = itemConfig.Value.PriceExpected,
                                    PriceMaximum      = itemConfig.Value.PriceMaximum,
                                    PriceMaxReduction = itemConfig.Value.PriceMaxReduction,
                                    PriceMinimum      = itemConfig.Value.PriceMinimum
                                };

                                if (ParentModule.config.ItemConfigs.TryAdd(newConfigStr, newConfig))
                                {
                                    ParentModule.config.Save(ParentModule);
                                    ImGui.CloseCurrentPopup();
                                }
                            }
                        );
                    }
                }

                if (itemConfig.Value is { itemID: 0, IsHQ: true })
                    ImGui.Separator();
            }
        }

        private void AddNewConfigItemPopup(Action confirmAction)
        {
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            itemSelectCombo.DrawRadio();

            ImGui.SameLine();
            ImGui.Checkbox("HQ", ref newConfigItemHQ);

            ImGui.SameLine();
            if (ImGui.Button(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Confirm")))
                confirmAction();
        }

        private void ItemConfigEditor()
        {
            childSizeRight.X = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X;
            using var child = ImRaii.Child("ItemConfigEditorChild", childSizeRight, true);

            if (selectedItemConfig == null) return;

            // 基本信息获取
            if (!LuminaGetter.TryGetRow<Item>(selectedItemConfig.itemID, out var item)) return;

            var itemName = selectedItemConfig.itemID == 0
                               ? DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-CommonItemPreset")
                               : item.Name.ToString() ?? string.Empty;

            var itemLogo = DService.Instance().Texture
                                   .GetFromGameIcon(new(selectedItemConfig.itemID == 0 ? 65002 : (uint)item.Icon, selectedItemConfig.IsHQ))
                                   .GetWrapOrDefault();
            if (itemLogo == null) return;

            var itemBuyingPrice = selectedItemConfig.itemID == 0 ? 1 : item.PriceLow;

            if (!child) return;

            // 物品基本信息展示
            ImGui.Image(itemLogo.Handle, ScaledVector2(48f));

            ImGui.SameLine();

            using (OmenTools.OmenService.FontManager.Instance().UIFont140.Push()){ImGui.TextUnformatted(itemName);}

            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6f * GlobalUIScale);
            ImGui.TextUnformatted(selectedItemConfig.IsHQ ? $"({DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("HQ")})" : string.Empty);

            ImGui.Separator();

            // 改价逻辑配置
            using (ImRaii.Group())
            {
                foreach (AdjustBehavior behavior in Enum.GetValues<AdjustBehavior>())
                {
                    if (ImGui.RadioButton(GetLoc(behavior), behavior == selectedItemConfig.AdjustBehavior))
                    {
                        selectedItemConfig.AdjustBehavior = behavior;
                        ParentModule.config.Save(ParentModule);
                    }
                }
            }

            ImGui.SameLine();

            using (ImRaii.Group())
            {
                if (selectedItemConfig.AdjustBehavior == AdjustBehavior.固定值)
                {
                    var originalValue = selectedItemConfig.AdjustValues[AdjustBehavior.固定值];
                    ImGui.SetNextItemWidth(100f * GlobalUIScale);
                    ImGui.InputInt(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-ValueReduction"), ref originalValue);

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        selectedItemConfig.AdjustValues[AdjustBehavior.固定值] = originalValue;
                        ParentModule.config.Save(ParentModule);
                    }
                }
                else
                    ImGui.Dummy(new(ImGui.GetTextLineHeightWithSpacing()));

                if (selectedItemConfig.AdjustBehavior == AdjustBehavior.百分比)
                {
                    var originalValue = selectedItemConfig.AdjustValues[AdjustBehavior.百分比];
                    ImGui.SetNextItemWidth(100f * GlobalUIScale);
                    ImGui.InputInt(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-PercentageReduction"), ref originalValue);

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        selectedItemConfig.AdjustValues[AdjustBehavior.百分比] = Math.Clamp(originalValue, -99, 99);
                        ParentModule.config.Save(ParentModule);
                    }
                }
                else
                    ImGui.Dummy(new(ImGui.GetTextLineHeightWithSpacing()));
            }

            ImGuiOm.ScaledDummy(10f);

            // 最低可接受价格
            var originalMin = selectedItemConfig.PriceMinimum;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-PriceMinimum"), ref originalMin);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceMinimum = Math.Max(1, originalMin);
                ParentModule.config.Save(ParentModule);
            }

            ImGui.SameLine();

            using (ImRaii.Disabled(selectedItemConfig.itemID == 0))
            {
                if (ImGuiOm.ButtonIcon("ObtainBuyingPrice", FontAwesomeIcon.Store, DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-ObtainBuyingPrice")))
                {
                    selectedItemConfig.PriceMinimum = Math.Max(1, (int)itemBuyingPrice);
                    ParentModule.config.Save(ParentModule);
                }
            }

            // 最高可接受价格
            var originalMax = selectedItemConfig.PriceMaximum;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-PriceMaximum"), ref originalMax);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceMaximum = Math.Min(int.MaxValue, originalMax);
                ParentModule.config.Save(ParentModule);
            }

            // 预期价格
            var originalExpected = selectedItemConfig.PriceExpected;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-PriceExpected"), ref originalExpected);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceExpected = Math.Max(originalMin + 1, originalExpected);
                ParentModule.config.Save(ParentModule);
            }

            ImGui.SameLine();

            using (ImRaii.Disabled(selectedItemConfig.itemID == 0))
            {
                if (ImGuiOm.ButtonIcon("OpenUniversalis", FontAwesomeIcon.Globe, DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-OpenUniversalis")))
                    Util.OpenLink($"https://universalis.app/market/{selectedItemConfig.itemID}");
            }

            // 可接受降价值
            var originalPriceReducion = selectedItemConfig.PriceMaxReduction;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-PriceMaxReduction"), ref originalPriceReducion);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceMaxReduction = Math.Max(0, originalPriceReducion);
                ParentModule.config.Save(ParentModule);
            }

            // 单次上架数
            var originalUpshelfCount = selectedItemConfig.UpshelfCount;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-UpshelfCount"), ref originalUpshelfCount);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.UpshelfCount = originalUpshelfCount;
                ParentModule.config.Save(ParentModule);
            }

            ImGuiOm.ScaledDummy(10f);

            // 意外情况
            using (ImRaii.Group())
            {
                ImGui.SetNextItemWidth(250f * GlobalUIScale);

                using (var combo = ImRaii.Combo("###AddNewLogicConditionCombo", GetLoc(conditionInput), ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        foreach (AbortCondition condition in Enum.GetValues(typeof(AbortCondition)))
                        {
                            if (condition == AbortCondition.无) continue;

                            if (ImGui.Selectable(GetLoc(condition), conditionInput.HasFlag(condition), ImGuiSelectableFlags.DontClosePopups))
                            {
                                var combinedCondition = conditionInput;
                                if (conditionInput.HasFlag(condition))
                                    combinedCondition &= ~condition;
                                else
                                    combinedCondition |= condition;

                                conditionInput = combinedCondition;
                            }
                        }
                    }
                }

                ImGui.SetNextItemWidth(250f * GlobalUIScale);

                using (var combo = ImRaii.Combo("###AddNewLogicBehaviorCombo", GetLoc(behaviorInput), ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        foreach (AbortBehavior behavior in Enum.GetValues(typeof(AbortBehavior)))
                        {
                            if (ImGui.Selectable(GetLoc(behavior), behaviorInput == behavior, ImGuiSelectableFlags.DontClosePopups))
                                behaviorInput = behavior;
                        }
                    }
                }
            }

            var groupSize0 = ImGui.GetItemRectSize();

            ImGui.SameLine();

            if (ImGuiOm.ButtonIconWithTextVertical
                (
                    FontAwesomeIcon.Plus,
                    DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Add"),
                    groupSize0 with { X = ImGui.CalcTextSize(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Add")).X * 2f }
                ))
            {
                if (conditionInput != AbortCondition.无)
                {
                    selectedItemConfig.AbortLogic.TryAdd(conditionInput, behaviorInput);
                    ParentModule.config.Save(ParentModule);
                }
            }

            ImGui.Separator();

            foreach (var logic in selectedItemConfig.AbortLogic.ToList())
            {
                // 条件处理 (键)
                var origConditionStr = GetLoc(logic.Key);
                ImGui.SetNextItemWidth(300f * GlobalUIScale);
                ImGui.InputText($"###Condition_{origConditionStr}", ref origConditionStr, 100, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.IsItemClicked())
                    ImGui.OpenPopup($"###ConditionSelectPopup_{origConditionStr}");

                using (var popup = ImRaii.Popup($"###ConditionSelectPopup_{origConditionStr}"))
                {
                    if (popup)
                    {
                        foreach (AbortCondition condition in Enum.GetValues(typeof(AbortCondition)))
                        {
                            if (ImGui.Selectable(GetLoc(condition), logic.Key.HasFlag(condition)))
                            {
                                var combinedCondition = logic.Key;
                                if (logic.Key.HasFlag(condition))
                                    combinedCondition &= ~condition;
                                else
                                    combinedCondition |= condition;

                                if (!selectedItemConfig.AbortLogic.ContainsKey(combinedCondition))
                                {
                                    var origBehavior = logic.Value;
                                    selectedItemConfig.AbortLogic[combinedCondition] = origBehavior;
                                    selectedItemConfig.AbortLogic.Remove(logic.Key);
                                    ParentModule.config.Save(ParentModule);
                                }
                            }
                        }
                    }
                }

                ImGui.SameLine();
                ImGui.TextUnformatted("→");

                // 行为处理 (值)
                var origBehaviorStr = GetLoc(logic.Value);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(300f * GlobalUIScale);
                ImGui.InputText($"###Behavior_{origBehaviorStr}", ref origBehaviorStr, 128, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.IsItemClicked())
                    ImGui.OpenPopup($"###BehaviorSelectPopup_{origBehaviorStr}");

                using (var popup = ImRaii.Popup($"###BehaviorSelectPopup_{origBehaviorStr}"))
                {
                    if (popup)
                    {
                        foreach (AbortBehavior behavior in Enum.GetValues<AbortBehavior>())
                        {
                            if (ImGui.Selectable(GetLoc(behavior), behavior == logic.Value))
                            {
                                selectedItemConfig.AbortLogic[logic.Key] = behavior;
                                ParentModule.config.Save(ParentModule);
                            }
                        }
                    }
                }

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon($"Delete_{logic.Key}_{logic.Value}", FontAwesomeIcon.TrashAlt, DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Delete")))
                    selectedItemConfig.AbortLogic.Remove(logic.Key);
            }
        }

        private void DrawMarketItemsTable()
        {
            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null) return;

            var currentActiveRetainer = retainerManager->GetActiveRetainer();
            if (currentActiveRetainer == null) return;

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null) return;

            var marketContainer = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (marketContainer == null || !marketContainer->IsLoaded) return;

            

            if (ImGui.BeginMenuBar())
            {
                ImGui.TextUnformatted($"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-Adjust")}:");

                using (ImRaii.Disabled(taskHelper.IsBusy))
                {
                    if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Start")))
                        EnqueuePriceAdjustSingle();
                }

                if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Stop")))
                    taskHelper.Abort();

                ImGui.TextDisabled("|");

                using (ImRaii.Disabled(taskHelper.IsBusy))
                {
                    if (ImGui.BeginMenu(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Shortcut")))
                    {
                        if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-ReturnAllToInventory")))
                        {
                            for (var i = 0; i < marketContainer->Size; i++)
                            {
                                var index = i;
                                taskHelper.Enqueue(() => ReturnRetainerMarketItemToInventory((ushort)index, true), $"将市场第 {index} 栏物品收回至背包");
                            }
                        }

                        if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-ReturnAllToRetainer")))
                        {
                            for (var i = 0; i < marketContainer->Size; i++)
                            {
                                var index = i;
                                taskHelper.Enqueue(() => ReturnRetainerMarketItemToInventory((ushort)index, false), $"将市场第 {index} 栏物品收回至雇员");
                            }
                        }

                        ImGui.EndMenu();
                    }
                }

                ImGui.TextDisabled("|");

                using (ImRaii.Disabled(taskHelper.IsBusy))
                {
                    if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-ClearCache")))
                    {
                        PriceCacheManager.ClearCache();
                        NotifyHelper.Instance().NotificationSuccess(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-CacheCleared"));
                    }
                }

                ImGui.TextDisabled("|");

                if (ImGui.BeginMenu(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Settings")))
                {
                    if (ImGui.BeginMenu(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("FontSize")))
                    {
                        for (var i = 0.6f; i < 1.8f; i += 0.2f)
                        {
                            var fontScale = (float)Math.Round(i, 1);

                            if (ImGui.MenuItem
                                (
                                    $"{fontScale}",
                                    string.Empty,
                                    fontScale == ParentModule.config.MarketItemsWindowFontScale
                                ))
                            {
                                ParentModule.config.MarketItemsWindowFontScale = fontScale;
                                ParentModule.config.Save(ParentModule);
                            }
                        }

                        ImGui.EndMenu();
                    }

                    if (ImGui.BeginMenu(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-SortOrder")))
                    {
                        foreach (var sortOrder in Enum.GetValues<SortOrder>())
                        {
                            if (ImGui.MenuItem
                                (
                                    $"{GetLoc(sortOrder)}",
                                    string.Empty,
                                    sortOrder == ParentModule.config.MarketItemsSortOrder
                                ))
                            {
                                ParentModule.config.MarketItemsSortOrder = sortOrder;
                                ParentModule.config.Save(ParentModule);
                            }
                        }

                        ImGui.EndMenu();
                    }

                    if (ImGui.MenuItem
                        (
                            DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale"),
                            string.Empty,
                            ParentModule.config.AutoPriceAdjustWhenNewOnSale
                        ))
                    {
                        ParentModule.config.AutoPriceAdjustWhenNewOnSale ^= true;
                        ParentModule.config.Save(ParentModule);
                    }

                    if (ImGui.MenuItem
                        (
                            DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-SendProcessMessage"),
                            string.Empty,
                            ParentModule.config.SendPriceAdjustProcessMessage
                        ))
                    {
                        ParentModule.config.SendPriceAdjustProcessMessage ^= true;
                        ParentModule.config.Save(ParentModule);
                    }

                    ImGui.EndMenu();
                }

                ImGui.TextDisabled("|");

                using (ImRaii.Disabled(taskHelper.IsBusy))
                {
                    if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2366)))
                        RetainerSellList->Callback(-1);
                }

                ImGui.EndMenuBar();
            }

            using var disabled = ImRaii.Disabled(taskHelper.IsBusy);
            using var table = ImRaii.Table
            (
                "MarketItemTable",
                5,
                ImGuiTableFlags.Borders     |
                ImGuiTableFlags.Reorderable |
                ImGuiTableFlags.Resizable   |
                ImGuiTableFlags.Hideable
            );
            if (!table) return;

            ImGui.TableSetupColumn("###Sort",                        ImGuiTableColumnFlags.WidthFixed,   ImGui.GetTextLineHeightWithSpacing());
            ImGui.TableSetupColumn(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Item"),                 ImGuiTableColumnFlags.WidthStretch, 30);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(933),  ImGuiTableColumnFlags.WidthStretch, 10);
            ImGui.TableSetupColumn(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Amount"),               ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Amount")).X * 1.2f);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(6936), ImGuiTableColumnFlags.WidthStretch, 10);

            ImGui.TableHeadersRow();

            if (!InventoryType.RetainerMarket.TryGetItems(x => x.ItemId != 0, out var validItems)) return;

            var itemSource = validItems
                             .Select
                             (x => new
                                 {
                                     Inventory = x,
                                     Data      = LuminaGetter.GetRow<Item>(x.ItemId).GetValueOrDefault(),
                                     Slot      = (ushort)x.Slot
                                 }
                             )
                             .OrderBy
                             (x => ParentModule.config.MarketItemsSortOrder switch
                                 {
                                     SortOrder.上架顺序 => (uint)x.Inventory.Slot,
                                     SortOrder.物品ID => x.Data.RowId,
                                     SortOrder.物品类型 => x.Data.FilterGroup,
                                     _              => 0U
                                 }
                             )
                             .ThenBy
                             (x => ParentModule.config.MarketItemsSortOrder switch
                                 {
                                     SortOrder.物品ID => x.Data.RowId,
                                     _              => 0U
                                 }
                             )
                             .ToArray();

            var isTooltip     = false;
            var tooltipItemID = 0U;

            for (var index = 0; index < itemSource.Length; index++)
            {
                var item      = itemSource[index];
                var itemPrice = GetRetainerMarketPrice(item.Slot);
                if (itemPrice == 0) continue;

                var isItemHQ = item.Inventory.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var itemIcon = DService.Instance().Texture.GetFromGameIcon(new(item.Data.Icon, isItemHQ)).GetWrapOrDefault();
                if (itemIcon == null) continue;

                var itemName = $"{item.Data.Name.ToString()}" + (isItemHQ ? "\ue03c" : string.Empty);

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{index + 1}");

                DrawItemColumn(item.Slot, item.Inventory.ItemId, itemName, itemIcon, ref isTooltip, ref tooltipItemID);

                DrawUnitPriceColumn(item.Slot, item.Inventory.ItemId, itemPrice, (uint)item.Inventory.Quantity, itemIcon, itemName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{item.Inventory.Quantity}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{(item.Inventory.Quantity * itemPrice).ToChineseString()}");
            }

            if (isTooltip)
            {
                AtkStage.Instance()->ShowItemTooltip(ScreenText->RootNode, tooltipItemID);
                isDisplayingTooltip = true;
            }
            else
            {
                if (isDisplayingTooltip)
                {
                    isDisplayingTooltip = false;
                    AtkStage.Instance()->HideTooltip(ScreenText->Id);
                }
            }
        }

        private void DrawItemColumn(ushort slot, uint itemID, string itemName, IDalamudTextureWrap itemIcon, ref bool isTooltip, ref uint tooltipItemID)
        {
            using var id    = ImRaii.PushId(slot);
            using var group = ImRaii.Group();

            ImGui.TableNextColumn();
            ImGuiOm.SelectableImageWithText(itemIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()), itemName, false);

            if (ImGui.IsItemHovered())
            {
                isTooltip     = true;
                tooltipItemID = itemID;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                RequestMarketItemData(itemID);

            using var popup = ImRaii.ContextPopupItem("MarketItemOperationPopup");
            if (!popup) return;

            if (ImGui.MenuItem(LuminaWrapper.GetAddonText(976)))
                ReturnRetainerMarketItemToInventory(slot, true);

            if (ImGui.MenuItem(LuminaWrapper.GetAddonText(958)))
                ReturnRetainerMarketItemToInventory(slot, false);
        }

        private void DrawUnitPriceColumn(ushort slot, uint itemID, uint price, uint quantity, IDalamudTextureWrap itemIcon, string itemName)
        {
            using var id    = ImRaii.PushId(slot);
            using var group = ImRaii.Group();

            ImGui.TableNextColumn();
            ImGui.Selectable($"{price.ToChineseString()}");

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            var isNeedOpenManualModifyPopup    = false;
            var isNeedOpenAllManualModifyPopup = false;

            using (var popup = ImRaii.ContextPopupItem("ModifyUnitPricePopup"))
            {
                if (popup)
                {
                    if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAuto")))
                        EnqueuePriceAdjustSingle(slot);

                    if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-AdjustUnitPriceManual")))
                    {
                        ImGui.CloseCurrentPopup();

                        RequestMarketItemData(itemID);
                        isNeedOpenManualModifyPopup = true;
                    }

                    using (ImRaii.Group())
                    {
                        if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAllSameItems")))
                        {
                            if (TryGetSameItemSlots(itemID, out var slots))
                                slots.ForEach(s => EnqueuePriceAdjustSingle(s));
                        }

                        if (ImGui.MenuItem(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAllSameItemsManual")))
                        {
                            ImGui.CloseCurrentPopup();

                            RequestMarketItemData(itemID);
                            isNeedOpenAllManualModifyPopup = true;
                        }
                    }

                    ImGuiOm.TooltipHover(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAllSameItemsHelp"));
                }
            }

            if (isNeedOpenManualModifyPopup)
                ImGui.OpenPopup("ModifyUnitPriceManualPopup");

            using (var popup = ImRaii.Popup("ModifyUnitPriceManualPopup"))
            {
                if (popup)
                {
                    if (ImGui.IsWindowAppearing())
                        itemModifyUnitPriceManual = price;

                    ImGui.Image(itemIcon.Handle, manualUnitPriceImageSize with { X = manualUnitPriceImageSize.Y });

                    ImGui.SameLine();

                    using (ImRaii.Group())
                    {
                        using (OmenTools.OmenService.FontManager.Instance().UIFont140.Push())
                            ImGui.TextUnformatted($"{itemName}");

                        ImGui.TextDisabled($"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-MarketItemsCount")}: {quantity}");
                    }

                    manualUnitPriceImageSize = ImGui.GetItemRectSize();

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(933)}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150f * GlobalUIScale);
                    ImGui.InputUInt("###UnitPriceInput", ref itemModifyUnitPriceManual);

                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(6936)}:");

                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{(quantity * itemModifyUnitPriceManual).ToChineseString()}");

                    ImGui.Separator();

                    if (ImGuiOm.ButtonSelectable(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Confirm")))
                    {
                        SetRetainerMarketItemPrice(slot, itemModifyUnitPriceManual);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }

            if (isNeedOpenAllManualModifyPopup)
                ImGui.OpenPopup("ModifyAllUnitPriceManualPopup");

            using (var popup = ImRaii.Popup("ModifyAllUnitPriceManualPopup"))
            {
                if (popup)
                {
                    if (ImGui.IsWindowAppearing())
                    {
                        itemModifyUnitPriceManual = price;
                        itemModifyCountManual     = (uint)(TryGetSameItemSlots(itemID, out var slots) ? slots.Count : 0);
                    }

                    ImGui.Image(itemIcon.Handle, manualUnitPriceImageSize with { X = manualUnitPriceImageSize.Y });

                    ImGui.SameLine();

                    using (ImRaii.Group())
                    {
                        using (OmenTools.OmenService.FontManager.Instance().UIFont140.Push())
                            ImGui.TextUnformatted($"{itemName}");

                        ImGui.TextDisabled($"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-MarketItemsCount")}: {quantity}");

                        ImGui.SameLine();
                        ImGui.TextDisabled("/");

                        ImGui.SameLine();
                        ImGui.TextDisabled($"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-SameItemsCount")}: {itemModifyCountManual}");
                    }

                    manualUnitPriceImageSize = ImGui.GetItemRectSize();

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(933)}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150f * GlobalUIScale);
                    ImGui.InputUInt("###UnitPriceInput", ref itemModifyUnitPriceManual);

                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(6936)}:");

                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{(quantity * itemModifyUnitPriceManual).ToChineseString()}");

                    ImGui.Separator();

                    if (ImGuiOm.ButtonSelectable(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Confirm")))
                    {
                        if (TryGetSameItemSlots(itemID, out var slots))
                            slots.ForEach(s => EnqueuePriceAdjustSingle(s, itemModifyUnitPriceManual));

                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }

        private void DrawMarketDataTable()
        {
            var info = InfoProxyItemSearch.Instance();
            if (info == null) return;

            if (info->SearchItemId == 0) return;

            var listingsArray = info->Listings.ToArray()
                                              .Where
                                              (x => x.ItemId    == info->SearchItemId &&
                                                    x.UnitPrice != 0                  &&
                                                    !ParentModule.playerRetainers.Contains(x.RetainerId)
                                              )
                                              .OrderBy(x => x.UnitPrice)
                                              .ToArray();

            if (!LuminaGetter.TryGetRow<Item>(info->SearchItemId, out var itemData)) return;

            var itemIcon = DService.Instance().Texture.GetFromGameIcon(new(itemData.Icon)).GetWrapOrDefault();
            if (itemIcon == null) return;

            

            ImGui.Image(itemIcon.Handle, marketDataTableImageSize with { X = marketDataTableImageSize.Y });

            ImGui.SameLine();

            using (ImRaii.Group())
            {
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted($"{itemData.Name}");
                }

                {
                    ImGui.TextDisabled($"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-OnSaleCount")}: {info->ListingCount}");

                    if (listingsArray.Length > 0)
                    {
                        var minPrice = listingsArray.Min(x => x.UnitPrice);
                        ImGui.SameLine();
                        ImGui.TextDisabled($" / {DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-MinPrice")}: {minPrice.ToChineseString()} / ");
                        ImGuiOm.ClickToCopyAndNotify(minPrice.ToString());

                        var maxPrice = listingsArray.Max(x => x.UnitPrice);
                        ImGui.SameLine();
                        ImGui.TextDisabled($"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-MaxPrice")}: {maxPrice.ToChineseString()}");
                        ImGuiOm.ClickToCopyAndNotify(maxPrice.ToString());
                    }
                }
            }

            marketDataTableImageSize = ImGui.GetItemRectSize();

            var       childSize = new Vector2(ImGui.GetContentRegionAvail().X, 250f * GlobalUIScale);
            using var child     = ImRaii.Child("MarketDataChild", childSize, false, ImGuiWindowFlags.NoBackground);
            if (!child) return;

            var isAnyHQ              = listingsArray.Any(x => x.IsHqItem);
            var isAnyOnMannequin     = listingsArray.Any(x => x.IsMannequin);
            var isAnyMateriaEquipped = itemData.MateriaSlotCount > 0 && listingsArray.Any(x => x.MateriaCount > 0);

            var columnsCount = 6;
            if (!isAnyHQ)
                columnsCount--;
            if (!isAnyMateriaEquipped)
                columnsCount--;
            if (!isAnyOnMannequin)
                columnsCount--;

            using var table = ImRaii.Table("MarketBoardDataTable", columnsCount, ImGuiTableFlags.Borders);
            if (!table) return;

            if (isAnyHQ)
                ImGui.TableSetupColumn("\ue03c", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("\ue03c").X);

            if (isAnyMateriaEquipped)
            {
                var materiaText = LuminaWrapper.GetAddonText(1937);
                ImGui.TableSetupColumn(materiaText, ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(materiaText).X);
            }

            if (isAnyOnMannequin)
                ImGui.TableSetupColumn(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Mannequin"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Mannequin")).X);

            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(357),  ImGuiTableColumnFlags.WidthStretch, 15);
            ImGui.TableSetupColumn(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Amount"),               ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Amount")).X);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(6936), ImGuiTableColumnFlags.WidthStretch, 15);

            ImGui.TableHeadersRow();

            foreach (var listing in listingsArray)
            {
                using var id = ImRaii.PushId(listing.ListingId.ToString());
                ImGui.TableNextRow();

                if (isAnyHQ)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.IsHqItem ? "√" : string.Empty);
                }

                if (isAnyMateriaEquipped)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{listing.MateriaCount}");
                }

                if (isAnyOnMannequin)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.IsMannequin ? "√" : string.Empty);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.UnitPrice.ToChineseString()}");
                ImGuiOm.ClickToCopyAndNotify(listing.UnitPrice.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.Quantity}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{(listing.UnitPrice * listing.Quantity + listing.TotalTax).ToChineseString()}");
            }
        }

        private void DrawMarketHistoryDataTable()
        {
            var info = InfoProxyItemSearch.Instance();
            if (info == null) return;

            if (historyListings.Key == 0) return;
            if (!LuminaGetter.TryGetRow<Item>(historyListings.Key, out _)) return;

            

            using (ImRaii.Group())
            {
                using (OmenTools.OmenService.FontManager.Instance().UIFont140.Push())
                    ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(1165)}");

                ImGui.TextDisabled($"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-OnSaleCount")}: {info->ListingCount}");

                if (historyListings.Value.Count > 0)
                {
                    var minPrice = historyListings.Value.Min(x => x.SalePrice);
                    ImGui.SameLine();
                    ImGui.TextDisabled($" / {DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-MinPrice")}: {minPrice.ToChineseString()} / ");
                    ImGuiOm.ClickToCopyAndNotify(minPrice.ToString());

                    var maxPrice = historyListings.Value.Max(x => x.SalePrice);
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-MaxPrice")}: {maxPrice.ToChineseString()}");
                    ImGuiOm.ClickToCopyAndNotify(maxPrice.ToString());
                }
            }

            var       childSize = new Vector2(ImGui.GetContentRegionAvail().X, 250f * GlobalUIScale);
            using var child     = ImRaii.Child("HistoryDataChild", childSize, false, ImGuiWindowFlags.NoBackground);
            if (!child) return;

            var isAnyHQ = historyListings.Value.Any(x => x.IsHq);

            var columnsCount = 5;
            if (!isAnyHQ)
                columnsCount--;

            using var table = ImRaii.Table("MarketBoardDataTable", columnsCount, ImGuiTableFlags.Borders);
            if (!table) return;

            if (isAnyHQ)
                ImGui.TableSetupColumn("\ue03c", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("\ue03c").X);

            ImGui.TableSetupColumn(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Amount"),               ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Amount")).X);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(357),  ImGuiTableColumnFlags.WidthStretch, 15);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1975), ImGuiTableColumnFlags.WidthStretch, 15);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1976), ImGuiTableColumnFlags.WidthStretch, 15);

            ImGui.TableHeadersRow();

            foreach (var listing in historyListings.Value)
            {
                if (listing.OnMannequin) continue;

                using var id = ImRaii.PushId($"{listing.BuyerName}-{listing.SalePrice}-{listing.Quantity}-{listing.PurchaseTime}");
                ImGui.TableNextRow();

                if (isAnyHQ)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.IsHq ? "√" : string.Empty);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.Quantity}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.SalePrice.ToChineseString()}");
                ImGuiOm.ClickToCopyAndNotify(listing.SalePrice.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.BuyerName}");
                ImGuiOm.ClickToCopyAndNotify(listing.BuyerName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.PurchaseTime.ToLocalTime():yyyy/MM/dd HH:mm:ss}");
            }
        }

        private void DrawMarketUpshelf()
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return;

            var container = manager->GetInventoryContainer(sourceUpshelfType);
            if (container == null || !container->IsLoaded) return;

            var slotData = container->GetInventorySlot(sourceUpshelfSlot);
            if (slotData == null || slotData->ItemId == 0) return;

            if (!LuminaGetter.TryGetRow<Item>(slotData->ItemId, out var itemData)) return;

            var isItemHQ = slotData->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

            var itemIcon = DService.Instance().Texture
                                   .GetFromGameIcon(new(itemData.Icon, isItemHQ))
                                   .GetWrapOrDefault();
            if (itemIcon == null) return;

            using var id   = ImRaii.PushId($"{sourceUpshelfType}_{sourceUpshelfSlot}");
            

            {
                if (ImGuiOm.ButtonSelectable(LuminaWrapper.GetAddonText(2366)))
                    isNeedToDrawMarketUpshelfWindow = false;
            }

            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Image(itemIcon.Handle, manualUnitPriceImageSize with { X = manualUnitPriceImageSize.Y });

            ImGui.SameLine();

            using (ImRaii.Group())
            using (OmenTools.OmenService.FontManager.Instance().UIFont140.Push())
                ImGui.TextUnformatted($"{itemData.Name.ToString()}" + (isItemHQ ? "\ue03c" : string.Empty));

            manualUnitPriceImageSize = ImGui.GetItemRectSize();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(933)}:");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f * GlobalUIScale);
            ImGui.InputUInt("###UnitPriceInput", ref upshelfUnitPriceInput);

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("Amount")}:");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f * GlobalUIScale);
            ImGui.InputUInt("###QuantityInput", ref upshelfQuantityInput);

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(6936)}:");

            ImGui.SameLine();
            ImGui.TextUnformatted($"{(upshelfQuantityInput * upshelfUnitPriceInput).ToChineseString()}");

            ImGui.Separator();

            if (ImGuiOm.ButtonSelectable(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-UpshelfAuto")))
            {
                if (TryGetFirstEmptyRetainerMarketSlot(out var firstEmptySlot))
                {
                    UpshelfMarketItem(sourceUpshelfType, sourceUpshelfSlot, upshelfQuantityInput, 9_9999_9999, (short)firstEmptySlot);
                    EnqueuePriceAdjustSingle(firstEmptySlot);
                    isNeedToDrawMarketUpshelfWindow = false;
                }
            }

            if (ImGuiOm.ButtonSelectable(DailyRoutines.Common.Runtime.Hosts.ManagerHost.Current.GetLoc("AutoRetainerWork-PriceAdjust-UpshelfManual")))
            {
                UpshelfMarketItem(sourceUpshelfType, sourceUpshelfSlot, upshelfQuantityInput, upshelfUnitPriceInput);
                isNeedToDrawMarketUpshelfWindow = false;
            }
        }

        #endregion

        #region 事件

        // 出售品列表 (悬浮窗控制)
        private void OnRetainerSellList(AddonEvent type, AddonArgs args)
        {
            // 因为有模特存在
            if (!DService.Instance().Condition[ConditionFlag.OccupiedSummoningBell]) return;

            switch (type)
            {
                case AddonEvent.PostDraw:
                    isNeedToDrawMarketListWindow = true;

                    if (RetainerSellList != null)
                    {
                        var listComponent = RetainerSellList->GetComponentListById(11);

                        if (listComponent != null)
                        {
                            for (var i = 0; i < listComponent->GetItemCount(); i++)
                            {
                                var item = listComponent->GetItemRenderer(i);
                                if (item == null || !item->OwnerNode->IsVisible()) continue;

                                item->OwnerNode->ToggleVisibility(false);
                            }
                        }
                    }

                    break;
                case AddonEvent.PreFinalize:
                    isNeedToDrawMarketListWindow = false;

                    isDisplayingTooltip = false;
                    AtkStage.Instance()->HideTooltip(ScreenText->Id);
                    break;
            }
        }

        // 出售界面
        private static void OnRetainerSell(AddonEvent type, AddonArgs args)
        {
            if (!DService.Instance().Condition[ConditionFlag.OccupiedSummoningBell]) return;
            if (!args.Addon.ToStruct()->IsAddonAndNodesReady()) return;
            args.Addon.ToStruct()->Callback(0);
        }

        // 当前市场数据获取
        private void OnOfferingReceived(IMarketBoardCurrentOfferings data) =>
            PriceCacheManager.OnOfferingReceived(ParentModule, data);

        // 历史交易数据获取
        private void OnHistoryReceived(IMarketBoardHistory history)
        {
            if (history.ItemId != historyListings.Key)
                historyListings = new(history.ItemId, []);
            historyListings.Value.AddRange(history.HistoryListings);

            PriceCacheManager.OnHistoryReceived(history);
        }

        // 上架 => 全部拦截
        private void MoveToRetainerMarketDetour
        (
            InventoryManager* manager,
            InventoryType     srcInv,
            ushort            srcSlot,
            InventoryType     dstInv,
            ushort            dstSlot,
            uint              quantity,
            uint              unitPrice
        )
        {
            var slot = manager->GetInventorySlot(srcInv, srcSlot);
            if (slot == null) return;

            if (!TryGetItemUpshelfCountLimit(*slot, out var upshelfQuantity)) return;

            if (ParentModule.config.AutoPriceAdjustWhenNewOnSale && !PluginConfig.Instance().ConflictKeyBinding.IsPressed())
            {
                MoveToRetainerMarketHook.Original(manager, srcInv, srcSlot, dstInv, dstSlot, upshelfQuantity, 9_9999_9999);
                EnqueuePriceAdjustSingle(dstSlot);
                return;
            }

            sourceUpshelfType = srcInv;
            sourceUpshelfSlot = srcSlot;

            var info = InfoProxyItemSearch.Instance();
            if (info == null) return;

            if (info->SearchItemId != slot->ItemId)
                RequestMarketItemData(slot->ItemId);

            upshelfUnitPriceInput = LuminaGetter.TryGetRow<Item>(slot->ItemId, out var itemRow) ? itemRow.PriceMid : 1;
            upshelfQuantityInput  = upshelfQuantity;

            isNeedToDrawMarketUpshelfWindow = true;
        }

        #endregion

        #region 队列

        private void EnqueuePriceAdjustAll()
        {
            if (taskHelper.AbortByConflictKey(ParentModule)) return;
            if (ParentModule.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var count = GetValidRetainerCount(x => x is { Available: true, MarketItemCount: > 0 }, out var validRetainers);
            if (count == 0) return;

            validRetainers
                .ForEach
                (index =>
                    {
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                                return ParentModule.EnterRetainer(index);
                            },
                            IsCN ? $"选择进入 {index} 号雇员" : $"Select {index}th retainer",
                            timeoutMS: 10000
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                                return SelectString->IsAddonAndNodesReady() && RetainerManager.Instance()->GetActiveRetainer() != null;
                            },
                            $"等待接收 {index} 号雇员的数据",
                            timeoutMS: 10000
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                                return AddonSelectStringEvent.Select(SellInventoryItemsText);
                            },
                            IsCN ? "点击进入出售玩家所持物品列表" : "Click to enter sell items list",
                            timeoutMS: 5000
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                                return RetainerSellList->IsAddonAndNodesReady();
                            },
                            IsCN ? "等待出售品列表界面完全加载" : "Wait for sell items list to load",
                            timeoutMS: 5000
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return;
                                EnqueuePriceAdjustSingle();
                            },
                            IsCN ? "由单一雇员商品改价接管后续逻辑" : "Single retainer price adjustment logic takes over"
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return;
                                if (RetainerSellList->IsAddonAndNodesReady())
                                    RetainerSellList->Callback(-1);
                            },
                            IsCN ? "单一雇员改价完成, 发出退出出售品列表界面指令" : "Single retainer price adjustment complete, exiting sell items list",
                            timeoutMS: 5000
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                                return !RetainerSellList->IsAddonAndNodesReady() && SelectString->IsAddonAndNodesReady();
                            },
                            IsCN ? "等待确认出售品列表已退出并回到交互菜单" : "Wait to confirm exiting sell items list and return to menu",
                            timeoutMS: 5000
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                                return LeaveRetainer();
                            },
                            IsCN ? "单一雇员改价完成, 返回至雇员列表界面" : "Single retainer price adjustment complete, return to retainer list",
                            timeoutMS: 5000
                        );
                    }
                );
        }

        private void EnqueuePriceAdjustSingle()
        {
            if (taskHelper.AbortByConflictKey(ParentModule)) return;
            if (ParentModule.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var retainer = RetainerManager.Instance()->GetActiveRetainer();
            if (retainer == null || retainer->MarketItemCount <= 0) return;

            var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return;

            for (ushort i = 0; i < container->Size; i++)
                EnqueuePriceAdjustSingle(i);
        }

        private void EnqueuePriceAdjustSingle(ushort slotIndex, uint forcePrice = 0)
        {
            if (taskHelper.AbortByConflictKey(ParentModule)) return;
            if (ParentModule.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            taskHelper.Enqueue
            (
                () =>
                {
                    var retainer = RetainerManager.Instance()->GetActiveRetainer();
                    if (retainer == null) return;

                    var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
                    if (container == null || !container->IsLoaded) return;

                    var slot   = container->GetInventorySlot(slotIndex);
                    var itemID = slot->ItemId;
                    if (slot == null || slot->ItemId == 0) return;

                    var itemName      = LuminaGetter.GetRow<Item>(itemID)?.Name ?? string.Empty;
                    var isItemHQ      = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    var isPriceCached = PriceCacheManager.TryGetPriceCache(itemID, isItemHQ, out var price);

                    if (!isPriceCached)
                    {
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return;
                                var isNothingSearched = InfoProxyItemSearch.Instance()->SearchItemId == 0;
                                RequestMarketItemData(itemID);
                                if (isNothingSearched)
                                    taskHelper.DelayNext(1000, "初始无数据, 等待 1 秒", 2);
                            },
                            IsCN ? $"请求雇员 {retainer->NameString} {slotIndex} 号位置处 {itemName} 的市场价格数据" : $"Requesting market price data for {itemName} at slot {slotIndex} of retainer {retainer->NameString}",
                            weight: 2
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                                if (IsMarketStuck()) return false;

                                return IsMarketItemDataReady(itemID);
                            },
                            IsCN ? $"等待 {itemName} 市场价格数据完全到达" : $"Wait for market price data of {itemName} to fully arrive",
                            timeoutMS: 8000,
                            weight: 2
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(ParentModule)) return;
                                
                                // 初次获取不到价格数据，进行二次尝试
                                if (!PriceCacheManager.TryGetPriceCache(itemID, isItemHQ, out price) || price == 0)
                                {
                                    RequestMarketItemData(itemID);
                                    taskHelper.DelayNext(1000, IsCN ? "初次无数据, 尝试重新获取" : "Retrying market data request", 2);
                                    
                                    taskHelper.Enqueue
                                    (
                                        () =>
                                        {
                                            if (taskHelper.AbortByConflictKey(ParentModule)) return true;
                                            if (IsMarketStuck()) return false;
                                            return IsMarketItemDataReady(itemID);
                                        },
                                        IsCN ? "等待二次请求的数据完全到达" : "Wait for second request data",
                                        timeoutMS: 8000,
                                        weight: 2
                                    );
                                    
                                    taskHelper.Enqueue
                                    (
                                        () =>
                                        {
                                            if (taskHelper.AbortByConflictKey(ParentModule)) return;
                                            // 二次获取依然失败，直接放弃
                                            if (!PriceCacheManager.TryGetPriceCache(itemID, isItemHQ, out price) || price == 0)
                                            {
                                                if (ParentModule.config.SendPriceAdjustProcessMessage)
                                                    NotifyHelper.Instance().Chat(IsCN ? $"由于无法获取到 {itemName} 的有效市场价格，已跳过。" : $"Skipped price adjustment for {itemName} due to missing market data.");
                                                return;
                                            }
                                            EnqueuePriceAdjustSingleItem(slotIndex, price, forcePrice);
                                        },
                                        IsCN ? "执行二次改价逻辑判定" : "Execute secondary price adjustment logic",
                                        weight: 2
                                    );
                                    return;
                                }

                                EnqueuePriceAdjustSingleItem(slotIndex, price, forcePrice);
                            },
                            IsCN ? "由单一物品改价接管后续逻辑" : "Single item price adjustment logic takes over",
                            weight: 2
                        );
                        return;
                    }

                    // 如果价格已经被缓存, 且价格不需要变化, 我们在此刻直接短路跳过它, 防止不必要的 Enqueue 占用时间
                    var itemMarketData = GetRetainerMarketItem(slotIndex);
                    if (itemMarketData != null)
                    {
                        var itemConfig = GetItemConfigByItemKey(itemMarketData.Value.Item);
                        var modifiedPrice = forcePrice > 0 ? forcePrice : GetModifiedPrice(itemConfig, price);
                        if (modifiedPrice == 0 || modifiedPrice == itemMarketData.Value.Price) return;
                    }

                    taskHelper.Enqueue(() => EnqueuePriceAdjustSingleItem(slotIndex, price, forcePrice), "由单一物品改价接管后续逻辑", weight: 2);
                },
                IsCN ? $"检查当前市场第 {slotIndex} 栏的物品数据, 强制价格: {forcePrice}" : $"Check item data at slot {slotIndex}, forced price: {forcePrice}",
                weight: 1
            );
        }

        /// <summary>
        ///     倒查并过滤意外超低物价，返回合理的当前市场最低价格
        /// </summary>
        private static uint GetFinalMarketPrice(ItemConfig itemConfig, List<uint> prices)
        {
            if (prices == null || prices.Count == 0) return 0;
            if (prices.Count == 1) return prices[0];

            var p1 = prices[0];

            // 触发了低于最小值，开始倒查
            if (prices.Count >= 2)
            {
                var p2 = prices[1];
                var modified2 = GetModifiedPrice(itemConfig, p2);

                // 判定 p1 是否是意外低价（低于 p2 的 50% 且 p2 改价后正常）
                if (p1 * 2 < p2 && modified2 >= itemConfig.PriceMinimum)
                {
                    // 确认 p1 是意外低价。如果还有 p3，递归检查 p2 是否相对于 p3 也是意外低价
                    if (prices.Count >= 3)
                    {
                        var p3 = prices[2];
                        var modified3 = GetModifiedPrice(itemConfig, p3);

                        if (p2 * 2 < p3 && modified3 >= itemConfig.PriceMinimum)
                        {
                            // p2 也是意外低价，使用 p3
                            return p3;
                        }
                    }
                    // 否则使用 p2
                    return p2;
                }
            }

            return p1;
        }

        private void EnqueuePriceAdjustSingleItem(ushort slot, uint marketPrice, uint forcePrice = 0)
        {
            if (taskHelper.AbortByConflictKey(ParentModule)) return;
            if (ParentModule.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var itemMarketData = GetRetainerMarketItem(slot);
            if (itemMarketData == null) return;

            var itemConfig = GetItemConfigByItemKey(itemMarketData.Value.Item);

            var finalMarketPrice = marketPrice;
            if (forcePrice == 0)
            {
                // 快速通道：若最低价改完后依旧在安全范围内，则直接短路，省去倒查和读取缓存的开销
                if (GetModifiedPrice(itemConfig, marketPrice) < itemConfig.PriceMinimum)
                {
                    if (PriceCacheManager.TryGetPricesCache(itemMarketData.Value.Item.itemID, itemMarketData.Value.Item.IsHQ, out var prices))
                    {
                        finalMarketPrice = GetFinalMarketPrice(itemConfig, prices);
                    }
                }
            }

            if (finalMarketPrice != marketPrice)
            {
                if (ParentModule.config.SendPriceAdjustProcessMessage)
                {
                    var itemPayload = new SeStringBuilder().AddItemLink(itemMarketData.Value.Item.itemID, itemMarketData.Value.Item.IsHQ).Build();
                    var builder = new SeStringBuilder();
                    if (IsCN)
                    {
                        builder.AddText("检测到 ")
                               .Append(itemPayload)
                               .AddText($" 存在意外超低价 {marketPrice.ToChineseString()}，已自动倒查并使用正常物价 {finalMarketPrice.ToChineseString()} 进行改价。");
                    }
                    else
                    {
                        builder.AddText("Detected unusual low price ")
                               .AddText(marketPrice.ToChineseString())
                               .AddText(" for ")
                               .Append(itemPayload)
                               .AddText($", automatically fallback to normal price {finalMarketPrice.ToChineseString()}.");
                    }
                    DService.Instance().Chat.Print(builder.Build());
                }
            }

            var modifiedPrice = forcePrice > 0 ? forcePrice : GetModifiedPrice(itemConfig, finalMarketPrice);

            // 价格为 0
            if (modifiedPrice == 0) return;

            // 价格不变
            if (modifiedPrice == itemMarketData.Value.Price) return;

            if (IsAnyAbortConditionsMet
                (
                    itemConfig,
                    itemMarketData.Value.Price,
                    modifiedPrice,
                    finalMarketPrice,
                    out var abortCondition,
                    out var abortBehavior
                ))
            {
                NotifyAbortCondition(itemMarketData.Value.Item.itemID, itemMarketData.Value.Item.IsHQ, abortCondition, finalMarketPrice);
                EnqueueAbortBehavior(abortBehavior);
                return;
            }

            SetRetainerMarketItemPrice(slot, modifiedPrice);
            NotifyPriceAdjustSuccessfully
            (
                itemMarketData.Value.Item.itemID,
                itemMarketData.Value.Item.IsHQ,
                itemMarketData.Value.Price,
                modifiedPrice
            );
            return;

            // 采取意外情况逻辑
            void EnqueueAbortBehavior(AbortBehavior behavior)
            {
                if (ParentModule.config.SendPriceAdjustProcessMessage)
                {
                    var message = DailyRoutines.Manager.LanguageManager.GetSe
                    (
                        "AutoRetainerWork-PriceAdjust-ConductAbortBehavior",
                        new SeStringBuilder().AddUiForeground(GetLoc(behavior), 67).Build()
                    );
                    NotifyHelper.Instance().Chat(message);
                }

                if (behavior == AbortBehavior.无) return;

                switch (behavior)
                {
                    case AbortBehavior.改价至最小值:
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceMinimum);
                        NotifyPriceAdjustSuccessfully
                        (
                            itemMarketData.Value.Item.itemID,
                            itemMarketData.Value.Item.IsHQ,
                            itemMarketData.Value.Price,
                            (uint)itemConfig.PriceMinimum
                        );
                        break;
                    case AbortBehavior.改价至预期值:
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceExpected);
                        NotifyPriceAdjustSuccessfully
                        (
                            itemMarketData.Value.Item.itemID,
                            itemMarketData.Value.Item.IsHQ,
                            itemMarketData.Value.Price,
                            (uint)itemConfig.PriceExpected
                        );
                        break;
                    case AbortBehavior.改价至最高值:
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceMaximum);
                        NotifyPriceAdjustSuccessfully
                        (
                            itemMarketData.Value.Item.itemID,
                            itemMarketData.Value.Item.IsHQ,
                            itemMarketData.Value.Price,
                            (uint)itemConfig.PriceMaximum
                        );
                        break;
                    case AbortBehavior.收回至雇员:
                        ReturnRetainerMarketItemToInventory(slot, false);
                        break;
                    case AbortBehavior.收回至背包:
                        ReturnRetainerMarketItemToInventory(slot, true);
                        break;
                    case AbortBehavior.出售至系统商店:
                        taskHelper.Enqueue(() => ReturnRetainerMarketItemToInventory(slot, true), "将物品收回背包, 以待出售", weight: 3);
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (!TrySearchItemInInventory(itemMarketData.Value.Item.itemID, itemMarketData.Value.Item.IsHQ, out var foundItems) ||
                                    foundItems is not { Count: > 0 })
                                    return false;

                                var foundItem = foundItems.FirstOrDefault();
                                return foundItem.OpenContext();
                            },
                            "找到物品并打开其右键菜单",
                            weight: 3
                        );
                        taskHelper.Enqueue(() => ContextMenuAddon->IsAddonAndNodesReady(),                       "等待右键菜单出现",  weight: 3);
                        taskHelper.Enqueue(() => AddonContextMenuEvent.Select(LuminaWrapper.GetAddonText(5480)), "出售物品至系统商店", weight: 3);
                        break;
                }
            }
        }

        private ItemConfig GetItemConfigByItemKey(ItemKey key) =>
            ParentModule.config.ItemConfigs.TryGetValue(key.ToString(), out var itemConfig)
                ? itemConfig
                : ParentModule.config.ItemConfigs[new ItemKey(0, key.IsHQ).ToString()];

        #endregion

        #region 操作

        /// <summary>
        ///     将当前雇员市场售卖物品收回背包/雇员
        /// </summary>
        /// <param name="slot"></param>
        /// <param name="isInventory">若为 True 则为收回背包, 否则则为收回雇员背包</param>
        private bool ReturnRetainerMarketItemToInventory(ushort slot, bool isInventory)
        {
            if (!ParentModule.retainerThrottler.Throttle("ReturnMarketItemToInventory", 100)) return false;

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return false;

            var inventoryItem = container->GetInventorySlot(slot);
            if (inventoryItem == null || inventoryItem->ItemId == 0) return true;

            if (isInventory)
                InventoryManager.Instance()->MoveFromRetainerMarketToPlayerInventory(InventoryType.RetainerMarket, slot, (uint)inventoryItem->Quantity);
            else
                InventoryManager.Instance()->MoveFromRetainerMarketToRetainerInventory(InventoryType.RetainerMarket, slot, (uint)inventoryItem->Quantity);
            return false;
        }

        /// <summary>
        ///     设定当前雇员市场售卖物品价格
        /// </summary>
        private static bool SetRetainerMarketItemPrice(ushort slot, uint price)
        {
            if (slot >= 20) return false;

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            manager->SetRetainerMarketPrice((short)slot, price);
            return true;
        }

        /// <summary>
        ///     上架物品至市场
        /// </summary>
        private void UpshelfMarketItem(InventoryType srcType, ushort srcSlot, uint quantity, uint unitPrice, short targetSlot = -1)
        {
            if (targetSlot >= 20) return;
            ushort slot;

            if (targetSlot < 0)
            {
                if (!TryGetFirstEmptyRetainerMarketSlot(out slot)) return;
            }
            else
                slot = (ushort)targetSlot;

            var manager = InventoryManager.Instance();
            if (manager == null) return;

            MoveToRetainerMarketHook.Original(manager, srcType, srcSlot, InventoryType.RetainerMarket, slot, quantity, unitPrice);
        }

        /// <summary>
        ///     获取当前雇员市场售卖物品数据
        /// </summary>
        private static (ItemKey Item, uint Price)? GetRetainerMarketItem(ushort slot)
        {
            if (slot >= 20) return null;

            var manager = InventoryManager.Instance();
            if (manager == null) return null;

            var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return null;

            var slotData = container->GetInventorySlot(slot);
            if (slotData == null) return null;

            var item = new ItemKey(slotData->ItemId, slotData->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
            return (item, GetRetainerMarketPrice(slot));
        }

        /// <summary>
        ///     获取当前雇员市场售卖物品价格
        /// </summary>
        private static uint GetRetainerMarketPrice(ushort slot)
        {
            if (slot >= 20) return 0;

            var manager = InventoryManager.Instance();
            if (manager == null) return 0;

            return (uint)manager->GetRetainerMarketPrice((short)slot);
        }

        /// <summary>
        ///     获取当前市场物品数据
        /// </summary>
        private static void RequestMarketItemData(uint itemID)
        {
            var proxy = InfoProxyItemSearch.Instance();
            if (proxy == null) return;

            proxy->EndRequest();
            proxy->ClearListData();
            proxy->EntryCount = 0;

            proxy->SearchItemId = itemID;
            proxy->RequestData();
        }

        /// <summary>
        ///     当前市场物品数据是否已就绪
        /// </summary>
        private static bool IsMarketItemDataReady(uint itemID)
        {
            var proxy = InfoProxyItemSearch.Instance();
            if (proxy == null) return false;

            if (proxy->SearchItemId != itemID)
            {
                RequestMarketItemData(itemID);
                return false;
            }

            if (IsMarketStuck()) return false;

            if (proxy->Listings.ToArray()
                               .Where(x => x.ItemId == proxy->SearchItemId && x.UnitPrice != 0)
                               .ToList().Count !=
                proxy->ListingCount)
                return false;

            return proxy->EntryCount switch
            {
                > 10 => proxy->ListingCount >= 10,
                0    => true,
                _    => proxy->ListingCount != 0
            };
        }

        /// <summary>
        ///     尝试获取雇员市场售卖列表中首个为空的槽位
        /// </summary>
        /// <returns></returns>
        private static bool TryGetFirstEmptyRetainerMarketSlot(out ushort slot)
        {
            slot = 0;
            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId != 0) continue;

                slot = (ushort)i;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     是否满足任何意外情况
        /// </summary>
        /// <returns>正常/不需要修改价格为 False</returns>
        private static bool IsAnyAbortConditionsMet
        (
            ItemConfig         config,
            uint               origPrice,
            uint               modifiedPrice,
            uint               marketPrice,
            out AbortCondition conditionMet,
            out AbortBehavior  behaviorNeeded
        )
        {
            conditionMet   = AbortCondition.无;
            behaviorNeeded = AbortBehavior.无;

            // 检查每个条件
            foreach (var condition in PriceCheckConditions.GetAll())
            {
                if (config.AbortLogic.Keys.Any(x => x.HasFlag(condition.Condition)) &&
                    condition.Predicate(config, origPrice, modifiedPrice, marketPrice))
                {
                    conditionMet   = condition.Condition;
                    behaviorNeeded = config.AbortLogic.FirstOrDefault(x => x.Key.HasFlag(condition.Condition)).Value;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     获取修改后价格结果
        /// </summary>
        private static uint GetModifiedPrice(ItemConfig config, uint marketPrice)
        {
            return config.AdjustBehavior switch
            {
                AdjustBehavior.固定值 => (uint)Math.Max(
                    1L,
                    (long)marketPrice - config.AdjustValues[AdjustBehavior.固定值]
                ),
                AdjustBehavior.百分比 => (uint)Math.Max(
                    1L,
                    (long)(marketPrice * (1.0 - config.AdjustValues[AdjustBehavior.百分比] / 100.0))
                ),
                _ => marketPrice
            };
        }

        /// <summary>
        ///     发送改价成功通知信息
        /// </summary>
        private void NotifyPriceAdjustSuccessfully(uint itemID, bool isHQ, uint origPrice, uint modifiedPrice)
        {
            if (!ParentModule.config.SendPriceAdjustProcessMessage) return;

            var itemPayload = new SeStringBuilder().AddItemLink(itemID, isHQ).Build();

            var priceChangedValue = (long)modifiedPrice - origPrice;

            var priceChangeText = priceChangedValue.ToChineseString();
            if (!priceChangeText.StartsWith('-'))
                priceChangeText = $"+{priceChangeText}";

            var priceChangeRate     = origPrice == 0 ? 0 : (double)priceChangedValue / origPrice * 100;
            var priceChangeRateText = priceChangeRate.ToString("+0.##;-0.##") + "%";

            NotifyHelper.Instance().Chat
            (
                DailyRoutines.Manager.LanguageManager.GetSe
                (
                    "AutoRetainerWork-PriceAdjust-PriceAdjustSuccessfully",
                    itemPayload,
                    RetainerManager.Instance()->GetActiveRetainer()->NameString,
                    origPrice.ToChineseString(),
                    modifiedPrice.ToChineseString(),
                    priceChangeText,
                    priceChangeRateText
                )
            );
        }

        /// <summary>
        ///     发送意外情况检测通知信息
        /// </summary>
        private void NotifyAbortCondition(uint itemID, bool isHQ, AbortCondition condition, uint marketPrice)
        {
            if (!ParentModule.config.SendPriceAdjustProcessMessage) return;

            var itemPayload = new SeStringBuilder().AddItemLink(itemID, isHQ).Build();
            var baseMessage = DailyRoutines.Manager.LanguageManager.GetSe
            (
                "AutoRetainerWork-PriceAdjust-DetectAbortCondition",
                itemPayload,
                RetainerManager.Instance()->GetActiveRetainer()->NameString,
                new SeStringBuilder().AddUiForeground(GetLoc(condition), 60).Build()
            );

            using var rented = new RentedSeStringBuilder();
            rented.Builder.Append(baseMessage);
            if (IsCN)
                rented.Builder.Append($" [当前市场最低价: {marketPrice.ToChineseString()}]");
            else
                rented.Builder.Append($" [Current Market Min Price: {marketPrice.ToChineseString()}]");

            NotifyHelper.Instance().Chat(rented.Builder.ToReadOnlySeString());
        }

        /// <summary>
        ///     获取当前雇员市场为同一物品的全部槽位
        /// </summary>
        private static bool TryGetSameItemSlots(uint itemID, out List<ushort> slots)
        {
            slots = [];

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemID) continue;

                slots.Add((ushort)i);
            }

            return slots.Count > 0;
        }

        /// <summary>
        ///     尝试获取物品最大可上架数量
        /// </summary>
        private bool TryGetItemUpshelfCountLimit(InventoryItem item, out uint count)
        {
            count = 0;
            if (item.ItemId == 0) return false;

            if (!LuminaGetter.TryGetRow<Item>(item.ItemId, out var itemData)) return false;

            var itemKey    = new ItemKey(item.ItemId, item.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
            var itemConfig = GetItemConfigByItemKey(itemKey);

            var itemStackSize     = itemData.StackSize;
            var defaultStackLimit = itemStackSize           == 9999 ? 9999U : 99U;
            var upshelfLimit      = itemConfig.UpshelfCount > 0 ? (uint)itemConfig.UpshelfCount : defaultStackLimit;

            count = (uint)Math.Min(item.Quantity, upshelfLimit);
            return true;
        }

        /// <summary>
        ///     当前市场是否正在重新请求
        /// </summary>
        /// <returns></returns>
        private static bool IsMarketStuck()
        {
            try
            {
                return DService.Instance().PI
                               .GetIpcSubscriber<bool>("DailyRoutines.Modules.AutoRefreshMarketSearchResult.IsMarketStuck")
                               .InvokeFunc();
            }
            catch
            {
                return false;
            }
        }

        #endregion

        private static string GetLoc(AdjustBehavior behavior)
        {
            var IsCN = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
            return behavior switch
            {
                AdjustBehavior.固定值 => IsCN ? "固定值" : "Fixed Value",
                AdjustBehavior.百分比 => IsCN ? "百分比" : "Percentage",
                _ => behavior.ToString()
            };
        }

        private static string GetLoc(AbortCondition condition)
        {
            var IsCN = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
            List<string> names = [];
            foreach (AbortCondition c in Enum.GetValues<AbortCondition>())
            {
                if (condition.HasFlag(c))
                {
                    names.Add(c switch
                    {
                        AbortCondition.无 => IsCN ? "无" : "None",
                        AbortCondition.低于最小值 => IsCN ? "低于最小值" : "Below Min Price",
                        AbortCondition.低于预期值 => IsCN ? "低于预期值" : "Below Expected Price",
                        AbortCondition.低于收购价 => IsCN ? "低于收购价" : "Below Cost Price",
                        AbortCondition.大于可接受降价值 => IsCN ? "大于可接受降价值" : "Exceeds Acceptable Drop",
                        AbortCondition.高于预期值 => IsCN ? "高于预期值" : "Above Expected Price",
                        AbortCondition.高于最大值 => IsCN ? "高于最大值" : "Above Max Price",
                        _ => c.ToString()
                    });
                }
            }
            return string.Join(", ", names);
        }

        private static string GetLoc(AbortBehavior behavior)
        {
            var IsCN = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
            return behavior switch
            {
                AbortBehavior.无 => IsCN ? "无" : "None",
                AbortBehavior.收回至雇员 => IsCN ? "收回至雇员" : "Return to Retainer",
                AbortBehavior.收回至背包 => IsCN ? "收回至背包" : "Return to Inventory",
                AbortBehavior.出售至系统商店 => IsCN ? "出售至系统商店" : "Sell to NPC Vendor",
                AbortBehavior.改价至最小值 => IsCN ? "改价至最小值" : "Adjust to Min Price",
                AbortBehavior.改价至预期值 => IsCN ? "改价至预期值" : "Adjust to Expected Price",
                AbortBehavior.改价至最高值 => IsCN ? "改价至最高值" : "Adjust to Max Price",
                _ => behavior.ToString()
            };
        }

        private static string GetLoc(SortOrder sortOrder)
        {
            var IsCN = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
            return sortOrder switch
            {
                SortOrder.上架顺序 => IsCN ? "上架顺序" : "Listing Order",
                SortOrder.物品ID => IsCN ? "物品ID" : "Item ID",
                SortOrder.物品类型 => IsCN ? "物品类型" : "Item Type",
                _ => sortOrder.ToString()
            };
        }
    }
}






