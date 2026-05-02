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
        Description = "在天书界面实时精准计算并显示连线概率与重排期望。",
        Category    = ModuleCategory.UIOptimization,
        Author      = ["nynpsu"]
    };

    private static readonly ushort[] WinLines = [
        0x000F, 0x00F0, 0x0F00, 0xF000, 0x1111, 0x2222, 0x4444, 0x8888, 0x8421, 0x1248
    ];

    private static readonly Dictionary<ushort, double[]> MathCache = [];
    
    private SeString? DefaultText;
    private ushort PrevMask = 0xFFFF;
    private bool RequestRedraw;

    private double[]? GlobalShuffleExpectation;

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "WeeklyBingo", OnAddonEvent);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "WeeklyBingo", OnAddonEvent);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "WeeklyBingo", OnAddonEvent);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "WeeklyBingo", OnAddonEvent);

        GlobalShuffleExpectation ??= CalculateExactProbabilities(0, 0);

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
                DefaultText = null;
                PrevMask = 0xFFFF;
                RequestRedraw = true;
                UpdateProbabilities((AddonWeeklyBingo*)args.Addon.Address);
                break;
            case AddonEvent.PostUpdate:
                UpdateProbabilities((AddonWeeklyBingo*)args.Addon.Address);
                break;
            case AddonEvent.PreFinalize:
                DefaultText = null;
                PrevMask = 0xFFFF;
                break;
        }
    }

    private void RefreshAddon()
    {
        var ptr = DService.Instance().GameGUI.GetAddonByName("WeeklyBingo").Address;
        if (ptr != nint.Zero)
        {
            DefaultText = null;
            PrevMask = 0xFFFF;
            RequestRedraw = true;
            UpdateProbabilities((AddonWeeklyBingo*)ptr);
        }
    }

    private void RestoreAddon()
    {
        var ptr = DService.Instance().GameGUI.GetAddonByName("WeeklyBingo").Address;
        if (ptr == nint.Zero || DefaultText == null) return;
        
        var addon = (AddonWeeklyBingo*)ptr;
        var node = addon->StringThing.TextNode;
        if (node != null)
        {
            node->SetText(DefaultText.Encode());
        }
        
        DefaultText = null;
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

        if (!RequestRedraw && currentMask == PrevMask) return;

        var textNode = addon->StringThing.TextNode;
        if (textNode == null || textNode->NodeText.StringPtr.Value == null) return;

        var currentSeString = MemoryHelper.ReadSeStringNullTerminated((nint)textNode->NodeText.StringPtr.Value);
        var currentText = currentSeString.TextValue ?? string.Empty;

        if (string.IsNullOrWhiteSpace(currentText)) return;

        var loc = DService.Instance().ClientState.ClientLanguage switch
        {
            ClientLanguage.Japanese => ("現在", "シャッフル", "L"),
            ClientLanguage.English => ("Current", "Shuffle", " Line(s)"),
            ClientLanguage.German => ("Aktuell", "Mischen", " Reihe(n)"),
            ClientLanguage.French => ("Actuel", "Mélange", " Ligne(s)"),
            _ => ("当前", "重排", "线")
        };

        if (currentText.Contains(loc.Item3 + ":"))
        {
            if (currentMask == PrevMask)
            {
                RequestRedraw = false;
                return;
            }
        }
        else if (DefaultText == null)
        {
            DefaultText = currentSeString;
        }

        PrevMask = currentMask;
        RequestRedraw = false;

        var currentProb = CalculateExactProbabilities(currentMask, stickers);
        var sb = new SeStringBuilder();
        
        sb.Append(StripOurText(DefaultText ?? currentSeString));
        sb.AddText("\n");

        sb.AddText($"{loc.Item1}: ");
        AppendProbabilityLine(sb, currentProb, GlobalShuffleExpectation!, loc.Item3);
        
        sb.AddText($"\n{loc.Item2}: ");
        if (stickers is > 0 and <= 7)
        {
            AppendProbabilityLine(sb, GlobalShuffleExpectation!, null, loc.Item3);
        }
        else
        {
            sb.AddText($"1{loc.Item3}: -   2{loc.Item3}: -   3{loc.Item3}: - ");
        }

        textNode->SetText(sb.Build().Encode());
    }

    private SeString StripOurText(SeString original)
    {
        var result = new List<Payload>();
        var lastWasNewLine = false;

        foreach (var p in original.Payloads)
        {
            if (p is NewLinePayload)
            {
                if (lastWasNewLine) continue;
                lastWasNewLine = true;
            }
            else
            {
                lastWasNewLine = false;
            }

            result.Add(p);
        }

        while (result.Count > 0 && result[^1] is NewLinePayload)
        {
            result.RemoveAt(result.Count - 1);
        }

        return new SeString(result);
    }

    private void AppendProbabilityLine(SeStringBuilder sb, double[] probs, double[]? compares, string lineText)
    {
        for (int i = 0; i < 3; i++)
        {
            sb.AddText($"{i + 1}{lineText}: ");
            FormatProb(sb, probs[i], compares?[i] ?? 0);
            if (i < 2) sb.AddText("   ");
        }
    }

    private void FormatProb(SeStringBuilder sb, double val, double baseline)
    {
        var text = $"{val * 100:F2}%";
        if (val <= 0)
        {
            sb.AddUiForeground(text, 3);
        }
        else if (Math.Abs(val - 1.0) < 0.0001)
        {
            sb.AddUiForeground(73).AddUiGlow(2).AddText(text).AddUiGlowOff().AddUiForegroundOff();
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
