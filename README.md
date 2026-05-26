# 🛠️ DailyRoutines Local Modules

> 个人自用的 DailyRoutines (DR) 本地增强与定制化模块备份。

---

## 🎰 金碟游乐场 (Gold Saucer)

### 1. 九宫幻卡自动化 (`AutoTripleTriad.cs`)
自动与 NPC 进行九宫幻卡对战，加速卡牌收集流程。
- **对战目标配置**：支持对战指定次数，或直到收集齐该 NPC 身上所有未拥有的幻卡为止。
- **智能策略选择**：支持自动读取并应用当前胜率最高的卡组。
- ⚠️ *注意：本模块仅作交互辅助，必须配合并开启外部插件 **TriadBuddy** 才能正常工作。*

<p align="center">
  <img src="images/att_icon.png" height="150" />
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img src="images/att_preview.png" height="150" />
</p>

<br>

### 2. 金碟机遇临门辅助 (`GoldSaucerGATEsHelper.cs`)
金碟游乐场随机活动（GATE）的交互与预判辅助，功能移植自 Saucy 插件。
- **喷风中的幸存者**：实时计算并提示被吹飞概率最小的“安全站位”。
- **必中一闪快刀斩魔**：高亮标示竹子的倒向与攻击波及范围。

<p align="center">
  <img src="images/gsgh_icon.png" width="600" />
  <br><br>
  <img src="images/gsgh_preview_wind.png" width="290" height="135" />
  &nbsp;&nbsp;
  <img src="images/gsgh_preview_slice.png" width="290" height="135" />
</p>

<br>

### 3. 自动每周仙人仙彩(改) (`AutoJumboCactpotCustom.cs`)
自动购买每周仙人仙彩，基于官方原版进行增强。
- **一号多买模式**：首张仙彩票随机生成号码，后续购买的票自动沿用该号码。

<p align="center">
  <img src="images/ajc_icon.png" height="130" />
</p>

---

## 📅 日常与周常 (Daily & Weekly)

### 4. 自动雇员作业(改) (`AutoRetainerWorkCustom.cs`)
自动收取雇员探索并重新派遣，提升日常收派遣的流畅度。
- **防卡死自动调优**：在执行收发雇员期间，系统将自动开启“跳过对话”功能，避免因网络延迟或多余弹窗导致派遣卡住。

<p align="center">
  <img src="images/arwc_icon.png" height="95" />
</p>

<br>

### 5. 自动任务说话 (`AutoQuestSay.cs`)
自动处理任务交互中需要玩家发送特定频道消息（如 `/say 任务词句`）的环节。
- **自动台词发送**：点击对应的任务目标时，若检测到属于这类任务，本模块会自动发送匹配的文本内容。

<p align="center">
  <img src="images/aqs_icon.png" height="140" />
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img src="images/aqs_preview.png" height="140" />
</p>

<br>

### 6. 天书连线概率 (`WondrousTailsPredictor.cs`)
在库洛的奇谈书界面提供期望值与连线概率计算辅助。
- **连线期望计算**：直观显示当前天书的连线成功概率。
- **重排收益期望**：展示使用洗牌（Shuffle）点数重排后的期望值，辅助决策是否进行洗牌。

<p align="center">
  <img src="images/wt_icon.png" width="600" />
  <br><br>
  <img src="images/wt_preview.png" width="600" />
</p>

---

## ⚙️ 系统辅助 (System & General)

### 7. 自定义闹钟 (`CustomAlarms.cs`)
专为艾欧泽亚生活设计的自定义多阶段闹钟提醒。
- **双重时间标准**：支持基于现实时间（LT）与艾欧泽亚时间（ET）的时间节点提醒。
- **多阶段提醒**：允许对单个事件设置多阶段、多次闹铃，确保重要时机不会漏掉。

<p align="center">
  <img src="images/ca_icon.png" height="200" />
</p>

---

## 🎨 界面示例

在 DailyRoutines 中加载后的本地模块列表界面展示：

<p align="center">
  <img src="images/local_modules_list.png" width="600" />
</p>
