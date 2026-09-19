# 折叠一条**正在播**的轨：试听该接着放（只是那一条不响），不该被这一下打断。
#
# 出声本身 UIA 断不了（那是耳朵的事，机器验的是 Core 与 PreviewPlayback 两层，
# 见 MidiPerformer.Tests/Preview/）。这里量的是**接线**：按了折叠之后窗口有没有走出
# 「正在播」那个状态、那一行小字是不是新的、进程还在不在 —— 也就是
# TrackLaneView.CollapseChanged → MainWindow.MutedTracks → PreviewPlayback.SetMutedTracks
# 这一条路有没有把人摔下来。
#
# 先跑 open-app.ps1 把窗口和一首曲子准备好，再跑这个。

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

$FOLDED = '已折叠 · 不发声'

function Get-App {
  Get-Process -Name dotnet, MidiPerformer -EA SilentlyContinue |
    Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
}

function Get-Root {
  $p = Get-App
  if (-not $p) { throw 'MidiPerformer 没在跑' }
  return $AE::FromHandle($p.MainWindowHandle)
}

function Find-All($root, $type) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
  return $root.FindAll($TS::Descendants, $cond)
}

function Find-Button($root, $name) {
  return Find-All $root $CT::Button | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
}

function Rect($e) {
  $r = $e.Current.BoundingRectangle
  return "{0,6:N0},{1,5:N0} {2,4:N0}x{3,-4:N0}" -f $r.Left, $r.Top, $r.Width, $r.Height
}

function Click($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

# 走带条上那两颗按钮就是「在不在播」的仪表：播着的时候播放键是灰的、停止键是亮的
function Transport {
  $root = Get-Root
  $play = Find-Button $root '▶ 从当前位置播放'
  $stop = Find-Button $root '■ 停止'
  return @{ PlayEnabled = $play.Current.IsEnabled; StopEnabled = $stop.Current.IsEnabled }
}

# Write-Host，不是 Write-Output：函数的**所有**输出都会被赋值语句收走，
# 写成 "$label : ..." 的话它就成了返回值的一部分，屏幕上反而什么都没有
function Show-Transport($label) {
  $t = Transport
  Write-Host ("{0} : 播放键可用={1}  停止键可用={2}" -f $label, $t.PlayEnabled, $t.StopEnabled)
}

# ---------- 起播 ----------
"===== 起播 ====="
if (-not (Transport).PlayEnabled) { throw '播放键是灰的 —— 先跑 open-app.ps1 导入一首曲子' }
Show-Transport '按之前'

Click (Find-Button (Get-Root) '▶ 从当前位置播放')
Start-Sleep -Milliseconds 1200
Show-Transport '按了播放'
if ((Transport).PlayEnabled -or -not (Transport).StopEnabled) { throw '没进「正在播」那个状态' }
""

# ---------- 播着折叠一条 ----------
"===== 播着折叠第一条轨 ====="
$fold = Find-Button (Get-Root) '折叠'
"折叠按钮: @ $(Rect $fold)"
$tip = ''
try { $tip = $fold.Current.HelpText } catch {}
"悬停提示 = '$tip'"

Click $fold
Start-Sleep -Milliseconds 1200

Show-Transport '折叠之后'
if ((Transport).PlayEnabled -or -not (Transport).StopEnabled) {
  throw '折叠把试听打断了 —— 这一下不该走出「正在播」'
}

$root = Get-Root
$strips = Find-All $root $CT::Text | Where-Object { $_.Current.Name -eq $FOLDED }
"'$FOLDED' 出现 $($strips.Count) 处"
foreach ($s in $strips) { "    @ $(Rect $s)" }

$folded = Find-Button $root '展开'
if (-not $folded) { throw '第一条轨的按钮没变成「展开」' }
"第一条轨的按钮现在是「展开」@ $(Rect $folded)"
$app = Get-App
"进程还在: $([bool]$app)  (PID $($app.Id))"
""

# ---------- 再展开回来 ----------
"===== 播着展开回来 ====="
Click $folded
Start-Sleep -Milliseconds 1200

Show-Transport '展开之后'
if ((Transport).PlayEnabled -or -not (Transport).StopEnabled) { throw '展开把试听打断了' }

$root = Get-Root
$strips2 = Find-All $root $CT::Text | Where-Object { $_.Current.Name -eq $FOLDED }
"那一行收回去了: $($strips2.Count -eq 0)  ('$FOLDED' 剩 $($strips2.Count) 处)"
"第一条轨的按钮 = '$((Find-Button $root '折叠').Current.Name)'"
""

# ---------- 收摊 ----------
"===== 停止 ====="
Click (Find-Button (Get-Root) '■ 停止')
Start-Sleep -Milliseconds 800
Show-Transport '按了停止'
