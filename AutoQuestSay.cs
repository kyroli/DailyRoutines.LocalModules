using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;

using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

using OmenTools;
using OmenTools.Dalamud;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

using EventHandler = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler;
using EventHandlerContent = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandlerContent;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoQuestSay : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = DService.Instance().ClientState.ClientLanguage == ClientLanguage.ChineseSimplified ? "自动任务说话" : "Auto Quest Say",
        Description = DService.Instance().ClientState.ClientLanguage == ClientLanguage.ChineseSimplified ? "当任务目标要求在当前频道说出指定台词时，点击目标将自动在当前频道发送台词。" : "Automatically sends the required chat line when clicking on quest targets that require saying specific lines.",
        Category    = ModuleCategory.General,
        Author      = ["nynpsu"],
        ReportURL   = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    public delegate ulong InteractWithObjectDelegate(TargetSystem* system, GameObject* obj, bool checkLOS);

    private static readonly Regex ChineseRegex = new(@"(?:“|""""|「)([^“”""""「」]+?)(?:”|""""|」)", RegexOptions.Compiled);
    private static readonly Regex JapaneseRegex = new(@"(?:「)([^「」]+?)(?:」)", RegexOptions.Compiled);
    private static readonly Regex EnglishRegex = new(@"(?:""""|“)([^""""“”]+?)(?:""""|”)", RegexOptions.Compiled);
    private static readonly Regex GermanRegex = new(@"(?:„|»)([^„“»«]+?)(?:“|«)", RegexOptions.Compiled);
    private static readonly Regex FrenchRegex = new(@"(?:«\s*|“)([^«»“”]+?)(?:\s*»|”)", RegexOptions.Compiled);
    private static readonly Regex SayKeyRegex = new(@"_(SAY|SAYTODO|SYSTEM)_", RegexOptions.Compiled);

    private Hook<InteractWithObjectDelegate>? InteractWithObjectHook;
    private readonly Dictionary<string, ExcelSheet<QuestDialogue>> DialogueSheets = [];
    private ChatManager? Chat;
    private Regex? CurrentSayRegex;
    private long LastMountedTime;

    #region Module Lifecycle

    protected override void Init()
    {
        Chat = DService.Instance().GetOmenService<ChatManager>();

        CurrentSayRegex = DService.Instance().ClientState.ClientLanguage switch
        {
            ClientLanguage.Japanese => JapaneseRegex,
            ClientLanguage.English  => EnglishRegex,
            ClientLanguage.German   => GermanRegex,
            ClientLanguage.French   => FrenchRegex,
            _                       => ChineseRegex
        };

        InteractWithObjectHook ??= DService.Instance().Hook.HookFromMemberFunction<InteractWithObjectDelegate>(
            typeof(TargetSystem.MemberFunctionPointers), "InteractWithObject", InteractWithObjectDetour);
        
        InteractWithObjectHook.Enable();
    }

    protected override void Uninit()
    {
        if (InteractWithObjectHook != null)
        {
            InteractWithObjectHook.Disable();
            InteractWithObjectHook.Dispose();
            InteractWithObjectHook = null;
        }

        DialogueSheets.Clear();
        LastMountedTime = 0;
    }

    #endregion

    #region Core Logic

    private ulong InteractWithObjectDetour(TargetSystem* system, GameObject* obj, bool checkLOS)
    {
        if (!ShouldProcessInteraction(obj, out var questID))
            return InteractWithObjectHook!.Original(system, obj, checkLOS);

        var sayMessage = GetSayMessageFromLumina(questID);

        if (!string.IsNullOrEmpty(sayMessage) && Throttler<string>.Shared.Throttle("AutoQuestSay-Say", 800))
        {
            Chat?.SendMessage($"/say {sayMessage.Trim()}");
        }

        return InteractWithObjectHook!.Original(system, obj, checkLOS);
    }

    private bool ShouldProcessInteraction(GameObject* obj, out ushort questID)
    {
        questID = 0;
        if (obj == null || obj->ObjectKind is not (ObjectKind.EventNpc or ObjectKind.EventObj))
            return false;

        var condition = DService.Instance().Condition;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.OccupiedInCutSceneEvent])
            return false;

        if (condition[ConditionFlag.Mounted])
        {
            LastMountedTime = Environment.TickCount64;
            return false;
        }

        if (Environment.TickCount64 - LastMountedTime < 400)
            return false;

        questID = GetQuestIDFromObject(obj);
        if (questID == 0)
            return false;

        var questManager = QuestManager.Instance();
        return questManager != null && questManager->IsQuestAccepted(questID);
    }

    private static ushort GetQuestIDFromObject(GameObject* obj)
    {
        var primaryEvent = obj->EventId;
        if (primaryEvent.ContentId == EventHandlerContent.Quest)
            return primaryEvent.EntryId;

        var handlers = stackalloc EventHandler*[32];
        var handlerCount = obj->GetEventHandlersImpl(handlers);

        for (var i = 0; i < handlerCount; i++)
        {
            var handler = handlers[i];
            if (handler != null && handler->Info.EventId.ContentId == EventHandlerContent.Quest)
                return handler->Info.EventId.EntryId;
        }

        return 0;
    }

    private string GetSayMessageFromLumina(ushort questID)
    {
        try
        {
            if (!TryGetDialogueSheet(questID, out var dialogueSheet))
                return string.Empty;

            // 借鉴 NoTypeSay 算法核心：遍历所有可能的 SAY 节点与指引描述文本求交集匹配
            foreach (var qd in dialogueSheet!)
            {
                if (qd.Value.IsEmpty) continue;
                var keyStr = qd.Key.ToString();

                // 筛选包含 _SAY_ / _SAYTODO_ / _SYSTEM_ 的候选台词节点
                if (!SayKeyRegex.IsMatch(keyStr)) continue;

                var candidateMessage = qd.Value.ToDalamudString().TextValue.Trim();
                if (string.IsNullOrEmpty(candidateMessage)) continue;

                // 验证该候选台词是否被包含在该 QuestDialogue 对话表的目标指引（TODO/SEQ）中
                if (IsMessageMatchedByGuidance(dialogueSheet, candidateMessage))
                    return candidateMessage;
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Error(ex, $"AutoQuestSay: Failed to get say message for quest {questID}");
        }

        return string.Empty;
    }

    private bool IsMessageMatchedByGuidance(ExcelSheet<QuestDialogue> dialogueSheet, string candidateMessage)
    {
        if (CurrentSayRegex == null) return false;

        foreach (var entry in dialogueSheet)
        {
            if (entry.Value.IsEmpty) continue;
            var keyStr = entry.Key.ToString();

            // 忽略台词节点本身，只比对指引与描述节点（如 TODO / SEQ）
            if (SayKeyRegex.IsMatch(keyStr)) continue;

            var guidanceText = entry.Value.ToDalamudString().TextValue;
            var matches = CurrentSayRegex.Matches(guidanceText);

            // NoTypeSay 算法精髓：判断当前指引句中被引号包裹的词汇，是否包含候选台词 candidateMessage
            if (matches.Any(m => m.Value.Contains(candidateMessage)))
                return true;
        }

        return false;
    }

    private bool TryGetDialogueSheet(ushort questID, out ExcelSheet<QuestDialogue>? dialogueSheet)
    {
        dialogueSheet = null;
        var questRow = LuminaGetter.GetRow<Quest>(questID + 65536U);
        if (questRow == null || questRow.Value.Id.IsEmpty)
            return false;

        var qIDStr = questRow.Value.Id.ToString().PadLeft(5, '0');
        var dir = qIDStr.Length >= 5 ? qIDStr[^5..^2] : "000";
        var sheetName = $"quest/{dir}/{qIDStr}";

        if (!DialogueSheets.TryGetValue(sheetName, out dialogueSheet))
        {
            dialogueSheet = DService.Instance().Data.GetExcelSheet<QuestDialogue>(name: sheetName);
            if (dialogueSheet != null)
                DialogueSheets[sheetName] = dialogueSheet;
        }

        return dialogueSheet != null;
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
