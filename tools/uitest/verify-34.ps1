# 34 号工单的实机验证：**「跳到 __ 小节」回车之后，焦点从框里放开**。
#
# 用法: pwsh -NoProfile -File verify-34.ps1     （脚本自己起 app、自己收尾）
#
# 复用 33 号那份骨架（UIA 真找元素、前台 + 落点双重闸门，两个坑的来龙去脉见 verify-23.ps1 抬头）。
#
# **这一票的题面就是「原样按一个空格」**，所以这支脚本在这一点上跟别的不一样：
#   · 回车之后**不 Tab、不碰鼠标、不挪焦点**，直接发一个空格 —— 用户就是这么按的。
#   · 发空格之前**不拽前台**（`前台` 里的 `Take` 会 `SetFocus(h)`，那一下会把焦点
#     **还回最后一次聚焦的输入框**，见 `.scratch/probe-20-space.ps1` 量出来的那条）。
#     前台只在**真掉了**的时候才拽，而且会当场报出来 —— 拽了之后的读数就不算数了。
#   · 脚本**不按空格来暂停**：控制状态一律走播放键的 `InvokePattern`，
#     免得「空格没到」被误读成「暂停坏了」（33 号那轮的教训）。
#
# 判据分三条，缺一条都不够：
#   1. **焦点不在任何输入框里、且落在卷帘上** —— UIA 的 `FocusedElement` 读到的 `ControlType`
#      不是 `Edit`，`AutomationId` 是 **`LanesScroll`**（这一轮量出来的，四条判据各读一遍都一致）。
#      `LanesHost` 自己按 AutomationId 找得 0 个（33 号那轮量的），读到的是它下面那层滚动容器 ——
#      所以落点判据写在 `LanesScroll` 上，「焦点在卷帘上」这句是量出来的、不是读代码推的。
#   2. **空格真的把曲子放起来了** —— 按钮字变「⏸ 暂停」+ 3 秒后「位置」往前走。
#      只量 1 的话，「焦点走了但空格没人接」照样过。
#   3. **框里没多出空格** —— `ValuePattern.Value` 读回来还是个纯数字。
#      只量 1+2 的话，「空格进了框、顺手又触发了播放」理论上也能过。
#
# 第 4 节是**反证**：焦点**主动**放进框里时，空格照样打进框里（20 号工单那条事实）。
# 它在，才能说第 1 节不是靠「空格反正进不了框」蒙过去的。

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class V34 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x,int y,int cx,int cy,uint f);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
  public static extern uint GetThreadPid(IntPtr h, out uint pid);
  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "（空）";
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    uint pid; GetThreadPid(h, out pid);
    return "句柄 " + h + " 类名'" + c + "' 标题'" + t + "' PID " + pid;
  }
  // 和 verify-19/20/33 同一份：合成按键要求目标窗口在前台，拽一次要连 Alt 一起打
  public static bool Take(IntPtr h) {
    IntPtr fg = GetForegroundWindow();
    uint a = GetWindowThreadProcessId(fg, IntPtr.Zero), b = GetWindowThreadProcessId(h, IntPtr.Zero);
    AttachThreadInput(a, b, true);
    keybd_event(0x12,0,0,IntPtr.Zero); keybd_event(0x12,0,2,IntPtr.Zero);
    SetForegroundWindow(h);
    if (GetForegroundWindow() != h) ShowWindow(h, 9);
    if (GetForegroundWindow() != h) {
      SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0040);
      SetWindowPos(h, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0040);
    }
    AttachThreadInput(a, b, false);
    SetFocus(h); System.Threading.Thread.Sleep(400);
    return GetForegroundWindow() == h;
  }
}
"@

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

# ---------- 起一个干净实例 ----------
$根 = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = Join-Path $根 'MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
  [void][V34]::ShowWindow($_.MainWindowHandle, 9)
  [void]$_.CloseMainWindow()
  if (-not $_.WaitForExit(8000)) { $_.Kill() }
}
Start-Sleep -Milliseconds 900
$proc = Start-Process -FilePath $exe -PassThru
$期限 = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; if ($proc.HasExited) { throw "窗口没起来，退出码 $($proc.ExitCode)" }; $proc.Refresh() }
while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }
Start-Sleep -Seconds 3
$h = $proc.MainWindowHandle
"起了个干净实例：PID $($proc.Id)"

if (-not [V34]::Take($h)) { throw '拽不到前台' }
# 摆在**工作区里**（26 号量出来的坑：这台机器任务栏从 y=1824 起）
[void][V34]::SetWindowPos($h, [IntPtr]::Zero, 405, 300, 2360, 1300, 0x0004 -bor 0x0010)
Start-Sleep -Milliseconds 900

# ---------- 取控件 ----------
function 找类型([object]$类型) { @(($AE::FromHandle($h)).FindAll($TS::Descendants, (& $条件 $类型))) }
function 按编号([string]$id) {
  @(@(找类型 $CT::Text) + @(找类型 $CT::Button) + @(找类型 $CT::Edit) + @(找类型 $CT::Pane) +
    @(找类型 $CT::Custom) + @(找类型 $CT::List) + @(找类型 $CT::ListItem) + @(找类型 $CT::MenuItem)) |
    Where-Object { $_.Current.AutomationId -eq $id }
}
function 文本([string]$含) { @(找类型 $CT::Text | Where-Object { $_.Current.Name -like "*$含*" }) }
function 数轨 { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' }).Count }
function 行里的文字($行) { @($行.FindAll($TS::Descendants, (& $条件 $CT::Text))) }
function 找行([string]$曲名) {
  @(找类型 $CT::ListItem | Where-Object {
    $t = 行里的文字 $_
    $t.Count -gt 0 -and $t[0].Current.Name -eq $曲名 }) | Select-Object -First 1
}
function 取值($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function 按钮字 { (按编号 'PlayButton')[0].Current.Name }

# 焦点现在在谁身上。**这一票的主判据之一**：`ControlType` 是不是 `Edit`。
# 只报得出「谁」，报不出「在卷帘上」—— `LanesHost` 在 UIA 树里读不到（见抬头）。
function 焦点是谁 {
  $f = [System.Windows.Automation.AutomationElement]::FocusedElement
  if (-not $f) { return '（没有焦点元素）' }
  "$($f.Current.ControlType.ProgrammaticName) id='$($f.Current.AutomationId)' 名='$($f.Current.Name)'"
}
function 焦点是输入框 { (焦点是谁) -like 'ControlType.Edit*' }
# 落点：回车之后焦点读到的是 `Pane id='LanesScroll'`（**量出来的**）。
# 注意 `LanesHost` 自己按 AutomationId 找得 0 个（33 号那轮量的），
# 读到的是它下面那层滚动容器 —— 所以判据写在 `LanesScroll` 上，不写在 `LanesHost` 上。
function 焦点是卷帘 { (焦点是谁) -like "*id='LanesScroll'*" }

# 「位置」那一格：先找「位置」标签，再取它右边紧挨着那格值（照抄 verify-20/33，不写死 Y）
function 位置原文 {
  $t = 找类型 $CT::Text
  $lbl = $t | Where-Object { $_.Current.Name -eq '位置' } | Select-Object -First 1
  if (-not $lbl) { return '(找不到「位置」标签)' }
  $lr = $lbl.Current.BoundingRectangle
  $v = $t | Where-Object { $r = $_.Current.BoundingRectangle
      [Math]::Abs($r.Y - $lr.Y) -lt 14 -and $r.X -gt $lr.X -and $r.X -lt ($lr.X + 160) } |
    Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1
  if ($v) { $v.Current.Name } else { '(读不到)' }
}
function 位置小节 {
  $m = [regex]::Match((位置原文), '^\s*(\d+)')
  if ($m.Success) { return [int]$m.Groups[1].Value }
  return -1
}

# **前台只在真掉了时才拽**（抬头里那条）：拽会把焦点打回输入框，拽完的读数就不算数了。
function 确保前台([string]$谁) {
  if ([V34]::GetForegroundWindow() -eq $h) { return $true }
  "  ！！ 「$谁」之前前台掉了（台面上是 $([V34]::Describe([V34]::GetForegroundWindow()))）—— 拽回来；"
  "     这一下 `Take` 里的 `SetFocus(h)` 会把焦点打回输入框，本节的焦点读数作废。"
  return [V34]::Take($h)
}

$小节框 = (按编号 'JumpBox')[0]
$速度框 = (按编号 'BpmBox')[0]
if (-not $小节框) { throw '找不到 JumpBox' }
if (-not $速度框) { throw '找不到 BpmBox' }

# 写值 + 回车提交。**没有 TAB** —— 这一票量的正是「回车之后焦点自己走不走」，
# 脚本用 Tab 帮忙就等于把判据自己实现了。
function 回车提交($框, [string]$值) {
  [void](确保前台 '回车提交')
  [void]$框.SetFocus(); Start-Sleep -Milliseconds 300
  $框.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($值)
  Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
  Start-Sleep -Milliseconds 700
}
function 跳小节([int]$n) { 回车提交 $小节框 ([string]$n) }

# **原样发一个空格**：不碰焦点、不 Tab、不拽前台（只在前台真掉了时才拽，而且会报出来）。
# 用户就是这么按的 —— 「跳到第 6 小节 → 回车 → 空格」。
function 按空格原样([string]$在哪) {
  [void](确保前台 "在「$在哪」按空格")
  "  （发空格前，焦点 = $(焦点是谁)）"
  [System.Windows.Forms.SendKeys]::SendWait(' ')
  Start-Sleep -Milliseconds 800
}
# 控制状态一律走播放键的 Invoke（同一条 TogglePlayback），不拿空格当控制键
function 按播放键 {
  [void](确保前台 '按播放键')
  (按编号 'PlayButton')[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 700
}
# 让曲子停下来、按钮回到「▶ 播放」：唯一的路是让它自己放到曲尾（33 号之后没有 ■ 停止 了）
function 停下 {
  if ((按钮字) -eq '▶ 继续') { 按播放键 }
  跳小节 124
  $等 = 0
  while ((按钮字) -ne '▶ 播放' -and $等 -lt 60) { Start-Sleep -Milliseconds 500; $等++ }
  "  （收工：等了 $([Math]::Round($等 * 0.5, 1)) 秒，按钮 = $(按钮字)）"
}

# ---------- 载入 Carulli ----------
$曲名 = 'Carulli_Duetto_No2_Op4'
$行 = 找行 $曲名
if (-not $行) { throw "曲库里没有「$曲名」" }
if (-not (确保前台 '载歌')) { throw '拽不到前台' }
[void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$行.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 5
"载入「$曲名」：$(数轨) 条轨"
# 速度 120：原速 50 太慢，「3 秒后有没有往前走」读不出变化（33 号那轮量出来的刻度）
回车提交 $速度框 '120'

# =====================================================================
"`n=== 1. 跳第 6 小节 → 回车 → 直接按空格 = 开始播放 ==="
# =====================================================================
# 用户报的那条路，一步一步走一遍：跳小节、回车、**原样**一个空格。
跳小节 6
"  回车之后：位置 = $(位置原文)；焦点 = $(焦点是谁)；JumpBox 里是「$(取值 $小节框)」"
断言 '跳到了第 6 小节' (位置小节) 6
断言真 '回车之后焦点不在输入框里了' (-not (焦点是输入框)) "焦点 = $(焦点是谁)"
断言真 '回车之后焦点落在卷帘上' (焦点是卷帘) "焦点 = $(焦点是谁)"
$跳后框 = 取值 $小节框
断言 '回车之后框里是（原样那串数字）' $跳后框 '6'

按空格原样 '跳小节回车之后'
断言 '回车之后直接按空格：按钮变成' (按钮字) '⏸ 暂停'
断言 '空格没有被打进框里' (取值 $小节框) $跳后框
断言真 '框里没有空格（长度 = 数字的长度）' ((取值 $小节框).Length -eq $跳后框.Length) `
  "「$(取值 $小节框)」长度 $((取值 $小节框).Length)"
Start-Sleep -Seconds 3
$放起来了 = 位置小节
"  又等了 3 秒，位置 = $(位置原文)"
断言真 '确实在往前放（不是只换了按钮上的字）' ($放起来了 -gt 6) "从第 6 小节走到第 $放起来了 小节"

# =====================================================================
"`n=== 2. 输错（不是数）回车：退回、**焦点一样出来** ==="
# =====================================================================
# 「放开焦点」放在校验之前（照抄 BPM 那一路）—— 这一节钉的就是这个次序：
# 只在成功那一路放开的话，输错了接着按空格修，空格又进框了。
#
# **量出来的一件事**：跳小节框输错是**静默退回**的 —— 一条报错都没有
# （`OnJumpKeyDown` 里那个 `return` 前面没有 `ShowError`，和速度框不一样）。
# 这条不是这一票改的，也不是这一票该改的（用户要的是焦点，不是报错文案），
# 但既然量到了就写下来 —— 免得下一个人以为「没报错 = 这条没走到」。
# 所以这一节的判据是「框退回了 + 焦点出来了 + 空格能放」，不是「有没有报错条」。
停下
跳小节 20
断言 '先跳到第 20 小节' (位置小节) 20
回车提交 $小节框 'abc'
$错 = (文本 '不算').Count
"  回车之后：错误条 $错 条（跳小节这一格是静默退回）；位置 = $(位置原文)；焦点 = $(焦点是谁)"
断言 '输错之后框退回当前小节' (取值 $小节框) '20'
断言真 '输错之后焦点也出来了' (-not (焦点是输入框)) "焦点 = $(焦点是谁)"
断言真 '输错之后焦点也落在卷帘上' (焦点是卷帘) "焦点 = $(焦点是谁)"
按空格原样 '输错回车之后'
断言 '输错之后直接按空格：按钮变成' (按钮字) '⏸ 暂停'
Start-Sleep -Seconds 3
$错后放 = 位置小节
"  又等了 3 秒，位置 = $(位置原文)"
断言真 '输错那一路也真的放起来了' ($错后放 -gt 20) "从第 20 小节走到第 $错后放 小节"

# =====================================================================
"`n=== 3. 对照：速度框回车之后本来就是这么做的 ==="
# =====================================================================
# 这一票是**跟上既有规矩**（`OnBpmKeyDown` / `OnSongNameKeyDown` 早就在调 `ReleaseEditFocus()`），
# 不是新发明 —— 这一节把「规矩」本身量一遍，省得以后有人以为是跳小节这一格的特殊处理。
停下
回车提交 $速度框 '120'
"  速度框回车之后：焦点 = $(焦点是谁)；速度框里是「$(取值 $速度框)」"
断言真 '速度框回车之后焦点也不在输入框里' (-not (焦点是输入框)) "焦点 = $(焦点是谁)"
按空格原样 '速度框回车之后'
断言 '速度框回车之后直接按空格：按钮变成' (按钮字) '⏸ 暂停'
Start-Sleep -Seconds 2
断言真 '速度框那一路也真的放起来了' ((位置小节) -gt 1) "位置 = $(位置小节)"

# 对照的第二半：**速度框输错是有报错条的**（「…不算」那句），跳小节框没有。
# 这一条跟焦点无关，但它把 §2 那句「静默退回」钉成对照 —— 免得下一个人把
# 「跳小节框没报错」当成「那一节没跑到」。
停下
回车提交 $速度框 'abc'
$速度错 = (文本 '不算').Count
"  速度框里写 abc 回车：错误条 $速度错 条；框里是「$(取值 $速度框)」；焦点 = $(焦点是谁)"
断言真 '速度框输错报了一条错（跳小节框是静默退回，见 §2）' ($速度错 -ge 1) "数到 $速度错 处"
断言真 '速度框输错之后焦点也出来了' (-not (焦点是输入框)) "焦点 = $(焦点是谁)"
回车提交 $速度框 '120'

# =====================================================================
"`n=== 4. 反证：焦点主动放进框里时，空格**照样**打进框里 ==="
# =====================================================================
# 20 号工单那条事实（`verify-20.ps1` §6）。它在，才能说第 1 节不是靠
# 「空格反正进不了框」蒙过去的 —— 两节合起来才说明「回车把焦点放开了」这件事本身有效。
停下
跳小节 6
[void](确保前台 '反证')
[void]$小节框.SetFocus(); Start-Sleep -Milliseconds 400
$前 = 取值 $小节框
"  （主动把焦点放进框里）焦点 = $(焦点是谁)；框里是「$前」"
[System.Windows.Forms.SendKeys]::SendWait(' '); Start-Sleep -Milliseconds 700
$后 = 取值 $小节框
断言真 '焦点在框里时，空格确实进了框' ($后.Length -gt $前.Length) "「$前」→「$后」"
断言 '空格进了框的时候，按钮没被带动' (按钮字) '▶ 播放'# 收尾：把那个空格清掉、把框恢复成正常值（不然留给下一个人一个「6 」）
回车提交 $小节框 '6'
断言 '收尾：框恢复成' (取值 $小节框) '6'

"`n实例已收（PID $($proc.Id)）"
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill() }

"`n========== 结果 =========="
if ($fail -eq 0) { '全过' } else { "$fail 条不过" }
exit $fail
