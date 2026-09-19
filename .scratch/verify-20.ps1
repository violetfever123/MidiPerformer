# 20 号工单：空格 = 播放 / 暂停 / 继续，走带条上播放暂停合成一颗切换按钮。
#
# 判据分两层：
#   · **按钮上的字** —— ▶ 播放 / ⏸ 暂停 / ▶ 继续。这三态之间的切换正是这张工单的正题，
#     而且 UIA 直接读得到（按钮的 Name 就是 Content），比看像素稳。
#   · **「位置」读数的小节号** —— 说清「播放头到底动没动」。暂停那一条不能只看按钮字：
#     字换成「继续」而播放头还在爬，是这条路最容易出的错，而且看着一切正常。
#     所以暂停之后**隔几秒读两次**，两次一样才算真冻住了。
#
# 两处为了让测试跑得动而做的安排，都不是被测对象的性质：
#   · **先把速度设成 240**（曲子的原速是 50 —— 一小节 4.8 秒，等一次要十几秒，
#     而且第 6 节要「放完」，124 小节按原速得十分钟）。速度是这张工单管不着的东西。
#   · **第 6 节先跳到第 120 小节**再放：从那儿到曲尾只剩几小节，几秒就放完了。
#
# 用法: pwsh -File verify-20.ps1   （app 要先开着、已经载入曲子）
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public class V20 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x,int y,int cx,int cy,uint f);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
  // 和 verify-19 同一个：合成按键要求目标窗口在前台，拽一次要连 Alt 一起打
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
$C = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$p = Get-Process -Name dotnet, MidiPerformer -EA SilentlyContinue |
  Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
if (-not $p) { throw 'MidiPerformer 没在跑' }
$h = $p.MainWindowHandle
$root = $AE::FromHandle($h)

$fail = 0
function 断言([string]$名字, $实际, $期望) {
  if ("$实际" -eq "$期望") { "  OK   $名字 = $实际" }
  else { "  FAIL $名字 = $实际（期望 $期望）"; $script:fail++ }
}
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}

# ---------- 取控件 ----------
function 全部([object]$类型) { @($root.FindAll($TS::Descendants, (& $C $类型))) }
function 按编号([string]$id) { 全部 $CT::Button | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1 }
function 输入框([string]$id) { 全部 $CT::Edit | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1 }

$播放键 = 按编号 'PlayButton'
$停止键 = 按编号 'StopButton'
$速度框 = 输入框 'BpmBox'
$小节框 = 输入框 'JumpBox'
foreach ($对 in @(@('PlayButton',$播放键), @('StopButton',$停止键), @('BpmBox',$速度框), @('JumpBox',$小节框))) {
  if (-not $对[1]) { throw "找不到控件 $($对[0])" }
}

function 按钮字 { $播放键.Current.Name }
function 停止可用 { $停止键.Current.IsEnabled }
function 播放可用 { $播放键.Current.IsEnabled }

# 「位置」那一格：先找「位置」标签，再取它右边紧挨着那格值（不写死 Y）
function 位置原文 {
  $t = 全部 $CT::Text
  $lbl = $t | Where-Object { $_.Current.Name -eq '位置' } | Select-Object -First 1
  if (-not $lbl) { return '(找不到「位置」标签)' }
  $lr = $lbl.Current.BoundingRectangle
  $v = $t | Where-Object { $r = $_.Current.BoundingRectangle
      [Math]::Abs($r.Y - $lr.Y) -lt 14 -and $r.X -gt $lr.X -and $r.X -lt ($lr.X + 160) } |
    Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1
  if ($v) { $v.Current.Name } else { '(读不到)' }
}
# 只取小节号那个数：「030 / 124 小节」-> 30
function 位置小节 {
  $m = [regex]::Match((位置原文), '^\s*(\d+)')
  if ($m.Success) { [int]$m.Groups[1].Value } else { -1 }
}

function 前台 { if (-not [V20]::Take($h)) { throw '拽不到前台 —— 台面上有别的窗口压着' } }
function 按空格 { 前台; [System.Windows.Forms.SendKeys]::SendWait(' '); Start-Sleep -Milliseconds 500 }
function 回车 { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Milliseconds 600 }

# 把焦点从输入框里挪出去。
#
# **不挪的话后面每一步的空格都打进框里** —— 而这正是这张工单要的行为（第 6 节验的就是它）。
# 于是「空格没反应」会伪装成「空格播放坏了」，而且伪装得看不出来：
# 按钮没变、位置没动，和「这段逻辑没生效」一模一样。
#
# 用 Tab 而不是鼠标点一下：点哪儿都可能顺带改点什么（点卷帘会改选中），
# 而 Tab 只动焦点。落的那个控件是什么无所谓 —— 走带键要的只是「焦点不在输入框里」。
function 松焦点 {
  前台
  for ($i = 0; $i -lt 12; $i++) {
    $f = [System.Windows.Automation.AutomationElement]::FocusedElement
    if (-not $f -or $f.Current.ControlType -ne $CT::Edit) { return }
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}'); Start-Sleep -Milliseconds 250
  }
  "  （警告：Tab 了 12 下焦点还在输入框里）"
}

# 往输入框里写值并回车提交。先 SetFocus 再写 —— 回车要落到那个框上才算提交
function 填框($框, [string]$值) {
  前台
  [void]$框.SetFocus(); Start-Sleep -Milliseconds 300
  try { $框.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($值) }
  catch { throw "往框里写值失败：$_" }
  Start-Sleep -Milliseconds 300
  回车
  松焦点
}
# 轮到第 n 小节（先退回去再加，省得「值没变、回车没生效」看不出区别）
function 跳小节([int]$n) {
  填框 $小节框 ([string]($n - 1))
  填框 $小节框 ([string]$n)
}

# ---------- 0. 前置 ----------
"=== 0. 前置 ==="
填框 $速度框 '240'          # 原速 50 太慢，等不起（见文件头）
断言 '开始时按钮是' (按钮字) '▶ 播放'
断言真 '开始时 ■ 可用' (停止可用) '有谱面就能按（20 号的决定：■ 始终可用）'
"  位置 = $(位置原文)"

# ---------- 1. 空格开始 ----------
"`n=== 1. 空格开始播放 ==="
跳小节 30
$起点 = 位置小节
"  跳到第 30 小节后位置 = $(位置原文)"
按空格
断言 '按空格之后按钮变成' (按钮字) '⏸ 暂停'
Start-Sleep -Seconds 3
$走了 = 位置小节
"  等了 3 秒，位置 = $(位置原文)"
断言真 '播放头在走' ($走了 -gt $起点) "从第 $起点 小节走到第 $走了 小节"

# ---------- 2. 再按空格 = 暂停，播放头冻住 ----------
"`n=== 2. 再按空格 = 暂停 ==="
按空格
断言 '暂停时按钮变成' (按钮字) '▶ 继续'
断言真 '暂停期间 ■ 可用' (停止可用) '■ 始终可用'
$冻1 = 位置小节
Start-Sleep -Seconds 3
$冻2 = 位置小节
"  暂停后隔 3 秒读两次：$(位置原文)"
断言 '两次读数一样（播放头冻住了）' $冻2 $冻1

# ---------- 3. 再按空格 = 从暂停处继续 ----------
"`n=== 3. 再按空格 = 从暂停处继续 ==="
按空格
断言 '继续时按钮变成' (按钮字) '⏸ 暂停'
Start-Sleep -Seconds 3
$续 = 位置小节
"  等了 3 秒，位置 = $(位置原文)"
断言真 '从暂停处接着走（比暂停点更靠后）' ($续 -gt $冻1) "暂停在 $冻1，现在 $续"
断言真 '不是从头开始' ($续 -gt $起点) "起点是 $起点，不是回到那儿"

# ---------- 4. 暂停期间按 ■ 停止 ----------
"`n=== 4. 暂停期间 ■ 停止 ==="
按空格
断言 '先停在「继续」上' (按钮字) '▶ 继续'
$停前 = 位置小节
前台
$停止键.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 800
断言 '按 ■ 之后按钮回到' (按钮字) '▶ 播放'
断言 '■ 不重置播放头（现有行为）' (位置小节) $停前

# ---------- 5. 播到结尾自动停止后，能重新开始 ----------
"`n=== 5. 放完自动停 ==="
跳小节 120
"  跳到第 120 小节，位置 = $(位置原文)"
按空格
断言 '开始放到曲尾' (按钮字) '⏸ 暂停'
$等 = 0
while ((按钮字) -ne '▶ 播放' -and $等 -lt 40) { Start-Sleep -Milliseconds 500; $等++ }
"  等了 $([Math]::Round($等 * 0.5, 1)) 秒"
断言 '放完之后按钮回到' (按钮字) '▶ 播放'
断言真 '放完之后播放键可用（空格能重新开始）' (播放可用) '没卡在「继续」上'
断言 '放完之后位置停在曲尾' (位置小节) 124

按空格
断言 '放完再按空格能重新开始' (按钮字) '⏸ 暂停'
前台
$停止键.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 600
断言 '收尾：■ 停回' (按钮字) '▶ 播放'

# ---------- 6. 焦点在输入框里时，空格打空格 ----------
# **放在最后**：这一节会把焦点留在小节号框里，后面再合成按键就全进那个框了
"`n=== 6. 焦点在输入框里，空格不触发播放 ==="
前台
[void]$小节框.SetFocus(); Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')     # 先把框里可能有的旧值收拾掉
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait('{BACKSPACE}{BACKSPACE}{BACKSPACE}{BACKSPACE}{BACKSPACE}')
Start-Sleep -Milliseconds 300
$空 = $小节框.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
"  输入框现在是「$空」，焦点在里面"
[System.Windows.Forms.SendKeys]::SendWait(' '); Start-Sleep -Milliseconds 700
$后 = $小节框.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
断言真 '空格进了输入框' ($后.Length -gt $空.Length) "「$空」→「$后」"
断言 '按钮没被空格带动' (按钮字) '▶ 播放'

"`n$(if ($fail -eq 0) { '全过' } else { "$fail 条不过" })"
exit $fail
