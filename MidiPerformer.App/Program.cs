using Avalonia;

namespace MidiPerformer.App;

internal static class Program
{
    // Avalonia 初始化之前不要用任何 Avalonia 类型，也不要碰任何依赖 SynchronizationContext 的东西。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // 供可视化设计器用的构造入口。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
