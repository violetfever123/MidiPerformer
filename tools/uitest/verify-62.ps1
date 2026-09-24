# 62 号工单的实机验证。
#
# 🔴 **本脚本只判一条：② —— 开曲耗时那行确实进了日志、分段齐**（2026-09-24 的裁定，见下）。
#    ①（加载期间窗口还活着）**照旧量、照旧打进报告，但不判红** —— 它在 HEAD 上就是绿的（四趟实测），
#    分不出改前改后，是一条假门。两条的来龙去脉都写在本文件里，别删那几段注释。
#
# 用法: pwsh -NoProfile -File verify-62.ps1 [-探针毫秒 120] [-曲名 X] [-再开 Y]
#       （脚本自己起 app、自己收尾）
#
# =====================================================================
# 这一票量的是什么（先说清尺子，不然下面那些数字没法读）
# =====================================================================
# 病根是「**这段代码跑在哪个线程上**」，而线程不是纯函数的性质（票面自己也写了：
# 「为什么测不到单测」）⇒ 只有实机这条路。
#
# 但「界面线程被占住」这件事**是可以直接量的**，不用看像素猜：
#   `SendMessageTimeout(主窗, WM_NULL, SMTO_ABORTIFHUNG, 上限)` ——
#   对方**抽消息**就毫秒级回来（返回非 0）；对方**不抽**就到点返回 0。
#   这不是本脚本发明的招：共用库 `uitest-lib.ps1` 的 `SendMessageTimeout` 注释里
#   留着 84 号票的实测（靶子消息循环在转 → 9ms 回 True；靶子线程不抽消息 →
#   2014ms 回 False），WM_NULL 是「任何窗口过程都必须处理」的那一条，
#   所以「点不着」「按钮没可见」这类干扰都不参与。
#   ⇒ **一次超时 = 界面线程有「超过 `-探针毫秒` 那么久不抽消息」的一段**。
#
# ①（加载期间窗口还活着）—— **本脚本只报，不判红**（2026-09-24 的裁定）：
#   A. 从「回车开曲」到**「曲库窗关掉之后再观察 600 毫秒」**这一段里，探针**有没有超时**
#      —— 反面就是用户那句「按什么键都没有用」。
#      ⚠️ 观察窗口为什么拖到窗关掉之后：票面那句是「从点击到**轨画出来**」，而 `window.Close()`
#         发生在 `LoadSong` 返回之后、布局/首次绘制**之前** —— 窗一关就收工等于把最可能卡的那
#         一段漏在窗口外。（600 毫秒是仪器旋钮，不是判据。）
#   B. 加载**还没结束**的时候截一张图，看画面**不是全白/全黑**（有内容）。
#      为什么 A 和 B 要合起来读：只截一张图是不够的 —— 僵住的窗口 `PrintWindow` 照样吐得出
#      **上一帧**（窗口自己的内容还在，DWM 那颗「未响应」是合成上去的、不在窗口位图里），
#      所以「有画面」这一条**单独是不会红的**。
#
#   🔴 **为什么这两条都不判红**：**改前它就是绿的**。HEAD（`a5f4cd83`）上连测四趟
#      （`tools/uitest/shots/62/62-报告C/D/F/G-*.txt`）：探针 5317~7290 发、**一次超时都没有**、
#      最长一发 26~88 毫秒，三趟都在加载中截到了画面（23~27 种颜色）。
#      ⇒ 它**分不出改前改后**，是一条**假门**（验收条目预设了一个不存在的红）。
#      ⇒ 探针与截图照旧量、照旧打进报告，但**不计数进 `$fail`**（像 70 号那边像素读回的处理）。
#      ⚠️ **不许**为了让这条显得有意义去调 `-探针毫秒`：那只是把「多长算卡」挪来挪去，
#         挪出来的红绿与 app 无关（同一台机器、同一首曲子，阈值调小就红、调大就绿）。
#   反过来说：A 那一串数**仍然是有用的读数** —— 谁哪天把开曲挪到后台去了，它看得出来。
#
# ② 的判定（**本脚本唯一的门**）= 应用日志里有那一行「打开「X」耗时 Nms：分段…」（61 号的 provider 落的盘）。
#   ⚠️ **不做「必须快于 N 毫秒」的断言** —— 机器快慢差异会把测试变成噪音（票面原话）。
#   这一条要的是**基线**：以后谁把这块改慢了，日志里有对照。
#   🔴 它能红的三种情形（都与机器快慢无关）：**耗时行没了**（有人把 `plan.Done` 删了）、
#      **分段漏了**（有人把某个 `Mark` 删了 ⇒ 那一段的名字在日志里找不到）、
#      **记的不是这一首**（`{Title}` 传错了）。
#
# ③（标题栏不出现「未响应」/ 光标不转圈）**本脚本判不了**，如实说：
#   · 「未响应」不是窗口自己的像素，是 DWM 合成上去的（见上）⇒ 截图里读不到。
#     能读的是 `IsHungAppWindow`（就是系统用来决定要不要盖那颗幽灵窗的那一个 API），
#     可它自带 **5 秒**阈值 ⇒ 一次几百毫秒的卡顿它压根不亮，**拿它当闸门是个恒绿**。
#     所以本脚本**只把它当数据打出来**（加载期间有没有采样到），不当断言。
#   · 「光标是不是转圈」要去比对 `GetCursorInfo` 里那个动画光标句柄，跨机器不稳，不做。
#   ⇒ ③ 留在票面上按「上机」人工验，本脚本不代签。
#
# =====================================================================
# `-探针毫秒` 这个刻度是怎么定的（**只影响 ① 那串读数，它不再是闸门**）
# =====================================================================
# 默认 120：这台机器上改前量到的**最长一发**是 26~88 毫秒（报告 C/D/F/G），120 是它的上界再放宽。
# 两头的实测数字见 `tools/uitest/shots/62/` 与 `.scratch/62-量具/`。
# ⚠️ 换机器/换曲子之后要重新量：**这是一个用实测校准过的尺子，不是拍出来的数**。
# ⚠️ 但刻度松紧**不影响裁决**（① 不判红，见上）；它只决定报告里那句「慢探针」有多少。
#
# 用法上的一个前提：**这一趟的开曲必须是本实例的第一次开曲**（最冷的一次 —— JIT 也在里面）。
# 所以脚本自己起实例、自己开曲库，不在别人的实例上跑。
param(
  [int]$探针毫秒 = 120,
  # 开哪一首（票面点名的基准是 Carulli_Duetto_No2_Op4：347.80 秒 / 2,288 个音）。
  # 换靶子只换「量谁」，尺子（探针上限、判据、观察窗口）一个字不动。
  [string]$曲名 = 'Carulli_Duetto_No2_Op4',
  # 可选的第 7 节：**已经载着 `$曲名` 再开一首 `$再开`**（空 = 不跑那一节）。
  # 为什么要有它：第 1~6 节量的是本实例**第一次**开曲（空态、最冷的一次）；
  # 而用户嘴里那一下多半是「已经开着一首，再开第二首」—— 那时 `SyncLanes(rebuildAll: true)`
  # 要把已经挂着的轨**拆掉重挂**，和空态不是同一条路。这只是**多量一趟**，
  # 尺子（探针上限、判据、观察窗口）一个字不动；第 1~6 节的行为一个字节不改。
  [string]$再开 = ''
)

# ---------- 共用驱动库 ----------
# 非提权 shell 里起 app，60 号那颗模态的「要以管理员身份重启吗？」必弹，**它开着的时候主窗是
# 禁用的**：点击会被它丢掉、`Take` 也拽不到前台 ⇒ 后面全是一串看不懂的红。库的
# `起窗口带清障` 里已经在按掉它了，所以这一票只从库里取，一份都不自己抄。
# ⚠️ 库在模块作用域里有 `Set-StrictMode -Version Latest`，而 dot-source 在**调用方作用域**里
#    执行 ⇒ 它会连本脚本剩下的全部代码一起收紧。所以下面**每一个变量都先赋初值**，
#    不留「可能没绑过」的读点。
. (Join-Path $PSScriptRoot 'uitest-lib.ps1')

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
# ⚠️ 这一段 C# **必须**用单引号 here-string（`@'…'@`）：上面 dot 了库，而库在模块作用域就
#    `Set-StrictMode -Version Latest`，dot-source 又在调用方作用域里执行 ⇒ 本脚本全文跑在
#    严格模式下；双引号 here-string 里的 `$` 会被 PowerShell 当变量展开。
Add-Type @'
using System; using System.Runtime.InteropServices;
public class V62 {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  // 「这颗窗口是不是被系统判成没响应」—— 就是决定要不要盖那颗幽灵窗（标题栏那句
  // 「未响应」）的那一个 API。**自带 5 秒阈值**，所以几百毫秒的卡顿它不亮（见抬头 ③）。
  [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
  // 回车**发完就返回**（不像 SendKeys.SendWait 会等对方处理完）。这一条是量法的命门：
  // 用 SendWait 的话，那 500 毫秒的同步加载正是它「在等」的东西 ⇒ 它回来时加载早结束了，
  // 探针一次都落不进加载窗口里，①A 会变成一个**看不见东西的绿**。
  // 30 毫秒在 down/up 之间：down 那一下就够 Avalonia 触发 KeyDown 了。
  public static void Enter() {
    keybd_event(0x0D, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(30);
    keybd_event(0x0D, 0, 2, IntPtr.Zero);
  }
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$条件 = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$fail = 0
function 断言([string]$名字, $实际, $期望) {
  if ("$实际" -eq "$期望") { "  OK   $名字 = $实际" }
  else { "  FAIL $名字 = $实际（期望 $期望）"; $script:fail++ }
}
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}

# ---------- 证据落地 ----------
$图 = Join-Path (Join-Path $PSScriptRoot 'shots') '62'
New-Item -ItemType Directory -Force -Path $图 | Out-Null
# 「这张图上有没有内容」：在画面上撒一张网格，数**采到几种颜色**。
# 全白/全黑/纯色 = 1 种 ⇒ 那不算「有画面」。这一条只是 ①B 的尺子（见抬头），
# 真正红的是 ①A 那一半。
function 数不同色([byte[]]$b, [int]$w, [int]$ht) {
  if ($null -eq $b -or $w -le 0 -or $ht -le 0) { return -1 }
  $色 = @{}
  $步X = [Math]::Max(1, [int]($w / 40))
  $步Y = [Math]::Max(1, [int]($ht / 30))
  for ($y = 4; $y -lt ($ht - 4); $y += $步Y) {
    for ($x = 4; $x -lt ($w - 4); $x += $步X) { $色[[P40]::Rgb($b, $w, $x, $y)] = 1 }
  }
  return $色.Count
}
# 时间戳：票面要「改前那条红必须留下证据（截图/日志 + 时刻）」，而这支脚本会被跑两遍
#（改前一遍、改后一遍）⇒ 报告文件名带上时刻，两遍不互相覆盖。
$时刻 = Get-Date -Format 'yyyyMMdd-HHmmss'
$报告 = Join-Path $图 "verify-62-$时刻.txt"
Start-Transcript -Path $报告 -Force | Out-Null

# 曲子：**曲库里最长的那一首**（票面点名的基准 = 347.80 秒 / 2,288 个音）。
# 它不是脚本随便挑的：这一票量的是「卡多久」，拿一首 10 秒的小曲子量不出东西来。
# ⚠️ 可以用 `-曲名` 换一首量（量的是**同一把尺子**，换的只是靶子）—— 62 号实测时用它
#    量过 `sm_mol`（曲库里轨数最多的那一首，20 条 MTrk）：实测见报告与票文件。
#    默认值就是票面点名的那一首，**不放宽任何判据**。

# 应用日志夹（61 号落的盘）。开跑前先把**已经在那儿的**文件名记下来，
# 跑完多出来的那一份才是这一趟的 —— 不靠 LastWriteTime 猜（别的 agent 也在跑）。
$日志夹 = Join-Path $env:LOCALAPPDATA 'MidiPerformer\logs'
$先有 = @()
if (Test-Path -LiteralPath $日志夹) {
  $先有 = @(Get-ChildItem -LiteralPath $日志夹 -Filter 'MidiPerformer-*.log' | ForEach-Object { $_.Name })
}
"脚本时刻 = $时刻（app 日志夹里原本有 $($先有.Count) 份）"

$h = [IntPtr]::Zero
$库窗 = [IntPtr]::Zero
$跑完了 = $false
$探针数 = 0; $超时数 = 0; $最长探针 = 0; $慢探针 = 0
$加载毫秒 = 0; $总观察毫秒 = 0; $截图毫秒 = 0; $饿过 = $false
$图1 = ''; $图2 = ''; $不同色1 = -1; $不同色2 = -1; $图1的偏移 = -1; $图1的耗时 = 0
$前提过了 = $false; $行文本 = @(); $行 = $null; $库根 = $null; $名框里 = ''; $轨数 = -1
# 第 7 节（`-再开`）的那几个变量也要**在 try 外面先绑**：那一节可能压根不跑，
# 而结尾的「证据」那几行是无条件读它们的；本脚本全文跑在严格模式下，裸读就是一条看不懂的抛。
$图3 = ''; $不同色3 = -1
try {
  # ---------- 0. 起一个干净实例 ----------
  "`n=== 0. 起实例 ==="
  $h = 起窗口带清障          # 桌面上已有实例就抛（不替调用方关别人的实例）
  # `$脚本PID` 得自己绑：库只把 PID 放在 `$script:pid脚本` 里，**没有**取名叫 `$脚本PID`
  # 的变量（33 号那边也是自己绑的）。严格模式下漏了这一句，第一次用就在 `Others()` 那行抛。
  $脚本PID = [uint32]$script:proc.Id
  $根 = $AE::FromHandle($h)
  function 找类型([object]$类型) { @($根.FindAll($TS::Descendants, (& $条件 $类型))) }
  function 找编号([string]$id) {
    @($根.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
      $AE::AutomationIdProperty, $id))))
  }
  function 取值($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
  # 轨数：每条轨的轨头上都有一颗「折叠」按钮（33 号那边量过，照抄）
  function 数轨 { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' }).Count }

  # ⚠️ `@(...)` 这几下**不是装饰**：本脚本全文跑在严格模式下（头上那条 dot 带进来的），
  #    而「函数返回一个只有 1 个元素的数组」会被 PowerShell **摊平成标量** ⇒ 直接写
  #    `(找编号 'X').Count` 在读到一个元素（或一个都没有）时**当场抛**
  #    「在此对象上找不到属性 Count」。33 号那边的写法就是每个取值点套一层 `@(...)`，
  #    照抄。（实测：`@(1)` 从函数里返回后 `.Count` 抛；`@(1,2)` 不抛；`@()` 也不抛。）
  $期限 = (Get-Date).AddSeconds(20)
  while ((Get-Date) -lt $期限 -and @(找编号 'LibraryButton').Count -eq 0) { Start-Sleep -Milliseconds 400 }
  if (@(找编号 'LibraryButton').Count -eq 0) { throw '主窗上等不到 LibraryButton —— 实例没起全' }
  # 🔴 拿「歌曲库」那颗按钮当「实例起全了」的锚，**不是** SongNameBox：曲名那一格
  #    （`SongNameCell`）在**没装曲子的时候整个藏掉**（`MainWindow.axaml:318` 的注释写着这条规矩），
  #    空态下它压根不在 UIA 树里 ⇒ 拿它当锚会等 20 秒然后抛「等不到 SongNameBox」。
  #    这不是猜的：本脚本前两趟（19:24、19:28 两趟）就是死在这一条上（报告里留着）。
  # 起手必须是「一首都没装」：这一票量的是**最冷的那一次开曲**（JIT 也在里面）。
  # 判据取「一条轨都没有」而不是「曲名框是空的」—— 空态时那格里写什么文案不是这一票管得着的。
  断言 '起手时一条轨都没有（还没载过曲子）' (数轨) 0

  # ---------- 1. 开曲库、找到最长那一首 ----------
  "`n=== 1. 开曲库、找到「$曲名」 ==="
  $库钮 = @(找编号 'LibraryButton')
  if ($库钮.Count -ne 1) { throw "工具栏上找不到「歌曲库」那颗按钮（按 AutomationId=LibraryButton 数到 $($库钮.Count) 颗）" }
  $库钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  # 曲库是**另一扇顶层窗**（标题「歌曲库」）。它 `ShowInTaskbar=False`，所以
  # `MainWindowTitle` 认不出来，只能按进程里的顶层窗 + 标题找。
  $期限 = (Get-Date).AddSeconds(20)
  while ((Get-Date) -lt $期限 -and $库窗 -eq [IntPtr]::Zero) {
    foreach ($w in @([P40]::Others($脚本PID, $h))) {
      if ([P40]::Title($w) -eq '歌曲库') { $库窗 = $w; break }
    }
    if ($库窗 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 300 }
  }
  if ($库窗 -eq [IntPtr]::Zero) { throw '点了「歌曲库」之后没等到标题为「歌曲库」的那扇窗' }
  "  曲库窗：$([P40]::Describe($库窗))"
  $库根 = $AE::FromHandle($库窗)
  # 🔴 这个函数**必须**从调用方给的根开始搜（33 号的形状），不能闭包 `$库根`：
  #    下面那句「这一行里有曲名吗」如果搜的是库根，它对**每一行**都成立 ⇒
  #    `-First 1` 拿到的是列表**第一行**，回车开出来的是另一首。
  #    本脚本 19:32/19:33 两趟就是这么过的：开出来的是「（三角洲适配）勾指起誓」，
  #    第 3 节两条红摆在那儿，而 ① 却绿了 —— 一次**量错了对象**的假绿。
  #    （`$根` 是函数参数，和上面主窗那个 `$根` 不是同一个东西。）
  function 库类型([object]$根, [object]$类型) {
    @($根.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
      $AE::ControlTypeProperty, $类型))))
  }
  # 一行的字：行自己的 Name 是容器类名（'Avalonia.Controls.Grid'），字在子树里的 Text 上
  function 行文([object]$行) {
    @(库类型 $行 $CT::Text | ForEach-Object { $_.Current.Name })
  }

  # 列表是异步填的 ⇒ 轮询等那一行。挖**这一行自己的**子树找「正好等于曲名」的那个 Text（33 号量过）。
  $期限 = (Get-Date).AddSeconds(30)
  $行 = $null
  while ((Get-Date) -lt $期限 -and $null -eq $行) {
    $行 = @(库类型 $库根 $CT::ListItem | Where-Object {
      @(行文 $_ | Where-Object { $_ -eq $曲名 }).Count -gt 0
    }) | Select-Object -First 1
    if ($null -eq $行) { Start-Sleep -Milliseconds 400 }
  }
  if ($null -eq $行) {
    $在 = (@(库类型 $库根 $CT::ListItem | ForEach-Object { (行文 $_) -join '+' }) -join ' / ')
    throw "曲库里没有「$曲名」这一行（现在有：$在）"
  }
  # 断言的是**这一行自己的字里有曲名**，不是「库里有这个字」（后者对每一行都成立，是句空话）
  $行文本 = 行文 $行
  断言真 "「$曲名」那一行**自己**的字里有它" ($行文本 -contains $曲名) "这一行里的字：$($行文本 -join ' / ')"

  # ---------- 2. 开曲 + 全程探针（这一节就是本票的正题） ----------
  "`n=== 2. 开曲、全程探针（上限 $探针毫秒 毫秒）==="
  # 回车之前拽的是**库窗**、不是主窗：模态期间主窗是死的，键给主窗这一下就白敲了。
  [void][P40]::Take($库窗)
  [void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 400
  [void]$行.SetFocus()
  Start-Sleep -Milliseconds 400
  # 🔴 回车**开的是选中的那一行** ⇒ 先把「选中的就是 Carulli 那一行」钉住，再发键。
  #    不钉这一条的话，下面 ① 量到的可能是另一首（19:32/19:33 两趟的假绿就是这么来的）。
  $选中 = $行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
  "  回车之前：选中的那一行 = $($行文本 -join ' / ')（IsSelected = $选中）"
  断言真 "回车要开的就是「$曲名」那一行" ($选中 -and ($行文本 -contains $曲名)) `
    "选中 = $选中；行里的字 = $($行文本 -join ' / ')"

  # 探针要打的是**主窗**的句柄：曲库窗加载完会自己销毁，拿它当靶子的话
  # 「最后那一下」会读成超时（那是窗没了，不是线程卡了）。两个窗同一条界面线程，
  # 谁当靶子量到的都是同一件事。
  $起 = Get-Date
  "  回车发下去的时刻 = $($起.ToString('HH:mm:ss.fff'))"
  [V62]::Enter()               # 发完就返回，不等对方（不等才落得进加载窗口，见 V62 的注释）

  # 探针打到什么时候停：**曲子真的装完了之后再多看 600 毫秒**。
  # 🔴 为什么不能「窗一关就停」：票面那句是「从点击到**轨画出来**」，而 `window.Close()`
  #    发生在 `LoadSong` 返回之后、**布局/首次绘制之前** —— 曲子真正被画出来是在
  #    曲库窗消失之后的那几个消息循环里。窗一关就收工 = 正好把最可能卡的那一段漏掉，
  #    那样就算改后全绿，也可能是一次**空洞的绿**（用户手里照样卡）。
  # 🔴 也不能只看「窗没了」：56/62 号之后 `TryOpenLibrarySong` 是**立刻返回**的（读盘挪去后台），
  #    窗当场就关 ⇒ 只按「窗关 + 600 毫秒」收工的话，观察窗口会在加载**中途**结束，
  #    又是一个空洞的绿。所以收工要等到「**曲子装完了**」这个信号：
  #    ② 那行「打开「X」耗时 Nms：…」是 `LoadSong` 的**最后一句**（`plan.Done("收尾")`），
  #    而 `FileLoggerProvider.Append` 是**一行一次开→追加→关**（61 号那份实现里写着
  #    「没有留在缓冲区里没落盘的东西」）⇒ 那行一写就看得见，不用等 flush，也不用碰界面。
  #    HEAD 那种没有这一行的构建（改前那一趟）等不到它 ⇒ 兜底：窗关掉之后再观察 3 秒收工，
  #    并**把收工原因打进报告**（「等到耗时行」还是「没等到、按兜底收的」），
  #    免得读者以为窗口一定盖住了加载。改前那个兜底是够的：`LoadSong` 同步跑，窗必然关在
  #    加载**之后**（报告 D/E 的日志时刻可以对着核）。
  # ⚠️ 「600 / 3000 毫秒」都是**观察窗口**（仪器旋钮），不是判据。
  $窗没了 = $null
  $装完 = $null          # 「耗时行出现在盘上」那一刻
  $装完原因 = '（还没装完）'
  $下次看日志 = Get-Date
  while ($true) {
    if (-not [P40]::Alive($库窗)) {
      if ($null -eq $窗没了) { $窗没了 = Get-Date }
      elseif ($null -ne $装完 -and ((Get-Date) - $装完).TotalMilliseconds -ge 600) { break }
      elseif ($null -eq $装完 -and ((Get-Date) - $窗没了).TotalMilliseconds -ge 3000) { break }
    }
    if (((Get-Date) - $起).TotalSeconds -gt 120) { break }   # 有上界，绝不挂死
    # 每 150 毫秒看一眼日志：找「这一趟新落的那份日志里有没有耗时行」
    #（新日志 = 开跑前 `$先有` 里没有的名字 ⇒ 不会把上一趟的那行算成这一趟的）
    if ((Get-Date) -ge $下次看日志) {
      $下次看日志 = (Get-Date).AddMilliseconds(150)
      $新份 = @(Get-ChildItem -LiteralPath $日志夹 -Filter 'MidiPerformer-*.log' -EA SilentlyContinue |
                Where-Object { $先有 -notcontains $_.Name })
      foreach ($份 in $新份) {
        if (Select-String -LiteralPath $份.FullName -Pattern '耗时' -SimpleMatch -Quiet) {
          if ($null -eq $装完) { $装完 = Get-Date; $装完原因 = "等到耗时行（$($份.Name)）" }
          break
        }
      }
    }
    $已过 = [int]((Get-Date) - $起).TotalMilliseconds
    # 🔴 **探针排在截图前面**，不是随意排的：`PrintWindow` 是**同步**调用，对面僵着的时候
    #    它自己会一直等（等到这窗口抽消息为止）⇒ 先截图就等于**用截图把那段僵住的时间吃掉**，
    #    等它回来时加载早结束了，后面的探针全是秒回，「①A 一次超时都没有」会变成一个
    #    **看不见东西的绿**。先发探针，超时那一发就已经落账了，再截图也不影响判据。
    $r = [IntPtr]::Zero
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $送到 = [P40]::SendMessageTimeout($h, [uint32]0, [IntPtr]::Zero, [IntPtr]::Zero,
                                      [uint32][P40]::SMTO_ABORTIFHUNG, [uint32]$探针毫秒, [ref]$r)
    $sw.Stop()
    $ms = $sw.ElapsedMilliseconds
    $探针数++
    if ($ms -gt $最长探针) { $最长探针 = $ms }
    $超时 = ($送到 -eq [IntPtr]::Zero)
    if ($超时) {
      $超时数++
      if ([V62]::IsHungAppWindow($h)) { $饿过 = $true }
    } elseif ($ms -ge [Math]::Floor($探针毫秒 / 2)) {
      # 「没到点、但也不快」= 有卡顿、只是没到上限。**不计进闸门**，只报出来 ——
      # 改后若这一格很大，说明界面线程还是被占着几十毫秒，闸门该往紧里调（见抬头校准那段）。
      $慢探针++
    }
    # ①B：加载**还没结束**的时候截一张（票面 ① 的字面要求）。
    # 落点按**时钟**挑（+40 毫秒、曲库窗还开着），不按「第几发探针」挑 ——
    # 改后探针是毫秒级回来的，几百毫秒里能跑几千发，「第 2 发」多半赶在 app
    # 处理回车**之前**，那截到的是「还没开始加载」。时钟这一版不受探针快慢影响。
    # ⚠️ 「40」是个**取景时刻**（仪器旋钮），不是判据 —— 它只负责让这张图落在加载区间里。
    #    这一趟加载要是短得没到 40 毫秒，图 1 会是空的，那一条断言会明说「这一趟没截到」，
    #    不会假绿。
    if ($图1 -eq '' -and $null -eq $窗没了 -and $已过 -ge 40) {
      # 重新取一次时钟：上面那一发探针可能就花掉了 $探针毫秒 那么久（改前正是如此），
      # 拿探针之前那个数当标签会把这行字写成一句假话
      $这一刻 = [int]((Get-Date) - $起).TotalMilliseconds
      $sw1 = [System.Diagnostics.Stopwatch]::StartNew()
      $b = [P40]::Shot($h); $bw = [P40]::LastW; $bh = [P40]::LastH
      $sw1.Stop()
      $图1 = Join-Path $图 "加载中(回车后$($这一刻)ms).png"
      $图1的偏移 = $这一刻; $图1的耗时 = $sw1.ElapsedMilliseconds
      存图 $b $bw $bh $图1
      $不同色1 = 数不同色 $b $bw $bh
      "  截图 1：回车后 $($这一刻) 毫秒、曲库窗还开着 —— $($图1.Substring($图.Length + 1))  不同色数 = $不同色1  截图本身花了 $图1的耗时 毫秒"
    }
    # 改前才有的一格：**第一次读到超时**的那一瞬间再截一张 —— 这是「僵住的那一刻」
    # 最直接的证据（报告里带它的时刻与偏移）。改后不会走到这里。
    if ($超时 -and $图2 -eq '') {
      $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
      $b = [P40]::Shot($h); $bw = [P40]::LastW; $bh = [P40]::LastH
      $sw2.Stop(); $截图毫秒 = $sw2.ElapsedMilliseconds
      $图2 = Join-Path $图 "僵住那一刻(回车后$($已过)ms).png"
      存图 $b $bw $bh $图2
      $不同色2 = 数不同色 $b $bw $bh
      "  ★ 第 $探针数 发探针超时（$ms 毫秒没抽消息）—— 截图 2：$($图2.Substring($图.Length + 1))  不同色数 = $不同色2  这次截图本身花了 $截图毫秒 毫秒"
    }
  }
  $加载毫秒 = [int]((Get-Date) - $起).TotalMilliseconds
  if ($null -ne $窗没了) { $加载毫秒 = [int](($窗没了) - $起).TotalMilliseconds }
  $总观察毫秒 = [int]((Get-Date) - $起).TotalMilliseconds
  if ($null -ne $装完) { $装完原因 = "$装完原因，回车后 $([int](($装完) - $起).TotalMilliseconds) 毫秒" }
  else { $装完原因 = '没等到耗时行 —— 按兜底（窗关后 3 秒）收的工' }
  "  曲库窗关掉用了 $加载毫秒 毫秒；连「轨画出来」那一段一起观察到 $总观察毫秒 毫秒"
  "  观察窗口的收工原因：$装完原因"
  "  探针 $探针数 发（超时 $超时数 发、慢 $慢探针 发、最长一发 $最长探针 毫秒）"
  if ([P40]::Alive($库窗)) {
    # 带上窗里看得见的几个字：没装上时页脚会写原因（读不出来那句话），这就是证据
    $页脚 = (@(库类型 $库根 $CT::Text | ForEach-Object { $_.Current.Name }) | Select-Object -First 6) -join ' / '
    throw "曲库窗没关掉（等了 $总观察毫秒 毫秒）—— 曲子没装上，后面所有判据都无从谈起（窗里看得见的字：$页脚）"
  }

  # ---------- 3. 曲子真的装上了吗（前提，不是本票的判据） ----------
  # 🔴 这一节的结论**是 ① 的闸门的一部分**（见第 4 节）：量错了对象的话，① 那串漂亮数字
  #    一个都不算数。19:32/19:33 两趟的教训就摆在这儿 —— 第 3 节两条红，① 却绿着。
  "`n=== 3. 前提：开出来的就是「$曲名」 ==="
  Start-Sleep -Milliseconds 800
  $期限 = (Get-Date).AddSeconds(20)
  $名框 = @(找编号 'SongNameBox')
  while ((Get-Date) -lt $期限 -and $名框.Count -ge 1 -and (取值 $名框[0]) -ne $曲名) {
    Start-Sleep -Milliseconds 400
    $名框 = @(找编号 'SongNameBox')
  }
  if ($名框.Count -eq 0) { throw '开完之后主窗上又找不到 SongNameBox 了' }
  $名框里 = 取值 $名框[0]
  # 轨数：**只当「轨挂上了没有」用**（≥1），具体几条是**记录**。
  # 界面上的轨头数 = .mid 头里声明的 MTrk 减掉没音的那种空轨（Carulli 声明 5、界面上 4），
  # 减几条只有 app 自己知道 ⇒ **不写死数字**：写死就是把一个没量准的数升格成判据
  #（票面明令不许），错了还会红在一个假地方。
  $轨数 = 数轨
  $前提过了 = (($名框里 -eq $曲名) -and ($轨数 -ge 1))
  断言 '曲名框里是那一首' $名框里 $曲名
  断言真 '轨都挂上了（至少一条）' ($轨数 -ge 1) "$轨数 条轨"
  if (-not $前提过了) {
    "  🔴🔴 **前提不成立** ⇒ 这一趟量的**不是「$曲名」**（量到的是「$名框里」）⇒"
    "       第 4 节 ① 的那串数字**整段作废**，不许当绿看（19:32/19:33 两趟就是这么假绿的）。"
  }

  # ---------- 4. ① 加载期间界面线程还在抽消息（**只报，不判红** —— 见抬头） ----------
  "`n=== 4. ① 加载期间界面线程还在抽消息（只报不判红） ==="
  "  探针上限 $探针毫秒 毫秒；一发超时 = 界面线程有超过 $探针毫秒 毫秒没抽消息"
  "  实测（**记录，不是判据**）：探针 $探针数 发，超时 $超时数 发，慢 $慢探针 发，最长一发 $最长探针 毫秒；"
  "        曲库窗关掉用了 $加载毫秒 毫秒、连「轨画出来」一起观察到 $总观察毫秒 毫秒；①B 那张图拍在回车后 $图1的偏移 毫秒"
  # 🔴 这一条**过去是闸门，现在只报**：HEAD 上四趟全 0 超时（报告 C/D/F/G）⇒ 它分不出改前改后。
  #    留着的原因是这串数字本身是有用的读数（「开曲是不是被挪到后台了」看得出来）。
  if ($超时数 -eq 0 -and $前提过了) {
    "  ○（只报不判红）加载期间**一次超时都没有**（界面线程一直在抽消息）：超时 0 发 / 共 $探针数 发，最长 $最长探针 毫秒"
  } else {
    "  ●（只报不判红）※ 不是 0 超时，或者前提不成立：超时 $超时数 发 / 共 $探针数 发，最长 $最长探针 毫秒；前提 = $(if ($前提过了) { '成立' } else { '不成立' })"
    "      （这一行**不判红**：那一串读数只说明「这一刻界面线程被占了多久」，不代表这次改动坏了）"
  }
  # 防「一眼没看就绿了」：探针得真的跑起来过。**这不是耗时闸门** —— 探针从回车那一刻
  # 起就在连续发，曲库窗关掉才停，所以它们的时间线**覆盖**整段加载；这条只确认仪器真的在转。
  # ⚠️ 它只在「仪器根本没转」时响（那时下面 ② 那条也多半红）—— 不当成对 app 的判决。
  if ($探针数 -lt 3) { "  ●（只报不判红）仪器好像没转：只发了 $探针数 发探针" }
  # ①B：加载还没结束时截到的那张图（只报）：没截到就明说没量到，不当绿也不当红
  if ($图1 -eq '') {
    "  ○（只报不判红）这一趟短得没截到加载中的图 —— **没量到**（不是绿）"
  } elseif ($不同色1 -ge 3) {
    "  ○（只报不判红）加载途中截到的那张图**不是全白/全黑**：$($图1.Substring($图.Length + 1)) 上采到 $不同色1 种颜色"
  } else {
    "  ○（只报不判红）加载途中那张图只有 $不同色1 种颜色（像是全白/全黑）—— 记下来，不当红"
  }
  "  （③ 的数据，不当闸门：加载期间 `IsHungAppWindow` = $(if ($饿过) { '亮过（系统判成没响应）' } else { '始终没亮' })"
  "    —— 它自带 5 秒阈值，几百毫秒的卡顿它不亮；这是数据，不是判据）"

  # ---------- 5. ② 耗时进了日志（**本脚本唯一的门**） ----------
  # 🔴 这条能红，而且红法清楚（都与机器快慢无关）：耗时行没了（有人把 plan.Done 删了）、
  #    分段漏了（有人把某个 Mark 删了）、记的不是这一首（{Title} 传错了）、毫秒数不是正数。
  "`n=== 5. ② 开曲耗时进了日志（唯一的门） ==="
  $新 = @(Get-ChildItem -LiteralPath $日志夹 -Filter 'MidiPerformer-*.log' -EA SilentlyContinue |
          Where-Object { $先有 -notcontains $_.Name } | Sort-Object LastWriteTime -Descending)
  断言真 '这一趟落了新日志（61 的 provider 一份一次）' ($新.Count -ge 1) "$($新.Count) 份新的"
  if ($新.Count -ge 1) {
    $本份 = $新[0].FullName
    "  本趟日志 = $本份"
    $命中 = @(Get-Content -LiteralPath $本份 -Encoding UTF8 | Where-Object { $_ -match '耗时' })
    断言真 '日志里有那一行耗时' ($命中.Count -ge 1) "$($命中.Count) 行"
    if ($命中.Count -ge 1) {
      $行文 = $命中[$命中.Count - 1]
      "  那一行：$行文"
      $m = [regex]::Match($行文, '打开「(?<曲>[^」]+)」耗时 (?<毫>[0-9]+)ms：(?<分段>.*)$')
      断言真 '耗时行认得出「哪首 + 多少毫秒 + 分段」' $m.Success '形状：打开「曲名」耗时 Nms：分段 分段…'
      if ($m.Success) {
        断言 '那一行记的是这一首' $m.Groups['曲'].Value $曲名
        断言真 '那个毫秒数是个正数' ([int]$m.Groups['毫'].Value -gt 0) "$($m.Groups['毫'].Value) 毫秒"
        $分段 = $m.Groups['分段'].Value
        "  分段：$分段"
        # **分段齐**：开曲那条路上的六段一段都不能少（少一段 = 有人把那个 Mark 删了）
        # ⚠️ 正则别写成 "$_ [0-9]+ms" —— 双引号里 `$_` 是个**变量读取**，而本脚本全文在
        #    严格模式下、这个作用域里没绑过 `$_` ⇒ 那一行会抛（跟判据无关的错）。用拼接。
        $该有 = @('读盘', '校验', '控制器', '挂轨', '试听', '收尾')
        $缺 = @($该有 | Where-Object { $分段 -notmatch ('(' + [regex]::Escape($_) + ' [0-9]+ms)') })
        断言真 '六段一段不少（读盘/校验/控制器/挂轨/试听/收尾）' ($缺.Count -eq 0) `
          $(if ($缺.Count -eq 0) { "分段：$分段" } else { "缺：$($缺 -join ' / ')；读到的是：$分段" })
      }
    }
  }

  # ---------- 6. 挪线程没有把功能挪坏 ----------
  "`n=== 6. 开完之后卷帘 / 试听还在 ==="
  # 卷帘：轨头那 4 颗「折叠」按钮（第 3 节数过）。
  # 🔴 **缩略条不在这里判**，不是漏了：`RollNavStrip` 是**自绘控件，UIA 树里没有它的框**
  #    （22 号实测，见 `verify-22.ps1` 抬头那句「导航条是自绘的 RollNavStrip，UIA 里没有它的框」）
  #    ⇒ 本脚本在这儿**判不了**它，就不装一条假绿。
  #    它「没被挪线程弄坏」由另外三件事兜住：① 这次改动碰的是**开曲那一条路**，
  #    `RefreshView` / `TrackLaneView.Refresh` / `BuildLane` 那条每帧的路一个字没动
  #    （diff 可查，票面也点名了）；② 票面要求的上机 ③ 那一趟；③ 代码里已有的两道
  #    `Bounds` 网（`MainWindow.axaml.cs:201` 的说明 + `:1842-1847` 的护栏）没被碰。
  # 试听那条路：播放键得是「有曲子才亮」那一态（`_playback.Load` 有没有把事件表交上去）
  $播 = @(找编号 'PlayButton')
  断言真 '播放键亮着（试听那条路被喂上了）' ($播.Count -eq 1 -and $播[0].Current.IsEnabled) `
    "数到 $($播.Count) 个，可用 = $(if ($播.Count -eq 1) { $播[0].Current.IsEnabled } else { '?' })"

  # ---------- 7. （可选，`-再开 X`）已经载着一首、**再开**第二首 ----------
  # 🔴 为什么补这一节：第 1~6 节量的是本实例**第一次**开曲（空态、最冷的一次）。
  #    可用户嘴里那句「打开时……按什么键都没有用」多半发生在**已经开着一首、再开第二首**
  #    那一下 —— 那时 `SyncLanes(rebuildAll: true)` 要把已经挂着的 $轨数 条轨**拆掉重挂**，
  #    和空态那一次不是同一条路。这只是**多量一趟**：尺子（探针上限、判据、观察窗口）
  #    一个字不动，第 1~6 节的行为也一个字节不改。
  if ($再开 -ne '') {
    "`n=== 7. 已经载着「$曲名」再开「$再开」（-再开 那一趟）==="
    $日志文件2 = ''
    $日志份2 = @(Get-ChildItem -LiteralPath $日志夹 -Filter 'MidiPerformer-*.log' -EA SilentlyContinue |
                 Where-Object { $先有 -notcontains $_.Name } | Sort-Object LastWriteTime -Descending)
    if ($日志份2.Count -ge 1) { $日志文件2 = $日志份2[0].FullName }
    $耗时行数前 = 0
    if ($日志文件2 -ne '') {
      $耗时行数前 = @(Select-String -LiteralPath $日志文件2 -Pattern '耗时' -SimpleMatch -EA SilentlyContinue).Count
    }
    $轨数前 = 数轨
    $名框前 = @(找编号 'SongNameBox')
    "  再开之前：曲名框 = $(if ($名框前.Count -ge 1) { 取值 $名框前[0] } else { '（没有）' })，挂着 $轨数前 条轨，日志里已有 $耗时行数前 行耗时"

    $库钮2 = @(找编号 'LibraryButton')
    if ($库钮2.Count -ne 1) { throw '第二次开曲库：工具栏上找不到 LibraryButton（按 AutomationId 数到 ' + $库钮2.Count + ' 颗）' }
    $库钮2[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $库窗2 = [IntPtr]::Zero
    $期限 = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $期限 -and $库窗2 -eq [IntPtr]::Zero) {
      foreach ($w in @([P40]::Others($脚本PID, $h))) {
        if ([P40]::Title($w) -eq '歌曲库') { $库窗2 = $w; break }
      }
      if ($库窗2 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 300 }
    }
    if ($库窗2 -eq [IntPtr]::Zero) { throw '第二次开曲库：没等到标题为「歌曲库」的那扇窗' }
    $库根2 = $AE::FromHandle($库窗2)
    $行2 = $null
    $期限 = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $期限 -and $null -eq $行2) {
      $行2 = @(库类型 $库根2 $CT::ListItem | Where-Object {
        @(行文 $_ | Where-Object { $_ -eq $再开 }).Count -gt 0
      }) | Select-Object -First 1
      if ($null -eq $行2) { Start-Sleep -Milliseconds 400 }
    }
    if ($null -eq $行2) { throw "第二次开曲库：列表里没有「$再开」这一行" }
    $行2文本 = 行文 $行2
    断言真 "第二趟：「$再开」那一行**自己**的字里有它" ($行2文本 -contains $再开) `
      "这一行里的字：$($行2文本 -join ' / ')"
    [void][P40]::Take($库窗2)
    [void]$行2.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
    [void]$行2.SetFocus()
    Start-Sleep -Milliseconds 400
    $选中2 = $行2.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
    "  回车之前：选中的那一行 = $($行2文本 -join ' / ')（IsSelected = $选中2）"
    断言真 "第二趟：回车要开的就是「$再开」那一行" ($选中2 -and ($行2文本 -contains $再开)) `
      "选中 = $选中2；行里的字 = $($行2文本 -join ' / ')"

    $起2 = Get-Date
    "  回车发下去的时刻 = $($起2.ToString('HH:mm:ss.fff'))"
    [V62]::Enter()

    $探针2数 = 0; $超时2数 = 0; $最长2 = 0; $慢2 = 0
    $图3 = ''; $不同色3 = -1; $图3的偏移 = -1; $图3的耗时 = 0
    $窗没了2 = $null; $装完2 = $null; $下次看日志2 = Get-Date
    while ($true) {
      if (-not [P40]::Alive($库窗2)) {
        if ($null -eq $窗没了2) { $窗没了2 = Get-Date }
        elseif ($null -ne $装完2 -and ((Get-Date) - $装完2).TotalMilliseconds -ge 600) { break }
        elseif ($null -eq $装完2 -and ((Get-Date) - $窗没了2).TotalMilliseconds -ge 3000) { break }
      }
      if (((Get-Date) - $起2).TotalSeconds -gt 120) { break }   # 有上界，绝不挂死
      # 收工信号：**日志里多出第二行耗时**（和第一趟同一把尺子，见第 2 节那串注释）
      if ((Get-Date) -ge $下次看日志2) {
        $下次看日志2 = (Get-Date).AddMilliseconds(150)
        if ($日志文件2 -ne '' -and $null -eq $装完2) {
          $现在行数 = @(Select-String -LiteralPath $日志文件2 -Pattern '耗时' -SimpleMatch -EA SilentlyContinue).Count
          if ($现在行数 -gt $耗时行数前) { $装完2 = Get-Date }
        }
      }
      $已过2 = [int]((Get-Date) - $起2).TotalMilliseconds
      # ⚠️ 取景时刻这里是 **10 毫秒**（第 2 节那边是 40）—— 实测校准出来的，不是随手改的：
      #    第二次开曲时那颗曲库窗**只活 48 毫秒**（19:45:54 那一趟：耗时 34ms、窗 48 毫秒就没了），
      #    40 毫秒那一下正好被一发慢探针跨过去 ⇒ 图 3 一次都拍不到（那一趟因此红了一条
      #    「没量到」，是**仪器的洞**、不是 app 的毛病）。10 毫秒稳稳落在窗还活着的时候。
      #    ⚠️ 它只是个**取景时刻**（仪器旋钮），不是判据；拍不到照样报红（见本节末尾那条断言）。
      # 探针在截图**前面**，理由和第 2 节一模一样（`PrintWindow` 是同步的）
      $r = [IntPtr]::Zero
      $sw = [System.Diagnostics.Stopwatch]::StartNew()
      $送到 = [P40]::SendMessageTimeout($h, [uint32]0, [IntPtr]::Zero, [IntPtr]::Zero,
                                        [uint32][P40]::SMTO_ABORTIFHUNG, [uint32]$探针毫秒, [ref]$r)
      $sw.Stop()
      $ms2 = $sw.ElapsedMilliseconds
      $探针2数++
      if ($ms2 -gt $最长2) { $最长2 = $ms2 }
      if ($送到 -eq [IntPtr]::Zero) { $超时2数++ }
      elseif ($ms2 -ge [Math]::Floor($探针毫秒 / 2)) { $慢2++ }
      if ($图3 -eq '' -and $null -eq $窗没了2 -and $已过2 -ge 10) {
        $这一刻2 = [int]((Get-Date) - $起2).TotalMilliseconds
        $sw3 = [System.Diagnostics.Stopwatch]::StartNew()
        $b3 = [P40]::Shot($h); $bw3 = [P40]::LastW; $bh3 = [P40]::LastH
        $sw3.Stop()
        $图3 = Join-Path $图 "第二趟加载中(回车后$($这一刻2)ms).png"
        $图3的偏移 = $这一刻2; $图3的耗时 = $sw3.ElapsedMilliseconds
        存图 $b3 $bw3 $bh3 $图3
        $不同色3 = 数不同色 $b3 $bw3 $bh3
        "  截图 3：回车后 $($这一刻2) 毫秒、曲库窗还开着 —— $($图3.Substring($图.Length + 1))  不同色数 = $不同色3  截图本身花了 $图3的耗时 毫秒"
      }
    }
    $总2 = [int]((Get-Date) - $起2).TotalMilliseconds
    $窗2毫秒 = if ($null -ne $窗没了2) { [int](($窗没了2) - $起2).TotalMilliseconds } else { $总2 }
    $收工2 = if ($null -ne $装完2) { "等到第二行耗时（回车后 $([int](($装完2) - $起2).TotalMilliseconds) 毫秒）" } `
             else { '没等到第二行耗时 —— 按兜底（窗关后 3 秒）收的工' }
    "  曲库窗关掉用了 $窗2毫秒 毫秒；连「轨画出来」那一段一起观察到 $总2 毫秒"
    "  观察窗口的收工原因：$收工2"
    "  探针 $探针2数 发（超时 $超时2数 发、慢 $慢2 发、最长一发 $最长2 毫秒）"
    if ([P40]::Alive($库窗2)) { throw "第二趟：曲库窗没关掉（等了 $总2 毫秒）—— 曲子没装上" }

    Start-Sleep -Milliseconds 800
    $名框2 = @(找编号 'SongNameBox')
    $期限 = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $期限 -and $名框2.Count -ge 1 -and (取值 $名框2[0]) -ne $再开) {
      Start-Sleep -Milliseconds 400
      $名框2 = @(找编号 'SongNameBox')
    }
    $轨数2 = 数轨
    $前提2 = ($名框2.Count -ge 1 -and (取值 $名框2[0]) -eq $再开 -and $轨数2 -ge 1)
    # 第二趟的 ① 那一半**也是只报不判红**（和第一趟同理：HEAD 上它就是绿的）。
    # 这里只留**前提**那两条当真门 —— 它们与机器快慢无关，且前提不成立时这一趟量的不是那首歌。
    断言 '第二趟：曲名框里是那一首' $(if ($名框2.Count -ge 1) { 取值 $名框2[0] } else { '（找不到 SongNameBox）' }) $再开
    断言真 '第二趟：轨都挂上了（至少一条）' ($轨数2 -ge 1) "$轨数2 条轨（这一趟开始前挂着 $轨数前 条）"
    # 🔴 下面三行**不计数进 `$fail`**（像第一趟第 4 节那样）：超时数 / 探针数 / 截图都是 ①。
    "  ○（只报不判红）第二趟加载期间：超时 $超时2数 发 / 共 $探针2数 发，最长 $最长2 毫秒（上限 $探针毫秒）；观察窗口 = 回车 → $收工2（$总2 毫秒）"
    if ($超时2数 -ne 0) {
      "      （这一行**不判红**：那一串读数只说明「这一刻界面线程被占了多久」，不代表这次改动坏了）"
    }
    if ($探针2数 -lt 3) { "  ●（只报不判红）第二趟仪器好像没转：只发了 $探针2数 发探针" }
    if ($图3 -eq '') {
      # 🔴 这一格在第二趟**基本拍不到**，而且原因是**仪器**、不是 app：`起2` 到第一发探针之间
      #    隔着 `[V62]::Enter()` 那 30 毫秒的抬键 + 写记录，**最早也要 ~45 毫秒**才看得到窗口；
      #    而第二次开曲那颗曲库窗只活 **46~48 毫秒**（19:45:54 与 19:46:48 两趟的打印：
      #    耗时 33~34 毫秒、窗 46~48 毫秒就没了）⇒ 「加载中」那一刻在仪器能睁眼之前就过去了。
      #    （旁证：这两趟里「等到第二行耗时」都报在**回车后 47 毫秒** —— 那正是第一发探针的时刻。）
      #    ⇒ 这一格是 **没量到**（不是绿，也不是 app 卡）：第二趟的 ① 只有探针那一半是齐的。
      "  ○（只报不判红）第二趟没截到加载中的图（**仪器的洞**：最早那一眼也在 ~45 毫秒，而窗只活 46 毫秒）—— 没量到，不是绿"
    } elseif ($不同色3 -ge 3) {
      "  ○（只报不判红）第二趟加载途中那张图不是全白/全黑：$($图3.Substring($图.Length + 1)) 上采到 $不同色3 种颜色"
    } else {
      "  ○（只报不判红）第二趟加载途中那张图只有 $不同色3 种颜色（像是全白/全黑）—— 记下来，不当红"
    }
    if ($日志文件2 -ne '') {
      $命中2 = @(Select-String -LiteralPath $日志文件2 -Pattern '耗时' -SimpleMatch -EA SilentlyContinue)
      "  日志里现在有 $($命中2.Count) 行耗时；最后一行：$(if ($命中2.Count -ge 1) { $命中2[$命中2.Count - 1].Line } else { '（没有）' })"
    }
  }

  $跑完了 = $true
}
catch { Write-Host "`n★ 脚本跑到一半抛了：$_" }
finally {
  # 收尾：只收自己起的那一个（库的 `收窗口` 认 $script:proc）。抛的时候也要收 ——
  # 留一个孤儿实例在桌面上会锁死下一个 agent 的构建。
  # ⚠️ 读 `$script:proc` 得包在 try 里：`起窗口带清障` 可能在 Start-Process **之前**就抛
  #    （「已经有实例在跑」那一条），那时这个变量**压根没绑过**，而本脚本全文跑在严格模式下
  #    —— 裸着读它会再抛一条，把收尾变成一句看不懂的错。
  $己 = '（没起来）'
  try { if ($null -ne $script:proc) { $己 = $script:proc.Id } } catch { }
  try { 收窗口 } catch { "  ⚠️ 收窗口时又抛了一条：$_" }
  Start-Sleep -Milliseconds 600
  "`n实例已收（PID $己）"
}

"`n========== 结果 =========="
if (-not $跑完了) { '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'; $fail = $fail + 1 }
elseif ($fail -eq 0) { '全过（**门只有 ②**：耗时行打出来了、分段齐、记的是这一首）' } else { "$fail 条红" }
# 🔴 抬头那件事重复一遍，免得有人把这份报告读成「① 也过了」：
#    ①（加载期间窗口还活着）在本脚本里**只报不判红** —— 它在 HEAD 上就是绿的（报告 C/D/F/G 四趟），
#    是一条分不出改前改后的**假门**。第 4 节 / 第 7 节里那些 `○（只报不判红）` 行**不参与这个数**。
'（① 加载期间界面还在抽消息 / 截到画面：本报告里只有读数，不判红 —— 见抬头）'
"证据：报告 $报告"
if ($图1 -ne '') { "     截图 1 $图1" }
if ($图2 -ne '') { "     截图 2 $图2" }
if ($图3 -ne '') { "     截图 3（第二趟）$图3" }
# 81 号票：裁决行 —— run-all.ps1 拿它跟退出码复核，对不上就把这一条降级成红（别删）。
"==== uitest 裁决 不过=$fail"
# 收尾这一句也要包起来：它抛的话会把**退出码**顶掉，run-all 那边就跟裁决行对不上了。
try { Stop-Transcript | Out-Null } catch { }
exit $fail
