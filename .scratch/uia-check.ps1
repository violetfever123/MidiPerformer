# 用 UI Automation 量界面上「折叠」和「音色」这两件东西。
# 不截图：这个进程是 DPI-aware 的，UIA 报的是物理像素，量出来的是真几何；
# 截图那条路（drive-ui.ps1 的 Shot）走的是 Windows.Forms 的虚拟坐标，
# 在 200% 缩放下只抠到屏幕左上角一块，看着像窗口被切了，其实是拍歪了。

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Get-Root {
  $p = Get-Process -Name dotnet -EA SilentlyContinue |
    Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
  if (-not $p) { throw 'MidiPerformer 没在跑' }
  return $AE::FromHandle($p.MainWindowHandle)
}

function Find-All($root, $type) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
  return $root.FindAll($TS::Descendants, $cond)
}

function Rect($e) {
  $r = $e.Current.BoundingRectangle
  return "{0,6:N0},{1,5:N0} {2,4:N0}x{3,-4:N0}" -f $r.Left, $r.Top, $r.Width, $r.Height
}

function Lane-Ys($root) {
  # 每条轨的「折叠/展开」按钮就是那条轨头的锚点
  $ys = @()
  foreach ($b in Find-All $root $CT::Button) {
    if ($b.Current.Name -in @('折叠', '展开')) { $ys += [int]$b.Current.BoundingRectangle.Top }
  }
  return ($ys | Sort-Object)
}

$root = Get-Root
$wr = $root.Current.BoundingRectangle
"窗口 = $($wr.Left),$($wr.Top) .. $($wr.Right),$($wr.Bottom)   ($($wr.Width)x$($wr.Height))"
""

# ---------- 一、音色下拉 ----------
"===== 音色 ====="
$combos = Find-All $root $CT::ComboBox
"下拉共 $($combos.Count) 个"
foreach ($c in $combos) {
  $val = ''
  try { $val = $c.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
  "  $(Rect $c)  name='$($c.Current.Name)'  value='$val'"
}

$first = $combos | Select-Object -First 1
if ($first) {
  $first.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  Start-Sleep -Milliseconds 700
  $items = Find-All $root $CT::ListItem
  "展开后列出 $($items.Count) 项"
  foreach ($i in $items | Select-Object -First 3) { "    '$($i.Current.Name)'" }
  # 口琴是 GM 23，也就是下标 22
  $harp = $items | Where-Object { $_.Current.Name -like '口琴*' } | Select-Object -First 1
  if ($harp) {
    "选中 -> '$($harp.Current.Name)'"
    $harp.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  } else { "没找到口琴那一项" }
  Start-Sleep -Milliseconds 800
  $after = (Find-All (Get-Root) $CT::ComboBox | Select-Object -First 1)
  $v = ''
  try { $v = $after.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
  "换完之后第一格 = name='$($after.Current.Name)' value='$v'"
}
""

# ---------- 二、折叠 ----------
"===== 折叠 ====="
$before = Lane-Ys (Get-Root)
"折叠前 每条轨头的 Y = $($before -join ', ')"

$fold = (Find-All (Get-Root) $CT::Button | Where-Object { $_.Current.Name -eq '折叠' } | Select-Object -First 1)
"第一条轨的按钮: $($fold.Current.Name) @ $(Rect $fold)"
$fold.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 900

$root2 = Get-Root
$names = (Find-All $root2 $CT::Button | Where-Object { $_.Current.Name -in @('折叠','展开') } | ForEach-Object { $_.Current.Name }) -join ', '
"按过之后 各轨按钮 = $names"
$strips = Find-All $root2 $CT::Text | Where-Object { $_.Current.Name -eq '已折叠' }
"'已折叠' 出现 $($strips.Count) 处"
foreach ($s in $strips) { "    @ $(Rect $s)" }
$after2 = Lane-Ys (Get-Root)
"折叠后 每条轨头的 Y = $($after2 -join ', ')"
if ($before.Count -ge 2 -and $after2.Count -ge 2) {
  "第二条轨上移了 $($before[1] - $after2[1]) px"
}
""

# ---------- 三、再展开 ----------
$unfold = (Find-All (Get-Root) $CT::Button | Where-Object { $_.Current.Name -eq '展开' } | Select-Object -First 1)
if ($unfold) {
  $unfold.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 900
  $back = Lane-Ys (Get-Root)
  $names2 = (Find-All (Get-Root) $CT::Button | Where-Object { $_.Current.Name -in @('折叠','展开') } | ForEach-Object { $_.Current.Name }) -join ', '
  "再按一次 各轨按钮 = $names2"
  "展开后 每条轨头的 Y = $($back -join ', ')"
  $same = ($before.Count -eq $back.Count) -and (@(Compare-Object $before $back).Count -eq 0)
  "和最开始一模一样: $same"
}
