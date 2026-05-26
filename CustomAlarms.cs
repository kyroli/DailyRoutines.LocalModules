using System;
using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.Bindings.ImGui;
using Newtonsoft.Json;
using OmenTools;
using OmenTools.ImGuiOm;
using OmenTools.Dalamud;
using OmenTools.Info.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public enum TimeType
{
    LT,
    ET
}

public enum AlarmSoundType
{
    Bell = 0,
    MusicBox = 1,
    Prelude = 2,
    Chocobo = 3,
    LaNoscea = 4,
    Festival = 5
}

public class AlarmStage
{
    public bool Enabled { get; set; } = true;
    public int? Minutes { get; set; } = null;
    
    public bool EnableChat { get; set; } = false;
    public bool EnableToast { get; set; } = false;
    public bool EnableSound { get; set; } = false;
    public AlarmSoundType AlarmSound { get; set; } = AlarmSoundType.Bell;

    [JsonIgnore] public bool HasTriggeredChat;
    [JsonIgnore] public bool HasTriggeredToast;
    [JsonIgnore] public bool HasTriggeredSound;
}

public class AlarmItem
{
    public bool Enabled { get; set; } = true;
    public bool Repeat { get; set; } = false;
    public string Name { get; set; } = string.Empty;
    public TimeType TimeType { get; set; } = TimeType.LT;
    
    public int TargetHour { get; set; } = 12;
    public int TargetMinute { get; set; } = 0;

    public List<AlarmStage> Stages { get; set; } = new();

    [JsonIgnore] public double LastRemainingMinutes { get; set; } = -9999.0;
}

public class CustomAlarmsConfig : ModuleConfig
{
    public List<AlarmItem> Alarms { get; set; } = new();
}

public class CustomAlarms : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "自定义闹钟" : "Custom Alarms",
        Description = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "支持多阶段时间节点提醒的自定义闹钟。支持现实时间 (LT) 与艾欧泽亚时间 (ET)。" : "Custom alarms with multi-stage reminders. Supports Local Time (LT) and Eorzea Time (ET).",
        Category = ModuleCategory.Notice,
        Author = ["npnpsu"],
        ReportURL = "https://github.com/kyroli/DailyRoutines.LocalModules/issues"
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private CustomAlarmsConfig config = new();
    
    // 性能优化与状态字段
    private long lastCheckTick = 0;
    private long lastEditTick = 0;
    private long lastSaveTick = 0;
    private bool configNeedsSave = false;
    private bool anyEnabledAlarmsAndStages = false;
    private bool anyEnabledETAlarms = false;

    private static readonly string[] SoundNamesCN = { "闹铃", "八音盒", "水晶序曲", "陆行鸟", "拉诺西亚", "节日" };
    private static readonly string[] SoundNamesEN = { "Alarm", "Music Box", "Prelude", "Chocobo", "La Noscea", "Seasonal" };
    private static string[] SoundNames => DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? SoundNamesCN : SoundNamesEN;

    protected override void Init()
    {
        config = LoadConfig<CustomAlarmsConfig>() ?? new();
        UpdateEnabledStateCache();
        DService.Instance().Framework.Update += OnUpdate;
    }

    protected override void Uninit()
    {
        DService.Instance().Framework.Update -= OnUpdate;
        if (configNeedsSave)
        {
            SaveConfig(config);
        }
    }

    private void QueueSaveConfig()
    {
        configNeedsSave = true;
        lastEditTick = Environment.TickCount64;
        UpdateEnabledStateCache();
    }

    private void UpdateEnabledStateCache()
    {
        anyEnabledAlarmsAndStages = false;
        anyEnabledETAlarms = false;

        foreach (var alarm in config.Alarms)
        {
            if (!alarm.Enabled) continue;

            var alarmHasEnabledStage = false;
            foreach (var stage in alarm.Stages)
            {
                if (stage.Enabled && stage.Minutes != null)
                {
                    alarmHasEnabledStage = true;
                    break;
                }
            }

            if (alarmHasEnabledStage)
            {
                anyEnabledAlarmsAndStages = true;
                if (alarm.TimeType == TimeType.ET)
                {
                    anyEnabledETAlarms = true;
                }
            }
        }
    }

    private unsafe void PlaySound(AlarmSoundType soundType)
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Alarm);
        if (agent != null)
        {
            ((AgentAlarm*)agent)->PlayAlarmSoundEffect((AlarmSoundEffect)soundType);
        }
    }

    protected override void ConfigUI()
    {
        var IsCN = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12f, 8f));

        var style = ImGui.GetStyle();
        var frameHeight = ImGui.GetFrameHeight();
        var itemInnerSpacingX = style.ItemInnerSpacing.X;
        var cellPaddingX = style.CellPadding.X;
        var framePaddingX = style.FramePadding.X;

        var numInputWidth = ImGui.CalcTextSize("9999").X + framePaddingX * 2 + 20f; 
        var testBtnWidth = ImGui.CalcTextSize(GetLoc("Test")).X + framePaddingX * 2;
        var deleteAlarmBtnWidth = ImGui.CalcTextSize(GetLoc("DelAlarm")).X + framePaddingX * 4 + 10f;
        var deleteStageBtnWidth = ImGui.CalcTextSize(GetLoc("Del")).X + framePaddingX * 2;
        var timeTypeComboWidth = ImGui.CalcTextSize(GetLoc("ET")).X + framePaddingX * 2 + frameHeight;

        if (ImGui.Button(GetLoc("NewAlarm")))
        {
            config.Alarms.Add(new AlarmItem());
            QueueSaveConfig();
        }

        ImGui.Spacing();

        for (var i = 0; i < config.Alarms.Count; i++)
        {
            var alarm = config.Alarms[i];
            var titleName = string.IsNullOrEmpty(alarm.Name) ? GetLoc("Unnamed") : alarm.Name;
            var timePrefix = alarm.TimeType == TimeType.LT ? "LT" : "ET";
            
            var open = false;
            using (var headerTable = ImRaii.Table($"HeaderTable_{i}", 2, ImGuiTableFlags.None))
            {
                if (headerTable)
                {
                    ImGui.TableSetupColumn("Header", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, deleteAlarmBtnWidth + cellPaddingX * 2);

                    ImGui.TableNextRow();
                    
                    ImGui.TableNextColumn();
                    var enabledPrefix = alarm.Enabled ? "" : (IsCN ? "[已禁用] " : "[Disabled] ");
                    open = ImGui.CollapsingHeader($"{enabledPrefix}[{timePrefix}] {titleName} - {alarm.TargetHour:D2}:{alarm.TargetMinute:D2}###Alarm{i}");

                    ImGui.TableNextColumn();
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.1f, 0.1f, 1f));
                    if (ImGui.Button($"{GetLoc("DelAlarm")}##DeleteAlarmBtn_{i}", new Vector2(-1f, 0f)))
                    {
                        config.Alarms.RemoveAt(i);
                        QueueSaveConfig();
                        ImGui.PopStyleColor(3);
                        i--;
                        continue;
                    }
                    ImGui.PopStyleColor(3);
                }
            }

            if (open)
            {
                ImGui.PushID($"AlarmData_{i}");
                ImGui.Indent();

                var alarmEnabled = alarm.Enabled;
                if (ImGui.Checkbox($"{GetLoc("Enabled")}##Enabled_{i}", ref alarmEnabled))
                {
                    alarm.Enabled = alarmEnabled;
                    QueueSaveConfig();
                }
                ImGui.SameLine(0f, itemInnerSpacingX * 4);
                var alarmRepeat = alarm.Repeat;
                if (ImGui.Checkbox($"{GetLoc("Repeat")}##Repeat_{i}", ref alarmRepeat))
                {
                    alarm.Repeat = alarmRepeat;
                    QueueSaveConfig();
                }
                ImGui.Spacing();
                
                ImGui.AlignTextToFramePadding();
                ImGui.Text(GetLoc("NamePrefix"));
                ImGui.SameLine(0f, itemInnerSpacingX);
                ImGui.SetNextItemWidth(-1f);
                var name = alarm.Name;
                if (ImGui.InputText("##Name", ref name))
                {
                    alarm.Name = name;
                    QueueSaveConfig();
                }

                ImGui.Spacing();

                using (var mainTable = ImRaii.Table($"MainTable_{i}", 4, ImGuiTableFlags.None))
                {
                    if (mainTable)
                    {
                        var timeInputCellWidth = numInputWidth * 2 + ImGui.CalcTextSize(GetLoc("Hour")).X + ImGui.CalcTextSize(GetLoc("Minute")).X + itemInnerSpacingX * 4;

                        ImGui.TableSetupColumn("Label1", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(GetLoc("ModePrefix")).X + itemInnerSpacingX);
                        ImGui.TableSetupColumn("Input1", ImGuiTableColumnFlags.WidthFixed, timeTypeComboWidth + cellPaddingX * 2);
                        ImGui.TableSetupColumn("Label2", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(GetLoc("TimePrefix")).X + itemInnerSpacingX);
                        ImGui.TableSetupColumn("Input2", ImGuiTableColumnFlags.WidthFixed, timeInputCellWidth + cellPaddingX * 2);

                        ImGui.TableNextRow();
                        
                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(GetLoc("ModePrefix"));
                        
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        var timeType = (int)alarm.TimeType;
                        if (ImGui.Combo("##TimeType", ref timeType, new[] { GetLoc("LT"), GetLoc("ET") }, 2))
                        {
                            alarm.TimeType = (TimeType)timeType;
                            QueueSaveConfig();
                        }
                        
                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(GetLoc("TimePrefix"));
                        
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(numInputWidth);
                        var targetHour = alarm.TargetHour;
                        if (ImGui.InputInt("##Hour", ref targetHour, 0, 0))
                        {
                            alarm.TargetHour = Math.Clamp(targetHour, 0, 23);
                            QueueSaveConfig();
                        }
                        ImGui.SameLine(0f, itemInnerSpacingX);
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(GetLoc("Hour"));
                        
                        ImGui.SameLine(0f, itemInnerSpacingX * 2);
                        ImGui.SetNextItemWidth(numInputWidth);
                        var targetMinute = alarm.TargetMinute;
                        if (ImGui.InputInt("##Minute", ref targetMinute, 0, 0))
                        {
                            alarm.TargetMinute = Math.Clamp(targetMinute, 0, 59);
                            QueueSaveConfig();
                        }
                        ImGui.SameLine(0f, itemInnerSpacingX);
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(GetLoc("Minute"));
                    }
                }

                ImGui.Separator();
                ImGui.TextDisabled(GetLoc("StageConf"));

                using (var stagesTable = ImRaii.Table($"StagesTable_{i}", 11, ImGuiTableFlags.None))
                {
                    if (stagesTable)
                    {
                        var checkboxWidthChat = frameHeight + ImGui.CalcTextSize(GetLoc("ChatWin")).X + itemInnerSpacingX;
                        var checkboxWidthToast = frameHeight + ImGui.CalcTextSize(GetLoc("DRNotif")).X + itemInnerSpacingX;
                        var checkboxWidthSound = frameHeight + ImGui.CalcTextSize(GetLoc("GameAlarm")).X + itemInnerSpacingX;
                        var longestSoundName = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified ? "水晶序曲" : "Music Box";
                        var soundComboWidth = ImGui.CalcTextSize(longestSoundName).X + framePaddingX * 2 + 35f;

                        ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, frameHeight);
                        ImGui.TableSetupColumn("Text1", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(GetLoc("Advance")).X + itemInnerSpacingX);
                        ImGui.TableSetupColumn("Minutes", ImGuiTableColumnFlags.WidthFixed, numInputWidth);
                        ImGui.TableSetupColumn("Text2", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(GetLoc("Mins")).X + cellPaddingX * 2);
                        ImGui.TableSetupColumn("Text3", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(GetLoc("NotifType")).X + cellPaddingX * 2);
                        ImGui.TableSetupColumn("Chat", ImGuiTableColumnFlags.WidthFixed, checkboxWidthChat + cellPaddingX * 2);
                        ImGui.TableSetupColumn("Toast", ImGuiTableColumnFlags.WidthFixed, checkboxWidthToast + cellPaddingX * 2);
                        ImGui.TableSetupColumn("Sound", ImGuiTableColumnFlags.WidthFixed, checkboxWidthSound + cellPaddingX * 2);
                        ImGui.TableSetupColumn("SoundCombo", ImGuiTableColumnFlags.WidthFixed, soundComboWidth + cellPaddingX * 2);
                        ImGui.TableSetupColumn("SoundTest", ImGuiTableColumnFlags.WidthFixed, testBtnWidth + cellPaddingX * 2);
                        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, deleteStageBtnWidth + cellPaddingX * 2);

                        for (var j = 0; j < alarm.Stages.Count; j++)
                        {
                            var stage = alarm.Stages[j];
                            ImGui.TableNextRow();
                            
                            ImGui.TableNextColumn();
                            var enabled = stage.Enabled;
                            if (ImGui.Checkbox($"##StageEnabled_{i}_{j}", ref enabled))
                            {
                                stage.Enabled = enabled;
                                QueueSaveConfig();
                            }
                            
                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(GetLoc("Advance"));
                            
                            ImGui.TableNextColumn();
                            ImGui.SetNextItemWidth(-1f);
                            var minutesStr = stage.Minutes?.ToString() ?? string.Empty;
                            if (ImGui.InputText($"##StageMin_{i}_{j}", ref minutesStr, 8))
                            {
                                if (string.IsNullOrWhiteSpace(minutesStr))
                                {
                                    stage.Minutes = null;
                                }
                                else if (int.TryParse(minutesStr, out var parsedMinutes))
                                {
                                    var maxMinutes = alarm.TimeType == TimeType.ET ? 69 : 1439;
                                    stage.Minutes = Math.Clamp(parsedMinutes, 0, maxMinutes);
                                }
                                QueueSaveConfig();
                            }
                            ImGuiOm.TooltipHover(GetLoc("LimitTip"));
                            
                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(GetLoc("Mins"));
                            
                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(GetLoc("NotifType"));
                            
                            ImGui.TableNextColumn();
                            var enableChat = stage.EnableChat;
                            if (ImGui.Checkbox($"{GetLoc("ChatWin")}##Chat_{i}_{j}", ref enableChat))
                            {
                                stage.EnableChat = enableChat;
                                QueueSaveConfig();
                            }
                            
                            ImGui.TableNextColumn();
                            var enableToast = stage.EnableToast;
                            if (ImGui.Checkbox($"{GetLoc("DRNotif")}##Toast_{i}_{j}", ref enableToast))
                            {
                                stage.EnableToast = enableToast;
                                QueueSaveConfig();
                            }
                            
                            ImGui.TableNextColumn();
                            var enableSound = stage.EnableSound;
                            if (ImGui.Checkbox($"{GetLoc("GameAlarm")}##Sound_{i}_{j}", ref enableSound))
                            {
                                stage.EnableSound = enableSound;
                                QueueSaveConfig();
                            }

                            ImGui.TableNextColumn();
                            ImGui.BeginDisabled(!stage.EnableSound);
                            ImGui.SetNextItemWidth(-1f);
                            var stageSound = (int)stage.AlarmSound;
                            if (ImGui.Combo($"##StageSoundCombo_{i}_{j}", ref stageSound, SoundNames, SoundNames.Length))
                            {
                                stage.AlarmSound = (AlarmSoundType)stageSound;
                                QueueSaveConfig();
                            }

                            ImGui.TableNextColumn();
                            if (ImGui.Button($"{GetLoc("Test")}##StageTestSoundBtn_{i}_{j}", new Vector2(-1f, 0f)))
                            {
                                PlaySound(stage.AlarmSound);
                            }
                            ImGui.EndDisabled();

                            ImGui.TableNextColumn();
                            if (alarm.Stages.Count > 1)
                            {
                                if (ImGui.Button($"{GetLoc("Del")}##StageDelete_{i}_{j}", new Vector2(-1f, 0f)))
                                {
                                    alarm.Stages.RemoveAt(j);
                                    QueueSaveConfig();
                                    j--;
                                    continue;
                                }
                            }
                        }
                    }
                }

                if (ImGui.Button($"{GetLoc("AddStage")}##AddStage_{i}"))
                {
                    alarm.Stages.Add(new AlarmStage());
                    QueueSaveConfig();
                }

                ImGui.Unindent();
                ImGui.PopID();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        ImGui.PopStyleVar();
    }

    private void OnUpdate(IFramework framework)
    {
        var currentTick = Environment.TickCount64;
        if (currentTick - lastCheckTick < 1000) return;
        lastCheckTick = currentTick;

        if (configNeedsSave && currentTick - lastEditTick >= 2000)
        {
            SaveConfig(config);
            configNeedsSave = false;
            lastSaveTick = currentTick;
        }

        if (!anyEnabledAlarmsAndStages) return;

        var nowLT = DateTime.Now;
        var nowETSeconds = anyEnabledETAlarms ? EorzeaDate.GetTime().EorzeaTimeStamp : 0L;

        for (var i = 0; i < config.Alarms.Count; i++)
        {
            var alarm = config.Alarms[i];
            if (!alarm.Enabled) continue;
            if (alarm.Stages.Count == 0) continue;

            var remainingLTMinutes = 0.0;

            if (alarm.TimeType == TimeType.LT)
            {
                var targetLT = new DateTime(nowLT.Year, nowLT.Month, nowLT.Day, alarm.TargetHour, alarm.TargetMinute, 0);
                if (nowLT >= targetLT.AddMinutes(1)) targetLT = targetLT.AddDays(1);
                remainingLTMinutes = (targetLT - nowLT).TotalMinutes;
            }
            else
            {
                var currentETSecsOfDay = nowETSeconds % 86400;
                var targetETSecsOfDay = (long)alarm.TargetHour * 3600 + alarm.TargetMinute * 60;
                
                var remainingETSeconds = targetETSecsOfDay - currentETSecsOfDay;
                if (remainingETSeconds < -180) remainingETSeconds += 86400;
                
                remainingLTMinutes = (remainingETSeconds / (144.0 / 7.0)) / 60.0;
            }

            var hasJumped = alarm.LastRemainingMinutes != -9999.0 && remainingLTMinutes - alarm.LastRemainingMinutes > 10.0;
            alarm.LastRemainingMinutes = remainingLTMinutes;

            if (hasJumped)
            {
                if (!alarm.Repeat)
                {
                    alarm.Enabled = false;
                    QueueSaveConfig();
                    continue;
                }
                else
                {
                    for (var j = 0; j < alarm.Stages.Count; j++)
                    {
                        var stage = alarm.Stages[j];
                        stage.HasTriggeredChat = false;
                        stage.HasTriggeredToast = false;
                        stage.HasTriggeredSound = false;
                    }
                }
            }

            for (var j = 0; j < alarm.Stages.Count; j++)
            {
                var stage = alarm.Stages[j];
                if (!stage.Enabled) continue;
                if (stage.Minutes == null) continue;

                var stageMinutes = stage.Minutes.Value;

                if (remainingLTMinutes > stageMinutes)
                {
                    stage.HasTriggeredChat = false;
                    stage.HasTriggeredToast = false;
                    stage.HasTriggeredSound = false;
                }

                if (stage.EnableChat && !stage.HasTriggeredChat && remainingLTMinutes <= stageMinutes)
                {
                    stage.HasTriggeredChat = true;
                    var message = string.Format(GetLoc("RemTimeMsg"), alarm.Name, stageMinutes);
                    var seString = new SeStringBuilder().AddUiForeground(31).AddText(message).AddUiForegroundOff().Build();
                    DService.Instance().Chat.Print(seString);
                }

                if (stage.EnableToast && !stage.HasTriggeredToast && remainingLTMinutes <= stageMinutes)
                {
                    stage.HasTriggeredToast = true;
                    var msg = string.Format(GetLoc("RemTimeMsg"), alarm.Name, stageMinutes);
                    NotifyHelper.Instance().NotificationInfo(msg);
                }

                if (stage.EnableSound && !stage.HasTriggeredSound && remainingLTMinutes <= stageMinutes)
                {
                    stage.HasTriggeredSound = true;
                    PlaySound(stage.AlarmSound);
                }
            }
        }
    }

    private static string GetLoc(string key)
    {
        var IsCN = DService.Instance().ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.ChineseSimplified;
        return key switch
        {
            "Test" => IsCN ? "试听" : "Test",
            "DelAlarm" => IsCN ? "删除闹钟" : "Delete Alarm",
            "Del" => IsCN ? "删除" : "Delete",
            "ET" => IsCN ? "艾欧泽亚时间 (ET)" : "Eorzea Time (ET)",
            "LT" => IsCN ? "现实时间 (LT)" : "Local Time (LT)",
            "NewAlarm" => IsCN ? "新建闹钟" : "New Alarm",
            "Unnamed" => IsCN ? "未命名" : "Unnamed",
            "NamePrefix" => IsCN ? "名称:" : "Name:",
            "ModePrefix" => IsCN ? "模式:" : "Mode:",
            "TimePrefix" => IsCN ? "时间:" : "Time:",
            "Hour" => IsCN ? "时" : "H",
            "Minute" => IsCN ? "分" : "M",
            "StageConf" => IsCN ? "提醒时段配置:" : "Reminder Stage Configuration:",
            "ChatWin" => IsCN ? "聊天窗口" : "Chat Window",
            "DRNotif" => IsCN ? "DR通知" : "DR Notice",
            "GameAlarm" => IsCN ? "游戏闹钟" : "Game Alarm",
            "Advance" => IsCN ? "提前" : "Advance",
            "Mins" => IsCN ? "分钟" : "Mins",
            "NotifType" => IsCN ? "通知类型:" : "Notification Type:",
            "AddStage" => IsCN ? "添加提醒阶段" : "Add Reminder Stage",
            "RemTimeMsg" => IsCN ? "距离“{0}”的设定时间还有 {1} 分钟" : "Time remaining for '{0}': {1} min(s)",
            "Enabled" => IsCN ? "启用闹钟" : "Enable Alarm",
            "Repeat" => IsCN ? "每日重复" : "Daily Repeat",
            "LimitTip" => IsCN ? "提前时间不能超过一天 (ET 最大 69 分钟 / LT 最大 1439 分钟)" : "Advance limit cannot exceed one day (ET max 69m / LT max 1439m)",
            _ => key
        };
    }
}
