#nullable enable

using System;
using System.Collections.Generic;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class FisherAnimationSkipper : ModuleBase
{
    private const uint FisherJobID = 18;

    public override ModuleInfo Info => new()
    {
        Title       = IClientState.Instance().ClientLanguage == ClientLanguage.ChineseSimplified ? "捕鱼人动画跳过" : "Fisher Animation Skipper",
        Description = IClientState.Instance().ClientLanguage == ClientLanguage.ChineseSimplified ? "跳过大部分钓鱼动画演出。" : "Skips most fishing animations.",
        Category    = ModuleCategory.CraftGather,
        Author      = ["nynpsu"],
        ReportURL   = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    #region State

    private bool isSkipperEnabled;
    private HashSet<string>? animationBlacklist;

    #endregion

    #region Module Lifecycle

    protected override void Init()
    {
        IClientState.Instance().ClassJobChanged += OnJobChanged;

        if (IClientState.Instance().IsLoggedIn)
            OnJobChanged(LocalPlayerState.ClassJob);
    }

    protected override void Uninit()
    {
        IClientState.Instance().ClassJobChanged -= OnJobChanged;
        ResetState();
    }

    #endregion

    #region UI

    protected override void ConfigUI()
    {
        var isChinese = IClientState.Instance().ClientLanguage == ClientLanguage.ChineseSimplified;

        if (ImGui.Checkbox(isChinese ? "跳过钓鱼动画" : "Skip Fishing Animations", ref isSkipperEnabled))
            ApplyFilterState(isSkipperEnabled);

        DrawCountPlayersCheckbox(isChinese);
    }

    private static void DrawCountPlayersCheckbox(bool isChinese)
    {
        var module = ModuleManager.Instance().GetModuleByName("AutoCountPlayers");
        if (module == null) return;

        var isEnabled = ModuleManager.Instance().IsModuleEnabled("AutoCountPlayers") ?? false;
        if (!ImGui.Checkbox(isChinese ? "自动显示周边玩家数量" : "Show Nearby Players Count", ref isEnabled))
            return;

        if (isEnabled)
            ModuleManager.Instance().LoadAsync(module);
        else
            ModuleManager.Instance().UnloadAsync(module);
    }

    #endregion

    #region Core Logic

    private void OnJobChanged(uint jobID)
    {
        if (jobID == FisherJobID)
            ToggleOverlayConfig(true);
        else
            ResetState();
    }

    private void ResetState()
    {
        ToggleOverlayConfig(false);

        if (!isSkipperEnabled) return;

        ApplyFilterState(false);
        isSkipperEnabled = false;
    }

    private void ApplyFilterState(bool enable)
    {
        var animations = GetOrResolveAnimations();
        if (animations.Count == 0) return;

        if (enable)
            GameResourceManager.Instance().AddToBlacklist(typeof(FisherAnimationSkipper), animations);
        else
            GameResourceManager.Instance().RemoveFromBlacklist(typeof(FisherAnimationSkipper), animations);
    }

    private HashSet<string> GetOrResolveAnimations()
    {
        if (animationBlacklist != null) return animationBlacklist;

        var set = new HashSet<string>(128, StringComparer.OrdinalIgnoreCase);
        var sheet = LuminaGetter.Get<ActionTimeline>();
        if (sheet == null)
            return animationBlacklist = set;

        foreach (var row in sheet)
        {
            var key = row.Key.ToString();
            if (key.Length == 0 || key[0] is not ('f' or 'e')) continue;

            if (IsFishingAction(key) && !IsExcludedAction(key))
                set.Add($"chara/action/{key}.tmb");
        }

        return animationBlacklist = set;
    }

    private static bool IsFishingAction(string key) =>
        key.StartsWith("fishing/", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("fishing_chair/", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("event_base/event_base_fishing", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedAction(string key) =>
        key.Contains("hit_bite", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("hit_strike", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("hit_excite", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("/idle", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("retrieve_idle", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("reeling_idle", StringComparison.OrdinalIgnoreCase);

    #endregion
}
