using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;

using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using DailyRoutines.Common.Extensions;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;

using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;

using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

using OmenTools;
using OmenTools.Dalamud;
using OmenTools.Dalamud.Helpers;
using OmenTools.Extensions;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

using EventHandler = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler;
using EventHandlerContent = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandlerContent;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoQuestSay : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "自动任务说话" : "Auto Quest Say",
        Description = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "当任务目标要求在当前频道说出指定台词时，点击目标将自动在当前频道发送台词。" : "Automatically sends the required chat line when clicking on quest targets that require saying specific lines.",
        Category    = ModuleCategory.General,
        Author      = ["nynpsu"],
        ReportURL   = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    public delegate ulong InteractWithObjectDelegate(TargetSystem* system, GameObject* obj, bool checkLOS);

    #region Static Fields & Cache

    private static readonly Regex ChineseRegex = new(@"(?:“|""""|「)([^“”""""「」]+?)(?:”|""""|」)", RegexOptions.Compiled);
    private static readonly Regex JapaneseRegex = new(@"(?:「)([^「」]+?)(?:」)", RegexOptions.Compiled);
    private static readonly Regex EnglishRegex = new(@"(?:""""|“)([^""""“”]+?)(?:""""|”)", RegexOptions.Compiled);
    private static readonly Regex GermanRegex = new(@"(?:„|»)([^„“»«]+?)(?:“|«)", RegexOptions.Compiled);
    private static readonly Regex FrenchRegex = new(@"(?:«\s*|“)([^«»“”]+?)(?:\s*»|”)", RegexOptions.Compiled);

    private static readonly Regex KeyRegex = new(@"_(SAY|SAYTODO|SYSTEM)_", RegexOptions.Compiled);
    
    private static Dictionary<string, (string IDStr, uint RowID)>? QuestNameIDCache;
    private static bool IsCacheInitializing;
    
    #endregion

    #region Instance Fields
    
    private Hook<InteractWithObjectDelegate>? InteractWithObjectHook;
    private readonly List<ActiveSayTask> ActiveSayTasks = [];
    private readonly Dictionary<string, ExcelSheet<QuestDialogue>> DialogueSheets = [];
    
    private ChatManager? Chat;
    private Regex? CurrentSayRegex;
    private long LastMountedTime; 
    private int LastQuestDataHash;
    
    #endregion

    #region Module Lifecycle

    protected override void Init()
    {
        Chat = DService.Instance().GetOmenService<ChatManager>();

        CurrentSayRegex = DService.Instance().ClientState.ClientLanguage switch
        {
            ClientLanguage.Japanese => JapaneseRegex,
            ClientLanguage.English => EnglishRegex,
            ClientLanguage.German => GermanRegex,
            ClientLanguage.French => FrenchRegex,
            _ => ChineseRegex
        };

        InteractWithObjectHook ??= DService.Instance().Hook.HookFromMemberFunction<InteractWithObjectDelegate>(
            typeof(TargetSystem.MemberFunctionPointers), "InteractWithObject", InteractWithObjectDetour);
        
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_ToDoList", OnToDoListUpdate);

        if (QuestNameIDCache == null && !IsCacheInitializing)
        {
            IsCacheInitializing = true;
            Task.Run(BuildQuestCache);
        }

        UpdateCache();
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnToDoListUpdate);

        if (InteractWithObjectHook != null)
        {
            InteractWithObjectHook.Disable();
            InteractWithObjectHook.Dispose();
            InteractWithObjectHook = null;
        }

        ActiveSayTasks.Clear();
        DialogueSheets.Clear();
        LastQuestDataHash = 0;
    }

    #endregion

    #region UI

    protected override void ConfigUI()
    {
        if (IsCacheInitializing)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, "正在初始化任务数据库缓存...");
        }

        if (ImGui.CollapsingHeader("任务详情", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ActiveSayTasks.Count == 0)
            {
                ImGui.TextDisabled("当前没有活动中的说话任务");
            }
            else
            {
                foreach (var task in ActiveSayTasks)
                {
                    ImGui.BulletText($"台词: {task.SayMessage}");
                    using (ImRaii.PushIndent())
                    {
                        ImGui.TextDisabled($"来源: {task.Detail}");
                    }
                }
            }
        }
    }

    #endregion

    #region Core Logic

    private static void BuildQuestCache()
    {
        try
        {
            var cache = new Dictionary<string, (string, uint)>(StringComparer.OrdinalIgnoreCase);
            var sheet = LuminaGetter.Get<Quest>();
            
            if (sheet != null)
            {
                foreach (var q in sheet)
                {
                    if (q.Name.IsEmpty) continue;
                    
                    var qName = q.Name.ToDalamudString().TextValue.Trim();
                    if (string.IsNullOrEmpty(qName)) continue;
                    
                    cache.TryAdd(qName, (q.Id.ToString(), q.RowId));
                }
            }
            
            QuestNameIDCache = cache;
        }
        finally
        {
            IsCacheInitializing = false;
        }
    }

    private void OnToDoListUpdate(AddonEvent type, AddonArgs args)
    {
        if (!Throttler.Shared.Throttle("AutoQuestSay-UpdateCache", 500)) 
            return;
            
        UpdateCache();
    }

    private ulong InteractWithObjectDetour(TargetSystem* system, GameObject* obj, bool checkLOS)
    {
        if (obj == null) 
            return InteractWithObjectHook!.Original(system, obj, checkLOS);

        var kind = obj->ObjectKind;
        if (kind is not (FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.EventNpc or FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.EventObj))
            return InteractWithObjectHook!.Original(system, obj, checkLOS);

        if (DService.Instance().Condition[ConditionFlag.Mounted])
        {
            LastMountedTime = Environment.TickCount64;
            return InteractWithObjectHook!.Original(system, obj, checkLOS);
        }

        if (Environment.TickCount64 - LastMountedTime < 400)
            return InteractWithObjectHook!.Original(system, obj, checkLOS);

        var msg = GetSayMessageFromCache(obj);
        if (!string.IsNullOrEmpty(msg))
        {
            if (Throttler<string>.Shared.Throttle("AutoQuestSay-Say", 800)) 
            {
                Chat?.SendMessage($"/say {msg.Trim()}");
            }
        }

        return InteractWithObjectHook!.Original(system, obj, checkLOS);
    }

    private void UpdateCache()
    {
        if (QuestNameIDCache == null) return;

        var numArray = ToDoListNumberArray.Instance();
        var stringArray = ToDoListStringArray.Instance();
        
        if (numArray == null || stringArray == null || !numArray->QuestListEnabled) 
        {
            if (ActiveSayTasks.Count > 0)
            {
                ActiveSayTasks.Clear();
                DialogueSheets.Clear();
                LastQuestDataHash = 0;
                ManageHookState();
            }
            return;
        }

        var entryCount = numArray->QuestCount;
        var currentEntries = new List<(string Title, string Detail)>();
        var currentHash = 17;

        for (var i = 0; i < entryCount; i++)
        {
            var rawTitlePtr = (byte*)stringArray->QuestTexts[i];
            var rawDetailPtr = (byte*)stringArray->QuestTexts[entryCount + i];
            
            if (rawTitlePtr == null || rawDetailPtr == null) continue;

            var titleSe = MemoryHelper.ReadSeStringNullTerminated((nint)rawTitlePtr);
            var detailSe = MemoryHelper.ReadSeStringNullTerminated((nint)rawDetailPtr);

            var titleText = titleSe.TextValue.Trim();
            var detailText = detailSe.TextValue.Trim();

            if (string.IsNullOrWhiteSpace(titleText) || string.IsNullOrWhiteSpace(detailText))
                continue;

            currentEntries.Add((titleText, detailText));
            currentHash = currentHash * 31 + titleText.GetHashCode();
            currentHash = currentHash * 31 + detailText.GetHashCode();
        }

        if (currentHash == LastQuestDataHash) return;

        LastQuestDataHash = currentHash;
        ActiveSayTasks.Clear();

        foreach (var entry in currentEntries)
        {
            if (!QuestNameIDCache.TryGetValue(entry.Title, out var questData)) continue;

            var sayMsg = GetQuestMessageFromLumina(questData.IDStr, entry.Detail);
            if (!string.IsNullOrEmpty(sayMsg))
            {
                ActiveSayTasks.Add(new ActiveSayTask
                {
                    QuestID = questData.RowID,
                    Title = entry.Title,
                    Detail = entry.Detail,
                    SayMessage = sayMsg
                });
            }
        }

        if (ActiveSayTasks.Count == 0 && DialogueSheets.Count > 0)
        {
            DialogueSheets.Clear();
        }

        ManageHookState();
    }

    private void ManageHookState()
    {
        if (InteractWithObjectHook == null) return;
        
        if (ActiveSayTasks.Count > 0) 
            InteractWithObjectHook.Enable();
        else 
            InteractWithObjectHook.Disable();
    }

    private string GetSayMessageFromCache(GameObject* obj)
    {
        if (obj == null) return string.Empty;

        var primaryEvent = obj->EventId;
        if (primaryEvent.ContentId == EventHandlerContent.Quest)
        {
            foreach (var task in ActiveSayTasks)
            {
                if ((task.QuestID & 0xFFFF) == primaryEvent.EntryId)
                    return task.SayMessage;
            }
        }

        var handlers = stackalloc EventHandler*[32];
        var handlerCount = obj->GetEventHandlersImpl(handlers);
        
        for (var i = 0; i < handlerCount; i++)
        {
            var handler = handlers[i];
            if (handler != null && handler->Info.EventId.ContentId == EventHandlerContent.Quest)
            {
                foreach (var task in ActiveSayTasks)
                {
                    if ((task.QuestID & 0xFFFF) == handler->Info.EventId.EntryId)
                        return task.SayMessage;
                }
            }
        }

        return string.Empty;
    }

    private string GetQuestMessageFromLumina(string qidRaw, string detail)
    {
        try
        {
            if (string.IsNullOrEmpty(qidRaw) || CurrentSayRegex == null) return string.Empty;

            var matches = CurrentSayRegex.Matches(detail);
            var matchStrings = matches.Cast<Match>()
                .Select(m => m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value ?? m.Value)
                .Distinct()
                .ToList();

            if (matchStrings.Count == 0) return string.Empty;

            var qidStr = qidRaw.PadLeft(5, '0');
            var dir = qidStr.Substring(qidStr.Length - 5, 3);
            var sheetName = $"quest/{dir}/{qidStr}";

            if (!DialogueSheets.TryGetValue(sheetName, out var dialogueSheet))
            {
                if (DialogueSheets.Count > 20) DialogueSheets.Clear();
                
                dialogueSheet = DService.Instance().Data.GetExcelSheet<QuestDialogue>(name: sheetName);
                if (dialogueSheet != null) DialogueSheets[sheetName] = dialogueSheet;
            }

            if (dialogueSheet == null) return string.Empty;

            var bestMessage = string.Empty;
            var minDiff = int.MaxValue;

            foreach (var qd in dialogueSheet)
            {
                if (!KeyRegex.IsMatch(qd.Key.ToString()) || qd.Value.IsEmpty) continue;

                var message = qd.Value.ToString();
                if (string.IsNullOrEmpty(message)) continue;

                foreach (var m in matchStrings)
                {
                    if (m.Contains(message) || message.Contains(m))
                    {
                        var diff = Math.Abs(message.Length - m.Length);
                        if (diff < minDiff)
                        {
                            minDiff = diff;
                            bestMessage = message;
                        }
                    }
                }
            }

            return bestMessage;
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Error(ex, "AutoQuestSay: Error matching quest message");
        }

        return string.Empty;
    }

    #endregion

    #region Nested Types

    private sealed class ActiveSayTask
    {
        public uint QuestID { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string SayMessage { get; set; } = string.Empty;
    }

    #endregion
}

#region Custom Excel Sheets

[Sheet("QuestDialogue")]
internal readonly struct QuestDialogue(RawRow row) : IExcelRow<QuestDialogue>
{
    public uint RowId => row.RowId;
    public ReadOnlySeString Key => row.ReadStringColumn(0);
    public ReadOnlySeString Value => row.ReadStringColumn(1);
    public ExcelPage ExcelPage => row.ExcelPage;
    public uint RowOffset => row.RowOffset;

    static QuestDialogue IExcelRow<QuestDialogue>.Create(ExcelPage page, uint offset, uint row)
    {
        return new QuestDialogue(new RawRow(page, offset, row));
    }
}

#endregion
