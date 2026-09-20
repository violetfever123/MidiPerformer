using Avalonia;
using MidiPerformer.App.SelfTest;

namespace MidiPerformer.App;

internal static class Program
{
    // Avalonia 初始化之前不要用任何 Avalonia 类型，也不要碰任何依赖 SynchronizationContext 的东西。
    [STAThread]
    public static void Main(string[] args)
    {
        // 自检分支必须是第一件事：这条路不建窗口、不注册热键、不碰按键与 MIDI 设备，
        // 只跑 Core 的纯函数，跑完直接 Exit，绝不落到 StartWithClassicDesktopLifetime ——
        // 发布产物的无人值守校验（tools/run-selftest.ps1）靠的就是这一点。
        if (PerformerSelfTest.Requested)
            Environment.Exit(PerformerSelfTest.Run());

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // 供可视化设计器用的构造入口。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
