# 35 号工单的实机验证：**Shift + 空格 = 回跳一小节并播放**（空格本身仍是播放 / 暂停）。
#
# 用法: pwsh -NoProfile -File verify-35.ps1     （脚本自己起 app、自己收尾）
#
# 复用 33/34 号那份骨架（UIA 真找元素、前台 + 落点双重闸门，两个坑的来龙去脉见 verify-23.ps1 抬头）。
#
# 这一票有两个**必须分开量**的东西，合起来才算数：
#   1. **Shift+空格 真的退了一小节**（「位置」读数 = 当前小节 − 1，且落在小节头上，不是退到小节中间）
#      —— 光量「按钮变成 ⏸ 暂停」的话，一个「什么都没退、只是开始放」的实现照样绿。
#   2. **空格本身一个字都没变**（用户原话：「空格还是播放和暂停的切换」）——
#      这一条由 §2 单独量，而且**用普通空格**：新加的那一支要是抢在空格前面，
#      症状就是「按空格回跳一小节」—— 播放 / 暂停就此消失，而这在屏幕上什么也看不出来。
#      （`verify-20.ps1` 那 28 条是这一条的全量版，重跑过；这里只留一句最短的对照。）
#
# 发键一律用 SendKeys 的 `+ `（Shift+空格）。**不挪焦点、不做 Tab**，理由和 34 号那一票一样：
# 这一票量的是「用户按下去会怎样」，脚本自己把焦点摆好就等于把判据自己实现了。
# 前台只在真掉了时才拽（`Take` 里的 `SetFocus(h)` 会把焦点打回最后一个输入框，见 34 号那条）。
#
# 顺带量两件**没人要求、但改了这一段就绕不开**的事：
#   · §0 没装曲子时它不该响（Shift+空格 是按键，绕得过按钮的灰 —— 所以这道闸门是这次新写的）
#   · §7 焦点停在按钮上时，它不该顺手把那颗按钮也按了（35 号把「收焦点」那一下提成了公共方法）

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class V35 {
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
  // 和 verify-19/20/33/34 同一份：合成按键要求目标窗口在前台，拽一次要连 Alt 一起打
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
$根 = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $根 'MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
  [void][V35]::ShowWindow($_.MainWindowHandle, 9)
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

if (-not [V35]::Take($h)) { throw '拽不到前台' }
# 摆在**工作区里**（26 号量出来的坑：这台机器任务栏从 y=1824 起）
[void][V35]::SetWindowPos($h, [IntPtr]::Zero, 405, 300, 2360, 1300, 0x0004 -bor 0x0010)
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
function 按钮亮 { (按编号 'PlayButton')[0].Current.IsEnabled }
function 焦点是谁 {
  $f = [System.Windows.Automation.AutomationElement]::FocusedElement
  if (-not $f) { return '（没有焦点元素）' }
  "$($f.Current.ControlType.ProgrammaticName) id='$($f.Current.AutomationId)' 名='$($f.Current.Name)'"
}

# 「位置」那一格：先找「位置」标签，再取它右边紧挨着那格值（照抄 verify-20/33/34，不写死 Y）
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

# **前台只在真掉了时才拽**：`Take` 里的 `SetFocus(h)` 会把焦点打回最后一个输入框（34 号量的）
function 确保前台([string]$谁) {
  if ([V35]::GetForegroundWindow() -eq $h) { return $true }
  "  ！！ 「$谁」之前前台掉了（台面上是 $([V35]::Describe([V35]::GetForegroundWindow()))）—— 拽回来；"
  "     这一下 `Take` 里的 `SetFocus(h)` 会把焦点打回输入框，本节的焦点读数作废。"
  return [V35]::Take($h)
}

$小节框 = (按编号 'JumpBox')[0]
$速度框 = (按编号 'BpmBox')[0]
if (-not $小节框) { throw '找不到 JumpBox' }

function 回车提交($框, [string]$值) {
  [void](确保前台 '回车提交')
  [void]$框.SetFocus(); Start-Sleep -Milliseconds 300
  $框.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($值)
  Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
  Start-Sleep -Milliseconds 700
}
function 跳小节([int]$n) { 回车提交 $小节框 ([string]$n) }

# **Shift + 空格** —— 这一票的题面。SendKeys 里 `+` 是 Shift 前缀，`+ ` = Shift+空格。
# 只等 180 毫秒就读：BPM 120 下那大约是 0.18 小节，读数还停在退到的那一小节上
#（settle 拉长就读成下一小节了 —— 33 号那次栽在尺子上就是这个形状）。
function 按Shift空格([string]$在哪) {
  [void](确保前台 "在「$在哪」按 Shift+空格")
  "  （按之前：焦点 = $(焦点是谁)；位置 = $(位置原文)；按钮 = $(按钮字)）"
  [System.Windows.Forms.SendKeys]::SendWait('+ ')
  Start-Sleep -Milliseconds 180
}
function 按空格([string]$在哪) {
  [void](确保前台 "在「$在哪」按空格")
  "  （按之前：焦点 = $(焦点是谁)；位置 = $(位置原文)；按钮 = $(按钮字)）"
  [System.Windows.Forms.SendKeys]::SendWait(' ')
  Start-Sleep -Milliseconds 180
}
function 按播放键 {
  [void](确保前台 '按播放键')
  (按编号 'PlayButton')[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 700
}
# 让曲子停下来、按钮回到「▶ 播放」：唯一的路是让它自己放到曲尾
function 停下 {
  if ((按钮字) -eq '▶ 继续') { 按播放键 }
  跳小节 124
  $等 = 0
  while ((按钮字) -ne '▶ 播放' -and $等 -lt 60) { Start-Sleep -Milliseconds 500; $等++ }
}

# =====================================================================
"`n=== 0. 没装曲子的时候：Shift+空格 什么都不该做 ==="
# =====================================================================
# Shift+空格 是**按键**，绕得过按钮的灰（↻ 那条路绕不过，它只能点）。
# 所以「一个音轨都没有时不响」这道闸门是这一票**新写的**，不是顺带有的。
断言 '干净实例里播放键是灰的' (按钮亮) 'False'
按Shift空格 '没装曲子'
断言 '没装曲子时按 Shift+空格：按钮还是' (按钮字) '▶ 播放'
断言真 '没装曲子时按 Shift+空格：位置读不到数（没有曲子）' ((位置小节) -lt 1) "位置 = $(位置原文)"

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
$轨数 = 数轨
"载入「$曲名」：$轨数 条轨"
# 速度 120：一把 1 小节/秒的尺子（33 号量的刻度）
回车提交 $速度框 '120'

# =====================================================================
"`n=== 1. 正在播的时候 Shift+空格 = 退回一小节并继续放 ==="
# =====================================================================
跳小节 20
断言 '先跳到第 20 小节' (位置小节) 20
按播放键
断言 '开始放了' (按钮字) '⏸ 暂停'
Start-Sleep -Seconds 2
$退前 = 位置小节
"  退之前位置 = $($退前)（已经往前走了两秒）"
按Shift空格 '正在播'
$退后 = 位置小节
"  退回之后 180 毫秒：位置 = $(位置原文)；按钮 = $(按钮字)"
断言 'Shift+空格 之后按钮仍是' (按钮字) '⏸ 暂停'
# **「上一小节」是相对「按下去那一刻的播放头」，不是相对「上一次跳小节跳到的那一小节」。**
# 第一版这儿写死了 19（= 跳过去的 20 减一），红了 —— 因为中间那 2 秒播放头已经走到 22，
# 而它退的是 22 那一格（读到 21）。那是**对的**（用户要的就是「从我现在这儿往回退一格」），
# 红的是这句预期。所以判据写成 `$退前 - 1`：退的永远是**读数**的前一格。
断言 'Shift+空格 退回到按下去那一刻的 −1 小节' $退后 ($退前 - 1)
断言真 '确实是退了（不是原地不动，也不是退多了）' ($退后 -lt $退前) "退之前 $退前 → 退之后 $退后"
Start-Sleep -Seconds 3
$退后走 = 位置小节
"  又等了 3 秒：位置 = $(位置原文)"
断言真 '退回去之后接着在放（不是停在那）' ($退后走 -gt $退后) "从第 $退后 小节走到第 $退后走 小节"

# =====================================================================
"`n=== 2. 对照：空格**本身**还是播放 / 暂停（用户原话）==="
# =====================================================================
# 新加的那一支要是排在空格那一支**后面**，症状是「Shift+空格 没反应」；
# 排在**前面**却忘了认 shift，症状是「空格变成回跳」—— 后者更坏（播放/暂停就此消失）。
# 所以这一节用**普通空格**量，而且量两下：暂停、再继续。
$暂停前 = 位置小节
按空格 '正在播'
断言 '普通空格仍然是暂停' (按钮字) '▶ 继续'
Start-Sleep -Seconds 2
$冻住 = 位置小节
Start-Sleep -Seconds 2
断言 '暂停期间播放头冻住了（两次读数一样）' (位置小节) $冻住
断言真 '普通空格**没有**回跳一小节（位置在暂停点附近，不是 18 上下）' ($冻住 -ge ($暂停前 - 1)) `
  "暂停前 $暂停前 → 冻在 $冻住"
按空格 '暂停中'
断言 '再按普通空格 = 继续' (按钮字) '⏸ 暂停'
Start-Sleep -Seconds 2
断言真 '继续之后在往前走' ((位置小节) -gt $冻住) "从 $冻住 走到 $(位置小节)"

# =====================================================================
"`n=== 3. 暂停中按 Shift+空格 = 退回一小节**并开始放**（不是停在那）==="
# =====================================================================
停下
跳小节 40
按播放键
Start-Sleep -Seconds 2
按空格 '先暂停在 42 上下'
断言 '先停在「继续」上' (按钮字) '▶ 继续'
$停在哪 = 位置小节
"  暂停在第 $停在哪 小节"
按Shift空格 '暂停中'
断言 '暂停中按 Shift+空格：按钮变成' (按钮字) '⏸ 暂停'
断言 '暂停中按 Shift+空格：退回到 −1 小节' (位置小节) ($停在哪 - 1)
Start-Sleep -Seconds 3
$走后 = 位置小节
"  又等了 3 秒：位置 = $(位置原文)"
断言真 '暂停点起步的那一路也真的在放' ($走后 -gt ($停在哪 - 1)) "从第 $($停在哪 - 1) 小节走到第 $走后 小节"

# =====================================================================
"`n=== 4. 边界：第 1 小节按 = 回第 1 小节（夹住，不是 0）==="
# =====================================================================
# 夹法照抄 ↻ / 「跳到 __ 小节」框：TickOfBarClamped 自己把 −1 夹成 0。
# 这一条防的是「第 1 小节按一下，读数变成 0 或整条读数没了」。
停下
跳小节 1
断言 '先跳到第 1 小节' (位置小节) 1
按Shift空格 '第 1 小节'
"  第 1 小节按 Shift+空格：位置 = $(位置原文)；按钮 = $(按钮字)"
断言 '第 1 小节按 Shift+空格：位置夹在' (位置小节) 1
断言 '第 1 小节按 Shift+空格：按钮变成' (按钮字) '⏸ 暂停'
Start-Sleep -Seconds 2
断言真 '夹在开头之后照样在放' ((位置小节) -gt 1) "位置 = $(位置小节)"

# =====================================================================
"`n=== 5. 放完之后按 Shift+空格 = 退回一小节并放 ==="
# =====================================================================
# 「放完自动停」停在曲尾（124），这一下应该是回 123 而不是回 1 —— 那是 ↻ 的活。
停下
断言 '停下之后按钮回到' (按钮字) '▶ 播放'
断言 '停下之后位置停在曲尾' (位置小节) 124
按Shift空格 '放完之后'
断言 '放完之后按 Shift+空格：按钮变成' (按钮字) '⏸ 暂停'
断言 '放完之后按 Shift+空格：退到倒数第二小节' (位置小节) 123
Start-Sleep -Seconds 2
断言真 '从 123 接着往后放' ((位置小节) -gt 123) "位置 = $(位置小节)"

# =====================================================================
"`n=== 6. 反证：焦点在输入框里时，Shift+空格 只是打个空格 ==="
# =====================================================================
# 空格那一支整块让开输入框（`OnWindowKeyDown` 开头那句），Shift+空格 走同一条路 ——
# 所以这一条是**同一件事的另一面**，不是新规矩。它值得量，因为症状和用户报的那个一模一样
#（「按了键，框里多一个看不见的字符，曲子一动不动」）。
停下
跳小节 60
[void](确保前台 '反证')
[void]$小节框.SetFocus(); Start-Sleep -Milliseconds 400
$框前 = 取值 $小节框
$位前 = 位置小节
"  （主动把焦点放进框里）焦点 = $(焦点是谁)；框里「$框前」；位置 = $位前"
[System.Windows.Forms.SendKeys]::SendWait('+ '); Start-Sleep -Milliseconds 700
断言真 '焦点在框里时，Shift+空格 进了框（多出一个字符）' ((取值 $小节框).Length -gt $框前.Length) `
  "「$框前」→「$(取值 $小节框)」"
断言 '焦点在框里时，Shift+空格 没动播放头' (位置小节) $位前
断言 '焦点在框里时，Shift+空格 没起播' (按钮字) '▶ 播放'
# 收尾：把框恢复成正常值（不然留给下一个人一个「60 」）
回车提交 $小节框 '60'
断言 '收尾：框恢复成' (取值 $小节框) '60'

# =====================================================================
"`n=== 7. 焦点停在按钮上时，Shift+空格 不会顺手把那颗按钮也按了 ==="
# =====================================================================
# 35 号把「把焦点从按钮上收回来」那一下提成了 `ReleaseControlFocus()`（空格和 Shift+空格 共用）。
# 不提的话，新那一支就要么漏了它、要么抄一份 —— 症状都是「一个键干了两件事」。
# 拿轨头那颗「折叠」量：它一被按，那条轨就收起来（「折叠」按钮的个数会变）。
停下
跳小节 30
$轨头 = @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' })
if ($轨头.Count -lt 1) { throw '找不到「折叠」按钮 —— 这一节没法量' }
$轨前 = 数轨
$位前7 = 位置小节
[void](确保前台 '焦点在按钮上')
[void]$轨头[0].SetFocus(); Start-Sleep -Milliseconds 400
"  （主动把焦点放到轨头那颗「折叠」上）焦点 = $(焦点是谁)；折叠按钮 $轨前 个；位置 = $位前7"
[System.Windows.Forms.SendKeys]::SendWait('+ '); Start-Sleep -Milliseconds 800
$轨后 = 数轨
"  按完 Shift+空格：折叠按钮 $轨后 个；位置 = $(位置原文)；按钮 = $(按钮字)"
断言 '焦点在按钮上时按 Shift+空格：轨数没变（那颗按钮没被顺手按下）' $轨后 $轨前
断言 '焦点在按钮上时按 Shift+空格：播放头退了一小节' (位置小节) ($位前7 - 1)
断言 '焦点在按钮上时按 Shift+空格：在放' (按钮字) '⏸ 暂停'

"`n实例已收（PID $($proc.Id)）"
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill() }

"`n========== 结果 =========="
if ($fail -eq 0) { '全过' } else { "$fail 条不过" }
exit $fail
