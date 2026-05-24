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
    public string Name { get; set; } = string.Empty;
    public TimeType TimeType { get; set; } = TimeType.LT;
    
    public int TargetHour { get; set; } = 12;
    public int TargetMinute { get; set; } = 0;

    public List<AlarmStage> Stages { get; set; } = new();
}

public class CustomAlarmsConfig : ModuleConfig
{
    public List<AlarmItem> Alarms { get; set; } = new();
}

public class CustomAlarms : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "自定义闹钟",
        Description = "支持多阶段时间节点提醒的自定义闹钟。支持现实时间 (LT) 与艾欧泽亚时间 (ET)。",
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

    private static readonly string[] SoundNames = { "闹铃", "八音盒", "水晶序曲", "陆行鸟", "拉诺西亚", "节日" };

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
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12f, 8f));

        var style = ImGui.GetStyle();
        var frameHeight = ImGui.GetFrameHeight();
        var itemInnerSpacingX = style.ItemInnerSpacing.X;
        var cellPaddingX = style.CellPadding.X;
        var framePaddingX = style.FramePadding.X;

        var numInputWidth = ImGui.CalcTextSize("9999").X; 
        var testBtnWidth = ImGui.CalcTextSize("试听").X + framePaddingX * 2;
        var deleteAlarmBtnWidth = ImGui.CalcTextSize("删除闹钟").X + framePaddingX * 4 + 10f;
        var deleteStageBtnWidth = ImGui.CalcTextSize("删除").X + framePaddingX * 2;
        var timeTypeComboWidth = ImGui.CalcTextSize("艾欧泽亚时间 (ET)").X + framePaddingX * 2 + frameHeight;

        if (ImGui.Button("新建闹钟"))
        {
            config.Alarms.Add(new AlarmItem());
            QueueSaveConfig();
        }

        ImGui.Spacing();

        for (var i = 0; i < config.Alarms.Count; i++)
        {
            var alarm = config.Alarms[i];
            var titleName = string.IsNullOrEmpty(alarm.Name) ? "未命名" : alarm.Name;
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
                    open = ImGui.CollapsingHeader($"[{timePrefix}] {titleName} - {alarm.TargetHour:D2}:{alarm.TargetMinute:D2}###Alarm{i}");

                    ImGui.TableNextColumn();
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.1f, 0.1f, 1f));
                    if (ImGui.Button($"删除闹钟##DeleteAlarmBtn_{i}", new Vector2(-1f, 0f)))
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
                
                ImGui.AlignTextToFramePadding();
                ImGui.Text("名称:");
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
                        var timeInputCellWidth = numInputWidth * 2 + ImGui.CalcTextSize("时").X + ImGui.CalcTextSize("分").X + itemInnerSpacingX * 4;

                        ImGui.TableSetupColumn("Label1", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("模式:").X + itemInnerSpacingX);
                        ImGui.TableSetupColumn("Input1", ImGuiTableColumnFlags.WidthFixed, timeTypeComboWidth + cellPaddingX * 2);
                        ImGui.TableSetupColumn("Label2", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("时间:").X + itemInnerSpacingX);
                        ImGui.TableSetupColumn("Input2", ImGuiTableColumnFlags.WidthFixed, timeInputCellWidth + cellPaddingX * 2);

                        ImGui.TableNextRow();
                        
                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text("模式:");
                        
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        var timeType = (int)alarm.TimeType;
                        if (ImGui.Combo("##TimeType", ref timeType, new[] { "现实时间 (LT)", "艾欧泽亚时间 (ET)" }, 2))
                        {
                            alarm.TimeType = (TimeType)timeType;
                            QueueSaveConfig();
                        }
                        
                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text("时间:");
                        
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
                        ImGui.Text("时");
                        
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
                        ImGui.Text("分");
                    }
                }

                ImGui.Separator();
                ImGui.TextDisabled("提醒时段配置:");

                using (var stagesTable = ImRaii.Table($"StagesTable_{i}", 11, ImGuiTableFlags.None))
                {
                    if (stagesTable)
                    {
                        var checkboxWidthChat = frameHeight + ImGui.CalcTextSize("聊天窗口").X + itemInnerSpacingX;
                        var checkboxWidthToast = frameHeight + ImGui.CalcTextSize("DR通知").X + itemInnerSpacingX;
                        var checkboxWidthSound = frameHeight + ImGui.CalcTextSize("游戏闹钟").X + itemInnerSpacingX;
                        var soundComboWidth = ImGui.CalcTextSize("水晶序曲").X + framePaddingX * 2 + 35f;

                        ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, frameHeight);
                        ImGui.TableSetupColumn("Text1", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("提前").X + itemInnerSpacingX);
                        ImGui.TableSetupColumn("Minutes", ImGuiTableColumnFlags.WidthFixed, numInputWidth);
                        ImGui.TableSetupColumn("Text2", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("分钟").X + cellPaddingX * 2);
                        ImGui.TableSetupColumn("Text3", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("通知类型:").X + cellPaddingX * 2);
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
                            ImGui.Text("提前");
                            
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
                                    stage.Minutes = Math.Max(0, parsedMinutes);
                                }
                                QueueSaveConfig();
                            }
                            
                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.Text("分钟");
                            
                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.Text("通知类型:");
                            
                            ImGui.TableNextColumn();
                            var enableChat = stage.EnableChat;
                            if (ImGui.Checkbox($"聊天窗口##Chat_{i}_{j}", ref enableChat))
                            {
                                stage.EnableChat = enableChat;
                                QueueSaveConfig();
                            }
                            
                            ImGui.TableNextColumn();
                            var enableToast = stage.EnableToast;
                            if (ImGui.Checkbox($"DR通知##Toast_{i}_{j}", ref enableToast))
                            {
                                stage.EnableToast = enableToast;
                                QueueSaveConfig();
                            }
                            
                            ImGui.TableNextColumn();
                            var enableSound = stage.EnableSound;
                            if (ImGui.Checkbox($"游戏闹钟##Sound_{i}_{j}", ref enableSound))
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
                            if (ImGui.Button($"试听##StageTestSoundBtn_{i}_{j}", new Vector2(-1f, 0f)))
                            {
                                PlaySound(stage.AlarmSound);
                            }
                            ImGui.EndDisabled();

                            ImGui.TableNextColumn();
                            if (alarm.Stages.Count > 1)
                            {
                                if (ImGui.Button($"删除##StageDelete_{i}_{j}", new Vector2(-1f, 0f)))
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

                if (ImGui.Button($"添加提醒阶段##AddStage_{i}"))
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
                    var message = $"距离“{alarm.Name}”的设定时间还有 {stageMinutes} 分钟";
                    var seString = new SeStringBuilder().AddUiForeground(31).AddText(message).AddUiForegroundOff().Build();
                    DService.Instance().Chat.Print(seString);
                }

                if (stage.EnableToast && !stage.HasTriggeredToast && remainingLTMinutes <= stageMinutes)
                {
                    stage.HasTriggeredToast = true;
                    var msg = $"距离“{alarm.Name}”的设定时间还有 {stageMinutes} 分钟";
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
}
