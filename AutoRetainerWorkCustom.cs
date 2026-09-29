using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using DailyRoutines.Manager;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Network.Structures;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.BaseTypes.ComponentNode;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Extensions;
using OmenTools.ImGuiOm;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Algorithms;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using Action = System.Action;
using AgentRetainer = OmenTools.Interop.Game.Models.Native.AgentRetainer;
using static OmenTools.Info.Game.Data.Addons;
using static OmenTools.Global.Globals;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoRetainerWorkCustom : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = IClientState.Instance().ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified
                    ? "自动雇员作业 (定制版)"
                    : "Auto Retainer Work (Custom)",
        Description = IClientState.Instance().ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified
                          ? "全自动处理雇员探险收取派遣、物品改价、金币管理等。\n※ 定制增强版：包含改价 1 Gil 保底、异常降价拦截防护、控制器导航对齐及 BetterMarketBoard 深度联动。"
                          : "Fully automated retainer venture collecting/dispatching, price adjusting, and gil management.\n※ Custom Enhanced: Includes 1 Gil price floor protection, abnormal price drop detection, controller navigation, and BetterMarketBoard integration.",
        Category  = ModuleCategory.Interface,
        Author    = ["AtmoOmen", "nynpsu"],
        ReportURL = "https://github.com/kyroli/DailyRoutines.LocalModules/issues",
        ModulesPrerequisite = ["AutoTalkSkip", "AutoRefreshMarketSearchResult", "BetterMarketBoard"]
    };

    [IPCSubscriber("DailyRoutines.Modules.BetterMarketBoard.SearchItem")]
    private static IPCSubscriber<uint, bool>? SearchItemIPC;

    [IPCSubscriber("DailyRoutines.Modules.BetterMarketBoard.ToggleOverlay")]
    private static IPCSubscriber<bool?, bool>? ToggleOverlayIPC;

    private readonly Throttler<string> retainerThrottler = new();
    private readonly HashSet<ulong>    playerRetainers   = [];

    private DRAutoRetainerWork? addon;
    private RetainerWorkerBase[] workers = null!;
    private Config config = null!;

    protected override void Init()
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

        config = LoadConfig<Config>() ?? new();

        foreach (var worker in workers)
            worker.Init();

        addon ??= new(this)
        {
            InternalName = "DRAutoRetainerWorkCustom",
            Title        = Info.Title,
            Size         = new(260f, 320f),
        };

        DService.Instance().Condition.ConditionChange += OnConditionChanged;

        if (DService.Instance().Condition[ConditionFlag.OccupiedSummoningBell])
            OnConditionChanged(ConditionFlag.OccupiedSummoningBell, true);
    }

    private static bool isTalkSkipAutoEnabled;

    protected override void Uninit()
    {
        addon?.Dispose();
        addon = null;

        foreach (var worker in workers)
            worker.Uninit();

        DService.Instance().Condition.ConditionChange -= OnConditionChanged;
        DisableTalkSkipIfAutoEnabled();
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag == ConditionFlag.OccupiedSummoningBell)
        {
            if (value)
            {
                if (isTalkSkipAutoEnabled) return;
                if (ModuleManager.Instance().GetModuleByName("AutoTalkSkip") is { } module &&
                    !(ModuleManager.Instance().IsModuleEnabled("AutoTalkSkip") ?? false))
                {
                    isTalkSkipAutoEnabled = true;
                    ModuleManager.Instance().LoadAsync(module);
                }
            }
            else DisableTalkSkipIfAutoEnabled();
        }
        else if ((flag == ConditionFlag.BoundByDuty || flag == ConditionFlag.BetweenAreas) && value)
        {
            DisableTalkSkipIfAutoEnabled();
        }
    }

    private static void DisableTalkSkipIfAutoEnabled()
    {
        if (isTalkSkipAutoEnabled && ModuleManager.Instance().GetModuleByName("AutoTalkSkip") is { } module)
        {
            ModuleManager.Instance().UnloadAsync(module);
        }
        isTalkSkipAutoEnabled = false;
    }

    protected override void ConfigUI()
    {
        foreach (var worker in workers)
        {
            if (!worker.DrawConfigCondition()) continue;
            worker.DrawConfig();
            ImGui.NewLine();
        }
    }

    protected override void OverlayUI()
    {
        foreach (var worker in workers)
            worker.DrawOverlay();
    }

    private static string GetAbortConditionName(AbortCondition condition)
    {
        if (Enum.IsDefined(condition))
            return GetLoc(condition);

        var builder = new StringBuilder();
        foreach (var flag in AbortConditions)
        {
            if (flag == AbortCondition.无 || (condition & flag) != flag) continue;

            if (builder.Length > 0)
                builder.Append(" | ");
            builder.Append(GetLoc(flag));
        }

        return builder.ToString();
    }

    private static uint GetValidRetainerCount(
        Func<RetainerManager.Retainer, bool> predicateFunc,
        out List<uint>                       validRetainers)
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

    private static bool TrySearchItemInInventory(
        uint                    itemID,
        bool                    isHQ,
        out List<InventoryItem> foundItem)
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
                    (!isHQ || (isHQ && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))))
                    foundItem.Add(*slot);
            }
        }

        return foundItem.Count > 0;
    }

    private void ObtainPlayerRetainers()
    {
        var retainerManager = RetainerManager.Instance();
        if (retainerManager == null) return;

        playerRetainers.Clear();

        for (var i = 0U; i < retainerManager->GetRetainerCount(); i++)
        {
            var retainer = retainerManager->GetRetainerBySortedIndex(i);
            if (retainer == null) break;

            playerRetainers.Add(retainer->RetainerId);
        }
    }

    private bool EnterRetainer(uint index)
    {
        if (!retainerThrottler.Throttle("EnterRetainer", 100)) return false;
        if (!RetainerList->IsAddonAndNodesReady()) return false;

        RetainerList->Callback(2, (int)index, 0, 0);
        return true;
    }

    private static bool LeaveRetainer()
    {
        if (SelectYesno->IsAddonAndNodesReady())
        {
            SelectYesno->Callback(0);
            return false;
        }

        if (RetainerSellList->IsAddonAndNodesReady())
        {
            RetainerSellList->Callback(-1);
            return false;
        }

        if (SelectString->IsAddonAndNodesReady())
        {
            SelectString->Callback(-1);
            return false;
        }

        return RetainerList->IsAddonAndNodesReady();
    }

    private bool IsAnyOtherWorkerBusy(Type currentWorkerType) =>
        workers.Any(w => w.GetType() != currentWorkerType && w.IsWorkerBusy());

    private bool IsAnyWorkerBusy() => workers.Any(w => w.IsWorkerBusy());

    #region 雇员列表 Overlay 扩展 (DRAutoRetainerWork)

    private class DRAutoRetainerWork(AutoRetainerWorkCustom module) : AttachedAddon("RetainerList")
    {
        private CollaspingNode? treeListNode;

        protected override Vector2 PositionOffset => new(0f, 6f);

        protected override bool CanCloseHostAddon(AtkUnitBase* hostAddon) => false;
        protected override bool CanOpenAddon => !module.IsAnyWorkerBusy();

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            module.ObtainPlayerRetainers();

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

            ApplyControllerNavigation(addon);
        }

        private void ApplyControllerNavigation(AtkUnitBase* addon)
        {
            if (treeListNode == null) return;

            List<ComponentNode> navigationNodes = [];

            foreach (var categoryNode in treeListNode.CategoryNodes)
            {
                var headerNode = new NavFocusNode
                {
                    Position     = new(2f, 14f),
                    OnSelected   = () => categoryNode.IsCollapsed = !categoryNode.IsCollapsed,
                    OnHoverStart = () => categoryNode.Timeline?.PlayAnimation(categoryNode.IsCollapsed ? 2 : 9),
                    OnHoverEnd   = () => categoryNode.Timeline?.PlayAnimation(categoryNode.IsCollapsed ? 1 : 8)
                };
                headerNode.AttachNode(categoryNode);
                navigationNodes.Add(headerNode);

                foreach (var contentNode in categoryNode.Children.OfType<VerticalListNode>().SelectMany(x => x.Nodes))
                {
                    switch (contentNode)
                    {
                        case CheckboxNode checkboxNode:
                            navigationNodes.Add(checkboxNode);
                            break;
                        case HorizontalFlexNode flexNode:
                        {
                            var buttonNodes = flexNode.Nodes.OfType<TextButtonNode>().ToList();
                            var rowStart    = navigationNodes.Count;

                            navigationNodes.AddRange(buttonNodes);

                            for (var index = 0; index < buttonNodes.Count; index++)
                            {
                                buttonNodes[index].NavLeft  = rowStart + (index == 0 ? buttonNodes.Count : index);
                                buttonNodes[index].NavRight = rowStart + (index == buttonNodes.Count - 1 ? 1 : index + 2);
                            }

                            break;
                        }
                    }
                }
            }

            if (navigationNodes.Count == 0) return;

            for (var index = 0; index < navigationNodes.Count; index++)
            {
                navigationNodes[index].NavIndex = index + 1;
                navigationNodes[index].NavUp    = index == 0 ? navigationNodes.Count : index;
                navigationNodes[index].NavDown  = index == navigationNodes.Count - 1 ? 1 : index + 2;
            }

            addon->FocusNode = navigationNodes[0];
        }
    }

    #endregion

    #region Worker 抽象基类

    private abstract class RetainerWorkerBase(AutoRetainerWorkCustom module)
    {
        protected AutoRetainerWorkCustom Module = module;

        public abstract bool IsWorkerBusy();
        public virtual bool DrawConfigCondition() => true;
        public abstract void Init();
        public abstract void Uninit();
        public virtual void DrawConfig() { }
        public virtual void DrawOverlay() { }

        public virtual CollaspingCategoryNode? CreateOverlayCategory(float width) => null;

        protected static uint GetValidRetainerCount(
            Func<RetainerManager.Retainer, bool> predicate,
            out List<uint> validRetainers) =>
            AutoRetainerWorkCustom.GetValidRetainerCount(predicate, out validRetainers);

        protected bool LeaveRetainer() => AutoRetainerWorkCustom.LeaveRetainer();

        protected static CollaspingCategoryNode CreateOverlayCategory(
            string title,
            float width,
            params NodeBase[] nodes)
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

            var categoryNode = new CollaspingCategoryNode
            {
                IsVisible = true,
                Size      = new(width, 28f),
                String    = title
            };
            categoryNode.AddNode(contentNode);
            categoryNode.IsCollapsed = true;

            return categoryNode;
        }

        protected static HorizontalFlexNode CreateOverlayButtonRow(
            Action startAction,
            Action stopAction,
            float width)
        {
            var row = new HorizontalFlexNode
            {
                IsVisible      = true,
                Size           = new(width, 28f),
                AlignmentFlags = FlexFlags.FitContentHeight | FlexFlags.FitWidth,
                ItemSpacing    = 4
            };
            row.AddNode(
            [
                new TextButtonNode
                {
                    IsVisible = true,
                    IsEnabled = true,
                    Size      = new(100f, 28f),
                    String    = GetLoc("Start"),
                    OnClick   = startAction
                },
                new TextButtonNode
                {
                    IsVisible = true,
                    IsEnabled = true,
                    Size      = new(100f, 28f),
                    String    = GetLoc("Stop"),
                    OnClick   = stopAction
                }
            ]);

            return row;
        }

        protected static CheckboxNode CreateOverlayCheckbox(
            string title,
            bool isChecked,
            Action<bool> onClick,
            float width,
            string? tooltip = null)
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
                AlignmentType = AlignmentType.Left
            };
            node.AutoAdjustTextSize();
            return node;
        }
    }

    #endregion

    #region 1. 自动探险收取派遣 (CollectWorker)

    private class CollectWorker(AutoRetainerWorkCustom module) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

        public override bool DrawConfigCondition() => false;
        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init()
        {
            taskHelper ??= new() { TimeoutMS = 15_000, ShowDebug = true };

            IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerList);
            IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,  "RetainerList", OnRetainerList);
        }

        public override void Uninit()
        {
            IAddonLifecycle.Instance().UnregisterListener(OnRetainerList);

            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override CollaspingCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory(
                GetLoc("AutoRetainerWork-Collect-Title"),
                width,
                CreateOverlayCheckbox(
                    GetLoc("AutoRetainerWork-Collect-AutoCollect"),
                    Module.config.AutoRetainerCollect,
                    isChecked =>
                    {
                        Module.config.AutoRetainerCollect = isChecked;
                        if (Module.config.AutoRetainerCollect)
                            EnqueueRetainersCollect();
                        Module.SaveConfig(Module.config);
                    },
                    width
                ),
                CreateOverlayCheckbox(
                    GetLoc("AutoRetainerWork-Collect-AutoPriceAdjustAfterCollect"),
                    Module.config.AutoPriceAdjustAfterCollect,
                    isChecked =>
                    {
                        Module.config.AutoPriceAdjustAfterCollect = isChecked;
                        Module.SaveConfig(Module.config);
                    },
                    width
                ),
                CreateOverlayButtonRow(EnqueueRetainersCollect, () => taskHelper?.Abort(), width)
            );

        private void OnRetainerList(AddonEvent type, AddonArgs args)
        {
            if (Module.IsAnyOtherWorkerBusy(typeof(CollectWorker))) return;

            switch (type)
            {
                case AddonEvent.PostSetup:
                    Module.ObtainPlayerRetainers();
                    if (taskHelper.IsBusy) return;
                    if (!Module.config.AutoRetainerCollect) break;
                    if (taskHelper.AbortByConflictKey(Module)) break;
                    EnqueueRetainersCollect();
                    break;
                case AddonEvent.PostDraw:
                    if (!Module.config.AutoRetainerCollect) break;
                    if (!Module.retainerThrottler.Throttle("AutoRetainerCollect-AFK", 5_000)) return;

                    IFramework.Instance().RunOnTick(
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
            if (taskHelper == null || taskHelper.AbortByConflictKey(Module)) return;

            var serverTime = Framework.GetServerTime();
            var count = GetValidRetainerCount(
                x => x.VentureId != 0 && x.VentureComplete != 0 && x.VentureComplete + 1 <= serverTime,
                out var validRetainers
            );

            if (count == 0)
            {
                if (taskHelper.IsBusy)
                {
                    taskHelper.Enqueue(LeaveRetainer, "确保所有雇员均已返回");

                    if (Module.config.AutoPriceAdjustAfterCollect)
                    {
                        taskHelper.Enqueue(
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return true;
                                IFramework.Instance().RunOnTick(() =>
                                {
                                    if (!Module.config.AutoPriceAdjustAfterCollect) return;
                                    var priceAdjustWorker = Array.Find(Module.workers, w => w is PriceAdjustWorker) as PriceAdjustWorker;
                                    priceAdjustWorker?.EnqueuePriceAdjustAllRetainers();
                                });
                                return true;
                            },
                            "收取完成后触发自动改价"
                        );
                    }
                }

                return;
            }

            foreach (var index in validRetainers)
            {
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return Module.EnterRetainer(index);
                    },
                    $"选择进入 {index} 号雇员"
                );

                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
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

                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        if (!RetainerTaskResult->IsAddonAndNodesReady()) return false;

                        RetainerTaskResult->Callback(14);
                        return true;
                    },
                    "重新派遣雇员探险"
                );

                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        if (!RetainerTaskAsk->IsAddonAndNodesReady()) return false;

                        RetainerTaskAsk->Callback(12);
                        return true;
                    },
                    "确认派遣雇员探险"
                );

                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return LeaveRetainer();
                    },
                    "回到雇员列表"
                );
            }

            taskHelper.Enqueue(EnqueueRetainersCollect, "重新检查是否有其他雇员需要收取");
        }

        private static readonly string[] VentureCompleteTexts =
        [
            "结束",
            "結束",
            "Complete",
            "完了",
            "완료",
            "Abgeschlossen",
            "Terminée"
        ];
    }

    #endregion

    #region 2. 自动改价工作器 (PriceAdjustWorker - 现代 Addon/Context 对标架构)

    private class PriceAdjustWorker(AutoRetainerWorkCustom module) : RetainerWorkerBase(module)
    {
        private Hook<InventoryManager.Delegates.MoveToRetainerMarket>? MoveToRetainerMarketHook;

        private TaskHelper?     taskHelper;
        private ItemSelectCombo itemSelectCombo = null!;

        private ItemConfig?     selectedItemConfig;
        private readonly Vector2 childSizeLeft     = ScaledVector2(200, 400);
        private Vector2         childSizeRight    = ScaledVector2(450, 400);
        private string          presetSearchInput = string.Empty;
        private bool            newConfigItemHQ;
        private AbortCondition  conditionInput = AbortCondition.低于最小值;
        private AbortBehavior   behaviorInput  = AbortBehavior.无;

        private PriceAdjustContextMenuEntry? contextMenuEntry;
        private PriceAdjustAddon?            priceAdjustAddon;

        private AtkEventWrapper? openMarketEvent;
        private AtkEventWrapper? priceAdjustAllSameEvent;
        private ResNode?         autoPriceAdjustWarningNode;

        private bool isPriceAdjustAllSameItems;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init()
        {
            itemSelectCombo = new("AddNewItem");

            MoveToRetainerMarketHook ??= IGameInteropProvider.Instance().HookFromMemberFunction(
                typeof(InventoryManager.MemberFunctionPointers),
                "MoveToRetainerMarket",
                (InventoryManager.Delegates.MoveToRetainerMarket)MoveToRetainerMarketDetour
            );
            MoveToRetainerMarketHook.Enable();

            taskHelper                 ??= new() { TimeoutMS = 30_000, ShowDebug = true };
            taskHelper.EnterBusyAction =   () => ToggleOverlayIPC?.TryInvokeFunc(true);
            taskHelper.LeaveBusyAction =   () => ToggleOverlayIPC?.TryInvokeFunc(false);

            contextMenuEntry = new(this);
            priceAdjustAddon = new(this)
            {
                InternalName = "DRAutoRetainerWorkPriceAdjustCustom",
                Title        = GetLoc("AutoRetainerWork-PriceAdjust-Title"),
                Size         = new(260f, 320f)
            };

            IMarketBoard.Instance().HistoryReceived   += OnHistoryReceived;
            IMarketBoard.Instance().OfferingsReceived += OnOfferingReceived;

            IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSell);
            IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "RetainerSell", OnRetainerSell);
            if (RetainerSell->IsAddonAndNodesReady())
                OnRetainerSell(AddonEvent.PostSetup, null!);

            ContextMenuManager.Instance().Reg(contextMenuEntry);
        }

        public override void Uninit()
        {
            ContextMenuManager.Instance().Unreg(contextMenuEntry);

            priceAdjustAddon?.Dispose();
            priceAdjustAddon = null;

            openMarketEvent?.Dispose();
            openMarketEvent = null;

            priceAdjustAllSameEvent?.Dispose();
            priceAdjustAllSameEvent = null;

            MoveToRetainerMarketHook?.Dispose();
            MoveToRetainerMarketHook = null;

            IAddonLifecycle.Instance().UnregisterListener(OnRetainerSell);

            autoPriceAdjustWarningNode?.Dispose();
            autoPriceAdjustWarningNode = null;

            IMarketBoard.Instance().HistoryReceived   -= OnHistoryReceived;
            IMarketBoard.Instance().OfferingsReceived -= OnOfferingReceived;

            PriceCacheManager.ClearCache();

            contextMenuEntry = null;

            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override void DrawConfig()
        {
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("AutoRetainerWork-PriceAdjust-Title"));

            ItemConfigSelector();

            ImGui.SameLine();
            ItemConfigEditor();
        }

        public override CollaspingCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory(
                GetLoc("AutoRetainerWork-PriceAdjust-Title"),
                width,
                CreateOverlayText(GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-AllRetainers"), width),
                CreateOverlayButtonRow(
                    () =>
                    {
                        if (taskHelper is not { IsBusy: false }) return;
                        EnqueuePriceAdjustAllRetainers();
                    },
                    () => taskHelper?.Abort(),
                    width
                ),
                CreateOverlayCheckbox(
                    GetLoc("AutoRetainerWork-PriceAdjust-SendProcessMessage"),
                    Module.config.SendPriceAdjustProcessMessage,
                    isChecked =>
                    {
                        Module.config.SendPriceAdjustProcessMessage = isChecked;
                        Module.SaveConfig(Module.config);
                    },
                    width
                )
            );

        #region 配置界面

        private void ItemConfigSelector()
        {
            using var child = ImRaii.Child("ItemConfigSelectorChild", childSizeLeft, true);
            if (!child) return;

            if (ImGuiOm.ButtonIcon("AddNewConfig", FontAwesomeIcon.Plus, GetLoc("Add")))
                ImGui.OpenPopup("AddNewPreset");

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("ImportConfig", FontAwesomeIcon.FileImport, GetLoc("ImportFromClipboard")))
            {
                var itemConfig = ImportFromClipboard<ItemConfig>();
                if (itemConfig != null)
                {
                    var itemKey = new ItemKey(itemConfig.ItemID, itemConfig.IsHQ).ToString();
                    Module.config.ItemConfigs[itemKey] = itemConfig;
                }
            }

            using (var popup0 = ImRaii.Popup("AddNewPreset"))
            {
                if (popup0)
                {
                    AddNewConfigItemPopup(() =>
                    {
                        var newConfigStr = new ItemKey(itemSelectCombo.SelectedID, newConfigItemHQ).ToString();
                        var newConfig    = new ItemConfig(itemSelectCombo.SelectedID, newConfigItemHQ);

                        if (Module.config.ItemConfigs.TryAdd(newConfigStr, newConfig))
                        {
                            Module.SaveConfig(Module.config);
                            ImGui.CloseCurrentPopup();
                        }
                    });
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("###PresetSearchInput", GetLoc("PleaseSearch"), ref presetSearchInput, 100);

            ImGui.Separator();

            foreach (var itemConfig in Module.config.ItemConfigs.ToList())
            {
                if (!string.IsNullOrWhiteSpace(presetSearchInput) && !itemConfig.Value.ItemName.Contains(presetSearchInput))
                    continue;

                if (ImGui.Selectable(
                        $"{itemConfig.Value.ItemName} {(itemConfig.Value.IsHQ ? "(HQ)" : "")}",
                        itemConfig.Value == selectedItemConfig
                    ))
                    selectedItemConfig = itemConfig.Value;

                var isOpenPopup = false;

                using (var popup1 = ImRaii.ContextPopupItem($"{itemConfig.Value}_{itemConfig.Key}_{itemConfig.Value.ItemID}"))
                {
                    if (popup1)
                    {
                        if (ImGui.MenuItem(GetLoc("ExportToClipboard")))
                            ExportToClipboard(itemConfig.Value);

                        if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-CreateNewBaseOnExisted")))
                            isOpenPopup = true;

                        if (itemConfig.Value.ItemID != 0)
                        {
                            if (ImGui.MenuItem(GetLoc("Delete")))
                            {
                                Module.config.ItemConfigs.Remove(itemConfig.Key);
                                Module.SaveConfig(Module.config);
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
                        AddNewConfigItemPopup(() =>
                        {
                            var newConfigStr = new ItemKey(itemSelectCombo.SelectedID, newConfigItemHQ).ToString();
                            var newConfig = new ItemConfig(itemSelectCombo.SelectedID, newConfigItemHQ)
                            {
                                AdjustBehavior    = itemConfig.Value.AdjustBehavior,
                                AdjustValues      = new(itemConfig.Value.AdjustValues),
                                PriceMinimum      = itemConfig.Value.PriceMinimum,
                                PriceMaximum      = itemConfig.Value.PriceMaximum,
                                PriceExpected     = itemConfig.Value.PriceExpected,
                                PriceMaxReduction = itemConfig.Value.PriceMaxReduction,
                                UpshelfCount      = itemConfig.Value.UpshelfCount,
                                AbortLogic        = new(itemConfig.Value.AbortLogic)
                            };

                            if (Module.config.ItemConfigs.TryAdd(newConfigStr, newConfig))
                            {
                                Module.SaveConfig(Module.config);
                                ImGui.CloseCurrentPopup();
                            }
                        });
                    }
                }
            }
        }

        private void AddNewConfigItemPopup(Action onConfirm)
        {
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            itemSelectCombo.DrawRadio();

            ImGui.SameLine();
            ImGui.Checkbox("HQ", ref newConfigItemHQ);

            if (ImGui.Button(GetLoc("Confirm")))
            {
                if (itemSelectCombo.SelectedID != 0)
                    onConfirm();
            }
        }

        private void ItemConfigEditor()
        {
            if (selectedItemConfig == null) return;

            var itemName = selectedItemConfig.ItemName;
            uint itemBuyingPrice = 1;

            if (selectedItemConfig.ItemID != 0)
            {
                if (LuminaGetter.TryGetRow<Item>(selectedItemConfig.ItemID, out var itemRow))
                {
                    itemName = itemRow.Name.ToString();
                    itemBuyingPrice = itemRow.PriceMid;
                }
                else
                    itemName = GetLoc("Unknown");
            }

            using var child = ImRaii.Child("ItemConfigEditorChild", childSizeRight, true);
            if (!child) return;

            using (FontManager.Instance().UIFont140.Push())
            {
                ImGui.TextUnformatted(itemName);
            }

            ImGui.Separator();

            using (ImRaii.Group())
            {
                ImGui.TextUnformatted(GetLoc("AutoRetainerWork-PriceAdjust-Behavior"));

                foreach (AdjustBehavior behavior in Enum.GetValues(typeof(AdjustBehavior)))
                {
                    var isSelected = selectedItemConfig.AdjustBehavior == behavior;
                    if (ImGui.RadioButton(GetLoc(behavior), isSelected))
                    {
                        selectedItemConfig.AdjustBehavior = behavior;
                        Module.SaveConfig(Module.config);
                    }
                }
            }

            ImGui.SameLine();

            using (ImRaii.Group())
            {
                ImGui.Dummy(new(ImGui.GetTextLineHeight()));

                if (selectedItemConfig.AdjustBehavior == AdjustBehavior.固定值)
                {
                    var originalValue = selectedItemConfig.AdjustValues[AdjustBehavior.固定值];
                    ImGui.SetNextItemWidth(100f * GlobalUIScale);
                    ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-ValueReduction"), ref originalValue);

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        selectedItemConfig.AdjustValues[AdjustBehavior.固定值] = originalValue;
                        Module.SaveConfig(Module.config);
                    }
                }
                else
                    ImGui.Dummy(new(ImGui.GetTextLineHeightWithSpacing()));

                if (selectedItemConfig.AdjustBehavior == AdjustBehavior.百分比)
                {
                    var originalValue = selectedItemConfig.AdjustValues[AdjustBehavior.百分比];
                    ImGui.SetNextItemWidth(100f * GlobalUIScale);
                    ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-PercentageReduction"), ref originalValue);

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        selectedItemConfig.AdjustValues[AdjustBehavior.百分比] = Math.Clamp(originalValue, -99, 99);
                        Module.SaveConfig(Module.config);
                    }
                }
                else
                    ImGui.Dummy(new(ImGui.GetTextLineHeightWithSpacing()));
            }

            ImGuiOm.ScaledDummy(10f);

            var originalMin = selectedItemConfig.PriceMinimum;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-PriceMinimum"), ref originalMin);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceMinimum = Math.Max(1, originalMin);
                Module.SaveConfig(Module.config);
            }

            ImGui.SameLine();

            using (ImRaii.Disabled(selectedItemConfig.ItemID == 0))
            {
                if (ImGuiOm.ButtonIcon("ObtainBuyingPrice", FontAwesomeIcon.Store, GetLoc("AutoRetainerWork-PriceAdjust-ObtainBuyingPrice")))
                {
                    selectedItemConfig.PriceMinimum = Math.Max(1, (int)itemBuyingPrice);
                    Module.SaveConfig(Module.config);
                }
            }

            var originalMax = selectedItemConfig.PriceMaximum;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-PriceMaximum"), ref originalMax);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceMaximum = Math.Min(int.MaxValue, originalMax);
                Module.SaveConfig(Module.config);
            }

            var originalExpected = selectedItemConfig.PriceExpected;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-PriceExpected"), ref originalExpected);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceExpected = Math.Max(originalMin + 1, originalExpected);
                Module.SaveConfig(Module.config);
            }

            ImGui.SameLine();

            using (ImRaii.Disabled(selectedItemConfig.ItemID == 0))
            {
                if (ImGuiOm.ButtonIcon("OpenUniversalis", FontAwesomeIcon.Globe, GetLoc("AutoRetainerWork-PriceAdjust-OpenUniversalis")))
                    Util.OpenLink($"https://universalis.app/market/{selectedItemConfig.ItemID}");
            }

            var originalPriceReduction = selectedItemConfig.PriceMaxReduction;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-PriceMaxReduction"), ref originalPriceReduction);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceMaxReduction = Math.Max(0, originalPriceReduction);
                Module.SaveConfig(Module.config);
            }

            var originalUpshelfCount = selectedItemConfig.UpshelfCount;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-UpshelfCount"), ref originalUpshelfCount);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.UpshelfCount = originalUpshelfCount;
                Module.SaveConfig(Module.config);
            }

            ImGuiOm.ScaledDummy(10f);

            using (ImRaii.Group())
            {
                ImGui.SetNextItemWidth(250f * GlobalUIScale);

                using (var combo = ImRaii.Combo("###AddNewLogicConditionCombo", GetAbortConditionName(conditionInput), ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        foreach (var condition in AbortConditions)
                        {
                            if (condition == AbortCondition.无) continue;

                            var isSelected = (conditionInput & condition) == condition;

                            if (ImGui.Selectable(GetLoc(condition), isSelected, ImGuiSelectableFlags.DontClosePopups))
                            {
                                var combinedCondition = conditionInput;
                                if (isSelected)
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

            if (ImGuiOm.ButtonIconWithTextVertical(
                    FontAwesomeIcon.Plus,
                    GetLoc("Add"),
                    groupSize0 with { X = ImGui.CalcTextSize(GetLoc("Add")).X * 2f }
                ))
            {
                if (conditionInput != AbortCondition.无)
                {
                    selectedItemConfig.AbortLogic.TryAdd(conditionInput, behaviorInput);
                    Module.SaveConfig(Module.config);
                }
            }

            ImGui.Separator();

            foreach (var logic in selectedItemConfig.AbortLogic.ToList())
            {
                var origConditionStr = GetAbortConditionName(logic.Key);
                ImGui.SetNextItemWidth(300f * GlobalUIScale);
                ImGui.InputText($"###Condition_{origConditionStr}", ref origConditionStr, 100, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.IsItemClicked())
                    ImGui.OpenPopup($"###ConditionSelectPopup_{origConditionStr}");

                using (var popup = ImRaii.Popup($"###ConditionSelectPopup_{origConditionStr}"))
                {
                    if (popup)
                    {
                        foreach (var condition in AbortConditions)
                        {
                            if (condition == AbortCondition.无) continue;

                            var isSelected = (logic.Key & condition) == condition;

                            if (ImGui.Selectable(GetLoc(condition), isSelected, ImGuiSelectableFlags.DontClosePopups))
                            {
                                var combinedCondition = logic.Key;
                                if (isSelected)
                                    combinedCondition &= ~condition;
                                else
                                    combinedCondition |= condition;

                                selectedItemConfig.AbortLogic.Remove(logic.Key);
                                if (combinedCondition != AbortCondition.无)
                                    selectedItemConfig.AbortLogic[combinedCondition] = logic.Value;

                                Module.SaveConfig(Module.config);
                            }
                        }
                    }
                }

                ImGui.SameLine();

                var origBehaviorStr = GetLoc(logic.Value);
                ImGui.SetNextItemWidth(200f * GlobalUIScale);
                ImGui.InputText($"###Behavior_{origConditionStr}", ref origBehaviorStr, 100, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.IsItemClicked())
                    ImGui.OpenPopup($"###BehaviorSelectPopup_{origBehaviorStr}");

                using (var popup = ImRaii.Popup($"###BehaviorSelectPopup_{origBehaviorStr}"))
                {
                    if (popup)
                    {
                        foreach (AbortBehavior behavior in Enum.GetValues(typeof(AbortBehavior)))
                        {
                            if (ImGui.Selectable(GetLoc(behavior), logic.Value == behavior, ImGuiSelectableFlags.DontClosePopups))
                            {
                                selectedItemConfig.AbortLogic[logic.Key] = behavior;
                                Module.SaveConfig(Module.config);
                            }
                        }
                    }
                }

                ImGui.SameLine();

                if (ImGuiOm.ButtonIcon($"DeleteLogic_{origConditionStr}", FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
                {
                    selectedItemConfig.AbortLogic.Remove(logic.Key);
                    Module.SaveConfig(Module.config);
                }
            }
        }

        private void OnRetainerSell(AddonEvent type, AddonArgs args)
        {
            if (!ICondition.Instance()[ConditionFlag.OccupiedSummoningBell]) return;

            switch (type)
            {
                case AddonEvent.PostSetup:
                    var slot = InventoryManager.Instance()->GetInventorySlot(
                        AgentRetainer.Instance()->SellItemInventoryType,
                        AgentRetainer.Instance()->SellItemInventorySlot
                    );
                    if (slot == null) return;

                    if (AgentRetainer.Instance()->SellItemInventoryType != InventoryType.RetainerMarket)
                    {
                        var itemConfig = GetItemConfigByItemKey(new(slot->GetBaseItemId(), slot->IsHighQuality()));
                        if (itemConfig.UpshelfCount > 0)
                        {
                            var quantityInput = (AtkComponentNumericInput*)RetainerSell->GetComponentByNodeId(14);
                            if (quantityInput != null)
                                quantityInput->InnerSetValue(itemConfig.UpshelfCount, true, false);
                        }

                        if (Module.config.AutoOnSale)
                        {
                            RetainerSell->Callback(0);
                            return;
                        }

                        if (Module.config.AutoPriceAdjustWhenNewOnSale)
                        {
                            var countInputComponent = (AtkComponentNumericInput*)RetainerSell->GetComponentByNodeId(14);
                            var priceInputComponent = (AtkComponentNumericInput*)RetainerSell->GetComponentByNodeId(10);

                            if (countInputComponent != null &&
                                priceInputComponent != null)
                            {
                                priceInputComponent->SetEnabledState(false);

                                var ownerNode = priceInputComponent->OwnerNode;
                                if (ownerNode == null) return;

                                var parentNode = ownerNode->ParentNode;
                                if (parentNode == null) return;

                                autoPriceAdjustWarningNode = new()
                                {
                                    Size        = new(ownerNode->Width, ownerNode->Height),
                                    Position    = new(ownerNode->X, ownerNode->Y),
                                    TextTooltip = GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale-Warning")
                                };
                                autoPriceAdjustWarningNode.AttachNode(parentNode);
                            }
                        }
                    }

                    var marketButton = RetainerSell->GetComponentButtonById(4);
                    if (marketButton != null)
                    {
                        marketButton->OwnerNode->ClearEvents();

                        openMarketEvent = new((_, _, _, _) => RequestMarketItemData(slot->GetBaseItemId(), true));
                        openMarketEvent.Add(RetainerSell, (AtkResNode*)marketButton->OwnerNode, AtkEventType.ButtonClick);
                    }

                    if (isPriceAdjustAllSameItems)
                    {
                        var confirmButton = RetainerSell->GetComponentButtonById(21);
                        if (confirmButton != null)
                        {
                            confirmButton->OwnerNode->ClearEvents();

                            priceAdjustAllSameEvent = new((_, _, _, _) =>
                            {
                                if (TryGetSameItemSlots(slot->GetBaseItemId(), out var slots))
                                {
                                    foreach (var s in slots)
                                        EnqueuePriceAdjustSlot(s, (uint)AgentRetainer.Instance()->SellItemUnitPrice);
                                }

                                RetainerSell->Close(true);
                                isPriceAdjustAllSameItems = false;
                            });
                            priceAdjustAllSameEvent.Add(RetainerSell, (AtkResNode*)confirmButton->OwnerNode, AtkEventType.ButtonClick);
                        }
                    }
                    break;

                case AddonEvent.PreFinalize:
                    if (!taskHelper.IsBusy)
                        ToggleOverlayIPC?.TryInvokeFunc(false);

                    autoPriceAdjustWarningNode?.Dispose();
                    autoPriceAdjustWarningNode = null;

                    openMarketEvent?.Dispose();
                    openMarketEvent = null;

                    priceAdjustAllSameEvent?.Dispose();
                    priceAdjustAllSameEvent = null;

                    isPriceAdjustAllSameItems = false;
                    break;
            }
        }

        private void OnOfferingReceived(IMarketBoardCurrentOfferings data) =>
            PriceCacheManager.OnOfferingReceived(Module, data);

        private static void OnHistoryReceived(IMarketBoardHistory history) =>
            PriceCacheManager.OnHistoryReceived(history);

        private void MoveToRetainerMarketDetour(
            InventoryManager* manager,
            InventoryType     srcInv,
            ushort            srcSlot,
            InventoryType     dstInv,
            ushort            dstSlot,
            uint              quantity,
            uint              unitPrice)
        {
            var slot = manager->GetInventorySlot(srcInv, srcSlot);
            if (slot == null)
            {
                InvokeOriginal();
                return;
            }

            if (Module.config.AutoPriceAdjustWhenNewOnSale && !PluginConfig.Instance().ConflictKeyBinding.IsPressed())
            {
                MoveToRetainerMarketHook.Original(manager, srcInv, srcSlot, dstInv, dstSlot, quantity, 9_9999_9999);
                EnqueuePriceAdjustSlot(dstSlot);
                return;
            }

            InvokeOriginal();
            return;

            void InvokeOriginal() =>
                MoveToRetainerMarketHook.Original(manager, srcInv, srcSlot, dstInv, dstSlot, quantity, unitPrice);
        }

        #endregion

        #region 改价调度与队列

        internal void EnqueuePriceAdjustAllRetainers()
        {
            if (taskHelper == null || taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            Module.ObtainPlayerRetainers();

            var count = GetValidRetainerCount(x => x is { Available: true, MarketItemCount: > 0 }, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach(index =>
            {
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return Module.EnterRetainer(index);
                    },
                    $"选择进入 {index} 号雇员"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return SelectString->IsAddonAndNodesReady() && RetainerManager.Instance()->GetActiveRetainer() != null;
                    },
                    $"等待接收 {index} 号雇员的数据"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return AddonSelectStringEvent.Select(SellInventoryItemsText);
                    },
                    "点击进入出售玩家所持物品列表"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        if (!RetainerSellList->IsAddonAndNodesReady()) return false;

                        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
                        if (container == null || !container->IsLoaded) return false;

                        EnqueuePriceAdjustRetainer();
                        return true;
                    },
                    "等待出售品列表界面就绪并接管改价"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        if (!RetainerSellList->IsAddonAndNodesReady()) return false;
                        RetainerSellList->Callback(-1);
                        return true;
                    },
                    "单一雇员改价完成, 退出出售品列表界面"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return LeaveRetainer();
                    },
                    "单一雇员改价完成, 返回至雇员列表界面"
                );
            });
        }

        private void EnqueuePriceAdjustRetainer()
        {
            if (taskHelper == null || taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var retainer = RetainerManager.Instance()->GetActiveRetainer();
            if (retainer == null || retainer->MarketItemCount <= 0) return;

            var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return;

            for (ushort i = 0; i < container->Size; i++)
                EnqueuePriceAdjustSlot(i);
        }

        private void EnqueuePriceAdjustSlot(ushort slotIndex, uint forcePrice = 0)
        {
            if (taskHelper == null || taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            taskHelper.Enqueue(
                () =>
                {
                    var retainer = RetainerManager.Instance()->GetActiveRetainer();
                    if (retainer == null) return;

                    var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
                    if (container == null || !container->IsLoaded) return;

                    var slot = container->GetInventorySlot(slotIndex);
                    if (slot == null || slot->ItemId == 0) return;
                    var itemID = slot->ItemId;

                    var itemName      = LuminaGetter.GetRow<Item>(itemID)?.Name.ToString() ?? string.Empty;
                    var isItemHQ      = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    var isPriceCached = PriceCacheManager.TryGetPriceCache(itemID, isItemHQ, out var price);

                    if (!isPriceCached)
                    {
                        var isNothingSearched = InfoProxyItemSearch.Instance()->SearchItemId == 0;

                        taskHelper.Enqueue(
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return;
                                RequestMarketItemData(itemID, false);
                            },
                            $"请求雇员 {retainer->NameString} {slotIndex} 号位置处 {itemName} 的市场价格数据",
                            weight: 2
                        );
                        if (isNothingSearched)
                            taskHelper.DelayNext(1000, "初始无数据, 等待 1 秒", 2);
                        taskHelper.Enqueue(
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return true;
                                return IsMarketItemDataReady(itemID);
                            },
                            $"等待 {itemName} 市场价格数据完全到达",
                            weight: 2
                        );
                        taskHelper.Enqueue(
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return;
                                if (!PriceCacheManager.TryGetPriceCache(itemID, isItemHQ, out price))
                                    price = 0;

                                EnqueuePriceAdjustSingleItem(slotIndex, price, forcePrice);
                            },
                            "由单一物品改价接管后续逻辑",
                            weight: 2
                        );
                        return;
                    }

                    taskHelper.Enqueue(() => EnqueuePriceAdjustSingleItem(slotIndex, price, forcePrice), "由单一物品改价接管后续逻辑", weight: 2);
                },
                $"检查当前市场第 {slotIndex} 栏的物品数据, 强制价格: {forcePrice}",
                weight: 1
            );
        }

        private const double AnomalyDropRatio = 0.60;
        private const uint   AnomalyMinGap    = 5;

        /// <summary>
        ///     过滤孤立的异常超低价格，返回合理的当前市场最低参考价
        /// </summary>
        private static uint GetFinalMarketPrice(List<uint> prices)
        {
            if (prices == null || prices.Count == 0) return 0;
            if (prices.Count == 1) return prices[0];

            var skippedCount = 0;
            for (var i = 0; i < prices.Count - 1; i++)
            {
                var current = prices[i];
                var next    = prices[i + 1];
                if (next == 0) continue;

                var gap = next - current;

                // 如果相邻价格跌幅未达 60% 或绝对差额小于 5 Gil，说明 current 是合理的起始物价
                if ((double)gap / next < AnomalyDropRatio || gap < AnomalyMinGap)
                    return current;

                // 否则 current 属于孤立超低价，跳过 current 尝试下一个
                skippedCount++;
                if (skippedCount >= 2)
                    return next;
            }

            return prices[^1];
        }

        private void EnqueuePriceAdjustSingleItem(ushort slot, uint marketPrice, uint forcePrice = 0)
        {
            var itemMarketData = GetRetainerMarketItem(slot);
            if (itemMarketData == null) return;

            var itemConfig = GetItemConfigByItemKey(itemMarketData.Value.Item);

            var finalMarketPrice = marketPrice;
            if (forcePrice == 0)
            {
                if (PriceCacheManager.TryGetPricesCache(itemMarketData.Value.Item.ItemID, itemMarketData.Value.Item.IsHQ, out var prices))
                {
                    finalMarketPrice = GetFinalMarketPrice(prices);
                }
            }

            var modifiedPrice = forcePrice > 0 ? forcePrice : GetModifiedPrice(itemConfig, finalMarketPrice);
            if (modifiedPrice == 0) return;

            if (IsAnyAbortConditionsMet(
                    itemConfig,
                    itemMarketData.Value.Price,
                    modifiedPrice,
                    finalMarketPrice,
                    out var abortCondition,
                    out var abortBehavior
                ))
            {
                NotifyAbortCondition(itemMarketData.Value.Item.ItemID, itemMarketData.Value.Item.IsHQ, abortCondition, finalMarketPrice);
                EnqueueAbortBehavior(abortBehavior);
                return;
            }

            if (modifiedPrice == itemMarketData.Value.Price) return;

            SetRetainerMarketItemPrice(slot, modifiedPrice);
            NotifyPriceAdjustSuccessfully(
                itemMarketData.Value.Item.ItemID,
                itemMarketData.Value.Item.IsHQ,
                itemMarketData.Value.Price,
                modifiedPrice
            );
            return;

            void EnqueueAbortBehavior(AbortBehavior behavior)
            {
                if (Module.config.SendPriceAdjustProcessMessage)
                {
                    var message = new SeStringBuilder()
                        .AddText(GetLoc("Prefix"))
                        .AddText(GetLoc("AutoRetainerWork-PriceAdjust-ConductAbortBehavior"))
                        .AddUiForeground(GetLoc(behavior), 67)
                        .Build();
                    NotifyHelper.Chat(message.Encode());
                }

                if (behavior == AbortBehavior.无) return;

                switch (behavior)
                {
                    case AbortBehavior.改价至最小值:
                        if (itemMarketData.Value.Price == (uint)itemConfig.PriceMinimum) break;
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceMinimum);
                        NotifyPriceAdjustSuccessfully(
                            itemMarketData.Value.Item.ItemID,
                            itemMarketData.Value.Item.IsHQ,
                            itemMarketData.Value.Price,
                            (uint)itemConfig.PriceMinimum
                        );
                        break;
                    case AbortBehavior.改价至预期值:
                        if (itemMarketData.Value.Price == (uint)itemConfig.PriceExpected) break;
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceExpected);
                        NotifyPriceAdjustSuccessfully(
                            itemMarketData.Value.Item.ItemID,
                            itemMarketData.Value.Item.IsHQ,
                            itemMarketData.Value.Price,
                            (uint)itemConfig.PriceExpected
                        );
                        break;
                    case AbortBehavior.改价至最高值:
                        if (itemMarketData.Value.Price == (uint)itemConfig.PriceMaximum) break;
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceMaximum);
                        NotifyPriceAdjustSuccessfully(
                            itemMarketData.Value.Item.ItemID,
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
                        taskHelper.Enqueue(
                            () =>
                            {
                                if (!TrySearchItemInInventory(itemMarketData.Value.Item.ItemID, itemMarketData.Value.Item.IsHQ, out var foundItems) ||
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

        private ItemConfig GetItemConfigByItemKey(ItemKey key)
        {
            if (Module.config.ItemConfigs.TryGetValue(key.ToString(), out var itemConfig))
                return itemConfig;

            var common = Module.config.ItemConfigs[new ItemKey(0, key.IsHQ).ToString()];
            return new ItemConfig
            {
                ItemID            = key.ItemID,
                IsHQ              = key.IsHQ,
                ItemName          = LuminaGetter.GetRow<Item>(key.ItemID)?.Name.ToString() ?? string.Empty,
                AbortLogic        = common.AbortLogic,
                AdjustBehavior    = common.AdjustBehavior,
                AdjustValues      = common.AdjustValues,
                PriceExpected     = common.PriceExpected,
                PriceMaximum      = common.PriceMaximum,
                PriceMaxReduction = common.PriceMaxReduction,
                PriceMinimum      = common.PriceMinimum,
                UpshelfCount      = common.UpshelfCount,
            };
        }

        #endregion

        #region 改价底层方法 (包含保底与防护)

        private bool ReturnRetainerMarketItemToInventory(ushort slot, bool isInventory)
        {
            if (!Module.retainerThrottler.Throttle("ReturnMarketItemToInventory", 100)) return false;

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

        private static bool SetRetainerMarketItemPrice(ushort slot, uint price)
        {
            if (slot >= 20) return false;

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            manager->SetRetainerMarketPrice((short)slot, price);
            RaptureAtkModule.Instance()->AgentUpdateFlag |= RaptureAtkModule.AgentUpdateFlags.RetainerMarketInventoryUpdate;
            return true;
        }

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

        private static uint GetRetainerMarketPrice(ushort slot)
        {
            if (slot >= 20) return 0;

            var manager = InventoryManager.Instance();
            if (manager == null) return 0;

            return (uint)manager->GetRetainerMarketPrice((short)slot);
        }

        private static void RequestMarketItemData(uint itemID, bool openOverlay = false)
        {
            var info = InfoProxyItemSearch.Instance();
            if (info == null) return;

            if (info->SearchItemId != itemID)
                SearchItemIPC?.TryInvokeFunc(itemID);
            if (openOverlay)
                ToggleOverlayIPC?.TryInvokeFunc(true);
        }

        private static bool IsMarketItemDataReady(uint itemID)
        {
            var proxy = InfoProxyItemSearch.Instance();
            if (proxy == null) return false;

            return proxy->IsFullyReceived(itemID);
        }

        private static bool IsAnyAbortConditionsMet(
            ItemConfig         config,
            uint               origPrice,
            uint               modifiedPrice,
            uint               marketPrice,
            out AbortCondition conditionMet,
            out AbortBehavior  behaviorNeeded)
        {
            conditionMet   = AbortCondition.无;
            behaviorNeeded = AbortBehavior.无;

            foreach (var condition in PriceCheckConditions.GetAll())
            {
                var hasBehavior = false;

                foreach (var logic in config.AbortLogic)
                {
                    if ((logic.Key & condition.Condition) != condition.Condition) continue;

                    behaviorNeeded = logic.Value;
                    hasBehavior    = true;
                    break;
                }

                if (!hasBehavior || !condition.Predicate(config, origPrice, modifiedPrice, marketPrice)) continue;

                conditionMet = condition.Condition;
                return true;
            }

            return false;
        }

        private static uint GetModifiedPrice(ItemConfig config, uint marketPrice)
        {
            if (marketPrice == 0) return 0;

            var calculatedPrice = config.AdjustBehavior switch
            {
                AdjustBehavior.固定值 => (long)marketPrice - config.AdjustValues[AdjustBehavior.固定值],
                AdjustBehavior.百分比 => (long)Math.Round(marketPrice * (1.0 - (config.AdjustValues[AdjustBehavior.百分比] / 100.0))),
                _                   => marketPrice
            };

            return (uint)Math.Clamp(calculatedPrice, 1, 999_999_999);
        }

        private void NotifyPriceAdjustSuccessfully(
            uint itemID,
            bool isHQ,
            uint origPrice,
            uint modifiedPrice)
        {
            if (!Module.config.SendPriceAdjustProcessMessage) return;

            var itemPayload = new SeStringBuilder().AddItemLink(itemID, isHQ).Build();
            var priceChangedValue = (long)modifiedPrice - origPrice;

            var priceChangeText = priceChangedValue.ToChineseString();
            if (!priceChangeText.StartsWith('-'))
                priceChangeText = $"+{priceChangeText}";

            var priceChangeRate = origPrice == 0 ? 0 : (double)priceChangedValue / origPrice * 100;
            var priceChangeRateText = priceChangeRate.ToString("+0.##;-0.##") + "%";

            var retainer = RetainerManager.Instance()->GetActiveRetainer();
            var retainerName = retainer != null ? retainer->NameString : string.Empty;

            var msg = new SeStringBuilder()
                .AddText(GetLoc("Prefix"))
                .Append(itemPayload)
                .AddText($" ({retainerName}) {origPrice.ToChineseString()} -> {modifiedPrice.ToChineseString()} ({priceChangeText} / {priceChangeRateText})")
                .Build();

            NotifyHelper.Chat(msg.Encode());
        }

        private void NotifyAbortCondition(
            uint           itemID,
            bool           isHQ,
            AbortCondition condition,
            uint           marketPrice)
        {
            if (!Module.config.SendPriceAdjustProcessMessage) return;

            var itemPayload = new SeStringBuilder().AddItemLink(itemID, isHQ).Build();
            var retainer = RetainerManager.Instance()->GetActiveRetainer();
            var retainerName = retainer != null ? retainer->NameString : string.Empty;

            var isCN = IClientState.Instance().ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
            var msg = new SeStringBuilder()
                .AddText(GetLoc("Prefix"))
                .Append(itemPayload)
                .AddText($" ({retainerName}) ")
                .AddUiForeground(GetLoc("AbortTriggered") + ": " + GetAbortConditionName(condition), 60);

            if (isCN)
                msg.AddText($" [当前市场最低价: {marketPrice.ToChineseString()}]");
            else
                msg.AddText($" [Current Market Min Price: {marketPrice.ToChineseString()}]");

            NotifyHelper.Chat(msg.Build().Encode());
        }

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

        #endregion

        #region 右键菜单与原生 Addon 挂载

        private class PriceAdjustContextMenuEntry(PriceAdjustWorker worker) : ContextMenuEntry
        {
            public override string Identifier => nameof(AutoRetainerWorkCustom);

            public override IReadOnlyList<ContextMenuItem>? CreateMultiple(ContextMenuOpenedArgs args)
            {
                if (args.AddonName != "RetainerSellList")
                    return null;

                var agent = AgentRetainer.Instance();
                if (agent->ContextMenuIndex   < 0  ||
                    agent->SellListEntryCount == 0 ||
                    agent->ContextMenuIndex   >= agent->SellListEntryCount)
                    return null;

                var manager = InventoryManager.Instance();
                var selectedSellListEntry = agent->SellListEntries[agent->ContextMenuIndex];
                var inventoryItem = manager->GetInventorySlot(
                    InventoryType.RetainerMarket,
                    selectedSellListEntry.InventorySlot
                );
                if (inventoryItem == null) return null;

                var itemID = inventoryItem->GetBaseItemId();
                if (!LuminaGetter.TryGetRow(itemID, out Item _))
                    return null;

                return
                [
                    new()
                    {
                        Name      = GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustPrice"),
                        OnClicked = _ => worker.EnqueuePriceAdjustSlot(inventoryItem->GetSlot())
                    },
                    new()
                    {
                        Name = GetLoc("AutoRetainerWork-PriceAdjust-ManualAdjustPrice-AllSame"),
                        OnClicked = _ =>
                        {
                            worker.isPriceAdjustAllSameItems = true;
                            AgentRetainer.Instance()->OpenRetainerSell(inventoryItem->GetInventoryType(), inventoryItem->GetSlot());
                        }
                    },
                    new()
                    {
                        Name = GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-AllSame"),
                        OnClicked = _ =>
                        {
                            if (TryGetSameItemSlots(itemID, out var slots))
                            {
                                foreach (var slot in slots)
                                    worker.EnqueuePriceAdjustSlot(slot);
                            }
                        }
                    }
                ];
            }
        }

        private class PriceAdjustAddon(PriceAdjustWorker worker) : AttachedAddon("RetainerSellList")
        {
            protected override bool CanOpenAddon =>
                ICondition.Instance()[ConditionFlag.OccupiedSummoningBell];

            public TextButtonNode? PriceAdjustButton         { get; private set; }
            public CheckboxNode?   AutoAdjustPriceCheckbox   { get; private set; }
            public CheckboxNode?   AutoOnSaleCheckbox        { get; private set; }
            public CheckboxNode?   NotifyPriceAdjustCheckbox { get; private set; }

            protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
            {
                var iconRow = new HorizontalListNode
                {
                    Alignment         = HorizontalListAnchor.Right,
                    Position          = ContentStartPosition + new Vector2(ContentSize.X - 1f, 0),
                    FitToContentWidth = true
                };
                iconRow.AttachNode(this);

                var settingsButton = new CircleButtonNode
                {
                    Icon        = CircleButtonIcon.GearCog,
                    Size        = new(28),
                    TextTooltip = GetLoc("Settings"),
                    OnClick     = () => ChatManager.Instance().SendCommand("/pdr search AutoRetainerWorkCustom")
                };
                iconRow.AddNode(settingsButton);

                var clearPriceCacheButton = new CircleButtonNode
                {
                    Icon        = CircleButtonIcon.Refresh,
                    TextTooltip = GetLoc("AutoRetainerWork-PriceAdjust-ClearCache"),
                    Size        = new(28),
                    OnClick = () =>
                    {
                        PriceCacheManager.ClearCache();
                        NotifyHelper.Toast(GetLoc("AutoRetainerWork-PriceAdjust-CacheCleared"));
                    }
                };
                iconRow.AddNode(clearPriceCacheButton);

                var rootContainer = new VerticalListNode
                {
                    ItemSpacing      = 5f,
                    FirstItemSpacing = 30f,
                    Position         = ContentStartPosition,
                    Width            = ContentSize.X,
                    FitContents      = true
                };
                rootContainer.AttachNode(this);

                PriceAdjustButton = new()
                {
                    String      = GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-Batch"),
                    Size        = new(rootContainer.Width, 36),
                    TextureType = ButtonTextureType.ButtonB,
                    OnClick = () =>
                    {
                        if (worker.taskHelper != null && worker.taskHelper.IsBusy)
                            worker.taskHelper.Abort();
                        else
                            worker.EnqueuePriceAdjustRetainer();
                    }
                };
                rootContainer.AddNode(PriceAdjustButton);

                var returnToInventory = new TextButtonNode
                {
                    String = GetLoc("AutoRetainerWork-PriceAdjust-ReturnAllToInventory"),
                    Size   = new(rootContainer.Width, 28),
                    OnClick = () =>
                    {
                        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
                        for (var i = 0; i < container->Size; i++)
                        {
                            var index = i;
                            worker.taskHelper.Enqueue(
                                () => worker.ReturnRetainerMarketItemToInventory((ushort)index, true),
                                $"将市场中的第{index}栏物品收回至自己"
                            );
                        }
                    }
                };
                rootContainer.AddNode(returnToInventory);

                var returnToRetainer = new TextButtonNode
                {
                    String = GetLoc("AutoRetainerWork-PriceAdjust-ReturnAllToRetainer"),
                    Size   = new(rootContainer.Width, 28),
                    OnClick = () =>
                    {
                        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
                        for (var i = 0; i < container->Size; i++)
                        {
                            var index = i;
                            worker.taskHelper.Enqueue(
                                () => worker.ReturnRetainerMarketItemToInventory((ushort)index, false),
                                $"将市场中的第{index}栏物品收回至雇员"
                            );
                        }
                    }
                };
                rootContainer.AddNode(returnToRetainer);

                rootContainer.AddDummy(2f);

                AutoAdjustPriceCheckbox = new()
                {
                    String      = GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale"),
                    TextTooltip = GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale-Help"),
                    Size        = new(rootContainer.Width, 28),
                    IsChecked   = worker.Module.config.AutoPriceAdjustWhenNewOnSale,
                    OnClick = value =>
                    {
                        worker.Module.config.AutoPriceAdjustWhenNewOnSale = value;
                        worker.Module.SaveConfig(worker.Module.config);
                    }
                };
                rootContainer.AddNode(AutoAdjustPriceCheckbox);

                AutoOnSaleCheckbox = new()
                {
                    String      = GetLoc("AutoRetainerWork-PriceAdjust-AutoOnSale"),
                    TextTooltip = GetLoc("AutoRetainerWork-PriceAdjust-AutoOnSale-Help"),
                    Size        = new(rootContainer.Width, 28),
                    IsChecked   = worker.Module.config.AutoOnSale,
                    OnClick = value =>
                    {
                        worker.Module.config.AutoOnSale = value;
                        worker.Module.SaveConfig(worker.Module.config);
                    }
                };
                rootContainer.AddNode(AutoOnSaleCheckbox);

                NotifyPriceAdjustCheckbox = new()
                {
                    String    = GetLoc("AutoRetainerWork-PriceAdjust-SendProcessMessage"),
                    Size      = new(rootContainer.Width, 28),
                    IsChecked = worker.Module.config.SendPriceAdjustProcessMessage,
                    OnClick = value =>
                    {
                        worker.Module.config.SendPriceAdjustProcessMessage = value;
                        worker.Module.SaveConfig(worker.Module.config);
                    }
                };
                rootContainer.AddNode(NotifyPriceAdjustCheckbox);

                rootContainer.RecalculateLayout();
                SetWindowSize(Size.X, ContentStartPosition.Y + rootContainer.Height + 20f);
                rootContainer.Position = ContentStartPosition;
            }

            protected override void OnAttachedAddonUpdate(AtkUnitBase* addon, AtkUnitBase* hostAddon)
            {
                if (PriceAdjustButton != null)
                {
                    PriceAdjustButton.String = (worker.taskHelper?.IsBusy ?? false) ?
                                                   GetLoc("Stop") :
                                                   GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-Batch");
                }
            }
        }

        #endregion

        #region 高性能价格缓存管理 (PriceCacheManager)

        public static class PriceCacheManager
        {
            private const int CACHE_EXPIRATION_MINUTES = 10;
            private static readonly PriceCache CurrentPriceCache = new();
            private static readonly PriceCache HistoryPriceCache = new();
            private static readonly List<uint> EmptyPrices       = [];

            public static void UpdateCache<T>(
                AutoRetainerWorkCustom module,
                PriceCache             cache,
                uint                   itemID,
                IEnumerable<T>         listings,
                Func<T, bool>          isHQSelector,
                Func<T, bool>          onMannequinSelector,
                Func<T, uint>          priceSelector,
                Func<T, ulong>?        retainerSelector = null)
            {
                var filteredListings = listings
                                       .Where(x => !onMannequinSelector(x))
                                       .ToLookup(isHQSelector);

                foreach (var isHQ in new[] { false, true })
                {
                    var items = filteredListings[isHQ];
                    if (retainerSelector != null)
                        items = items.Where(x => !module.playerRetainers.Contains(retainerSelector(x)));

                    var enumerable = items as T[] ?? [.. items];
                    if (enumerable.Length == 0) continue;

                    var sortedPrices = enumerable.Select(priceSelector).Where(p => p > 0).OrderBy(p => p).Take(5).ToList();
                    if (sortedPrices.Count == 0) continue;

                    var cacheKey = CacheKeys.Create(itemID, isHQ);
                    if (!cache.TryGetPrice(cacheKey, out var currentPrice) || sortedPrices[0] <= currentPrice)
                        cache.SetPrices(cacheKey, sortedPrices);
                }
            }

            public static void UpdateHistoryCache<T>(
                PriceCache     cache,
                uint           itemID,
                IEnumerable<T> listings,
                Func<T, bool>  isHQSelector,
                Func<T, bool>  onMannequinSelector,
                Func<T, uint>  priceSelector)
            {
                var filteredListings = listings
                                       .Where(x => !onMannequinSelector(x))
                                       .ToLookup(isHQSelector);

                foreach (var isHQ in new[] { false, true })
                {
                    var items      = filteredListings[isHQ];
                    var enumerable = items as T[] ?? [.. items];
                    if (enumerable.Length == 0) continue;

                    var sortedPrices = enumerable.Select(priceSelector).Where(p => p > 0).OrderBy(p => p).Take(5).ToList();
                    if (sortedPrices.Count == 0) continue;

                    var cacheKey = CacheKeys.Create(itemID, isHQ);
                    if (!cache.TryGetPrice(cacheKey, out var currentPrice) || sortedPrices[0] <= currentPrice)
                        cache.SetPrices(cacheKey, sortedPrices);
                }
            }

            public static void OnOfferingReceived(AutoRetainerWorkCustom module, IMarketBoardCurrentOfferings data)
            {
                if (!data.ItemListings.Any()) return;
                UpdateCache(
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
                UpdateHistoryCache(
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

                CurrentPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));
                HistoryPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));

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

                CurrentPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));
                HistoryPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));

                if (CurrentPriceCache.TryGetPrices(cacheKey, out prices) && prices.Count > 0)
                    return true;
                if (CurrentPriceCache.TryGetPrices(oppositeCacheKey, out prices) && prices.Count > 0)
                    return true;
                if (HistoryPriceCache.TryGetPrices(cacheKey, out prices) && prices.Count > 0)
                    return true;
                if (HistoryPriceCache.TryGetPrices(oppositeCacheKey, out prices) && prices.Count > 0)
                    return true;

                return false;
            }

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
                prices = [];
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

        #endregion

        #region 常量

        private static readonly string[] SellInventoryItemsText =
        [
            "玩家所持物品",
            "Sell items in your inventory",
            "プレイヤー所持品から",
            "플레이어 소지품에서 선택",
            "Gegenstände aus dem eigenen Inventar verkaufen",
            "Mettre en vente un objet de votre inventaire"
        ];

        #endregion
    }

    #endregion

    #region 3. 存放相同道具 (EntrustDupsWorker)

    private class EntrustDupsWorker(AutoRetainerWorkCustom module) : RetainerWorkerBase(module)
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

        public override CollaspingCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory(
                GetLoc("AutoRetainerWork-EntrustDups-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersEntrustDups, () => taskHelper?.Abort(), width)
            );

        private void EnqueueRetainersEntrustDups()
        {
            if (taskHelper == null || taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(EntrustDupsWorker))) return;

            var count = GetValidRetainerCount(_ => true, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach(index =>
            {
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return Module.EnterRetainer(index);
                    },
                    $"选择进入 {index} 号雇员"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return AddonSelectStringEvent.Select(EntrustItemsText);
                    },
                    "选择进入道具管理"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        if (!InventoryRetainer->IsAddonAndNodesReady()) return false;

                        InventoryRetainer->Callback(5);
                        return true;
                    },
                    "存放相同道具"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return LeaveRetainer();
                    },
                    "回到雇员列表"
                );
            });
        }

        private static readonly string[] EntrustItemsText =
        [
            "道具管理",
            "Entrust or withdraw items",
            "アイテムの受け渡し",
            "아이템 주고받기",
            "Gegenstände geben oder nehmen",
            "Confier ou récupérer des objets"
        ];
    }

    #endregion

    #region 4. 提取金币 (GilsWithdrawWorker)

    private class GilsWithdrawWorker(AutoRetainerWorkCustom module) : RetainerWorkerBase(module)
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

        public override CollaspingCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory(
                GetLoc("AutoRetainerWork-GilsWithdraw-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersGilWithdraw, () => taskHelper?.Abort(), width)
            );

        private void EnqueueRetainersGilWithdraw()
        {
            if (taskHelper == null || taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(GilsWithdrawWorker))) return;

            var count = GetValidRetainerCount(x => x.Gil > 0, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach(index =>
            {
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return Module.EnterRetainer(index);
                    },
                    $"选择进入 {index} 号雇员"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return AddonSelectStringEvent.Select(GilManageTexts);
                    },
                    "选择进入金币管理"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
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
                    "取出所有的金币"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return LeaveRetainer();
                    },
                    "回到雇员列表"
                );
            });
        }

        private static readonly string[] GilManageTexts =
        [
            "金币管理",
            "Gil管理",
            "Entrust or withdraw gil",
            "ギルの受け渡し",
            "길 주고받기",
            "Gil geben oder nehmen",
            "Confier ou récupérer de l'argent"
        ];
    }

    #endregion

    #region 5. 平分金币 (GilsShareWorker)

    private class GilsShareWorker(AutoRetainerWorkCustom module) : RetainerWorkerBase(module)
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

        public override CollaspingCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory(
                GetLoc("AutoRetainerWork-GilsShare-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersGilsShare, () => taskHelper?.Abort(), width)
            );

        private void EnqueueRetainersGilsShare()
        {
            if (taskHelper == null || taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(GilsShareWorker))) return;

            var manager = RetainerManager.Instance();
            if (manager == null || manager->GetRetainerCount() == 0) return;

            var playerGil = InventoryManager.Instance()->GetGil();
            if (playerGil >= MAX_PLAYER_GIL)
            {
                NotifyHelper.Instance().NotificationError(
                    GetLoc("AutoRetainerWork-GilsShare-PlayerGilFull"),
                    Module.Info.Title
                );
                return;
            }

            var retainers = new List<(uint Index, uint Gil)>();
            ulong totalGil = playerGil;

            for (var i = 0U; i < manager->GetRetainerCount(); i++)
            {
                var retainer = manager->GetRetainerBySortedIndex(i);
                if (retainer == null || retainer->RetainerId == 0) continue;

                retainers.Add((i, retainer->Gil));
                totalGil += retainer->Gil;
            }

            if (retainers.Count == 0) return;

            var targetPerRetainer = (uint)(totalGil / (ulong)retainers.Count);
            if (targetPerRetainer > MAX_PLAYER_GIL)
            {
                NotifyHelper.Instance().NotificationError(
                    GetLoc("AutoRetainerWork-GilsShare-NoNeedToShare"),
                    Module.Info.Title
                );
                return;
            }

            var richRetainers = new List<(uint Index, uint Excess)>();
            var poorRetainers = new List<(uint Index, uint Deficit)>();

            foreach (var (index, gil) in retainers)
            {
                if (gil > targetPerRetainer)
                    richRetainers.Add((index, gil - targetPerRetainer));
                else if (gil < targetPerRetainer)
                    poorRetainers.Add((index, targetPerRetainer - gil));
            }

            if (richRetainers.Count == 0 && poorRetainers.Count == 0)
            {
                NotifyHelper.Instance().NotificationWarning(
                    GetLoc("AutoRetainerWork-GilsShare-NoNeedToShare"),
                    Module.Info.Title
                );
                return;
            }

            var operations = new List<(uint Index, uint Amount, bool IsWithdraw)>();
            var richIdx = 0;
            var poorIdx = 0;

            var pendingExcess  = richRetainers.Count > 0 ? richRetainers[0].Excess : 0U;
            var pendingDeficit = poorRetainers.Count > 0 ? poorRetainers[0].Deficit : 0U;

            while (richIdx < richRetainers.Count || poorIdx < poorRetainers.Count)
            {
                var madeProgress = false;

                if (poorIdx < poorRetainers.Count && playerGil > 0 && pendingDeficit > 0)
                {
                    var amount = Math.Min(playerGil, pendingDeficit);
                    operations.Add((poorRetainers[poorIdx].Index, amount, false));
                    playerGil      -= amount;
                    pendingDeficit -= amount;
                    madeProgress   =  true;

                    if (pendingDeficit == 0)
                    {
                        poorIdx++;
                        if (poorIdx < poorRetainers.Count)
                            pendingDeficit = poorRetainers[poorIdx].Deficit;
                    }
                }

                if (richIdx < richRetainers.Count && pendingExcess > 0)
                {
                    var maxCanHold = MAX_PLAYER_GIL - playerGil;
                    if (maxCanHold > 0)
                    {
                        var amount = Math.Min(pendingExcess, maxCanHold);
                        operations.Add((richRetainers[richIdx].Index, amount, true));
                        playerGil     += amount;
                        pendingExcess -= amount;
                        madeProgress  =  true;

                        if (pendingExcess == 0)
                        {
                            richIdx++;
                            if (richIdx < richRetainers.Count)
                                pendingExcess = richRetainers[richIdx].Excess;
                        }
                    }
                }

                if (!madeProgress) break;
            }

            foreach (var (index, amount, isWithdraw) in operations)
                EnqueueRetainerGilOperation(index, amount, isWithdraw);

            taskHelper.Enqueue(
                () =>
                {
                    NotifyHelper.Instance().NotificationSuccess(
                        GetLoc("AutoRetainerWork-GilsShare-Complete"),
                        Module.Info.Title
                    );
                    return true;
                },
                "发送完成通知"
            );
        }

        private void EnqueueRetainerGilOperation(uint index, uint amount, bool isWithdraw)
        {
            taskHelper.Enqueue(
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    return Module.EnterRetainer(index);
                },
                $"选择进入 {index} 号雇员"
            );
            taskHelper.Enqueue(
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    return AddonSelectStringEvent.Select(GilManageTexts);
                },
                "选择进入金币管理"
            );
            taskHelper.Enqueue(
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    if (!Bank->IsAddonAndNodesReady()) return false;

                    if (!isWithdraw)
                        AddonBankEvent.SwitchMode();

                    AddonBankEvent.SetNumber(amount);
                    AddonBankEvent.ClickConfirm();
                    Bank->Close(true);
                    return true;
                },
                $"{(isWithdraw ? "取出" : "存入")} {amount} 金币 ({index} 号雇员)"
            );
            taskHelper.Enqueue(
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    return LeaveRetainer();
                },
                "回到雇员列表"
            );
        }

        private const uint MAX_PLAYER_GIL = 999_999_999U;

        private static readonly string[] GilManageTexts =
        [
            "金币管理",
            "Gil管理",
            "Entrust or withdraw gil",
            "ギルの受け渡し",
            "길 주고받기",
            "Gil geben oder nehmen",
            "Confier ou récupérer de l'argent"
        ];
    }

    #endregion

    #region 6. 刷新雇员信息 (RefreshWorker)

    private class RefreshWorker(AutoRetainerWorkCustom module) : RetainerWorkerBase(module)
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

        public override CollaspingCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory(
                GetLoc("AutoRetainerWork-Refresh-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersRefresh, () => taskHelper?.Abort(), width)
            );

        private void EnqueueRetainersRefresh()
        {
            if (taskHelper == null || taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(RefreshWorker))) return;

            var count = GetValidRetainerCount(_ => true, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach(index =>
            {
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return Module.EnterRetainer(index);
                    },
                    $"选择进入 {index} 号雇员"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return LeaveRetainer();
                    },
                    "回到雇员列表"
                );
            });
        }
    }

    #endregion

    #region 7. 城镇派遣 (TownDispatchWorker)

    private class TownDispatchWorker(AutoRetainerWorkCustom module) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

        public override bool DrawConfigCondition() => true;
        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init() => taskHelper ??= new() { TimeoutMS = 15_000 };

        public override void Uninit()
        {
            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override void DrawConfig()
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("AutoRetainerWork-Dispatch-Title"));

            using var indent = ImRaii.PushIndent();

            if (ImGui.Button(GetLoc("Start")))
                EnqueueRetainersDispatch();

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Stop")))
                taskHelper?.Abort();
        }

        private void EnqueueRetainersDispatch()
        {
            if (taskHelper == null || taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(TownDispatchWorker))) return;

            var addon = (AddonSelectString*)SelectString;
            if (addon == null) return;

            var entryCount = addon->PopupMenu.PopupMenu.EntryCount;
            if (entryCount - 1 <= 0) return;

            for (var i = 0; i < entryCount - 1; i++)
            {
                var tempI = i;
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return AddonSelectStringEvent.Select(tempI);
                    },
                    $"点击第 {tempI} 位雇员, 拉起市场变更请求"
                );
                taskHelper.Enqueue(
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return AddonSelectYesnoEvent.ClickYes();
                    },
                    "确认市场变更"
                );
            }
        }
    }

    #endregion

    #region 数据结构与配置类

    public enum AdjustBehavior
    {
        固定值,
        百分比
    }

    [Flags]
    public enum AbortCondition
    {
        无        = 1,
        低于最小值    = 2,
        低于预期值    = 4,
        低于收购价    = 8,
        大于可接受降价值 = 16,
        高于预期值    = 32,
        高于最大值    = 64
    }

    public enum AbortBehavior
    {
        无,
        收回至雇员,
        收回至背包,
        出售至系统商店,
        改价至最小值,
        改价至预期值,
        改价至最高值
    }

    public enum SortOrder
    {
        上架顺序,
        物品ID,
        物品类型
    }

    private class PriceCheckCondition(
        AbortCondition                           condition,
        Func<ItemConfig, uint, uint, uint, bool> predicate)
    {
        public AbortCondition                           Condition { get; } = condition;
        public Func<ItemConfig, uint, uint, uint, bool> Predicate { get; } = predicate;
    }

    private static class PriceCheckConditions
    {
        private static readonly PriceCheckCondition[] Conditions =
        [
            new(
                AbortCondition.高于最大值,
                (cfg, _, modified, _) =>
                    modified > cfg.PriceMaximum
            ),
            new(
                AbortCondition.高于预期值,
                (cfg, _, modified, _) =>
                    modified > cfg.PriceExpected
            ),
            new(
                AbortCondition.大于可接受降价值,
                (cfg, orig, modified, _) =>
                    cfg.PriceMaxReduction != 0         &&
                    orig                  != 999999999 &&
                    orig - modified       > 0          &&
                    orig - modified       > cfg.PriceMaxReduction
            ),
            new(
                AbortCondition.低于收购价,
                (cfg, _, modified, _) =>
                    LuminaGetter.TryGetRow<Item>(cfg.ItemID, out var itemRow) &&
                    modified <= itemRow.PriceMid
            ),
            new(
                AbortCondition.低于最小值,
                (cfg, _, modified, _) =>
                    modified < cfg.PriceMinimum
            ),
            new(
                AbortCondition.低于预期值,
                (cfg, _, modified, _) =>
                    modified < cfg.PriceExpected
            )
        ];

        public static IEnumerable<PriceCheckCondition> GetAll() => Conditions;

        public static PriceCheckCondition? Get(AbortCondition condition) =>
            Conditions.FirstOrDefault(x => x.Condition == condition);
    }

    public class Config : ModuleConfig
    {
        public bool AutoPriceAdjustWhenNewOnSale = true;
        public bool AutoOnSale = true;
        public bool AutoRetainerCollect = true;
        public bool AutoPriceAdjustAfterCollect;

        public Dictionary<string, ItemConfig> ItemConfigs = new()
        {
            { new ItemKey(0, false).ToString(), new ItemConfig(0, false) },
            { new ItemKey(0, true).ToString(), new ItemConfig(0,  true) }
        };

        public SortOrder MarketItemsSortOrder       = SortOrder.上架顺序;
        public float     MarketItemsWindowFontScale = 0.8f;
        public bool      SendPriceAdjustProcessMessage = true;
    }

    public class ItemKey : IEquatable<ItemKey>
    {
        public ItemKey() { }
        public ItemKey(uint itemID, bool isHQ)
        {
            ItemID = itemID;
            IsHQ   = isHQ;
        }

        public uint ItemID { get; set; }
        public bool IsHQ   { get; set; }

        public bool Equals(ItemKey? other)
        {
            if (other is null || GetType() != other.GetType()) return false;
            return ItemID == other.ItemID && IsHQ == other.IsHQ;
        }

        public override string ToString() => $"{ItemID}_{(IsHQ ? "HQ" : "NQ")}";
        public override bool Equals(object? obj) => Equals(obj as ItemKey);
        public override int GetHashCode() => HashCode.Combine(ItemID, IsHQ);

        public static bool operator ==(ItemKey? lhs, ItemKey? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(ItemKey? lhs, ItemKey? rhs) => !(lhs == rhs);
    }

    public class ItemConfig : IEquatable<ItemConfig>
    {
        public ItemConfig() { }
        public ItemConfig(uint itemID, bool isHQ)
        {
            ItemID   = itemID;
            IsHQ     = isHQ;
            ItemName = itemID == 0 ?
                           GetLoc("AutoRetainerWork-PriceAdjust-CommonItemPreset") :
                           LuminaGetter.GetRow<Item>(ItemID)?.Name.ToString() ?? string.Empty;
        }

        public uint   ItemID   { get; set; }
        public bool   IsHQ     { get; set; }
        public string ItemName { get; set; } = string.Empty;

        public AdjustBehavior AdjustBehavior { get; set; } = AdjustBehavior.固定值;
        public Dictionary<AdjustBehavior, int> AdjustValues { get; set; } = new()
        {
            { AdjustBehavior.固定值, 1 },
            { AdjustBehavior.百分比, 10 }
        };

        public int PriceMinimum { get; set; } = 100;
        public int PriceMaximum { get; set; } = 100000000;
        public int PriceExpected { get; set; } = 200;
        public int PriceMaxReduction { get; set; }
        public int UpshelfCount { get; set; }

        public Dictionary<AbortCondition, AbortBehavior> AbortLogic { get; set; } = [];

        public bool Equals(ItemConfig? other)
        {
            if (other is null || GetType() != other.GetType()) return false;
            return ItemID == other.ItemID && IsHQ == other.IsHQ;
        }

        public override bool Equals(object? obj) => Equals(obj as ItemConfig);
        public override int GetHashCode() => HashCode.Combine(ItemID, IsHQ);

        public static bool operator ==(ItemConfig? lhs, ItemConfig? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(ItemConfig? lhs, ItemConfig? rhs) => !(lhs == rhs);
    }

    #endregion

    #region 多语言自包含文本与常量字典

    private static string GetLoc(string key)
    {
        var isCN = IClientState.Instance().ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
        return (key, isCN) switch
        {
            ("Start", true) => "开始",
            ("Start", false) => "Start",
            ("Stop", true) => "停止",
            ("Stop", false) => "Stop",
            ("Add", true) => "添加",
            ("Add", false) => "Add",
            ("Delete", true) => "删除",
            ("Delete", false) => "Delete",
            ("Item", true) => "物品",
            ("Item", false) => "Item",
            ("Confirm", true) => "确认",
            ("Confirm", false) => "Confirm",
            ("Settings", true) => "设置",
            ("Settings", false) => "Settings",
            ("PleaseSearch", true) => "搜索预设...",
            ("PleaseSearch", false) => "Search preset...",
            ("Unknown", true) => "未知物品",
            ("Unknown", false) => "Unknown Item",
            ("ExportToClipboard", true) => "导出至剪贴板",
            ("ExportToClipboard", false) => "Export to Clipboard",
            ("ImportFromClipboard", true) => "从剪贴板导入",
            ("ImportFromClipboard", false) => "Import from Clipboard",

            ("AutoRetainerWork-Collect-Title", true) => "自动收取雇员",
            ("AutoRetainerWork-Collect-Title", false) => "Auto Collect Retainer Exploration",
            ("AutoRetainerWork-Collect-AutoCollect", true) => "打开界面后自动收取",
            ("AutoRetainerWork-Collect-AutoCollect", false) => "Auto Collect Exploration",
            ("AutoRetainerWork-Collect-AutoPriceAdjustAfterCollect", true) => "收取后自动改价",
            ("AutoRetainerWork-Collect-AutoPriceAdjustAfterCollect", false) => "Auto Adjust Price After Collecting Exploration",

            ("AutoRetainerWork-EntrustDups-Title", true) => "自动道具合并递交",
            ("AutoRetainerWork-EntrustDups-Title", false) => "Auto Entrust Duplicates",

            ("AutoRetainerWork-GilsShare-Title", true) => "自动平均雇员金币",
            ("AutoRetainerWork-GilsShare-Title", false) => "Auto Half Gils",
            ("AutoRetainerWork-GilsShare-PlayerGilFull", true) => "玩家金币已满, 无法进行平分",
            ("AutoRetainerWork-GilsShare-PlayerGilFull", false) => "It's unable to half the gils since the gil cap has been reached.",
            ("AutoRetainerWork-GilsShare-NoNeedToShare", true) => "当前雇员金币无需平分",
            ("AutoRetainerWork-GilsShare-NoNeedToShare", false) => "No need to half the retainer's gils.",
            ("AutoRetainerWork-GilsShare-Complete", true) => "雇员金币平分已完成",
            ("AutoRetainerWork-GilsShare-Complete", false) => "Gils held by the retainer has been halved.",

            ("AutoRetainerWork-GilsWithdraw-Title", true) => "自动取出雇员金币",
            ("AutoRetainerWork-GilsWithdraw-Title", false) => "Auto Retrieve Gils",

            ("AutoRetainerWork-Refresh-Title", true) => "自动刷新雇员状态",
            ("AutoRetainerWork-Refresh-Title", false) => "Refresh Retainers Data",

            ("AutoRetainerWork-Dispatch-Title", true) => "自动变更雇员登记市场",
            ("AutoRetainerWork-Dispatch-Title", false) => "Auto Dispatch Retainer",

            ("AutoRetainerWork-PriceAdjust-Title", true) => "自动雇员改价",
            ("AutoRetainerWork-PriceAdjust-Title", false) => "Price Adjustment",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustPrice", true) => "自动修改价格",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustPrice", false) => "Auto Adjust Price",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-Batch", true) => "批量修改价格",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-Batch", false) => "Batch Adjust",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-AllRetainers", true) => "改价（全部雇员）",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-AllRetainers", false) => "Adjust (All Retainers)",
            ("AutoRetainerWork-PriceAdjust-ManualAdjustPrice-AllSame", true) => "修改同类物品价格",
            ("AutoRetainerWork-PriceAdjust-ManualAdjustPrice-AllSame", false) => "Adjust (Same Items)",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-AllSame", true) => "自动修改同类物品价格",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-AllSame", false) => "Auto Adjust Price (Same Items)",
            ("AutoRetainerWork-PriceAdjust-ReturnAllToInventory", true) => "全部收回给自己",
            ("AutoRetainerWork-PriceAdjust-ReturnAllToInventory", false) => "Retrieve To Inventory",
            ("AutoRetainerWork-PriceAdjust-ReturnAllToRetainer", true) => "全部收回给雇员",
            ("AutoRetainerWork-PriceAdjust-ReturnAllToRetainer", false) => "Retrieve To Retainer",
            ("AutoRetainerWork-PriceAdjust-ClearCache", true) => "清除价格缓存",
            ("AutoRetainerWork-PriceAdjust-ClearCache", false) => "Clear Price Cache",
            ("AutoRetainerWork-PriceAdjust-CacheCleared", true) => "清除了价格缓存。",
            ("AutoRetainerWork-PriceAdjust-CacheCleared", false) => "Price data cache has been cleared.",
            ("AutoRetainerWork-PriceAdjust-SendProcessMessage", true) => "通知自动改价详情",
            ("AutoRetainerWork-PriceAdjust-SendProcessMessage", false) => "Send adjustment process message to chat",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale", true) => "自动指定出售价格",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale", false) => "Auto Specify Listing Price",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale-Help", true) => "上架商品时，仅可在“价格调整”界面内调整上架数量，点击“确认”后，将根据配置与市场时价自动指定出售价格。",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale-Help", false) => "When listing an item, you can only adjust the quantity in the Price Adjustment window. Clicking 'Confirm' will automatically determine the price based on configs and market price.",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale-Warning", true) => "无法手动调整价格，将根据配置与市场时价自动确定出售价格。",
            ("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale-Warning", false) => "The price cannot be adjusted manually and will be determined based on configs and data.",
            ("AutoRetainerWork-PriceAdjust-AutoOnSale", true) => "自动确认上架",
            ("AutoRetainerWork-PriceAdjust-AutoOnSale", false) => "Auto Confirm Listing",
            ("AutoRetainerWork-PriceAdjust-AutoOnSale-Help", true) => "上架商品时，将自动点击“价格调整”界面内的“确认”按钮。",
            ("AutoRetainerWork-PriceAdjust-AutoOnSale-Help", false) => "Automatically clicks 'Confirm' in the Price Adjustment window when listing an item.",
            ("AutoRetainerWork-PriceAdjust-CommonItemPreset", true) => "通用物品配置",
            ("AutoRetainerWork-PriceAdjust-CommonItemPreset", false) => "General Item Preset",
            ("AutoRetainerWork-PriceAdjust-CreateNewBaseOnExisted", true) => "以此为基础创建新配置",
            ("AutoRetainerWork-PriceAdjust-CreateNewBaseOnExisted", false) => "Create new preset based on this",
            ("AutoRetainerWork-PriceAdjust-Behavior", true) => "改价行为",
            ("AutoRetainerWork-PriceAdjust-Behavior", false) => "Adjustment Behavior",
            ("AutoRetainerWork-PriceAdjust-ValueReduction", true) => "单次降价值",
            ("AutoRetainerWork-PriceAdjust-ValueReduction", false) => "Fixed reduction value",
            ("AutoRetainerWork-PriceAdjust-PercentageReduction", true) => "单次降价幅度",
            ("AutoRetainerWork-PriceAdjust-PercentageReduction", false) => "Percentage reduction (%)",
            ("AutoRetainerWork-PriceAdjust-PriceMinimum", true) => "最低可接受价格",
            ("AutoRetainerWork-PriceAdjust-PriceMinimum", false) => "Minimum acceptable price",
            ("AutoRetainerWork-PriceAdjust-PriceMaximum", true) => "最高可接受价格",
            ("AutoRetainerWork-PriceAdjust-PriceMaximum", false) => "Maximum acceptable price",
            ("AutoRetainerWork-PriceAdjust-PriceExpected", true) => "预期价格",
            ("AutoRetainerWork-PriceAdjust-PriceExpected", false) => "Expected price",
            ("AutoRetainerWork-PriceAdjust-PriceMaxReduction", true) => "可接受降价值",
            ("AutoRetainerWork-PriceAdjust-PriceMaxReduction", false) => "Maximum acceptable reduction",
            ("AutoRetainerWork-PriceAdjust-UpshelfCount", true) => "单次上架数",
            ("AutoRetainerWork-PriceAdjust-UpshelfCount", false) => "Single upshelf quantity",
            ("AutoRetainerWork-PriceAdjust-ObtainBuyingPrice", true) => "获取收购价格",
            ("AutoRetainerWork-PriceAdjust-ObtainBuyingPrice", false) => "Get NPC shop price",
            ("AutoRetainerWork-PriceAdjust-OpenUniversalis", true) => "打开 Universalis",
            ("AutoRetainerWork-PriceAdjust-OpenUniversalis", false) => "View on Universalis",

            ("AutoRetainerWork-PriceAdjust-ConductAbortBehavior", true) => "执行设定逻辑: ",
            ("AutoRetainerWork-PriceAdjust-ConductAbortBehavior", false) => "Execute logic: ",

            ("Prefix", true) => "[自动雇员作业] ",
            ("Prefix", false) => "[AutoRetainerWork] ",
            ("AbortTriggered", true) => "触发中断条件",
            ("AbortTriggered", false) => "Abort Condition Triggered",

            _ => key
        };
    }

    private static string GetLoc(AdjustBehavior behavior)
    {
        var isCN = IClientState.Instance().ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
        return (behavior, isCN) switch
        {
            (AdjustBehavior.固定值, true) => "固定值",
            (AdjustBehavior.固定值, false) => "Fixed",
            (AdjustBehavior.百分比, true) => "百分比",
            (AdjustBehavior.百分比, false) => "Percentage",
            _ => behavior.ToString()
        };
    }

    private static string GetLoc(AbortCondition condition)
    {
        var isCN = IClientState.Instance().ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
        return (condition, isCN) switch
        {
            (AbortCondition.无, true) => "无",
            (AbortCondition.无, false) => "None",
            (AbortCondition.低于最小值, true) => "低于最小值",
            (AbortCondition.低于最小值, false) => "Below Minimum",
            (AbortCondition.低于预期值, true) => "低于预期值",
            (AbortCondition.低于预期值, false) => "Below Expected",
            (AbortCondition.低于收购价, true) => "低于收购价",
            (AbortCondition.低于收购价, false) => "Below Buying Price",
            (AbortCondition.大于可接受降价值, true) => "大于可接受降价值",
            (AbortCondition.大于可接受降价值, false) => "Exceeds Max Concession",
            (AbortCondition.高于预期值, true) => "高于预期值",
            (AbortCondition.高于预期值, false) => "Above Expected",
            (AbortCondition.高于最大值, true) => "高于最大值",
            (AbortCondition.高于最大值, false) => "Above Maximum",
            _ => condition.ToString()
        };
    }

    private static string GetLoc(AbortBehavior behavior)
    {
        var isCN = IClientState.Instance().ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
        return (behavior, isCN) switch
        {
            (AbortBehavior.无, true) => "无 (中断改价)",
            (AbortBehavior.无, false) => "None (Abort)",
            (AbortBehavior.改价至最小值, true) => "改价至最小值",
            (AbortBehavior.改价至最小值, false) => "Adjust To Minimum",
            (AbortBehavior.改价至预期值, true) => "改价至预期值",
            (AbortBehavior.改价至预期值, false) => "Adjust To Expected",
            (AbortBehavior.改价至最高值, true) => "改价至最高值",
            (AbortBehavior.改价至最高值, false) => "Adjust To Maximum",
            (AbortBehavior.收回至雇员, true) => "收回至雇员背包",
            (AbortBehavior.收回至雇员, false) => "Retrieve To Retainer",
            (AbortBehavior.收回至背包, true) => "收回至玩家背包",
            (AbortBehavior.收回至背包, false) => "Retrieve To Inventory",
            (AbortBehavior.出售至系统商店, true) => "出售至系统商店",
            (AbortBehavior.出售至系统商店, false) => "Sell To NPC Vendor",
            _ => behavior.ToString()
        };
    }

    private static readonly AbortCondition[] AbortConditions = Enum.GetValues<AbortCondition>();

    #endregion
}
