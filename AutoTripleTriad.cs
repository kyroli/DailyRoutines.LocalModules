using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools;
using OmenTools.Dalamud;
using OmenTools.Extensions;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoTripleTriad : ModuleBase
{
        public class Config : ModuleConfig
        {
            public bool EnableTripleTriad = false;
            public bool PlayUntilAllUnownedCardsDrop = false;
            public bool PlayXTimes = false;
            public int TimesToPlay = 1;
            public bool UseRecommendedDeck = true;
            public int SelectedDeck = 1;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 0x1d0)]
        private unsafe struct AgentTripleTriad
        {
            [System.Runtime.InteropServices.FieldOffset(0x1c8)] public uint rewardItemID;
        }

        public override ModuleInfo Info => new()
        {
            Title = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "九宫幻卡自动化" : "Auto Triple Triad",
            Description = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "自动与 NPC 进行九宫幻卡连续对战。\n支持直到集齐未拥有卡牌后停止、完成指定次数后停止，以及自动读取胜率最高的卡组。\n※ 本模块仅作交互辅助，【必须】配合并加载外部插件 TriadBuddy 才能正常工作。" : "Auto play Triple Triad matches continuously.\nSupports stopping after collecting all cards, reaching target count, and auto recommended deck.\n※ This is only an interaction helper. MUST load external plugin TriadBuddy to work.",
            Category = ModuleCategory.GoldSaucer,
            Author = ["nynpsu"],
            ReportURL = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
        };

        private Config config = null!;
        private int matchCount = 0;
        private bool isInMatch = false;
        private bool isResultShown = false;
        private bool reflectionFailed = false;
        private bool isReflectionInitialized = false;
        private static bool isCN = false;

        private long lastSelectStringTime = 0;
        private long lastRequestTime = 0;
        private long lastSelDeckTime = 0;
        private long resultShowTime = 0;
        private long lastResultActionTime = 0;

        // Reflection caching
        private FieldInfo? solverGameStaticField;
        private FieldInfo? solverPreGameDecksStaticField;


        // Caching reflection members for performance
        private FieldInfo? solverGameCurrentNPCField;
        private PropertyInfo? solverGameHasErrorsProp;
        private FieldInfo? solverGameStatusField;
        private FieldInfo? solverGameHasMoveField;
        private FieldInfo? solverGameMoveCardIdxField;
        private FieldInfo? solverGameMoveBoardIdxField;

        private FieldInfo? solverPreGameNPCField;
        private PropertyInfo? solverPreGameProgressProp;
        private FieldInfo? solverPreGameBestIDField;

        private MethodInfo? gameNPCDBGetMethod;
        private FieldInfo? gameNPCDBMapNPCsField;

        private MethodInfo? gameCardDBGetMethod;
        private MethodInfo? gameCardDBRefreshMethod;
        private MethodInfo? gameCardDBFindByIDMethod;

        private FieldInfo? triadNPCIDField;
        private FieldInfo? gameNPCInfoRewardCardsField;

        private FieldInfo? gameCardInfoIsOwnedField;
        private FieldInfo? gameCardInfoItemIDField;

        private bool isTriadUIActive = false;
        private bool wasPrepOpen = false;
        private bool wasAnyTriadUIOpen = false;
        private long lastPrepCloseTime = 0;
        private long lastAnyTriadUIOpenTime = 0;
        private long lastNPCCheckTime = 0;
        private int cachedNPCID = -1;
        private bool isExitingFromCompletion = false;
        private List<(string Name, bool IsOwned, uint ItemID)> npcDropsCache = new();
        private HashSet<uint> sessionDroppedItemIDs = new();

        protected override void ConfigUI()
        {
            if (config.EnableTripleTriad)
            {
                if (ImGui.Button(GetLoc("StopAuto")))
                {
                    config.EnableTripleTriad = false;
                    SaveConfig(config);
                }
                
                ImGui.Spacing();
                ImGui.Text($"{GetLoc("Status")}{matchCount}{GetLoc("StatusEnd")}");
            }
            else
            {
                ImGui.Text(GetLoc("Hint"));
            }
            
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (ImGui.Checkbox(GetLoc("StopAllCol"), ref config.PlayUntilAllUnownedCardsDrop))
            {
                if (config.PlayUntilAllUnownedCardsDrop) config.PlayXTimes = false;
                SaveConfig(config);
            }
            
            if (ImGui.Checkbox(GetLoc("PlayX"), ref config.PlayXTimes))
            {
                if (config.PlayXTimes) config.PlayUntilAllUnownedCardsDrop = false;
                SaveConfig(config);
            }
            
            if (config.PlayXTimes)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(100f);
                if (ImGui.InputInt(GetLoc("TargetX"), ref config.TimesToPlay))
                {
                    if (config.TimesToPlay < 1) config.TimesToPlay = 1;
                    SaveConfig(config);
                }
                ImGui.Unindent();
            }

            if (ImGui.Checkbox(GetLoc("RecDeck"), ref config.UseRecommendedDeck)) SaveConfig(config);
            if (!config.UseRecommendedDeck)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(100f);
                if (ImGui.InputInt(GetLoc("ManualDeck"), ref config.SelectedDeck))
                {
                    config.SelectedDeck = Math.Max(1, Math.Min(10, config.SelectedDeck));
                    SaveConfig(config);
                }
                ImGui.Unindent();
            }

            if (wasAnyTriadUIOpen && npcDropsCache.Count > 0)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Text(GetLoc("CardList"));
                ImGui.Indent();
                foreach (var drop in npcDropsCache)
                {
                    bool isOwned = drop.IsOwned || sessionDroppedItemIDs.Contains(drop.ItemID);
                    string symbol = isOwned ? "[√]" : "[  ]";
                    ImGui.Text($"{symbol} {drop.Name}");
                }
                ImGui.Unindent();
            }

            ImGui.Unindent();
        }

        protected override void Init()
        {
            config = LoadConfig<Config>() ?? new Config();
            reflectionFailed = false;
            isReflectionInitialized = false;
            isCN = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;

            DService.Instance().Framework.Update += OnUpdate;

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "TripleTriadRequest", OnTriadUIChange);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "TripleTriadSelDeck", OnTriadUIChange);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "TripleTriad", OnTriadUIChange);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "TripleTriadResult", OnTriadUIChange);

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "TripleTriadRequest", OnTriadUIChange);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "TripleTriadSelDeck", OnTriadUIChange);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "TripleTriad", OnTriadUIChange);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "TripleTriadResult", OnTriadUIChange);

            if (CheckAnyTriadUIOpen())
            {
                isTriadUIActive = true;
            }
        }

        protected override void Uninit()
        {
            DService.Instance().Framework.Update -= OnUpdate;
            DService.Instance().AddonLifecycle.UnregisterListener(OnTriadUIChange);
            
            isTriadUIActive = false;
            isInMatch = false;
            isResultShown = false;
            matchCount = 0;
            reflectionFailed = false;
            isReflectionInitialized = false;
            
            wasPrepOpen = false;
            wasAnyTriadUIOpen = false;
            cachedNPCID = -1;
            lastNPCCheckTime = 0;
            isExitingFromCompletion = false;
            sessionDroppedItemIDs.Clear();

            config.EnableTripleTriad = false;
            SaveConfig(config);

            // 清理缓存反射变量
            solverGameCurrentNPCField = null;
            solverGameHasErrorsProp = null;
            solverGameStatusField = null;
            solverGameHasMoveField = null;
            solverGameMoveCardIdxField = null;
            solverGameMoveBoardIdxField = null;

            solverPreGameNPCField = null;
            solverPreGameProgressProp = null;
            solverPreGameBestIDField = null;

            gameNPCDBGetMethod = null;
            gameNPCDBMapNPCsField = null;

            gameCardDBGetMethod = null;
            gameCardDBRefreshMethod = null;
            gameCardDBFindByIDMethod = null;

            triadNPCIDField = null;
            gameNPCInfoRewardCardsField = null;

            gameCardInfoIsOwnedField = null;
            gameCardInfoItemIDField = null;

            solverGameStaticField = null;
            solverPreGameDecksStaticField = null;


        }

        private bool TryInitializeReflection()
        {
            if (isReflectionInitialized) return true;
            reflectionFailed = true;
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "TriadBuddy");
                if (assembly == null) return false;

                var solverUtilsType = assembly.GetType("TriadBuddyPlugin.SolverUtils");
                if (solverUtilsType == null) return false;

                solverGameStaticField = solverUtilsType.GetField("solverGame", BindingFlags.Public | BindingFlags.Static);
                solverPreGameDecksStaticField = solverUtilsType.GetField("solverPreGameDecks", BindingFlags.Public | BindingFlags.Static);
                
                if (solverGameStaticField == null || solverPreGameDecksStaticField == null) return false;

                var solverGameType = assembly.GetType("TriadBuddyPlugin.SolverGame");
                var solverPreGameDecksType = assembly.GetType("TriadBuddyPlugin.SolverPreGameDecks");

                if (solverGameType == null || solverPreGameDecksType == null) return false;

                var gameCardDBType = assembly.GetType("TriadBuddyPlugin.GameCardDB");
                var gameNpcDBType = assembly.GetType("TriadBuddyPlugin.GameNpcDB");
                var gameNpcInfoType = assembly.GetType("TriadBuddyPlugin.GameNpcInfo");
                var gameCardInfoType = assembly.GetType("TriadBuddyPlugin.GameCardInfo");

                if (gameCardDBType == null || gameNpcDBType == null || gameNpcInfoType == null || gameCardInfoType == null) return false;

                const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
                const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.IgnoreCase;

                // SolverGame fields & properties
                solverGameCurrentNPCField = solverGameType.GetField("currentNpc", instanceFlags);
                solverGameHasErrorsProp = solverGameType.GetProperty("HasErrors", instanceFlags);
                solverGameStatusField = solverGameType.GetField("status", instanceFlags);
                solverGameHasMoveField = solverGameType.GetField("hasMove", instanceFlags);
                solverGameMoveCardIdxField = solverGameType.GetField("moveCardIdx", instanceFlags);
                solverGameMoveBoardIdxField = solverGameType.GetField("moveBoardIdx", instanceFlags);

                // SolverPreGameDecks fields & properties
                solverPreGameNPCField = solverPreGameDecksType.GetField("preGameNpc", instanceFlags);
                solverPreGameProgressProp = solverPreGameDecksType.GetProperty("preGameProgress", instanceFlags);
                solverPreGameBestIDField = solverPreGameDecksType.GetField("preGameBestId", instanceFlags);

                // GameNpcDB methods & fields
                gameNPCDBGetMethod = gameNpcDBType.GetMethod("Get", staticFlags);
                gameNPCDBMapNPCsField = gameNpcDBType.GetField("mapNpcs", instanceFlags);

                // GameCardDB methods
                gameCardDBGetMethod = gameCardDBType.GetMethod("Get", staticFlags);
                gameCardDBRefreshMethod = gameCardDBType.GetMethod("Refresh", instanceFlags);
                gameCardDBFindByIDMethod = gameCardDBType.GetMethod("FindById", instanceFlags);

                // GameNpcInfo / NPC fields
                var triadNpcType = assembly.GetType("FFTriadBuddy.TriadNpc");
                if (triadNpcType == null) return false;
                triadNPCIDField = triadNpcType.GetField("Id", instanceFlags);
                gameNPCInfoRewardCardsField = gameNpcInfoType.GetField("rewardCards", instanceFlags);

                // GameCardInfo fields
                gameCardInfoIsOwnedField = gameCardInfoType.GetField("IsOwned", instanceFlags);
                gameCardInfoItemIDField = gameCardInfoType.GetField("ItemId", instanceFlags);

                // 核心反射字段必须全部获取成功
                if (solverGameCurrentNPCField == null || solverGameHasErrorsProp == null || solverGameHasMoveField == null ||
                    solverGameMoveCardIdxField == null || solverGameMoveBoardIdxField == null ||
                    solverPreGameNPCField == null || solverPreGameProgressProp == null || solverPreGameBestIDField == null ||
                    gameNPCDBGetMethod == null || gameNPCDBMapNPCsField == null ||
                    gameCardDBGetMethod == null || gameCardDBRefreshMethod == null || gameCardDBFindByIDMethod == null ||
                    triadNPCIDField == null ||
                    gameNPCInfoRewardCardsField == null || gameCardInfoIsOwnedField == null || gameCardInfoItemIDField == null)
                {
                    DService.Instance().Log.Warning("AutoTripleTriad: 反射获取 TriadBuddy 核心字段失败，已触发熔断拦截。");
                    return false;
                }

                reflectionFailed = false;
                isReflectionInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                DService.Instance().Log.Warning($"AutoTripleTriad: 反射初始化异常: {ex.Message}，已触发熔断拦截。");
                return false;
            }
        }

        public void OnUpdate(IFramework framework)
        {
            if (config == null) return;
            if (!isTriadUIActive && !wasAnyTriadUIOpen) return;

            var prepAddon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriadRequest").Address;
            var selDeckAddon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriadSelDeck").Address;
            var gameAddon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriad").Address;
            var resultAddon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriadResult").Address;

            bool isPrepOpen = prepAddon != null && prepAddon->IsVisible;
            bool isSelDeckOpen = selDeckAddon != null && selDeckAddon->IsVisible;
            bool isGameOpen = gameAddon != null && gameAddon->IsVisible;
            bool isResultOpen = resultAddon != null && resultAddon->IsVisible;

            bool isAnyTriadUIOpen = isPrepOpen || isSelDeckOpen || isGameOpen || isResultOpen;

            if (isAnyTriadUIOpen)
            {
                lastAnyTriadUIOpenTime = Environment.TickCount64;
                if (!wasAnyTriadUIOpen)
                {
                    ToggleOverlayConfig(true);
                    wasAnyTriadUIOpen = true;
                }
                
                if (isPrepOpen && Environment.TickCount64 - lastNPCCheckTime > 500)
                {
                    lastNPCCheckTime = Environment.TickCount64;
                    UpdateNpcDropsCacheIfChanged();
                }
            }
            else
            {
                if (wasAnyTriadUIOpen && Environment.TickCount64 - lastAnyTriadUIOpenTime > 3000)
                {
                    npcDropsCache.Clear();
                    cachedNPCID = -1;
                    ToggleOverlayConfig(false);
                    wasAnyTriadUIOpen = false;
                    
                    if (config.EnableTripleTriad)
                    {
                        config.EnableTripleTriad = false;
                        SaveConfig(config);
                    }
                }
            }

            if (wasPrepOpen && !isPrepOpen)
            {
                lastPrepCloseTime = Environment.TickCount64;
            }
            wasPrepOpen = isPrepOpen;

            if (Environment.TickCount64 - lastPrepCloseTime < 3000 && isSelDeckOpen && !config.EnableTripleTriad)
            {
                if (!reflectionFailed && TryInitializeReflection())
                {
                    config.EnableTripleTriad = true;
                    matchCount = 0;
                    sessionDroppedItemIDs.Clear();
                    SaveConfig(config);
                }
                lastPrepCloseTime = 0;
            }

            if (isExitingFromCompletion)
            {
                if (isPrepOpen)
                {
                    if (Environment.TickCount64 - lastRequestTime > 500)
                    {
                        lastRequestTime = Environment.TickCount64;
                        prepAddon->Callback(-1);
                        isExitingFromCompletion = false;
                    }
                }
            }

            if (!config.EnableTripleTriad) return;

            if (DService.Instance().ClientState.IsPvP) return;

            UpdateGameState();
            
            if (config.EnableTripleTriad)
            {
                if (isInMatch)
                {
                    ProcessAutoPlay();
                }
                else
                {
                    ProcessSelectString();
                    ProcessRequest();
                    ProcessSelDeck();
                }

                ProcessTripleTriadResult();
            }
        }

        private void OnTriadUIChange(AddonEvent type, AddonArgs args)
        {
            isTriadUIActive = type switch
            {
                AddonEvent.PostSetup => true,
                AddonEvent.PreFinalize => CheckAnyTriadUIOpen(),
                _ => isTriadUIActive
            };
        }

        private bool CheckAnyTriadUIOpen()
        {
            var prepAddon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriadRequest").Address;
            var selDeckAddon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriadSelDeck").Address;
            var gameAddon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriad").Address;
            var resultAddon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriadResult").Address;

            return (prepAddon != null && prepAddon->IsVisible) ||
                   (selDeckAddon != null && selDeckAddon->IsVisible) ||
                   (gameAddon != null && gameAddon->IsVisible) ||
                   (resultAddon != null && resultAddon->IsVisible);
        }

        private unsafe void ProcessSelectString()
        {
            var addon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("SelectString").Address;
            if (addon != null && addon->IsVisible && addon->UldManager.NodeListCount > 0)
            {
                if (Environment.TickCount64 - lastSelectStringTime < 1000) return;
                lastSelectStringTime = Environment.TickCount64;
                
                if (config.PlayXTimes && matchCount >= config.TimesToPlay)
                {
                    config.EnableTripleTriad = false;
                    SaveConfig(config);
                    DService.Instance().Chat.Print(GetLoc("DoneCount"));

                    var selectString = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectString*)addon;
                    var popupMenu = (FFXIVClientStructs.FFXIV.Client.UI.PopupMenu*)((byte*)selectString + 0x238);
                    int exitIndex = popupMenu->EntryCount - 1;
                    if (exitIndex >= 0)
                    {
                        addon->Callback(exitIndex);
                    }
                    return;
                }
                
                addon->Callback(0);
            }
        }

        private unsafe void ProcessTripleTriadResult()
        {
            var addonResult = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriadResult").Address;
            if (addonResult != null && addonResult->IsVisible)
            {
                if (!isResultShown)
                {
                    isResultShown = true;
                    resultShowTime = Environment.TickCount64;
                    if (config.EnableTripleTriad) matchCount++;
                }

                // 等待结算动画播放完毕 (1800ms)
                if (Environment.TickCount64 - resultShowTime < 1800) return; 
                
                if (Environment.TickCount64 - lastResultActionTime < 1000) return;
                lastResultActionTime = Environment.TickCount64;

                nint agentPtr = DService.Instance().GameGUI.FindAgentInterface((nint)addonResult);
                if (agentPtr != nint.Zero)
                {
                    var agent = (AgentTripleTriad*)agentPtr;
                    if (agent->rewardItemID > 0)
                    {
                        sessionDroppedItemIDs.Add(agent->rewardItemID);
                    }
                }

                bool shouldStop = false;

                var externalSolverGame = solverGameStaticField?.GetValue(null);
                if (config.PlayUntilAllUnownedCardsDrop && externalSolverGame != null)
                {
                    try
                    {
                        object? currentNPC = solverGameCurrentNPCField!.GetValue(externalSolverGame);
                        if (currentNPC != null)
                        {
                            var idVal = triadNPCIDField!.GetValue(currentNPC);
                            if (idVal == null) return;
                            int NPCID = (int)idVal;
                            
                            object npcDBInstance = gameNPCDBGetMethod!.Invoke(null, null)!;
                            if (npcDBInstance == null) return;
                            object mapNPCs = gameNPCDBMapNPCsField!.GetValue(npcDBInstance)!;
                            if (mapNPCs == null) return;
                            
                            if (mapNPCs is System.Collections.IDictionary dict)
                            {
                                if (dict.Contains(NPCID))
                                {
                                    object? npcInfo = dict[NPCID];
                                    if (npcInfo == null) return;

                                    var rewardCards = (System.Collections.IEnumerable)gameNPCInfoRewardCardsField!.GetValue(npcInfo)!;
                                    if (rewardCards == null) return;
                                    
                                    object cardDBInstance = gameCardDBGetMethod!.Invoke(null, null)!;
                                    if (cardDBInstance == null) return;
                                    gameCardDBRefreshMethod!.Invoke(cardDBInstance, null);
                                    
                                    bool allCardsOwned = true;
                                    foreach (int cardID in rewardCards)
                                    {
                                        object? cardInfo = gameCardDBFindByIDMethod!.Invoke(cardDBInstance, new object[] { cardID });
                                        if (cardInfo != null)
                                        {
                                            bool isOwned = (bool)gameCardInfoIsOwnedField!.GetValue(cardInfo)!;
                                            uint ItemID = (uint)gameCardInfoItemIDField!.GetValue(cardInfo)!;
                                            
                                            if (sessionDroppedItemIDs.Contains(ItemID)) isOwned = true;
                                            
                                            if (!isOwned)
                                            {
                                                allCardsOwned = false;
                                                break;
                                            }
                                        }
                                    }
                                    
                                    if (allCardsOwned)
                                    {
                                        DService.Instance().Chat.Print(GetLoc("DoneCol"));
                                        shouldStop = true;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DService.Instance().Chat.PrintError($"{GetLoc("ErrCheck")}{ex.Message}");
                        DService.Instance().Log.Error(ex, "AutoTripleTriad: 检查全收集状态失败");
                    }
                }
                else if (config.PlayXTimes && matchCount >= config.TimesToPlay)
                {
                    DService.Instance().Chat.Print(GetLoc("DoneCount"));
                    shouldStop = true;
                }

                if (shouldStop)
                {
                    config.EnableTripleTriad = false;
                    isExitingFromCompletion = true;
                    SaveConfig(config);
                    var values = stackalloc AtkValue[2];
                    values[0].Type = AtkValueType.Int;
                    values[0].Int = 0;
                    values[1].Type = AtkValueType.UInt;
                    values[1].UInt = 0;
                    addonResult->FireCallback(2, values, true);
                    addonResult->Close(true);
                }
                else
                {
                    var values = stackalloc AtkValue[2];
                    values[0].Type = AtkValueType.Int;
                    values[0].Int = 0;
                    values[1].Type = AtkValueType.UInt;
                    values[1].UInt = 1;
                    addonResult->FireCallback(2, values, true);
                }
            }
            else
            {
                isResultShown = false;
            }
        }

        private void UpdateNpcDropsCacheIfChanged()
        {
            if (solverPreGameNPCField == null)
            {
                if (reflectionFailed || !TryInitializeReflection()) return;
            }
            try
            {
                var externalSolverPreGameDecks = solverPreGameDecksStaticField?.GetValue(null);
                if (externalSolverPreGameDecks == null) return;
                
                object? currentNPC = solverPreGameNPCField!.GetValue(externalSolverPreGameDecks);
                if (currentNPC != null)
                {
                    var idVal = triadNPCIDField!.GetValue(currentNPC);
                    if (idVal == null) return;
                    int NPCID = (int)idVal;
                    
                    if (NPCID == cachedNPCID) return;
                    
                    npcDropsCache.Clear();
                    cachedNPCID = NPCID;
                    
                    object npcDBInstance = gameNPCDBGetMethod!.Invoke(null, null)!;
                    if (npcDBInstance == null) return;
                    object mapNPCs = gameNPCDBMapNPCsField!.GetValue(npcDBInstance)!;
                    if (mapNPCs == null) return;
                    
                    if (mapNPCs is System.Collections.IDictionary dict)
                    {
                        if (dict.Contains(NPCID))
                        {
                            object? npcInfo = dict[NPCID];
                            if (npcInfo == null) return;
                            var rewardCards = (System.Collections.IEnumerable)gameNPCInfoRewardCardsField!.GetValue(npcInfo)!;
                            if (rewardCards == null) return;
                            
                            object cardDBInstance = gameCardDBGetMethod!.Invoke(null, null)!;
                            if (cardDBInstance == null) return;
                            gameCardDBRefreshMethod!.Invoke(cardDBInstance, []);
                            
                            var sheet = DService.Instance().Data.GetExcelSheet<TripleTriadCard>();
                            
                            foreach (int cardID in rewardCards)
                            {
                                object? cardInfo = gameCardDBFindByIDMethod!.Invoke(cardDBInstance, [cardID]);
                                bool isOwned = false;
                                uint ItemID = 0;
                                if (cardInfo != null)
                                {
                                    isOwned = (bool)gameCardInfoIsOwnedField!.GetValue(cardInfo)!;
                                    ItemID = (uint)gameCardInfoItemIDField!.GetValue(cardInfo)!;
                                }
                                
                                string cardName = $"Card #{cardID}";
                                try
                                {
                                    var row = sheet?.GetRow((uint)cardID);
                                    var tempName = row?.Name.ToString();
                                    if (!string.IsNullOrEmpty(tempName)) cardName = tempName;
                                }
                                catch
                                {
                                    // 忽略 Lumina 解析异常，防止空指针阻断流程
                                }
                                
                                npcDropsCache.Add((cardName, isOwned, ItemID));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DService.Instance().Chat.PrintError($"{GetLoc("ErrDrop")}{ex.Message}");
                DService.Instance().Log.Error(ex, "AutoTripleTriad: 获取NPC卡牌掉落失败");
            }
        }

        private unsafe void UpdateGameState()
        {
            var addonGame = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriad").Address;
            if (addonGame != null && addonGame->IsVisible)
            {
                isInMatch = true;
            }
            else
            {
                isInMatch = false;
            }
        }

        private unsafe void ProcessAutoPlay()
        {
            var externalSolverGame = solverGameStaticField?.GetValue(null);
            if (externalSolverGame == null) return;

            try
            {
                var hasErrors = (bool)solverGameHasErrorsProp!.GetValue(externalSolverGame)!;
                if (hasErrors)
                {
                    if (Environment.TickCount64 - lastRequestTime > 5000)
                    {
                        lastRequestTime = Environment.TickCount64;
                        var status = solverGameStatusField?.GetValue(externalSolverGame);
                        DService.Instance().Chat.Print($"{GetLoc("ErrBlock")}{status}");
                    }
                    return;
                }

                var hasMove = (bool)solverGameHasMoveField!.GetValue(externalSolverGame)!;
                if (hasMove)
                {
                    var move = (int)solverGameMoveCardIdxField!.GetValue(externalSolverGame)!;
                    var pos = (int)solverGameMoveBoardIdxField!.GetValue(externalSolverGame)!;

                    var addon = (FFXIVClientStructs.FFXIV.Client.UI.AddonTripleTriad*)DService.Instance().GameGUI.GetAddonByName("TripleTriad").Address;
                    if (addon == null) return;
                    
                    var values = stackalloc AtkValue[2];
                    values[0].Type = AtkValueType.Int;
                    values[0].Int = 14;
                    values[1].Type = AtkValueType.UInt;
                    values[1].UInt = (uint)pos + ((uint)move << 16);
                    
                    addon->AtkUnitBase.FireCallback(2, values, true);
                    addon->AtkUnitBase.Update(0);
                    addon->TurnState = 0;
                }
            }
            catch (Exception ex)
            {
                DService.Instance().Chat.PrintError($"{GetLoc("ErrInvoke")}{ex.Message}");
                DService.Instance().Log.Error(ex, "AutoTripleTriad: 自动出牌反射调用失败");
                config.EnableTripleTriad = false;
            }
        }

        private unsafe void ProcessRequest()
        {
            var addon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriadRequest").Address;
            if (addon != null && addon->IsVisible)
            {
                if (Environment.TickCount64 - lastRequestTime < 1000) return;
                lastRequestTime = Environment.TickCount64;
                
                addon->Callback(0);
            }
        }

        private unsafe void ProcessSelDeck()
        {
            var addon = (AtkUnitBase*)DService.Instance().GameGUI.GetAddonByName("TripleTriadSelDeck").Address;
            if (addon != null && addon->IsVisible)
            {
                if (Environment.TickCount64 - lastSelDeckTime < 1000) return;
                lastSelDeckTime = Environment.TickCount64;

                int targetDeck = config.SelectedDeck - 1;
                
                var externalSolverPreGameDecks = solverPreGameDecksStaticField?.GetValue(null);
                if (config.UseRecommendedDeck && externalSolverPreGameDecks != null)
                {
                    try
                    {
                        var progress = (float)solverPreGameProgressProp!.GetValue(externalSolverPreGameDecks)!;
                        if (progress < 1.0f) return;

                        var bestID = (int)solverPreGameBestIDField!.GetValue(externalSolverPreGameDecks)!;
                        if (bestID != -1)
                        {
                            targetDeck = bestID;
                        }
                    }
                    catch (Exception ex)
                    {
                        DService.Instance().Chat.PrintError($"{GetLoc("ErrRec")}{ex.Message}");
                        DService.Instance().Log.Error(ex, "AutoTripleTriad: 读取推荐卡组失败");
                    }
                }
                
                addon->Callback((int)targetDeck);
            }
        }

        private static string GetLoc(string key)
        {
            var IsCN = isCN;
            return key switch
            {
                "StopAuto" => IsCN ? "停止自动打牌" : "Stop Auto Play",
                "Status" => IsCN ? "状态：自动打牌中 (已战 " : "Status: Auto Playing (Matches played: ",
                "StatusEnd" => IsCN ? " 场)" : ")",
                "Hint" => IsCN ? "提示：请点击游戏原生的【挑战】按钮开始自动挂机。" : "Hint: Please click the in-game [Challenge] button to start auto-farming.",
                "StopAllCol" => IsCN ? "直到集齐该 NPC 所有未拥有卡牌后停止" : "Stop when all unowned cards from this NPC are collected",
                "PlayX" => IsCN ? "挑战指定次数" : "Play a specific number of times",
                "TargetX" => IsCN ? "目标挑战次数" : "Target match count",
                "RecDeck" => IsCN ? "自动选用胜率最高卡组" : "Automatically use recommended deck",
                "ManualDeck" => IsCN ? "手动指定卡组编号 (1-10)" : "Manually select deck number (1-10)",
                "CardList" => IsCN ? "卡牌掉落列表：" : "Card Drop List:",
                "DoneCount" => IsCN ? "已完成设定的游玩次数，自动停止九宫幻卡自动对战。" : "Target match count reached, auto play stopped.",
                "DoneCol" => IsCN ? "该 NPC 的所有卡牌已集齐，自动停止九宫幻卡自动对战。" : "All unowned cards from this NPC collected, auto play stopped.",
                "ErrCheck" => IsCN ? "检查全收集状态失败: " : "Failed to check collection status: ",
                "ErrDrop" => IsCN ? "获取NPC卡牌掉落失败: " : "Failed to get NPC card drops: ",
                "ErrBlock" => IsCN ? "九宫幻卡出牌受阻，外部解析器反馈状态: " : "Auto play blocked, external solver status: ",
                "ErrInvoke" => IsCN ? "自动出牌反射调用失败: " : "Failed to invoke external solver: ",
                "ErrRec" => IsCN ? "读取推荐卡组失败: " : "Failed to read recommended deck: ",
                _ => key
            };
        }
    }
