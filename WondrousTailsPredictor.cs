using System;
using System.Collections.Generic;

using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;

using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Memory;

using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using OmenTools;

namespace DailyRoutines.ModulesPublic;

public unsafe class WondrousTailsPredictor : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "天书连线概率",
        Description = "在天书界面实时计算并显示连线概率与重排期望。",
        Category    = ModuleCategory.UIOptimization,
        Author      = ["nynpsu"],
        ReportURL   = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    private static readonly ushort[] WinLines = [
        0x000F, 0x00F0, 0x0F00, 0xF000, 0x1111, 0x2222, 0x4444, 0x8888, 0x8421, 0x1248
    ];

    private static readonly Dictionary<ushort, double[]> MathCache = [];
    
    private ushort PrevMask = 0xFFFF;

    private double[]? GlobalShuffleExpectation;
    private (string Current, string Shuffle, string Line) LocStrings;

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "WeeklyBingo", OnAddonEvent);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "WeeklyBingo", OnAddonEvent);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "WeeklyBingo", OnAddonEvent);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "WeeklyBingo", OnAddonEvent);

        GlobalShuffleExpectation ??= CalculateExactProbabilities(0, 0);
        
        LocStrings = DService.Instance().ClientState.ClientLanguage switch
        {
            ClientLanguage.English => ("Current", "Shuffle", " Line(s)"),
            _ => ("当前", "重排", "线")
        };

        RefreshAddon();
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonEvent);
        RestoreAddon();
    }

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        if (args.Addon.Address == nint.Zero) return;
        
        switch (type)
        {
            case AddonEvent.PostSetup:
            case AddonEvent.PostRefresh:
                PrevMask = 0xFFFF;
                UpdateProbabilities((AddonWeeklyBingo*)args.Addon.Address);
                break;
            case AddonEvent.PostUpdate:
                UpdateProbabilities((AddonWeeklyBingo*)args.Addon.Address);
                break;
            case AddonEvent.PreFinalize:
                PrevMask = 0xFFFF;
                break;
        }
    }

    private void RefreshAddon()
    {
        var ptr = DService.Instance().GameGUI.GetAddonByName("WeeklyBingo").Address;
        if (ptr != nint.Zero)
        {
            PrevMask = 0xFFFF;
            UpdateProbabilities((AddonWeeklyBingo*)ptr);
        }
    }

    private void RestoreAddon()
    {
        var ptr = DService.Instance().GameGUI.GetAddonByName("WeeklyBingo").Address;
        if (ptr == nint.Zero) return;
        
        var addon = (AddonWeeklyBingo*)ptr;
        var node = addon->StringThing.TextNode;
        if (node != null && node->NodeText.StringPtr.Value != null)
        {
            var currentSeString = MemoryHelper.ReadSeStringNullTerminated((nint)node->NodeText.StringPtr.Value);
            var baseText = StripOurText(currentSeString);
            node->SetText(baseText.Encode());
        }
    }

    private void UpdateProbabilities(AddonWeeklyBingo* addon)
    {
        var state = PlayerState.Instance();
        ushort currentMask = 0;
        int stickers = 0;

        for (var i = 0; i < 16; i++)
        {
            if (state->IsWeeklyBingoStickerPlaced(i))
            {
                currentMask |= (ushort)(1 << i);
                stickers++;
            }
        }

        var textNode = addon->StringThing.TextNode;
        if (textNode == null || textNode->NodeText.StringPtr.Value == null) return;

        var currentSeString = MemoryHelper.ReadSeStringNullTerminated((nint)textNode->NodeText.StringPtr.Value);
        var currentText = currentSeString.TextValue ?? string.Empty;

        if (string.IsNullOrWhiteSpace(currentText)) return;

        bool hasOurText = currentText.Contains($"{LocStrings.Current}: ");

        if (currentMask == PrevMask && hasOurText) return;

        PrevMask = currentMask;

        var currentProb = CalculateExactProbabilities(currentMask, stickers);
        var sb = new SeStringBuilder();
        
        sb.Append(StripOurText(currentSeString));
        sb.AddText("\n");

        sb.AddText($"{LocStrings.Current}: ");
        AppendProbabilityLine(sb, currentProb, GlobalShuffleExpectation!, LocStrings.Line);
        
        sb.AddText($"\n{LocStrings.Shuffle}: ");
        if (stickers is > 0 and <= 7)
        {
            AppendProbabilityLine(sb, GlobalShuffleExpectation!, null, LocStrings.Line);
        }
        else
        {
            sb.AddText($"1{LocStrings.Line}:-  2{LocStrings.Line}:-  3{LocStrings.Line}:-");
        }

        textNode->SetText(sb.Build().Encode());
    }

    private SeString StripOurText(SeString original)
    {
        var result = new List<Payload>();
        var marker = $"{LocStrings.Current}: ";

        for (int i = 0; i < original.Payloads.Count; i++)
        {
            var p = original.Payloads[i];

            if (p is TextPayload tp && tp.Text != null)
            {
                var text = tp.Text;
                var idx = text.IndexOf(marker, StringComparison.Ordinal);

                if (idx >= 0)
                {
                    if (idx > 0) 
                    {
                        var beforeText = text[..idx];
                        if (beforeText != "\n") 
                        {
                            result.Add(new TextPayload(beforeText));
                        }
                    }
                    else
                    {
                        if (result.Count > 0)
                        {
                            var last = result[^1];
                            if (last is NewLinePayload)
                            {
                                result.RemoveAt(result.Count - 1);
                            }
                            else if (last is TextPayload prevTp && prevTp.Text != null && prevTp.Text.EndsWith('\n'))
                            {
                                var stripped = prevTp.Text[..^1];
                                if (string.IsNullOrEmpty(stripped)) result.RemoveAt(result.Count - 1);
                                else result[^1] = new TextPayload(stripped);
                            }
                        }
                    }
                    break;
                }
            }
            result.Add(p);
        }

        while (result.Count > 0)        {
            var last = result[^1];
            if (last is NewLinePayload)
            {
                result.RemoveAt(result.Count - 1);
            }
            else if (last is TextPayload t && string.IsNullOrWhiteSpace(t.Text))
            {
                result.RemoveAt(result.Count - 1);
            }
            else
            {
                break;
            }
        }

        return new SeString(result);
    }

    private void AppendProbabilityLine(SeStringBuilder sb, double[] probs, double[]? compares, string lineText)
    {
        for (int i = 0; i < 3; i++)
        {
            sb.AddText($"{i + 1}{lineText}:");
            FormatProb(sb, probs[i], compares?[i] ?? 0);
            if (i < 2) sb.AddText("  ");
        }
    }

    private void FormatProb(SeStringBuilder sb, double val, double baseline)
    {
        var percent = val * 100;
        var text = percent >= 100 ? $"{percent:F2}%" : percent >= 10 ? $"  {percent:F2}%" : $"    {percent:F2}%";
        if (val <= 0)
        {
            sb.AddUiForeground(text, 3);
        }
        else if (Math.Abs(val - 1.0) < 0.0001)
        {
            sb.AddUiForeground(31).AddText(text).AddUiForegroundOff();
        }
        else if (baseline > 0 && val / baseline >= 1.5)
        {
            sb.AddUiForeground(text, 67);
        }
        else
        {
            sb.AddText(text);
        }
    }

    private static double[] CalculateExactProbabilities(ushort state, int placed)
    {
        if (placed > 9) return [0, 0, 0];
        if (MathCache.TryGetValue(state, out var cached)) return cached;

        int remainStickers = 9 - placed;
        int remainSpots = 16 - placed;

        var emptySlots = new int[remainSpots];
        int eIdx = 0;
        for (int i = 0; i < 16; i++)
        {
            if ((state & (1 << i)) == 0) emptySlots[eIdx++] = i;
        }

        long totalPaths = 0;
        long line1 = 0, line2 = 0, line3 = 0;

        void Enumerate(int startIdx, int needToPlace, ushort currentBoard)
        {
            if (needToPlace == 0)
            {
                totalPaths++;
                int lines = 0;
                foreach (var w in WinLines)
                {
                    if ((currentBoard & w) == w) lines++;
                }
                if (lines >= 1) line1++;
                if (lines >= 2) line2++;
                if (lines >= 3) line3++;
                return;
            }

            for (int i = startIdx; i <= remainSpots - needToPlace; i++)
            {
                Enumerate(i + 1, needToPlace - 1, (ushort)(currentBoard | (1 << emptySlots[i])));
            }
        }

        Enumerate(0, remainStickers, state);

        double[] result = totalPaths == 0 
            ? [0, 0, 0] 
            : [(double)line1 / totalPaths, (double)line2 / totalPaths, (double)line3 / totalPaths];
            
        MathCache[state] = result;
        return result;
    }
}
