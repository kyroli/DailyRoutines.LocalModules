#nullable enable

using System;
using System.Collections.Generic;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class FisherAnimationSkipper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
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
    private Hook<ResourceManager.Delegates.GetResourceSync>?  getResourceSyncHook;
    private Hook<ResourceManager.Delegates.GetResourceAsync>? getResourceAsyncHook;

    #endregion

    #region Module Lifecycle

    protected override void Init()
    {
        getResourceSyncHook = IGameInteropProvider.Instance().HookFromMemberFunction(
            typeof(ResourceManager.MemberFunctionPointers),
            "GetResourceSync",
            (ResourceManager.Delegates.GetResourceSync)GetResourceSyncDetour);

        getResourceAsyncHook = IGameInteropProvider.Instance().HookFromMemberFunction(
            typeof(ResourceManager.MemberFunctionPointers),
            "GetResourceAsync",
            (ResourceManager.Delegates.GetResourceAsync)GetResourceAsyncDetour);

        IClientState.Instance().ClassJobChanged += OnJobChanged;

        if (GameState.IsLoggedIn)
            OnJobChanged(LocalPlayerState.ClassJob);
    }

    protected override void Uninit()
    {
        IClientState.Instance().ClassJobChanged -= OnJobChanged;
        ResetState();

        getResourceSyncHook?.Dispose();
        getResourceSyncHook = null;

        getResourceAsyncHook?.Dispose();
        getResourceAsyncHook = null;
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
        if (enable)
        {
            if (GetOrResolveAnimations().Count == 0) return;

            getResourceSyncHook?.Enable();
            getResourceAsyncHook?.Enable();
        }
        else
        {
            getResourceSyncHook?.Disable();
            getResourceAsyncHook?.Disable();
        }
    }

    private ResourceHandle* GetResourceSyncDetour(
        ResourceManager* manager, ResourceCategory* category, uint* type, uint* hash,
        CStringPointer path, void* unknown, void* unkDebugPtr, uint unkDebugInt)
    {
        if (!ShouldRedirectResource(category, path))
            return getResourceSyncHook!.Original(manager, category, type, hash, path, unknown, unkDebugPtr, unkDebugInt);

        var redirectedHash = NothingHash;
        fixed (byte* redirectedPath = "vfx/path/nothing.avfx"u8)
            return getResourceSyncHook!.Original(manager, category, type, &redirectedHash, redirectedPath, unknown, unkDebugPtr, unkDebugInt);
    }

    private ResourceHandle* GetResourceAsyncDetour(
        ResourceManager* manager, ResourceCategory* category, uint* type, uint* hash,
        CStringPointer path, void* unknown, bool isUnknown, void* unkDebugPtr, uint unkDebugInt)
    {
        if (isUnknown || !ShouldRedirectResource(category, path))
            return getResourceAsyncHook!.Original(manager, category, type, hash, path, unknown, isUnknown, unkDebugPtr, unkDebugInt);

        var redirectedHash = NothingHash;
        fixed (byte* redirectedPath = "vfx/path/nothing.avfx"u8)
            return getResourceAsyncHook!.Original(manager, category, type, &redirectedHash, redirectedPath, unknown, isUnknown, unkDebugPtr, unkDebugInt);
    }

    private bool ShouldRedirectResource(ResourceCategory* category, CStringPointer path)
    {
        if (category == null || *category != ResourceCategory.Chara || !path.HasValue)
            return false;

        if (((byte*)path)[0] is not ((byte)'c' or (byte)'C'))
            return false;

        var blacklist = animationBlacklist;
        return blacklist is { Count: > 0 } && blacklist.Contains(path.ToString().Trim());
    }

    private HashSet<string> GetOrResolveAnimations()
    {
        if (animationBlacklist != null) return animationBlacklist;

        var set = new HashSet<string>(128, StringComparer.OrdinalIgnoreCase);
        var sheet = LuminaGetter.Get<ActionTimeline>();
        if (sheet == null) return set;

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

    #region 常量

    private const uint FisherJobID = 18;
    private const uint NothingHash = 0x61565D6Cu;

    #endregion
}
