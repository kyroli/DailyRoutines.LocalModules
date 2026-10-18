using System;

using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;

using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;

using OmenTools.Dalamud;
using OmenTools.Extensions;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public unsafe class WondrousTailsPredictor : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = IClientState.Instance().ClientLanguage == ClientLanguage.ChineseSimplified ? "天书连线概率" : "Wondrous Tails Predictor",
        Description = IClientState.Instance().ClientLanguage == ClientLanguage.ChineseSimplified ? "在天书界面实时计算并显示连线概率，辅助洗牌决策。" : "Calculates and displays line probabilities in Wondrous Tails, assisting with shuffle decisions.",
        Category    = ModuleCategory.Interface,
        Author      = ["nynpsu"],
        ReportURL   = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    private static readonly byte[] LineTable = new byte[65536];
    private static readonly int[] TotalPaths = [11440, 6435, 3432, 1716, 792, 330, 120, 36, 8, 1];

    private static string[] BaseTitles = [];
    private ushort PrevMask = 0xFFFF;

    static WondrousTailsPredictor()
    {
        ReadOnlySpan<ushort> winLines = [
            0x000F, 0x00F0, 0x0F00, 0xF000, 0x1111, 0x2222, 0x4444, 0x8888, 0x8421, 0x1248
        ];

        for (var state = 0; state < 65536; state++)
            foreach (var w in winLines)
                if ((state & w) == w) LineTable[state]++;
    }

    protected override void Init()
    {
        var al = IAddonLifecycle.Instance();
        al.RegisterListener(AddonEvent.PostSetup,   "WeeklyBingo", OnAddonEvent);
        al.RegisterListener(AddonEvent.PostRefresh, "WeeklyBingo", OnAddonEvent);
        al.RegisterListener(AddonEvent.PreFinalize, "WeeklyBingo", OnAddonEvent);

        BaseTitles = [
            LuminaWrapper.GetAddonText(5611),
            LuminaWrapper.GetAddonText(5612),
            LuminaWrapper.GetAddonText(5613)
        ];

        PrevMask = 0xFFFF;
        var addon = (AddonWeeklyBingo*)Addons.WeeklyBingo;
        if (addon != null) UpdateProbabilities(addon);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddonEvent);
        RestoreAddon();
    }

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PreFinalize)
            PrevMask = 0xFFFF;
        else
            UpdateProbabilities((AddonWeeklyBingo*)args.Addon.Address);
    }

    private void UpdateProbabilities(AddonWeeklyBingo* addon)
    {
        var player = PlayerState.Instance();
        if (player == null || !player->HasWeeklyBingoJournal) return;

        ushort currentMask = 0;
        var stickers = 0;
        for (var i = 0; i < 16; i++)
        {
            if (player->IsWeeklyBingoStickerPlaced(i))
            {
                currentMask |= (ushort)(1 << i);
                stickers++;
            }
        }

        if (currentMask == PrevMask) return;
        PrevMask = currentMask;

        var prob = CalculateExactProbabilities(currentMask, stickers);
        addon->GetTextNodeById(78)->SetText(BuildLineText(BaseTitles[0], prob.Line1, 0.70));
        addon->GetTextNodeById(89)->SetText(BuildLineText(BaseTitles[1], prob.Line2, 0.20));
        addon->GetTextNodeById(100)->SetText(BuildLineText(BaseTitles[2], prob.Line3, 0.05));
    }

    private static void RestoreAddon()
    {
        var addon = (AddonWeeklyBingo*)Addons.WeeklyBingo;
        if (addon == null) return;

        addon->GetTextNodeById(78)->SetText(BaseTitles[0]);
        addon->GetTextNodeById(89)->SetText(BaseTitles[1]);
        addon->GetTextNodeById(100)->SetText(BaseTitles[2]);
    }

    private static byte[] BuildLineText(string title, double val, double highThreshold)
    {
        var text = $"{val:P2}";
        ushort color = val switch
        {
            >= 0.9999 => 32,
            <= 0 => 3,
            _ when val >= highThreshold => 45,
            _ => 0
        };

        var sb = new SeStringBuilder().AddText(title).AddText(" (");
        if (color != 0) sb.AddUiForeground(text, color);
        else sb.AddText(text);

        return sb.AddText(")").Build().Encode();
    }

    private ref struct SearchContext
    {
        public int Line1, Line2, Line3;
        public ReadOnlySpan<byte> EmptySlots;
    }

    private static (double Line1, double Line2, double Line3) CalculateExactProbabilities(ushort state, int placed)
    {
        if (placed == 9)
        {
            var lines = LineTable[state];
            return (lines >= 1 ? 1 : 0, lines >= 2 ? 1 : 0, lines >= 3 ? 1 : 0);
        }

        Span<byte> emptySlots = stackalloc byte[16];
        var emptyCount = 0;
        for (var i = 0; i < 16; i++)
            if ((state & (1 << i)) == 0) emptySlots[emptyCount++] = (byte)i;

        var ctx = new SearchContext { EmptySlots = emptySlots[..emptyCount] };
        Enumerate(0, 9 - placed, state, ref ctx);

        var total = TotalPaths[placed];
        return ((double)ctx.Line1 / total, (double)ctx.Line2 / total, (double)ctx.Line3 / total);

        static void Enumerate(int startIdx, int need, ushort board, ref SearchContext ctx)
        {
            if (need == 1)
            {
                for (var i = startIdx; i < ctx.EmptySlots.Length; i++)
                {
                    var lines = LineTable[board | (1 << ctx.EmptySlots[i])];
                    if (lines >= 1) ctx.Line1++;
                    if (lines >= 2) ctx.Line2++;
                    if (lines >= 3) ctx.Line3++;
                }
                return;
            }

            var limit = ctx.EmptySlots.Length - need;
            for (var i = startIdx; i <= limit; i++)
                Enumerate(i + 1, need - 1, (ushort)(board | (1 << ctx.EmptySlots[i])), ref ctx);
        }
    }
}
