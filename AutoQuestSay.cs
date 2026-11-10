#nullable enable

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;

using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
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

    private readonly Dictionary<ushort, SayQuestData?> questCache = [];
    private ChatManager?   chat;
    private TargetManager? target;
    private uint           lastTargetID;
    private long           lastSayTime;

    #region Lifecycle

    protected override void Init()
    {
        chat   = ChatManager.Instance();
        target = TargetManager.Instance();
        target.RegPreInteractWithObject(OnPreInteractWithObject);
    }

    protected override void Uninit()
    {
        target?.Unreg((TargetManager.PreInteractWithObjectDelegate)OnPreInteractWithObject);
        questCache.Clear();
        lastTargetID = 0;
        lastSayTime  = 0;
    }

    #endregion

    #region Interaction Handling

    private void OnPreInteractWithObject(ref bool isPrevented, ref IGameObject? targetObj, ref bool checkLOS)
    {
        // 静态目标白名单过滤
        if (targetObj == null || !SayTargetDataIDs.Contains(targetObj.DataID))
            return;

        try
        {
            // 目标可交互状态校验
            if (!targetObj.IsTargetable)
                return;

            // 任务管理器与当前说话任务检索
            var questManager = QuestManager.Instance();
            if (questManager == null) return;

            if (!TryMatchActiveSayQuest(questManager, targetObj.DataID, out var phrase))
                return;

            // 玩家状态校验（防骑乘、占用、战斗、读条与生产采集）
            if (ICondition.Instance().Any(BlockedConditions))
                return;

            // 交互有效距离校验
            var localPlayer = LocalPlayerState.Object;
            if (localPlayer == null || Vector3.Distance(localPlayer.Position, targetObj.Position) > 5.5f)
                return;

            // 拦截原生交互，防止弹出对话树打断暗号判定
            isPrevented = true;

            // 冷却防抖与截流校验
            var now = Environment.TickCount64;
            if (targetObj.DataID == lastTargetID && now - lastSayTime < 3000)
                return;

            lastTargetID = targetObj.DataID;
            lastSayTime  = now;
            chat?.SendMessage($"/say {phrase}");
        }
        catch (Exception ex)
        {
            DLog.Error("AutoQuestSay: Failed to process target interaction.", ex);
        }
    }

    private bool TryMatchActiveSayQuest(QuestManager* questManager, uint targetID, out string phrase)
    {
        phrase = string.Empty;
        var normalQuests = questManager->NormalQuests;

        for (var i = 0; i < normalQuests.Length; i++)
        {
            var q = normalQuests[i];
            if (q.QuestId == 0 || !SayQuestIDs.Contains(q.QuestId))
                continue;

            if (!questCache.TryGetValue(q.QuestId, out var data))
            {
                data = LoadSayQuestData(q.QuestId);
                questCache[q.QuestId] = data;
            }

            if (data == null) continue;

            foreach (var step in data.Steps)
            {
                if (step.Sequence == q.Sequence && step.TargetIDs.Contains(targetID))
                {
                    phrase = step.Phrase;
                    return true;
                }
            }
        }

        return false;
    }

    #endregion

    #region Dialogue & Sheet Extraction

    private static SayQuestData? LoadSayQuestData(ushort questID)
    {
        var questRow = LuminaGetter.GetRow<Quest>(questID + 65536U);
        if (questRow == null || questRow.Value.Id.IsEmpty)
            return null;

        var seqToTargets = new Dictionary<byte, HashSet<uint>>();
        foreach (var param in questRow.Value.QuestListenerParams)
        {
            if (param.ConditionType == 23 || param.ConditionValue == 23)
            {
                var targetID = param.ConditionType != 23 && param.ConditionType != 0
                    ? (uint)param.ConditionType
                    : (param.ConditionValue != 23 && param.ConditionValue != 0 ? param.ConditionValue : param.Listener);

                if (targetID != 0 && targetID != 255)
                {
                    var seq = param.ActorSpawnSeq;
                    if (!seqToTargets.TryGetValue(seq, out var set))
                    {
                        set = [];
                        seqToTargets[seq] = set;
                    }
                    set.Add(targetID);
                }
            }
        }

        if (seqToTargets.Count == 0) return null;

        var qIDStr = questRow.Value.Id.ToString().PadLeft(5, '0');
        var dir = qIDStr.Length >= 5 ? qIDStr[^5..^2] : "000";
        var sheetName = $"quest/{dir}/{qIDStr}";

        var dialogueSheet = IDataManager.Instance().GetExcelSheet<QuestDialogue>(name: sheetName);
        if (dialogueSheet == null) return null;

        var (seqToPhrases, fallbackPhrase) = ExtractPhrases(dialogueSheet);
        var steps = new List<SayStep>();

        foreach (var (seq, targets) in seqToTargets)
        {
            if (seqToPhrases.TryGetValue(seq, out var p))
                steps.Add(new SayStep(seq, targets, p));
            else if (!string.IsNullOrEmpty(fallbackPhrase))
                steps.Add(new SayStep(seq, targets, fallbackPhrase));
        }

        return steps.Count > 0 ? new SayQuestData(steps) : null;
    }

    private static (Dictionary<byte, string> SeqToPhrases, string? FallbackPhrase) ExtractPhrases(
        ExcelSheet<QuestDialogue> dialogueSheet)
    {
        var seqToPhrases = new Dictionary<byte, string>();
        string? pureSayPhrase = null;
        string? fallbackPhrase = null;

        foreach (var entry in dialogueSheet)
        {
            if (entry.Value.IsEmpty) continue;
            var key = entry.Key.ToString();

            // 优先匹配独立暗号台词节点
            if (pureSayPhrase == null && (key.Contains("_SAY_") || key.Contains("_SAYTODO_")))
            {
                var text = TagRegex.Replace(entry.Value.ExtractText().Trim(), string.Empty).Trim();
                if (!string.IsNullOrEmpty(text))
                    pureSayPhrase = text;
            }

            if (!key.Contains("_TODO_") && !key.Contains("_SEQ_") && !key.Contains("_SYSTEM_"))
                continue;

            var val = TagRegex.Replace(entry.Value.ExtractText().Trim(), " ").Trim();
            if (string.IsNullOrEmpty(val)) continue;

            var phrase = ExtractQuotePhrase(val);
            if (string.IsNullOrEmpty(phrase)) continue;

            var match = SeqSuffixRegex.Match(key);
            if (match.Success && byte.TryParse(match.Groups[1].Value, out var seq) && seq > 0)
                seqToPhrases.TryAdd(seq, phrase);
            else
                fallbackPhrase ??= phrase;
        }

        return (seqToPhrases, pureSayPhrase ?? fallbackPhrase);
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

    #region Structs, Static Target Pools & Constants

    private sealed record SayQuestData(List<SayStep> Steps);
    private readonly record struct SayStep(byte Sequence, HashSet<uint> TargetIDs, string Phrase);

    private static readonly ConditionFlag[] BlockedConditions =
    [
        ConditionFlag.Occupied,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.Mounted,
        ConditionFlag.MountOrOrnamentTransition,
        ConditionFlag.InCombat,
        ConditionFlag.Casting,
        ConditionFlag.Casting87,
        ConditionFlag.BetweenAreas,
        ConditionFlag.Crafting,
        ConditionFlag.Gathering,
        ConditionFlag.Fishing
    ];

    private static readonly Regex SeqSuffixRegex = new(@"_SEQ_(\d{2})", RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>|\*+", RegexOptions.Compiled);
    private static readonly Regex QuoteRegex = new(@"[""“「『„«\[]([^""”」』“«»\]\r\n]+?)[""”」』“»\]]", RegexOptions.Compiled);
    private static readonly Regex FrenchAppelerRegex = new(@"appeler\s+([A-Za-zÀ-ÿ\s\'-]+?)(?:\s+en prononçant|,|\s+devant)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ChannelKeywords =
    [
        "say", "sagen", "dire", "chat mode", "chat-modus", "chatmodus", "mode de discussion",
        "说话", "说话频道"
    ];

    private static readonly FrozenSet<ushort> SayQuestIDs = new ushort[]
    {
        287, 487, 502, 1428, 1429, 1455, 1497, 1501, 1507, 1515, 1568, 1656, 1674, 1910, 1970, 2058,
        2210, 2257, 2277, 2295, 2500, 2574, 2689, 2695, 2757, 2864, 2985, 3012, 3104, 3203, 3204, 3214,
        3221, 3234, 3310, 3325, 3358, 3395, 3397, 3426, 3441, 3444, 3550, 3691, 3712, 3819, 3909, 3952,
        3978, 4241, 4252, 4262, 4344, 4425, 4577, 4608, 4700, 4746, 4870, 5048, 5105, 5275, 5349, 5481
    }.ToFrozenSet();

    private static readonly FrozenSet<uint> SayTargetDataIDs = new uint[]
    {
        1000100, 1000461, 1000981, 1000984, 1001313, 1005576, 1009633, 1009634, 1009637, 1009639, 1009649,
        1010727, 1012413, 1013138, 1014171, 1014413, 1016848, 1016871, 1016872, 1016873, 1016874, 1017365,
        1017366, 1017367, 1017368, 1019360, 1019376, 1020281, 1021381, 1023259, 1024525, 1024526, 1024527,
        1024528, 1027463, 1027750, 1028484, 1028922, 1029024, 1029141, 1029197, 1030382, 1030810, 1030811,
        1031526, 1032140, 1032141, 1032142, 1032208, 1032413, 1033770, 1033771, 1033772, 1034520, 1040061,
        1041789, 1041790, 1041791, 1042387, 1042388, 1042472, 1042473, 1044043, 1044044, 1044585, 1046699,
        1046752, 1046753, 1050694, 1051056, 1052572, 1052573, 1052574, 1055825, 1055826, 2004149, 2005188,
        2006080, 2006305, 2007423, 2008168, 2008169, 2008379, 2008502, 2009099, 2009491, 2009492, 2009827,
        2009979, 2009997, 2010182, 2010183, 2010256, 2010722, 2011156, 2012260, 2012313, 2012600, 2015339
    }.ToFrozenSet();

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
