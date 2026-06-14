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
    public override ModuleInfo Info => new()
    {
        Title       = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "天书连线概率" : "Wondrous Tails Predictor",
        Description = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "在天书界面实时计算并显示连线概率，辅助洗牌决策。" : "Calculates and displays line probabilities in Wondrous Tails, assisting with shuffle decisions.",
        Category    = ModuleCategory.Interface,
        Author      = ["nynpsu"],
        ReportURL   = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    private static readonly ushort[] WinLines = [
        0x000F, 0x00F0, 0x0F00, 0xF000, 0x1111, 0x2222, 0x4444, 0x8888, 0x8421, 0x1248
    ];

    private struct ProbResult
    {
        public bool Calculated;
        public double Line1;
        public double Line2;
        public double Line3;
    }

    private static readonly ProbResult[] MathCache = new ProbResult[65536];
    
    private static readonly double[] GlobalShuffleExpectation = [
        6688.0 / 11440.0, // ~58.46% (1线理论常数)
        1208.0 / 11440.0, // ~10.56% (2线理论常数)
        24.0 / 11440.0    // ~0.21% (3线理论常数)
    ];

    private struct LineCountInfo
    {
        public byte Line1;
        public byte Line2;
        public byte Line3;
    }

    private static readonly LineCountInfo[] LineInfoTable = new LineCountInfo[65536];
    
    private static readonly int[] TotalPathsTable = [11440, 6435, 3432, 1716, 792, 330, 120, 36, 8, 1];

    static WondrousTailsPredictor()
    {
        for (var state = 0; state < 65536; state++)
        {
            byte lines = 0;
            foreach (var w in WinLines)
            {
                if ((state & w) == w) lines++;
            }
            LineInfoTable[state] = new LineCountInfo
            {
                Line1 = (byte)(lines >= 1 ? 1 : 0),
                Line2 = (byte)(lines >= 2 ? 1 : 0),
                Line3 = (byte)(lines >= 3 ? 1 : 0)
            };
        }
    }

    private ushort PrevMask = 0xFFFF;
    private bool IsOurTextAttached;
    private nint PrevTextPtr = nint.Zero;

    private (string Current, string Line) LocStrings;
    private string LocMarker = string.Empty;

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "WeeklyBingo", OnAddonEvent);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "WeeklyBingo", OnAddonEvent);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "WeeklyBingo", OnAddonEvent);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "WeeklyBingo", OnAddonEvent);
        
        LocStrings = DService.Instance().ClientState.ClientLanguage switch
        {
            Dalamud.Game.ClientLanguage.ChineseSimplified => ("当前", "线"),
            _ => ("Current", " Line(s)")
        };
        LocMarker = $"{LocStrings.Current}: ";

        PrevMask = 0xFFFF;
        IsOurTextAttached = false;
        PrevTextPtr = nint.Zero;
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
                IsOurTextAttached = false;
                PrevTextPtr = nint.Zero;
                UpdateProbabilities((AddonWeeklyBingo*)args.Addon.Address);
                break;
            case AddonEvent.PostUpdate:
                UpdateProbabilities((AddonWeeklyBingo*)args.Addon.Address);
                break;
            case AddonEvent.PreFinalize:
                PrevMask = 0xFFFF;
                IsOurTextAttached = false;
                PrevTextPtr = nint.Zero;
                break;
        }
    }

    private void RefreshAddon()
    {
        var ptr = DService.Instance().GameGUI.GetAddonByName("WeeklyBingo").Address;
        if (ptr != nint.Zero)
        {
            PrevMask = 0xFFFF;
            IsOurTextAttached = false;
            PrevTextPtr = nint.Zero;
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
            var baseText = StripOurText(currentSeString, out _);
            node->SetText(baseText.Encode());
            IsOurTextAttached = false;
            PrevTextPtr = nint.Zero;
        }
    }

    private void UpdateProbabilities(AddonWeeklyBingo* addon)
    {
        var state = PlayerState.Instance();
        ushort currentMask = 0;
        var stickers = 0;

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

        if (currentMask == PrevMask && IsOurTextAttached && (nint)textNode->NodeText.StringPtr.Value == PrevTextPtr) return;

        var currentSeString = MemoryHelper.ReadSeStringNullTerminated((nint)textNode->NodeText.StringPtr.Value);
        var strippedSeString = StripOurText(currentSeString, out var hasOurText);

        if (currentMask == PrevMask && hasOurText)
        {
            IsOurTextAttached = true;
            PrevTextPtr = (nint)textNode->NodeText.StringPtr.Value;
            return;
        }

        PrevMask = currentMask;

        var currentProb = CalculateExactProbabilities(currentMask, stickers);
        var sb = new SeStringBuilder();
        
        sb.Append(strippedSeString);
        sb.AddText("\n");

        sb.AddText(LocMarker);
        
        sb.AddText($"1{LocStrings.Line}: ");
        FormatProb(sb, currentProb.Line1, GlobalShuffleExpectation[0]);
        sb.AddText("   ");

        sb.AddText($"2{LocStrings.Line}: ");
        FormatProb(sb, currentProb.Line2, GlobalShuffleExpectation[1]);
        sb.AddText("   ");

        sb.AddText($"3{LocStrings.Line}: ");
        FormatProb(sb, currentProb.Line3, GlobalShuffleExpectation[2]);

        textNode->SetText(sb.Build().Encode());
        IsOurTextAttached = true;
        PrevTextPtr = (nint)textNode->NodeText.StringPtr.Value;
    }

    private SeString StripOurText(SeString original, out bool hasOurText)
    {
        var marker = LocMarker;
        var payloads = original.Payloads;
        var count = payloads.Count;
        
        var markerIdx = -1;
        var textIdxWithinPayload = -1;
        for (var i = 0; i < count; i++)
        {
            if (payloads[i] is TextPayload tp && tp.Text != null)
            {
                var idx = tp.Text.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    markerIdx = i;
                    textIdxWithinPayload = idx;
                    break;
                }
            }
        }

        if (markerIdx == -1)
        {
            hasOurText = false;
            return original;
        }

        hasOurText = true;

        var tempPayloads = new List<Payload>(markerIdx + 1);
        for (var i = 0; i < markerIdx; i++)
        {
            tempPayloads.Add(payloads[i]);
        }

        var targetTextPayload = (TextPayload)payloads[markerIdx];
        var beforeText = targetTextPayload.Text[..textIdxWithinPayload];
        
        if (beforeText.EndsWith('\n'))
        {
            beforeText = beforeText[..^1];
        }
        else if (beforeText.Length == 0 && tempPayloads.Count > 0)
        {
            var last = tempPayloads[^1];
            if (last is NewLinePayload)
            {
                tempPayloads.RemoveAt(tempPayloads.Count - 1);
            }
            else if (last is TextPayload prevTextPayload && prevTextPayload.Text != null && prevTextPayload.Text.EndsWith('\n'))
            {
                var prevText = prevTextPayload.Text;
                if (prevText.Length == 1) tempPayloads.RemoveAt(tempPayloads.Count - 1);
                else tempPayloads[^1] = new TextPayload(prevText[..^1]);
            }
        }

        if (beforeText.Length > 0)
        {
            tempPayloads.Add(new TextPayload(beforeText));
        }

        return new SeString(tempPayloads);
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
            sb.AddUiForeground(text, 31);
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

    private ref struct SearchContext
    {
        public int Line1;
        public int Line2;
        public int Line3;
        public ReadOnlySpan<byte> EmptySlots;
    }

    private static ProbResult CalculateExactProbabilities(ushort state, int placed)
    {
        if (placed > 9) return default;
        
        ref var cached = ref MathCache[state];
        if (cached.Calculated) return cached;

        if (placed == 9)
        {
            var info = LineInfoTable[state];
            cached = new ProbResult
            {
                Calculated = true,
                Line1 = info.Line1,
                Line2 = info.Line2,
                Line3 = info.Line3
            };
            return cached;
        }

        var remainStickers = 9 - placed;
        var remainSpots = 16 - placed;

        Span<byte> emptySlots = stackalloc byte[16];
        var emptyIndex = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((state & (1 << i)) == 0) emptySlots[emptyIndex++] = (byte)i;
        }

        var ctx = new SearchContext
        {
            EmptySlots = emptySlots[..remainSpots]
        };
        Enumerate(0, remainStickers, state, ref ctx);

        var totalPaths = TotalPathsTable[placed];
        cached = new ProbResult
        {
            Calculated = true,
            Line1 = (double)ctx.Line1 / totalPaths,
            Line2 = (double)ctx.Line2 / totalPaths,
            Line3 = (double)ctx.Line3 / totalPaths
        };
            
        return cached;

        static void Enumerate(
            int startIdx, 
            int needToPlace, 
            ushort currentBoard, 
            ref SearchContext ctx)
        {
            if (needToPlace == 1)
            {
                var len = ctx.EmptySlots.Length;
                for (var i = startIdx; i < len; i++)
                {
                    var board = (ushort)(currentBoard | (1 << ctx.EmptySlots[i]));
                    var info = LineInfoTable[board];
                    ctx.Line1 += info.Line1;
                    ctx.Line2 += info.Line2;
                    ctx.Line3 += info.Line3;
                }
                return;
            }

            var limit = ctx.EmptySlots.Length - needToPlace;
            for (var i = startIdx; i <= limit; i++)
            {
                Enumerate(i + 1, needToPlace - 1, (ushort)(currentBoard | (1 << ctx.EmptySlots[i])), ref ctx);
            }
        }
    }
}
