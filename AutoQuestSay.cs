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
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
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
    private long           lastUnstableEndTime;

    #region Lifecycle

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 5_000, RetryIntervalMS = 30 };
        chat   = ChatManager.Instance();
        target = TargetManager.Instance();
        target.RegPreInteractWithObject(OnPreInteractWithObject);
        ICondition.Instance().ConditionChange += OnConditionChanged;
    }

    protected override void Uninit()
    {
        ICondition.Instance().ConditionChange -= OnConditionChanged;
        target?.Unreg((TargetManager.PreInteractWithObjectDelegate)OnPreInteractWithObject);
        TaskHelper?.Abort();
        TaskHelper?.Dispose();
        TaskHelper          = null;
        lastTargetID        = 0;
        lastSayTime         = 0;
        lastUnstableEndTime = 0;
    }

    #endregion

    #region Interaction Handling

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (!value && flag is ConditionFlag.Jumping or ConditionFlag.Jumping61 or ConditionFlag.Mounted or ConditionFlag.MountOrOrnamentTransition)
            lastUnstableEndTime = Environment.TickCount64;
    }

    private static bool IsPlayerJumping()
    {
        if (ICondition.Instance().Any(ConditionFlag.Jumping, ConditionFlag.Jumping61))
            return true;

        var chara = LocalPlayerState.Object?.ToStruct();
        return chara != null && chara->IsJumping();
    }

    private void ResetDebounce()
    {
        lastTargetID = 0;
        lastSayTime  = 0;
    }

    private void OnPreInteractWithObject(ref bool isPrevented, ref IGameObject? targetObj, ref bool checkLOS)
    {
        if (targetObj == null || targetObj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.EventObj) || !targetObj.IsTargetable)
            return;

        try
        {
            var questManager = QuestManager.Instance();
            if (questManager == null || !TryMatchActiveSayQuest(questManager, targetObj.DataID, out var phrase))
                return;

            var condition = ICondition.Instance();
            if (condition.Any(BlockedConditions))
                return;

            var localPlayer = LocalPlayerState.Object;
            var maxDist     = 7.5f + targetObj.HitboxRadius;
            var maxDistSq   = maxDist * maxDist;
            if (localPlayer == null || Vector3.DistanceSquared(localPlayer.Position, targetObj.Position) > maxDistSq)
                return;

            // 拦截原生交互，防止弹出对话树打断暗号判定
            isPrevented = true;

            var now = Environment.TickCount64;
            if (targetObj.DataID == lastTargetID && now - lastSayTime < 3000)
                return;

            lastTargetID = targetObj.DataID;
            lastSayTime  = now;

            if (!condition[ConditionFlag.Mounted]                  &&
                !condition[ConditionFlag.MountOrOrnamentTransition] &&
                !IsPlayerJumping()                                 &&
                now - lastUnstableEndTime >= GroundedSettleMS)
            {
                TaskHelper?.Abort();
                chat?.SendMessage($"/say {phrase}");
                return;
            }

            WaitAndSay(targetObj.DataID, targetObj.Position, maxDistSq, phrase);
        }
        catch (Exception ex)
        {
            DLog.Error("AutoQuestSay: Failed to process target interaction.", ex);
        }
    }

    private void WaitAndSay(uint targetID, Vector3 targetPos, float maxDistSq, string phrase)
    {
        TaskHelper?.Abort();

        var dismountSent = false;
        var groundedAt   = 0L;

        TaskHelper?.Enqueue(() =>
        {
            var condition = ICondition.Instance();
            if (condition.Any(BlockedConditions))
            {
                ResetDebounce();
                return true;
            }

            var now     = Environment.TickCount64;
            var jumping = IsPlayerJumping();

            if (condition[ConditionFlag.Mounted])
            {
                if (!dismountSent && !jumping && now - lastUnstableEndTime >= DismountGroundedMS)
                {
                    MountCommand.Dismount();
                    dismountSent = true;
                }

                groundedAt = 0;
                return false;
            }

            if (condition[ConditionFlag.MountOrOrnamentTransition] || jumping)
            {
                groundedAt = 0;
                return false;
            }

            if (groundedAt == 0)
                groundedAt = now;

            if (now - Math.Max(groundedAt, lastUnstableEndTime) < GroundedSettleMS)
                return false;

            var localPlayer = LocalPlayerState.Object;
            if (localPlayer == null || Vector3.DistanceSquared(localPlayer.Position, targetPos) > maxDistSq)
            {
                ResetDebounce();
                return true;
            }

            lastTargetID = targetID;
            lastSayTime  = now;
            chat?.SendMessage($"/say {phrase}");
            return true;
        }, "WaitAndSay", timeoutAction: ResetDebounce);
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
        var qIDStr        = questRow.Id.ToString().PadLeft(5, '0');
        var dir           = qIDStr.Length >= 5 ? qIDStr[^5..^2] : "000";
        var dialogueSheet = IDataManager.Instance().GetExcelSheet<QuestDialogue>(name: $"quest/{dir}/{qIDStr}");
        if (dialogueSheet == null)
            return null;

        string? seqPhrase      = null;
        string? pureSayPhrase  = null;
        string? fallbackPhrase = null;

        foreach (var entry in dialogueSheet)
        {
            if (entry.Value.IsEmpty)
                continue;

            var key = entry.Key.ToString();

            if (pureSayPhrase == null && (key.Contains("_SAY_") || key.Contains("_SAYTODO_")))
            {
                var text = TagRegex.Replace(entry.Value.ExtractText().Trim(), string.Empty).Trim();
                if (!string.IsNullOrEmpty(text))
                    pureSayPhrase = text;
            }

            if (TodoSuffixRegex.Match(key) is { Success: true } todo && int.TryParse(todo.Groups[1].Value, out var todoIdx))
            {
                if (TryExtractQuote(entry.Value) is { } p)
                {
                    if (todoIdx >= 0 && todoIdx < questRow.TodoParams.Count && questRow.TodoParams[todoIdx].ToDoCompleteSeq == sequence)
                        return p;

                    fallbackPhrase ??= p;
                }
            }
            else if (SeqSuffixRegex.Match(key) is { Success: true } seq && byte.TryParse(seq.Groups[1].Value, out var seqNum) && seqNum > 0)
            {
                if (TryExtractQuote(entry.Value) is { } p)
                {
                    if (seqPhrase == null && seqNum == sequence)
                        seqPhrase = p;

                    fallbackPhrase ??= p;
                }
            }
            else if (key.Contains("_SYSTEM_") && TryExtractQuote(entry.Value) is { } sysPhrase)
            {
                fallbackPhrase ??= sysPhrase;
            }
        }

        return seqPhrase ?? pureSayPhrase ?? fallbackPhrase;
    }

    private static string? TryExtractQuote(ReadOnlySeString value) =>
        ExtractQuotePhrase(TagRegex.Replace(value.ExtractText().Trim(), " ").Trim());

    private static string? ExtractQuotePhrase(string text)
    {
        foreach (Match match in QuoteRegex.Matches(text))
        {
            var p = match.Groups[1].Value.Trim();
            if (p.Length > 0 && !ChannelKeywords.Contains(p))
                return p;
        }

        // 无引号法语句式提取兜底
        var frMatch = FrenchAppelerRegex.Match(text);
        return frMatch.Success ? frMatch.Groups[1].Value.Trim() : null;
    }

    #endregion

    #region Constants

    private const long GroundedSettleMS   = 300;
    private const long DismountGroundedMS = 150;

    private static readonly ConditionFlag[] BlockedConditions =
    [
        ConditionFlag.Occupied,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.InFlight,
        ConditionFlag.RidingPillion,
        ConditionFlag.InCombat,
        ConditionFlag.Casting,
        ConditionFlag.Casting87,
        ConditionFlag.BetweenAreas,
        ConditionFlag.Crafting,
        ConditionFlag.Gathering,
        ConditionFlag.Fishing
    ];

    private static readonly Regex TodoSuffixRegex = new(@"_TODO_(\d{2})$");
    private static readonly Regex SeqSuffixRegex = new(@"_SEQ_(\d{2})$");
    private static readonly Regex TagRegex = new(@"<[^>]+>|\*+");
    private static readonly Regex QuoteRegex = new(@"[""“「『„«\[]([^""”」』“«»\]\r\n]+?)[""”」』“»\]]");
    private static readonly Regex FrenchAppelerRegex = new(@"appeler\s+([A-Za-zÀ-ÿ\s\'-]+?)(?:\s+en prononçant|,|\s+devant)", RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ChannelKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "say", "sagen", "dire", "chat mode", "chat-modus", "chatmodus", "mode de discussion",
        "说话", "说话频道"
    };

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
