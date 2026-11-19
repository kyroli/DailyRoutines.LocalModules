#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;

using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

using OmenTools.Dalamud;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

using IGameObject = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IGameObject;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoQuestSay : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = IClientState.Instance().ClientLanguage == ClientLanguage.ChineseSimplified ? "自动任务说话" : "Auto Quest Say",
        Description = IClientState.Instance().ClientLanguage == ClientLanguage.ChineseSimplified ? "当任务目标要求在当前频道说出指定台词时，点击目标将自动在当前频道发送台词。" : "Automatically sends the required chat line when clicking on quest targets that require saying specific lines.",
        Category    = ModuleCategory.General,
        Author      = ["nynpsu"],
        ReportURL   = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    public override string ModuleName => nameof(AutoQuestSay);

    private ChatManager?   chat;
    private TargetManager? target;
    private uint           lastTargetID;
    private long           lastSayTime;

    #region Lifecycle

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 3_000 };
        chat   = ChatManager.Instance();
        target = TargetManager.Instance();
        target.RegPreInteractWithObject(OnPreInteractWithObject);
    }

    protected override void Uninit()
    {
        target?.Unreg((TargetManager.PreInteractWithObjectDelegate)OnPreInteractWithObject);
        TaskHelper?.Abort();
        TaskHelper?.Dispose();
        TaskHelper = null;
        lastTargetID = 0;
        lastSayTime  = 0;
    }

    #endregion

    #region Interaction Handling

    private void OnPreInteractWithObject(ref bool isPrevented, ref IGameObject? targetObj, ref bool checkLOS)
    {
        if (targetObj == null || targetObj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.EventObj) || !targetObj.IsTargetable)
            return;

        try
        {
            var questManager = QuestManager.Instance();
            if (questManager == null) return;

            if (!TryMatchActiveSayQuest(questManager, targetObj.DataID, out var phrase))
                return;

            if (ICondition.Instance().Any(BlockedConditions))
                return;

            var localPlayer = LocalPlayerState.Object;
            if (localPlayer == null || Vector3.Distance(localPlayer.Position, targetObj.Position) > 7.5f + targetObj.HitboxRadius)
                return;

            // 拦截原生交互，防止弹出对话树打断暗号判定
            isPrevented = true;

            var now = Environment.TickCount64;
            if (targetObj.DataID == lastTargetID && now - lastSayTime < 3000)
                return;

            lastTargetID = targetObj.DataID;
            lastSayTime  = now;

            if (ICondition.Instance()[ConditionFlag.Mounted])
                DismountAndSay(phrase);
            else
                chat?.SendMessage($"/say {phrase}");
        }
        catch (Exception ex)
        {
            DLog.Error("AutoQuestSay: Failed to process target interaction.", ex);
        }
    }

    private void DismountAndSay(string phrase)
    {
        TaskHelper?.Abort();
        MovementManager.Instance().Dismount();
        TaskHelper?.Enqueue(() =>
            !MovementManager.Instance().IsManagerBusy &&
            !ICondition.Instance().Any(ConditionFlag.Mounted, ConditionFlag.MountOrOrnamentTransition, ConditionFlag.Jumping));
        TaskHelper?.DelayNext(100);
        TaskHelper?.Enqueue(() =>
        {
            if (!ICondition.Instance().Any(BlockedConditions) && !ICondition.Instance()[ConditionFlag.Mounted])
                chat?.SendMessage($"/say {phrase}");
        });
    }

    private static bool TryMatchActiveSayQuest(QuestManager* questManager, uint targetID, out string phrase)
    {
        phrase = string.Empty;
        var normalQuests = questManager->NormalQuests;

        for (var i = 0; i < normalQuests.Length; i++)
        {
            var q = normalQuests[i];
            if (q.QuestId == 0)
                continue;

            var questRow = LuminaGetter.GetRow<Quest>(q.QuestId + 65536U);
            if (questRow == null || questRow.Value.Id.IsEmpty)
                continue;

            var matched = false;
            foreach (var param in questRow.Value.QuestListenerParams)
            {
                // Lumina.Excel 中 QuestUInt8A (offset +14) 存储实际监听器动作类型，23 表示说话频道暗号
                if (param.QuestUInt8A == 23 && param.ActorSpawnSeq == q.Sequence && param.Listener == targetID)
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
                continue;

            var extracted = ExtractSayPhrase(questRow.Value, q.Sequence);
            if (!string.IsNullOrEmpty(extracted))
            {
                phrase = extracted;
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Dialogue & Sheet Extraction

    private static string? ExtractSayPhrase(Quest questRow, byte sequence)
    {
        var qIDStr = questRow.Id.ToString().PadLeft(5, '0');
        var dir = qIDStr.Length >= 5 ? qIDStr[^5..^2] : "000";
        var sheetName = $"quest/{dir}/{qIDStr}";

        var dialogueSheet = IDataManager.Instance().GetExcelSheet<QuestDialogue>(name: sheetName);
        if (dialogueSheet == null) return null;

        string? seqPhrase = null;
        string? pureSayPhrase = null;
        string? fallbackPhrase = null;

        foreach (var entry in dialogueSheet)
        {
            if (entry.Value.IsEmpty) continue;
            var key = entry.Key.ToString();

            if (pureSayPhrase == null && (key.Contains("_SAY_") || key.Contains("_SAYTODO_")))
            {
                var text = TagRegex.Replace(entry.Value.ExtractText().Trim(), string.Empty).Trim();
                if (!string.IsNullOrEmpty(text))
                    pureSayPhrase = text;
            }

            var todoMatch = TodoSuffixRegex.Match(key);
            if (todoMatch.Success && int.TryParse(todoMatch.Groups[1].Value, out var todoIdx))
            {
                var val = TagRegex.Replace(entry.Value.ExtractText().Trim(), " ").Trim();
                var phrase = ExtractQuotePhrase(val);
                if (!string.IsNullOrEmpty(phrase))
                {
                    if (todoIdx >= 0 && todoIdx < questRow.TodoParams.Count && questRow.TodoParams[todoIdx].ToDoCompleteSeq == sequence)
                        return phrase;
                    fallbackPhrase ??= phrase;
                }
                continue;
            }

            var seqMatch = SeqSuffixRegex.Match(key);
            if (seqMatch.Success && byte.TryParse(seqMatch.Groups[1].Value, out var seqNum) && seqNum > 0)
            {
                var val = TagRegex.Replace(entry.Value.ExtractText().Trim(), " ").Trim();
                var phrase = ExtractQuotePhrase(val);
                if (!string.IsNullOrEmpty(phrase))
                {
                    if (seqPhrase == null && seqNum == sequence)
                        seqPhrase = phrase;
                    fallbackPhrase ??= phrase;
                }
                continue;
            }

            if (key.Contains("_SYSTEM_"))
            {
                var val = TagRegex.Replace(entry.Value.ExtractText().Trim(), " ").Trim();
                var phrase = ExtractQuotePhrase(val);
                if (!string.IsNullOrEmpty(phrase))
                    fallbackPhrase ??= phrase;
            }
        }

        return seqPhrase ?? pureSayPhrase ?? fallbackPhrase;
    }

    private static string? ExtractQuotePhrase(string text)
    {
        foreach (Match match in QuoteRegex.Matches(text))
        {
            var p = match.Groups[1].Value.Trim();
            if (p.Length > 0 && !ChannelKeywords.Contains(p.ToLowerInvariant()))
                return p;
        }

        // 无引号法语句式提取兜底
        var frMatch = FrenchAppelerRegex.Match(text);
        return frMatch.Success ? frMatch.Groups[1].Value.Trim() : null;
    }

    #endregion

    #region Constants

    private static readonly ConditionFlag[] BlockedConditions =
    [
        ConditionFlag.Occupied,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.InFlight,
        ConditionFlag.RidingPillion,
        ConditionFlag.MountOrOrnamentTransition,
        ConditionFlag.InCombat,
        ConditionFlag.Casting,
        ConditionFlag.Casting87,
        ConditionFlag.BetweenAreas,
        ConditionFlag.Crafting,
        ConditionFlag.Gathering,
        ConditionFlag.Fishing
    ];

    private static readonly Regex TodoSuffixRegex = new(@"_TODO_(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex SeqSuffixRegex = new(@"_SEQ_(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>|\*+", RegexOptions.Compiled);
    private static readonly Regex QuoteRegex = new(@"[""“「『„«\[]([^""”」』“«»\]\r\n]+?)[""”」』“»\]]", RegexOptions.Compiled);
    private static readonly Regex FrenchAppelerRegex = new(@"appeler\s+([A-Za-zÀ-ÿ\s\'-]+?)(?:\s+en prononçant|,|\s+devant)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ChannelKeywords =
    [
        "say", "sagen", "dire", "chat mode", "chat-modus", "chatmodus", "mode de discussion",
        "说话", "说话频道"
    ];

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
