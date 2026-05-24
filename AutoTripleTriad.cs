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

namespace DailyRoutines.ModulesPublic
{
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
            [System.Runtime.InteropServices.FieldOffset(0x1c8)] public uint rewardItemId;
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

        private long lastSelectStringTime = 0;
        private long lastRequestTime = 0;
        private long lastSelDeckTime = 0;
        private long resultShowTime = 0;
        private long lastResultActionTime = 0;

        // Reflection caching
        private object? externalSolverGame;
        private object? externalSolverPreGameDecks;
        private Type? solverGameType;
        private Type? solverPreGameDecksType;
        private Type? solverUtilsType;

        private Type? gameCardDbType;
        private Type? gameNpcDbType;
        private Type? gameNpcInfoType;
        private Type? gameCardInfoType;

        private bool wasPrepOpen = false;
        private bool wasAnyTriadUIOpen = false;
        private long lastPrepCloseTime = 0;
        private long lastAnyTriadUIOpenTime = 0;
        private long lastNpcCheckTime = 0;
        private int cachedNpcId = -1;
        private bool isExitingFromCompletion = false;
        private List<(string Name, bool IsOwned, uint ItemId)> npcDropsCache = new();
        private HashSet<uint> sessionDroppedItemIds = new();

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
                    bool isOwned = drop.IsOwned || sessionDroppedItemIds.Contains(drop.ItemId);
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

            DService.Instance().Framework.Update += OnUpdate;
        }

        protected override void Uninit()
        {
            DService.Instance().Framework.Update -= OnUpdate;
            
            isInMatch = false;
            isResultShown = false;
            matchCount = 0;
            
            wasPrepOpen = false;
            wasAnyTriadUIOpen = false;
            cachedNpcId = -1;
            lastNpcCheckTime = 0;
            isExitingFromCompletion = false;
            sessionDroppedItemIds.Clear();

            config.EnableTripleTriad = false;
            SaveConfig(config);
        }

        private bool TryInitializeReflection()
        {
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "TriadBuddy");
                if (assembly == null) return false;

                solverUtilsType = assembly.GetType("TriadBuddyPlugin.SolverUtils");
                if (solverUtilsType == null) return false;

                var sgField = solverUtilsType.GetField("solverGame", BindingFlags.Public | BindingFlags.Static);
                var sdField = solverUtilsType.GetField("solverPreGameDecks", BindingFlags.Public | BindingFlags.Static);
                
                if (sgField == null || sdField == null) return false;

                externalSolverGame = sgField.GetValue(null);
                externalSolverPreGameDecks = sdField.GetValue(null);

                if (externalSolverGame == null || externalSolverPreGameDecks == null) return false;

                solverGameType = externalSolverGame.GetType();
                solverPreGameDecksType = externalSolverPreGameDecks.GetType();

                gameCardDbType = assembly.GetType("TriadBuddyPlugin.GameCardDB");
                gameNpcDbType = assembly.GetType("TriadBuddyPlugin.GameNpcDB");
                gameNpcInfoType = assembly.GetType("TriadBuddyPlugin.GameNpcInfo");
                gameCardInfoType = assembly.GetType("TriadBuddyPlugin.GameCardInfo");

                return true;
            }
            catch
            {
                return false;
            }
        }

        public void OnUpdate(IFramework framework)
        {
            if (config == null) return;

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
                
                if (isPrepOpen && Environment.TickCount64 - lastNpcCheckTime > 500)
                {
                    lastNpcCheckTime = Environment.TickCount64;
                    UpdateNpcDropsCacheIfChanged();
                }
            }
            else
            {
                if (wasAnyTriadUIOpen && Environment.TickCount64 - lastAnyTriadUIOpenTime > 3000)
                {
                    npcDropsCache.Clear();
                    cachedNpcId = -1;
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
                if (TryInitializeReflection())
                {
                    config.EnableTripleTriad = true;
                    matchCount = 0;
                    sessionDroppedItemIds.Clear();
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

                IntPtr agentPtr = DService.Instance().GameGUI.FindAgentInterface((IntPtr)addonResult);
                if (agentPtr != IntPtr.Zero)
                {
                    var agent = (AgentTripleTriad*)agentPtr;
                    if (agent->rewardItemId > 0)
                    {
                        sessionDroppedItemIds.Add(agent->rewardItemId);
                    }
                }

                bool shouldStop = false;

                if (config.PlayUntilAllUnownedCardsDrop && solverGameType != null && gameCardDbType != null && gameNpcDbType != null)
                {
                    try
                    {
                        object? currentNpc = solverGameType.GetField("currentNpc")?.GetValue(externalSolverGame);
                        if (currentNpc != null)
                        {
                            int npcId = (int)currentNpc.GetType().GetField("Id")!.GetValue(currentNpc)!;
                            object npcDbInstance = gameNpcDbType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
                            object mapNpcs = gameNpcDbType.GetField("mapNpcs")!.GetValue(npcDbInstance)!;
                            
                            var containsKeyMethod = mapNpcs.GetType().GetMethod("ContainsKey")!;
                            if ((bool)containsKeyMethod.Invoke(mapNpcs, new object[] { npcId })!)
                            {
                                var getItemMethod = mapNpcs.GetType().GetProperty("Item")!.GetGetMethod()!;
                                object npcInfo = getItemMethod.Invoke(mapNpcs, new object[] { npcId })!;
                                
                                var rewardCards = (System.Collections.IEnumerable)gameNpcInfoType!.GetField("rewardCards")!.GetValue(npcInfo)!;
                                
                                object cardDbInstance = gameCardDbType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
                                gameCardDbType.GetMethod("Refresh")!.Invoke(cardDbInstance, null);
                                
                                bool allCardsOwned = true;
                                foreach (int cardId in rewardCards)
                                {
                                    object? cardInfo = gameCardDbType.GetMethod("FindById")!.Invoke(cardDbInstance, new object[] { cardId });
                                    if (cardInfo != null)
                                    {
                                        bool isOwned = (bool)gameCardInfoType!.GetField("IsOwned")!.GetValue(cardInfo)!;
                                        uint itemId = (uint)gameCardInfoType!.GetField("ItemId")!.GetValue(cardInfo)!;
                                        
                                        if (sessionDroppedItemIds.Contains(itemId)) isOwned = true;
                                        
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
                    catch (Exception ex)
                    {
                        DService.Instance().Chat.PrintError($"{GetLoc("ErrCheck")}{ex.Message}");
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
            if (solverPreGameDecksType == null)
            {
                if (!TryInitializeReflection()) return;
            }
            if (solverPreGameDecksType == null || gameCardDbType == null || gameNpcDbType == null || gameNpcInfoType == null || gameCardInfoType == null) return;
            try
            {
                object? currentNpc = solverPreGameDecksType.GetField("preGameNpc")?.GetValue(externalSolverPreGameDecks);
                if (currentNpc != null)
                {
                    var propId = currentNpc.GetType().GetProperty("Id");
                    var fieldId = currentNpc.GetType().GetField("Id");
                    int npcId = propId != null ? (int)propId.GetValue(currentNpc)! : (int)fieldId!.GetValue(currentNpc)!;
                    
                    if (npcId == cachedNpcId) return;
                    
                    npcDropsCache.Clear();
                    cachedNpcId = npcId;
                    
                    object npcDbInstance = gameNpcDbType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
                    object mapNpcs = gameNpcDbType.GetField("mapNpcs")!.GetValue(npcDbInstance)!;
                    
                    var containsKeyMethod = mapNpcs.GetType().GetMethod("ContainsKey")!;
                    if ((bool)containsKeyMethod.Invoke(mapNpcs, new object[] { npcId })!)
                    {
                        var getItemMethod = mapNpcs.GetType().GetProperty("Item")!.GetGetMethod()!;
                        object npcInfo = getItemMethod.Invoke(mapNpcs, new object[] { npcId })!;
                        var rewardCards = (System.Collections.IEnumerable)gameNpcInfoType.GetField("rewardCards")!.GetValue(npcInfo)!;
                        
                        object cardDbInstance = gameCardDbType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
                        gameCardDbType.GetMethod("Refresh")!.Invoke(cardDbInstance, null);
                        
                        Type triadCardDbType = currentNpc.GetType().Assembly.GetType("FFTriadBuddy.TriadCardDB")!;
                        object triadCardDbInstance = triadCardDbType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
                        
                        foreach (int cardId in rewardCards)
                        {
                            object? cardInfo = gameCardDbType.GetMethod("FindById")!.Invoke(cardDbInstance, new object[] { cardId });
                            bool isOwned = false;
                            uint itemId = 0;
                            if (cardInfo != null)
                            {
                                isOwned = (bool)gameCardInfoType.GetField("IsOwned")!.GetValue(cardInfo)!;
                                itemId = (uint)gameCardInfoType.GetField("ItemId")!.GetValue(cardInfo)!;
                            }
                            
                            object triadCard = triadCardDbType.GetMethod("FindById")!.Invoke(triadCardDbInstance, new object[] { cardId })!;
                            object locString = triadCard.GetType().GetField("Name")!.GetValue(triadCard)!;
                            string cardName = (string)locString.GetType().GetMethod("GetLocalized")!.Invoke(locString, null)!;
                            
                            npcDropsCache.Add((cardName, isOwned, itemId));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DService.Instance().Chat.PrintError($"{GetLoc("ErrDrop")}{ex.Message}");
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
            if (solverGameType == null || externalSolverGame == null) return;

            try
            {
                var hasErrors = (bool)solverGameType.GetProperty("HasErrors")!.GetValue(externalSolverGame)!;
                if (hasErrors)
                {
                    if (Environment.TickCount64 - lastRequestTime > 5000)
                    {
                        lastRequestTime = Environment.TickCount64;
                        var status = solverGameType.GetField("status")?.GetValue(externalSolverGame);
                        DService.Instance().Chat.Print($"{GetLoc("ErrBlock")}{status}");
                    }
                    return;
                }

                var hasMove = (bool)solverGameType.GetField("hasMove")!.GetValue(externalSolverGame)!;
                if (hasMove)
                {
                    var move = (int)solverGameType.GetField("moveCardIdx")!.GetValue(externalSolverGame)!;
                    var pos = (int)solverGameType.GetField("moveBoardIdx")!.GetValue(externalSolverGame)!;

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
                
                if (config.UseRecommendedDeck && solverPreGameDecksType != null && externalSolverPreGameDecks != null)
                {
                    try
                    {
                        var progress = (float)solverPreGameDecksType.GetProperty("preGameProgress")!.GetValue(externalSolverPreGameDecks)!;
                        if (progress < 1.0f) return;

                        var bestId = (int)solverPreGameDecksType.GetField("preGameBestId")!.GetValue(externalSolverPreGameDecks)!;
                        if (bestId != -1)
                        {
                            targetDeck = bestId;
                        }
                    }
                    catch (Exception ex)
                    {
                        DService.Instance().Chat.PrintError($"{GetLoc("ErrRec")}{ex.Message}");
                    }
                }
                
                addon->Callback((int)targetDeck);
            }
        }

        private static string GetLoc(string key)
        {
            var isCn = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
            return key switch
            {
                "StopAuto" => isCn ? "停止自动打牌" : "Stop Auto Play",
                "Status" => isCn ? "状态：自动打牌中 (已战 " : "Status: Auto Playing (Matches played: ",
                "StatusEnd" => isCn ? " 场)" : ")",
                "Hint" => isCn ? "提示：请点击游戏原生的【挑战】按钮开始自动挂机。" : "Hint: Please click the in-game [Challenge] button to start auto-farming.",
                "StopAllCol" => isCn ? "直到集齐该 NPC 所有未拥有卡牌后停止" : "Stop when all unowned cards from this NPC are collected",
                "PlayX" => isCn ? "挑战指定次数" : "Play a specific number of times",
                "TargetX" => isCn ? "目标挑战次数" : "Target match count",
                "RecDeck" => isCn ? "自动选用胜率最高卡组" : "Automatically use recommended deck",
                "ManualDeck" => isCn ? "手动指定卡组编号 (1-10)" : "Manually select deck number (1-10)",
                "CardList" => isCn ? "卡牌掉落列表：" : "Card Drop List:",
                "DoneCount" => isCn ? "已完成设定的游玩次数，自动停止九宫幻卡自动对战。" : "Target match count reached, auto play stopped.",
                "DoneCol" => isCn ? "该 NPC 的所有卡牌已集齐，自动停止九宫幻卡自动对战。" : "All unowned cards from this NPC collected, auto play stopped.",
                "ErrCheck" => isCn ? "检查全收集状态失败: " : "Failed to check collection status: ",
                "ErrDrop" => isCn ? "获取NPC卡牌掉落失败: " : "Failed to get NPC card drops: ",
                "ErrBlock" => isCn ? "九宫幻卡出牌受阻，外部解析器反馈状态: " : "Auto play blocked, external solver status: ",
                "ErrInvoke" => isCn ? "自动出牌反射调用失败: " : "Failed to invoke external solver: ",
                "ErrRec" => isCn ? "读取推荐卡组失败: " : "Failed to read recommended deck: ",
                _ => key
            };
        }
    }
}
