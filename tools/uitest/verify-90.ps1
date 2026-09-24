#Requires -Version 5.1
<#
  90 号工单的实机验证：**没载入任何曲子的时候，「打开日志文件夹」也点得动。**

  用法（**整段都在仓库锁里** —— 起 → 交互 → 关，收干净了再放锁）：
    pwsh -NoProfile -File 'C:\Users\cao17\.claude\jobs\10c1fd1e\tmp\repolock.ps1' `
         -Script '<repo>\tools\uitest\verify-90.ps1' -Tag '90-machine'

  病根（票面第一节）：「操作 ▾」那一组共用一道门
  `OperationMenu.IsEnabled = _song is not null;`，而「打开日志文件夹」跟谱面毫无关系
  —— 它是**诊断入口**，用户最想翻日志的时刻恰恰是「刚装好、还没载曲子就出事了」。

  本脚本量四件事（都是这台机器上真的看得见的东西）：
    ① 空态下那一项**够得着、能点**（拿 UIA 的 IsEnabled 读，不猜）
    ② 点下去**真的把日志目录开出来了**（三份独立证据，见下）
    ③ 载了曲子之后它**照样能点**（别为了修空态把有曲子那条路弄坏）
    ④ 「撤销 / 重做」**仍然跟着谱面灰**（没扩大范围）

  ②为什么三份证据一起看：单看哪一份都不够 ——
    · 「资源管理器窗口出现了」可能是系统把某个已有的窗口拽到前面来（Win11 有标签页）；
    · 「日志文件里多了一行」能证明处理函数跑了，但证明不了目录真被打开；
    · 「目录在盘上」只证明目录存在（61 号的 EnsureExists 本来就会建它）。
    三份一起看才是「点了它，处理函数跑了，资源管理器真的开在那个目录上」。
    ⚠️ 顺带一条：处理函数自己会往日志里写「打开了日志文件夹 {目录}」，
       所以这一行的**增量**是「处理函数真的跑了」的**直接**证据，不是旁证。
    ⚠️ 找那扇窗口**不能拿标题逐字比 'logs'**（本机实测标题是「logs - 文件资源管理器」，
       逐字比一条都匹配不上 ⇒ 窗口开着、读数却是 0）。走 [W90]::FindFolder，按文件夹名比。

  空态 / 有曲子怎么认（不靠曲名那一格，那一格在 UIA 里未必看得见）：
    「演奏」那颗按钮（PerformerButton）能不能点 —— 它的判据就是 `_song is not null`，
    verify-48 也拿它当空态判据。没曲子=灰，装了曲子=亮。

  🔴 本脚本**不判**「那一项长在窗口树的哪个位置」那类结构问题 —— 那是 NUnit 那边的。
    这儿只看**点得动点不动**。

  退出码（跟这套脚本的约定一致）：0 全过 / 1 有断言没过 / 2 环境没到位 / 3 前提不满足。
#>

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

# ★ 这几个名字要在**脚本作用域**里先存在（库会往调用者作用域里漏一条 StrictMode，
#   读未绑定变量会抛，抛在收尾里就是「起了实例、没收掉」—— 见 verify-48 抬头那段）。
$proc = $null
$script:proc = $null
$script:pid脚本 = 0
$script:h = [IntPtr]::Zero
$root = $null
$失败 = 0
$script:原有的日志窗 = @()

function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { Write-Host "  [通过] $名字" -ForegroundColor Green }
  else { Write-Host "  [不过] $名字`n          实际：$原文" -ForegroundColor Red; $script:失败++ }
}

try { . (Join-Path $PSScriptRoot 'uitest-lib.ps1') }
catch { Write-Host "前提不满足，停手：$($_.Exception.Message)" -ForegroundColor Red; exit 3 }

# =====================================================================
# 「资源管理器开出来没有」要数**本进程之外**的顶层窗，共用库的 P40::Others 只数本进程的，
# 所以这里自带一小段（uitest 这套 harness 整活就是驱动窗口，89 号那条安全边界只管产品源码）。
# =====================================================================
Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class W90 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string Txt(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
  /// 按**窗口类**找顶层窗（可选：标题逐字相等、必须可见）。资源管理器那个窗口的类是 CabinetWClass。
  public static List<IntPtr> Find(string cls, string title) {
    var 得 = new List<IntPtr>();
    EnumWindows((h, l) => {
      if (Cls(h) != cls) return true;
      if (title != null && Txt(h) != title) return true;
      if (!IsWindowVisible(h)) return true;
      得.Add(h); return true;
    }, IntPtr.Zero);
    return 得;
  }
  /// 按**文件夹名**找资源管理器窗口。**标题不能逐字比**：90 号在本机（中文 Win11 26200）
  /// 量到的标题是「logs - 文件资源管理器」，逐字跟 'logs' 比一条都匹配不上 ——
  /// 窗口明明开出来了，读数却是 0 个（第一节那条红就是这么来的）。
  /// 英文机上是「logs」，「显示完整路径」打开时又是全路径，所以按**段**比：
  /// 标题本身就是 <name>、或第一段（" - " 前）是 <name>、或标题末尾是 \<name>。
  public static List<IntPtr> FindFolder(string cls, string name) {
    var 得 = new List<IntPtr>();
    EnumWindows((h, l) => {
      if (Cls(h) != cls) return true;
      if (!IsWindowVisible(h)) return true;
      var t = Txt(h);
      var 第一段 = t.Split(new[]{" - "}, 2, StringSplitOptions.None)[0];
      bool 是 = 第一段.Equals(name, StringComparison.OrdinalIgnoreCase)
             || t.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase);
      if (!是) return true;
      得.Add(h); return true;
    }, IntPtr.Zero);
    return 得;
  }
  /// 关一个窗口（WM_CLOSE）。只用来收**本脚本自己点出来的**那些资源管理器窗口。
  public static void Close(IntPtr h) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
  public static bool Has(List<IntPtr> 少, IntPtr h) {
    foreach (var x in 少) { if (x == h) return true; }
    return false;
  }
}
'@

# =====================================================================
# UIA 取数的小工具（照 verify-48 那一套）
# =====================================================================
function 找类型($根, $类型) { if ($null -eq $根) { return @() } @($根.FindAll($TS::Descendants, (& $条件 $类型))) }
function 按id($根, [string]$id) {
  foreach ($t in @($CT::Text, $CT::Button, $CT::Edit, $CT::List, $CT::ListItem, $CT::Pane, $CT::Custom, $CT::MenuItem)) {
    $h2 = @(找类型 $根 $t | Where-Object { $_.Current.AutomationId -eq $id })
    if ($h2.Count) { return $h2[0] }
  }
  return $null
}
function 取根($句柄) {
  $最后 = $null
  for ($i = 0; $i -lt 6; $i++) {
    try { return $AE::FromHandle($句柄) }
    catch {
      $最后 = $_
      if ($script:proc -and $script:proc.HasExited) { throw "主窗所在的进程已经退出（退出码 $($script:proc.ExitCode)）—— 这不是 UIA 的锅" }
      Start-Sleep -Milliseconds 400
    }
  }
  throw "FromHandle 连试 6 次都没成：$最后"
}
# 工具栏那一排里那个「操作」头：MenuItem 且 Name 逐字是「操作」。
function 操作头($根) {
  foreach ($e in @(找类型 $根 $CT::MenuItem)) {
    if ($e.Current.Name -eq '操作') { return $e }
  }
  return $null
}
# 点一下「操作」头，把弹出层里那几条收回来（每 100 毫秒看一眼，理由见 verify-23 的 `展开`：
# 这台机器上有个窗口会在一秒多以后抢前台，Avalonia 一失活就把弹出层收掉）。
function 展开操作([IntPtr]$窗, $头) {
  if ($null -eq $头) { return @() }
  $r = $头.Current.BoundingRectangle
  if ([double]::IsNaN($r.X) -or $r.Width -le 0) { return @() }
  [void][P40]::Take($窗)
  Start-Sleep -Milliseconds 250
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  if ([P40]::PidAt($cx, $cy) -ne $script:pid脚本) { return @() }
  [P40]::ClickAt($cx, $cy)
  $项 = @()
  foreach ($等 in 1..8) {
    Start-Sleep -Milliseconds 100
    $项 = @(找类型 $头 $CT::MenuItem)
    if ($项.Count) { break }
  }
  return $项
}
function 收弹出层 { [P40]::Key(0x1B); Start-Sleep -Milliseconds 300 }
# 「演奏」那颗按钮：它跟「打开日志文件夹」无关，判据就是 `_song is not null`
# ⇒ 拿它认「空态 / 装了曲子」，比去读曲名那一格可靠（那一格没曲子时整个 IsVisible=False）。
function 空态吗($根) {
  $演 = 按id $根 'PerformerButton'
  if ($null -eq $演) { return $null }
  return (-not $演.Current.IsEnabled)
}

$日志目录 = Join-Path $env:LOCALAPPDATA 'MidiPerformer\logs'
function 最新日志 {
  if (-not (Test-Path $日志目录)) { return $null }
  $f = @(Get-ChildItem -LiteralPath $日志目录 -Filter 'MidiPerformer-*.log' -File -EA SilentlyContinue |
         Sort-Object LastWriteTimeUtc)
  if (-not $f.Count) { return $null }
  return $f[-1].FullName
}
# 处理函数跑了没有：日志里「打开了日志文件夹」那一行的**条数**。
function 打开日志条数([string]$文件) {
  if (-not $文件 -or -not (Test-Path $文件)) { return -1 }
  return @(Select-String -LiteralPath $文件 -Pattern '打开了日志文件夹' -SimpleMatch -EA SilentlyContinue).Count
}
function 日志窗 { @([W90]::FindFolder('CabinetWClass', 'logs')) }

# 那一项够不够得着。甲案之后它是工具栏上的一颗按钮（x:Name → AutomationId）；
# 还留在「操作 ▾」里时只能靠展开弹出层去读 —— 两条路都试，**先报它走的是哪条路**。
$script:弹出层开着 = $false
function 找日志项($根, [IntPtr]$窗, [ref]$怎么找到的) {
  $钮 = 按id $根 'OpenLogFolderButton'
  if ($null -ne $钮) { $怎么找到的.Value = '工具栏上的按钮（AutomationId=OpenLogFolderButton）'; return $钮 }
  $旧 = 按id $根 'OpenLogFolderMenuItem'
  if ($null -ne $旧) { $怎么找到的.Value = '窗口树里那个菜单项（AutomationId=OpenLogFolderMenuItem）'; return $旧 }
  $头 = 操作头 $根
  if ($null -eq $头) { $怎么找到的.Value = '连「操作」那个头都没找到'; return $null }
  $项 = @(展开操作 $窗 $头)
  $中 = @($项 | Where-Object { $_.Current.Name -eq '打开日志文件夹' })
  if ($中.Count) {
    # ⚠️ **别在这里收弹出层**：调用方马上要拿这一条去点，收了就点不着了。
    $script:弹出层开着 = $true
    $怎么找到的.Value = "展开「操作 ▾」之后在弹出层里（弹出层读出来 $($项.Count) 条）"
    return $中[0]
  }
  收弹出层
  # 弹出层一条都没读出来时要说清是哪一种 0 条：**整组被灰掉导致弹不开**，还是
  # 「组弹开了、里面就是没有这一项」。这两件事在报告里意思完全不同。
  if ($项.Count -eq 0 -and -not $头.Current.IsEnabled) {
    $怎么找到的.Value = '「操作 ▾」整组打不开（那个头本身是灰的 ⇒ 弹出层 0 条）—— 那一项被关在灰掉的组里'
  }
  else {
    $怎么找到的.Value = "「操作 ▾」里没有这一项（弹出层读出来 $($项.Count) 条）"
  }
  return $null
}
function 点日志项($e, [IntPtr]$窗, [string]$怎么找到的) {
  if ($script:弹出层开着) {
    # 弹出层里那些条目**有 Invoke**（76 号实测：顶级菜单头没有，叶子有）—— 走 Invoke 就绕开了
    # 鼠标那一整套脆弱环节。但**不是每个叶子都支持**（90 号实测这一条就不支持），
    # 所以留一条兜底：就地按鼠标，而且**不 Take** —— 弹出层一失活就会被 Avalonia 收掉。
    try {
      $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      Write-Host '    [点] 走的是 Invoke（弹出层里的条目）'
      $script:弹出层开着 = $false
      Start-Sleep -Milliseconds 600
      return
    }
    catch {
      $r = $e.Current.BoundingRectangle
      if ([double]::IsNaN($r.X) -or $r.Width -le 0) { throw "「打开日志文件夹」在屏幕上没有位置（矩形 $r）" }
      [P40]::ClickAt([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
      Write-Host "    [点] 走的是真鼠标（弹出层里的条目：Invoke 不支持 —— $($_.Exception.Message)）"
      $script:弹出层开着 = $false
      Start-Sleep -Milliseconds 600
      return
    }
  }
  $r = $e.Current.BoundingRectangle
  if ([double]::IsNaN($r.X) -or $r.Width -le 0) { throw "「打开日志文件夹」在屏幕上没有位置（矩形 $r）" }
  [void][P40]::Take($窗)
  Start-Sleep -Milliseconds 250
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  $点上是 = [P40]::PidAt($cx, $cy)
  if ($点上是 -ne $script:pid脚本) { throw "点「打开日志文件夹」之前 $cx,$cy 上压着的不是本进程（是 PID $点上是）" }
  [P40]::ClickAt($cx, $cy)
  Write-Host '    [点] 走的是真鼠标（工具栏上的那颗按钮）'
}
# 点下去之后：等日志那行增量 + 等资源管理器那个窗口
function 等等看([string]$日志文件, [int]$条数前, [int]$窗数前, [int]$秒 = 12) {
  $期限 = (Get-Date).AddSeconds($秒)
  while ($true) {
    $条数后 = 打开日志条数 $日志文件
    $窗数后 = @(日志窗).Count
    if ($条数后 -gt $条数前 -and $窗数后 -gt $窗数前) { return [pscustomobject]@{ 条数 = $条数后; 窗数 = $窗数后 } }
    if ((Get-Date) -ge $期限) { return [pscustomobject]@{ 条数 = $条数后; 窗数 = $窗数后 } }
    Start-Sleep -Milliseconds 400
  }
}

$图目录 = Join-Path (Join-Path $PSScriptRoot 'shots') '90'
New-Item -ItemType Directory -Force -Path $图目录 | Out-Null

try {
  $script:原有的日志窗 = @(日志窗)

  # ---------- 0. 起一个干净实例：**这就是空态**（没载入任何曲子） ----------
  Write-Host "`n=== 0. 起实例（带清障：桌面上有别的实例就抛，不替你关）==="
  $h = 起窗口带清障
  $root = 取根 $h
  Write-Host "  窗口 $([P40]::Rect($h))"

  Write-Host "`n=== 1. 空态：这一项够不够得着、能不能点 ==="
  $空态 = 空态吗 $root
  断言真 '这个实例真的是空态（「演奏」是灰的 = 手上没有曲子）' ($空态 -eq $true) "演奏能点=$(if ($null -eq $空态) { '找不到 PerformerButton' } else { -not $空态 })"
  $头 = 操作头 $root
  断言真 '工具栏上找得到「操作」那个头' ($null -ne $头) '没找到 Name=操作 的 MenuItem'
  $空态头能点 = if ($头) { $头.Current.IsEnabled } else { $false }
  Write-Host "  [读数] 空态下「操作」那个头：能点=$空态头能点"
  # 弹出层能不能打开（空态下）—— 「操作」组跟着谱面灰的话，这一步会打不开
  $弹出条数 = @(展开操作 $h $头).Count
  Write-Host "  [读数] 空态下展开「操作 ▾」：读出来 $弹出条数 条"
  收弹出层

  $怎么找到的 = ''
  $空态项 = 找日志项 $root $h ([ref]$怎么找到的)
  Write-Host "  [读数] 空态下「打开日志文件夹」：$怎么找到的"
  if ($空态项) { Write-Host "  [读数] 空态下「打开日志文件夹」：能点=$($空态项.Current.IsEnabled)" }

  断言真 '空态下够得着「打开日志文件夹」' ($null -ne $空态项) $怎么找到的
  断言真 '空态下「打开日志文件夹」点得动（IsEnabled=True）' `
    ($null -ne $空态项 -and $空态项.Current.IsEnabled) `
    "找到=$(if ($空态项) { '是' } else { '否' })，能点=$(if ($空态项) { $空态项.Current.IsEnabled } else { '够不着' })"

  # ---------- 2. 点下去：真的开出那个目录 ----------
  Write-Host "`n=== 2. 空态下点它：日志那行 + 资源管理器窗口 + 目录在盘上 ==="
  $日志文件 = 最新日志
  $条数前 = 打开日志条数 $日志文件
  $窗数前 = @(日志窗).Count
  Write-Host "  点之前：日志文件='$(Split-Path $日志文件 -Leaf)'  「打开了日志文件夹」$条数前 条  资源管理器(logs) $窗数前 个"
  if ($null -ne $空态项 -and $空态项.Current.IsEnabled) {
    点日志项 $空态项 $h $怎么找到的
    $读后 = 等等看 $日志文件 $条数前 $窗数前
    Write-Host "  点之后： 「打开了日志文件夹」$($读后.条数) 条  资源管理器(logs) $($读后.窗数) 个"
    断言真 '处理函数真的跑了（日志里那一行多了一条）' ($读后.条数 -gt $条数前) "之前 $条数前 条，之后 $($读后.条数) 条"
    断言真 '资源管理器真的开在日志那个目录上（多出一个 CabinetWClass/logs 窗口）' `
      ($读后.窗数 -gt $窗数前) "之前 $窗数前 个，之后 $($读后.窗数) 个"
    断言真 "日志目录在盘上（$日志目录）" (Test-Path $日志目录) "Test-Path=$((Test-Path $日志目录))"
    $null = 存图 ([P40]::Shot($h)) ([P40]::LastW) ([P40]::LastH) (Join-Path $图目录 '空态点开日志.png')
    Write-Host "  截图：$(Join-Path $图目录 '空态点开日志.png')"
  }
  else {
    断言真 '空态下点它 → 处理函数跑了（日志多一行）' $false '那一项够不着 / 是灰的，**没点**（空态这条正是这一票的病根）'
    断言真 '空态下点它 → 资源管理器开了那个目录' $false '同上：没点成'
    断言真 "日志目录在盘上（$日志目录）" (Test-Path $日志目录) "Test-Path=$((Test-Path $日志目录))"
  }

  # ---------- 3. 「撤销 / 重做」仍然跟着谱面灰（没扩大范围） ----------
  Write-Host "`n=== 3. 空态：「撤销 / 重做」仍然是灰的，整组跟着谱面 ==="
  $空态条目 = @(展开操作 $h (操作头 $root))
  if ($空态条目.Count) {
    foreach ($n in '撤销', '重做') {
      $t = @($空态条目 | Where-Object { $_.Current.Name -eq $n })
      断言真 "空态下「$n」是灰的" ($t.Count -gt 0 -and -not $t[0].Current.IsEnabled) `
        "$(if ($t.Count) { "能点=$($t[0].Current.IsEnabled)" } else { '弹出层里没有这一条' })"
    }
    收弹出层
  }
  else {
    # 组整组灰的话弹出层打不开 —— 那本身就是「整组跟着谱面走」的读数
    Write-Host "  [读数] 空态下「操作 ▾」整组打不开（组跟着谱面灰）⇒ 里面那两条读不到，按「整组灰」记"
    断言真 '空态下「操作」整组是灰的（弹出层打不开 = 两条都在灰里）' (-not $空态头能点) "头能点=$空态头能点"
  }

  # ---------- 4. 载一首曲子：那一项照样能点 ----------
  Write-Host "`n=== 4. 从曲库载一首：那一项照样能点 ==="
  $曲库钮 = 按id $root 'LibraryButton'
  if ($null -eq $曲库钮) { Write-Host '  主窗上没有「歌曲库」那颗按钮'; exit 2 }
  $r = $曲库钮.Current.BoundingRectangle
  [void][P40]::Take($h); Start-Sleep -Milliseconds 250
  [P40]::ClickAt([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  $曲库窗 = $null
  foreach ($等 in 1..30) {
    Start-Sleep -Milliseconds 400
    $找 = @([P40]::Others($script:pid脚本, $h) | Where-Object { [P40]::Title($_) -eq '歌曲库' })
    if ($找.Count) { $曲库窗 = $找[0]; break }
  }
  if (-not $曲库窗) { Write-Host '  曲库窗没开出来'; exit 2 }
  $曲库根 = 取根 $曲库窗
  $行 = $null
  foreach ($it in @(找类型 $曲库根 $CT::ListItem)) {
    $rr = $it.Current.BoundingRectangle
    if ([double]::IsNaN($rr.X) -or $rr.Width -le 0) { continue }
    if (@(找类型 $it $CT::Text | Where-Object { $_.Current.Name -eq 'Carulli_Duetto_No2_Op4' }).Count -gt 0) { $行 = $it; break }
  }
  if ($null -eq $行) { Write-Host '  曲库里没有 Carulli 那一行'; exit 2 }
  $rr = $行.Current.BoundingRectangle
  [void][P40]::Take($曲库窗); Start-Sleep -Milliseconds 250
  [P40]::DoubleClickAt([int]($rr.X + $rr.Width / 2), [int]($rr.Y + $rr.Height / 2))
  foreach ($等 in 1..40) { Start-Sleep -Milliseconds 500; if (-not @([P40]::Others($script:pid脚本, $h)).Count) { break } }
  Start-Sleep -Seconds 2
  $root = 取根 $h     # 装曲子会把主窗控件整套重建，开场那个根作废
  $装了 = (空态吗 $root) -eq $false
  断言真 '曲子真装上了（「演奏」变亮了 = 手上有曲子）' $装了 "演奏能点=$(-not (空态吗 $root))"
  if (-not $装了) { exit 2 }

  $怎么找到的2 = ''
  $有曲项 = 找日志项 $root $h ([ref]$怎么找到的2)
  Write-Host "  [读数] 载了曲子之后：$怎么找到的2"
  if ($有曲项) { Write-Host "  [读数] 载了曲子之后：能点=$($有曲项.Current.IsEnabled)" }
  $有曲头 = 操作头 $root
  Write-Host "  [读数] 载了曲子之后「操作」那个头：能点=$(if ($有曲头) { $有曲头.Current.IsEnabled } else { '没找到' })"

  断言真 '载了曲子之后够得着「打开日志文件夹」' ($null -ne $有曲项) $怎么找到的2
  断言真 '载了曲子之后「打开日志文件夹」点得动' ($null -ne $有曲项 -and $有曲项.Current.IsEnabled) `
    "找到=$(if ($有曲项) { '是' } else { '否' })，能点=$(if ($有曲项) { $有曲项.Current.IsEnabled } else { '够不着' })"

  # 有曲子这条路上也真点一下：**日志那行还得再多一条**（别为了修空态把这条路弄坏）
  $日志文件2 = 最新日志
  $条数前2 = 打开日志条数 $日志文件2
  if ($null -ne $有曲项 -and $有曲项.Current.IsEnabled) {
    点日志项 $有曲项 $h $怎么找到的2
    $读后2 = 等等看 $日志文件2 $条数前2 0 6
    Write-Host "  点之后： 「打开了日志文件夹」$($读后2.条数) 条"
    断言真 '载了曲子之后点它，处理函数照样跑了（日志那一行又多一条）' ($读后2.条数 -gt $条数前2) `
      "之前 $条数前2 条，之后 $($读后2.条数) 条"
  }

  # 「撤销 / 重做」在载入之后仍然跟着谱面（刚载入、什么都没动 ⇒ 两条都还灰着）
  $有曲条目 = @(展开操作 $h $有曲头)
  if ($有曲条目.Count) {
    foreach ($n in '撤销', '重做') {
      $t = @($有曲条目 | Where-Object { $_.Current.Name -eq $n })
      断言真 "刚载入、什么都没动时「$n」还是灰的（跟着谱面走，没被这一票带跑）" `
        ($t.Count -gt 0 -and -not $t[0].Current.IsEnabled) `
        "$(if ($t.Count) { "能点=$($t[0].Current.IsEnabled)" } else { '弹出层里没有这一条' })"
    }
    收弹出层
  }
  Write-Host '  [读数] 「操作」那一组里跟着 _song 走的：撤销、重做（两条）—— 组整组仍由 OperationMenu.IsEnabled 管'
}
finally {
  # 只关**本脚本点出来的**那几扇资源管理器窗口（跑之前就在的不动），再收自己起的那个实例
  foreach ($w in @(日志窗)) {
    if (-not [W90]::Has($script:原有的日志窗, $w)) { [W90]::Close($w) | Out-Null }
  }
  Start-Sleep -Milliseconds 900
  $剩 = @(日志窗 | Where-Object { -not [W90]::Has($script:原有的日志窗, $_) }).Count
  if ($剩) { Write-Host "  还有 $剩 个本脚本点出来的 logs 资源管理器窗口没收掉" }
  收窗口
  if ($null -ne $script:proc) { Write-Host "收了实例 PID $($script:proc.Id)" }
}

if ($失败) { Write-Host "`n$失败 条没过" -ForegroundColor Red } else { Write-Host "`n全过" -ForegroundColor Green }
# 81 号票：裁决行（run-all.ps1 拿它跟退出码复核，对不上就把这一条降级成红）。
"==== uitest 裁决 不过=$失败"
exit $失败

