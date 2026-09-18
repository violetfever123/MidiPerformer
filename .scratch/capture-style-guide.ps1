# 把样板窗口截下来，好当场核对"控件长什么样"这类只能看出来的事。
# 用法: pwsh -File capture-style-guide.ps1 -Pages 2
param(
    [int]$Wheel = 0,
    [string]$Out = "$env:TEMP\styleguide.png",
    [switch]$NoBuild,
    # 把光标停在窗口的这个比例位置上（0~1），用来触发悬停态再截图
    [double]$HoverX = 0,
    [double]$HoverY = 0
)

# 先编译。截的是 bin 下的 exe —— 改了 XAML 不重编译，截到的还是上一版，
# 白折腾一轮还以为改动没生效。
# 编译没过必须当场停：二进制没更新，接着截就是上一版，
# 而屏幕上看不出区别 —— 会得出「改了没生效」这个正好相反的结论。
if (-not $NoBuild) {
    dotnet build (Join-Path $PSScriptRoot "..\MidiPerformer.App\MidiPerformer.App.csproj") --nologo -v q |
        Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) {
        Write-Error "编译没过（exit $LASTEXITCODE），bin 下还是上一版，截了等于没截"
        exit 1
    }
}

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint WheelFlag = 0x0800;
}
"@

$exe = Join-Path $PSScriptRoot "..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe"
$proc = Start-Process -FilePath $exe -ArgumentList "--style-guide" -PassThru

# 从起来那一刻就包进 try —— 中间任何一步抛异常（等不到句柄、PrintWindow 失败）
# 都不会把进程漏在后台，下次再跑就多一个窗口叠着。
try {
    # 等窗口真的出来 —— 抢在 MainWindowHandle 还是 0 的时候截，会截到别的窗口
    $deadline = (Get-Date).AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 500
        if ($proc.HasExited) { Write-Error "窗口没起来，退出码 $($proc.ExitCode)"; exit 1 }
        $proc.Refresh()
    } while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)

    if ($proc.MainWindowHandle -eq 0) { Write-Error "等不到窗口句柄"; exit 1 }
    Start-Sleep -Seconds 2
    $proc.Refresh()

    [Win]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 600

    $r = New-Object Win+RECT
    [Win]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top

    # 滚轮只认光标底下的窗口，所以先把光标挪进窗口再滚
    if ($Wheel -ne 0) {
        [Win]::SetCursorPos($r.Left + [int]($w / 2), $r.Top + [int]($h / 3)) | Out-Null
        Start-Sleep -Milliseconds 300
        for ($i = 0; $i -lt [Math]::Abs($Wheel); $i++) {
            [Win]::mouse_event([Win]::WheelFlag, 0, 0, $(if ($Wheel -gt 0) { -120 } else { 120 }), [IntPtr]::Zero)
            Start-Sleep -Milliseconds 60
        }
        Start-Sleep -Milliseconds 500
    }

    if ($HoverX -gt 0 -or $HoverY -gt 0) {
        [Win]::SetCursorPos($r.Left + [int]($w * $HoverX), $r.Top + [int]($h * $HoverY)) | Out-Null
        Start-Sleep -Milliseconds 800
    }

    # PrintWindow 直接让窗口自己画一遍，不靠 z 序 ——
    # 抢不到前台时 CopyFromScreen 会把压在上面的编辑器截进来。
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    # PW_RENDERFULLCONTENT
    [Win]::PrintWindow($proc.MainWindowHandle, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
}

Write-Output "saved $Out ($w x $h)"
