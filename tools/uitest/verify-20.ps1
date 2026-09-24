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
#   · **先把速度设成 120**（曲子的原速是 50 —— 一小节 4.8 秒，等一次要十几秒，
#     而且第 6 节要「放完」，124 小节按原速得十分钟）。速度是这张工单管不着的东西。
#     为什么不是 240：33 号那次量出来 240 下约 2 小节/秒，读「播放头回到第 1 小节」时
#     尺子的分辨力已经不够（读到的永远是 2）—— 太快和太慢一样量不准，120 才是这把尺子的刻度。
#   · **第 6 节先跳到第 120 小节**再放：从那儿到曲尾只剩几小节，几秒就放完了。
#
# **33 号工单改过这一支**（`■ 停止` → `↻ 重头播放`）：走带条右边那颗按钮不再是「停下」，
# 所以第 0 / 2 节那两条「■ 始终可用」和第 4 节整节都按**新的事实**改写过了 ——
# 第 4 节现在是「暂停期间按 ↻ = 回开头 + 立刻重放」，它跟 20 号那句「不重置播放头」
# 正好相反（那是「停下」的性质，不是「重播」的）。20 号的正题（空格三态、播放头冻不冻）
# 一个字都没改。
#
# **一处量法上的坑，是 33 号那次跑出来的**（细节写在 `松焦点` / `按空格` 的注释里）：
# 这张工单的正题是空格，而空格是**窗口级**的键 —— 它归 Avalonia 当前聚焦的那个元素，
# 落在输入框里就变成框里一个字符、落在按钮上就是「按那颗按钮」。两种都长得像
# 「空格播放坏了」（红得看不出破绽）。所以每次发空格之前都要把焦点挪到安全落点，
# 而这一步本身踩过两次雷：
#   · `Take`（拽前台）里的 `SetFocus(h)` 会把焦点**打回最后一次聚焦的输入框** ——
#     于是 `按空格` 不能再无条件拽前台（app 一直是前台，掉了才拽）；
#   · UIA 的 `FocusedElement` **会过期** —— 读到 `MenuItem 文件` 时真实焦点还在
#     `RestartButton` 上，所以「先读一眼再决定挪不挪」是错的，得**先无条件 Tab 两下**。
# 落点安不安全是**量出来的**：`.scratch/probe-20-space.ps1` 里焦点停在菜单条上时，
# 那发空格照样冒泡到窗口（按钮从「⏸ 暂停」变成「▶ 继续」）—— 这也正是 app 自己的假定
#（走带那两颗按钮 `IsTabStop="False"`，见 `MainWindow.axaml` 里那段注释）。
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
# 33 号之后右边那颗是 `↻ 重头播放`（`RestartButton`）。**这颗脚本不量 `StopButton` 在不在**
# —— 「■ 停止 真的拆了」是 33 号自己的判据（`verify-33.ps1` §1：找 `StopButton` 得 0 个）。
# 这儿只要 `RestartButton` 找得到就行，下面那个 foreach 会当场 throw。
$重播键 = 按编号 'RestartButton'
$速度框 = 输入框 'BpmBox'
$小节框 = 输入框 'JumpBox'
foreach ($对 in @(@('PlayButton',$播放键), @('RestartButton',$重播键), @('BpmBox',$速度框), @('JumpBox',$小节框))) {
  if (-not $对[1]) { throw "找不到控件 $($对[0])" }
}

function 按钮字 { $播放键.Current.Name }
function 重播可用 { $重播键.Current.IsEnabled }
function 重播字 { $重播键.Current.Name }
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

# 焦点现在在谁身上。**空格是窗口级的键，落在谁身上就归谁**：落在输入框里就变成一个空格字符、
# 落在按钮上就是「按那颗按钮」、落在曲库上可能什么也不发生。这三种都长得像「空格播放坏了」。
# 所以每次发空格之前先把落点报出来 —— 红了的时候一眼就能分清是「键没到」还是「路坏了」。
function 焦点是谁 {
  $f = [System.Windows.Automation.AutomationElement]::FocusedElement
  if (-not $f) { return '（没有焦点元素）' }
  "$($f.Current.ControlType.ProgrammaticName) id='$($f.Current.AutomationId)' 名='$($f.Current.Name)'"
}
function 按空格 {
  # **不无条件拽前台**：`前台`（`Take` 里的 `SetFocus(h)`）会把 Avalonia 的焦点
  # **还回最后一次聚焦的输入框** —— 诊断实测：松焦点 刚把焦点挪到菜单项上，
  # `前台` 一跑又读成 `Edit id='JumpBox'`，于是那一发空格被打进框里、
  # 按钮纹丝不动（第 1 节第一次跑就是这么红的）。app 一直是前台（脚本每一步都盯着它），
  # 所以这儿只在真掉了前台时才拽 —— 那时也只能接受焦点被打回输入框，所以拽完再松一次。
  if ([V20]::GetForegroundWindow() -ne $h) {
    "  （空格前掉了前台 —— 拽回来，这一下会把焦点打回输入框）"
    前台
  }
  松焦点
  "  （空格发给：$(焦点是谁)）"
  [System.Windows.Forms.SendKeys]::SendWait(' '); Start-Sleep -Milliseconds 500
}
function 回车 { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Milliseconds 600 }

# 把焦点挪到一个**空格到得了窗口**的落点上。
#
# 空格是**窗口级**的键（见 MainWindow.OnWindowKeyDown）：它从 Avalonia 当前聚焦的元素
# 开始冒泡，落点若是不该收它的地方就永远到不了窗口 —— 而「到不了」和「这条路坏了」
# 长得一模一样：按钮没变、位置没动。所以两处不安全落点都要绕开：
#   · `Edit` —— 空格变成框里一个字符。这正是第 6 节**要验**的行为，所以那一节特意
#     把焦点放进框里、而且不走这个函数。
#   · `Button` —— 空格就是「按那颗按钮」。按 ↻ 走 `InvokePattern` 之后焦点**真的会**
#     落在它身上（诊断实测：`Button id='RestartButton'`），下一发空格于是变成
#     「再按一次 ↻」—— 看着就像「空格暂停坏了」（第 4 节收尾第一次跑就是这么红的）。
#
# 什么算**安全**落点是量出来的，不是想出来的：`.scratch/probe-20-space.ps1` 里
# 焦点停在菜单条（`MenuItem 名='文件'`）上时，那发空格照样一路冒泡到窗口
#（按钮从「⏸ 暂停」变成「▶ 继续」）。菜单条是 Tab 序的入口，而走带那两颗按钮是
# `IsTabStop="False"`（`MainWindow.axaml` 里那段注释写着为什么）—— 也就是说
# **app 自己就假定「焦点停在走带条以外的地方、空格归窗口」**，Tab 到菜单条正合这个假定。
#
# 用 Tab 而不是鼠标点一下：点哪儿都可能顺带改点什么（点卷帘会改选中），而 Tab 只动焦点。
function 松焦点 {
  前台
  # **先无条件 Tab 两下** —— 这一条是被一次假绿骗出来的：
  # `松焦点` 原本是「读一眼焦点，不在输入框里就收工」，而那一眼**读到的可能是过期的**。
  # 第 4 节收尾那一次读出来是 `MenuItem 名='文件'`，于是它一 Tab 没按就收工了；
  # 可真实焦点还在 `RestartButton` 上（前一句 ↻ 的 `InvokePattern` 把它搁在那儿的），
  # 那一发空格就变成了「又按了一次 ↻」—— 按钮还是「⏸ 暂停」，断言红得莫名其妙。
  # 读数不能当依据，所以先挪两下再说：Tab 只动焦点，多按几下无害（落点全在走带条以外）。
  for ($i = 0; $i -lt 2; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}'); Start-Sleep -Milliseconds 200
  }
  for ($i = 0; $i -lt 16; $i++) {
    $f = [System.Windows.Automation.AutomationElement]::FocusedElement
    $t = if ($f) { $f.Current.ControlType } else { $null }
    if (-not $f -or ($t -ne $CT::Edit -and $t -ne $CT::Button)) {
      "  （又 Tab $i 下之后焦点落在：$(焦点是谁)）"
      return
    }
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}'); Start-Sleep -Milliseconds 250
  }
  "  （警告：Tab 了 16 下焦点还在输入框 / 按钮上）"
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

# ↻ 那颗按钮**真按下去**：UIA 的 InvokePattern —— 和鼠标点等价，但不依赖坐标、不依赖前台，
# 也就不会像合成鼠标那样点到别人窗口上去（32 号那个教训）。
#
# **只等 180 毫秒**：按完它就立刻开始放了，「播放头回到第 1 小节」这一条只有在这个窗口里才读得到。
# 从前这儿等 800 毫秒、速度又是 240（约 2 小节/秒）—— 读到的永远是 2 而不是 1，
# 于是三条断言一起红。那不是 app 错了，是**尺子太快**（33 号第一次跑就是这么红的）。
# 速度降到 120（约 1 小节/秒）之后 180 毫秒大约走 0.2 小节，这个窗口才量得准。
#
# 按完**把焦点报出来**：Invoke 若把 Avalonia 的焦点挪到这颗按钮身上，
# 后面那一发空格就变成「再按一次 ↻」—— 长得和「空格暂停坏了」一模一样。
function 按重播 {
  前台
  "  （按 ↻ 之前焦点：$(焦点是谁)）"
  $重播键.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 180
  "  （按 ↻ 之后焦点：$(焦点是谁)）"
}

# ---------- 0. 前置 ----------
"=== 0. 前置 ==="
填框 $速度框 '120'          # 原速 50 太慢，240 又太快（见文件头）
断言 '开始时按钮是' (按钮字) '▶ 播放'
# 33 号把判据换成了和播放键**同一条**（`_song is { Tracks.Count: > 0 }`）：
# 重头播放的意义就是「放」，一条轨都没有时亮着等于承诺一件做不到的事。
# （从前这儿验的是「■ 始终可用」，那是「停下」的判据 —— 语义换了，判据跟着换。）
断言真 '开始时 ↻ 可用' (重播可用) '有轨才放得响（33 号：判据跟播放键一致）'
断言 '↻ 那颗按钮上写着' (重播字) '↻ 重头播放'
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
断言真 '暂停期间 ↻ 可用' (重播可用) '有轨就亮（33 号：判据跟播放键一致）'
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

# ---------- 4. 暂停期间按 ↻ 重头播放 = 回开头 + 立刻重放 ----------
# 33 号之前这一节验的是「暂停期间按 ■ 停止 → 按钮回到 ▶ 播放、播放头**不**重置」。
# 那颗按钮换成语义相反的 ↻ 之后，这一节整节跟着反过来 —— 但**判据一点没松**：
# 播放头要真的回到第 1 小节，而且按钮要真的回到「正在播」那一态（不是「▶ 播放」、
# 也不是「▶ 继续」）。少了这两条，一个「什么都没做的按钮」也能过。
"`n=== 4. 暂停期间按 ↻ 重头播放 ==="
按空格
断言 '先停在「继续」上' (按钮字) '▶ 继续'
$停前 = 位置小节
断言真 '暂停点不在开头（不然下一句看不出「回到开头」）' ($停前 -gt 1) "暂停在第 $停前 小节"
按重播
断言 '按 ↻ 之后按钮变成' (按钮字) '⏸ 暂停'
断言 '按 ↻ 之后播放头回到开头' (位置小节) 1
Start-Sleep -Seconds 3
$重播后 = 位置小节
"  按 ↻ 之后等了 3 秒，位置 = $(位置原文)"
断言真 '确实是**在往前放**（不是停在开头）' ($重播后 -gt 1) "从第 1 小节走到第 $重播后 小节"
断言真 '是从头重放，不是从暂停点接着放' ($重播后 -lt $停前) "暂停在第 $停前 小节，现在才第 $重播后 小节"
# 收尾：停下这一遍（按空格回「▶ 继续」），后面几节从干净的状态起
按空格
断言 '收尾：再按空格回到' (按钮字) '▶ 继续'

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

# 收尾。33 号之后**没有「■ 停止」了** —— 唯一能让它自己停下来的路就是「放完」，
# 所以收尾改成「按 ↻ 重头播放 → 跳到 120 小节 → 等它走到尾巴」。
# 这一下顺带又验了一次「正在播的时候按 ↻ = 从头重放」：位置先回 1、再被跳到 120。
按重播
断言 '收尾：按 ↻ 之后还在播' (按钮字) '⏸ 暂停'
断言 '收尾：按 ↻ 之后播放头回开头' (位置小节) 1
跳小节 120
$等2 = 0
while ((按钮字) -ne '▶ 播放' -and $等2 -lt 40) { Start-Sleep -Milliseconds 500; $等2++ }
断言 '收尾：放完自己停下来' (按钮字) '▶ 播放'

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
# 81 号票：裁决行 —— run-all.ps1 拿它跟退出码复核，对不上就把这一条降级成红（别删）。
"==== uitest 裁决 不过=$fail"
exit $fail
