using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MidiPerformer.App.Views;

/// <summary>
/// 未保存确认弹窗：**两颗按钮 + 右上角那颗 ✕**，默认焦点在「是」。
///
/// 三处入口（切曲子 / 点演奏 / 关窗口）走的是同一个它、同一个 <see cref="AskAsync"/>，
/// 差别只有传进来的 <see cref="UnsavedScene"/> —— 那点差别全在 <see cref="UnsavedPrompt.Of"/>
/// 那张表里（第二行文案、第二颗的名字、第二颗穿不穿警示色）。这个文件不判场景，只照着表摆。
///
/// 三颗出口：**是** = 先存再继续；**第二颗** = 不存也继续；**✕ / Esc** = 什么都不做，留在原地。
/// ✕ 那颗是系统标题栏画的（平时素色、移上去才变红底白叉），这儿不用管它长什么样，
/// 只需保证「关掉窗口」这条路不改 <see cref="Choice"/>：它默认停在
/// <see cref="UnsavedChoice.Cancel"/>，而全文件只有两颗按钮的点击处理会改它。
/// </summary>
public partial class UnsavedChangesDialog : Window
{
    /// <summary>
    /// 用户选了哪一条出口。默认就是「什么都不做」—— 叉掉窗口、按 Esc 走的都是这个默认值，
    /// 调用方只判这一个值，不必再分「点了哪颗」和「怎么关的」。
    /// </summary>
    public UnsavedChoice Choice { get; private set; } = UnsavedChoice.Cancel;

    /// <summary>
    /// 给可视化设计器用的空构造。它同时还是「这份 XAML 编译得过」的条件 ——
    /// 窗口没有公开的无参构造时，XAML 编译器会给一条 AVLN3001。
    /// </summary>
    public UnsavedChangesDialog() => InitializeComponent();

    /// <param name="scene">在哪一处被拦下的 —— 只有正文第二行与第二颗按钮跟着它变。</param>
    /// <param name="songName">正文第一行里那个曲名（三处的第一行逐字相同，只有名字不同）。</param>
    public UnsavedChangesDialog(UnsavedScene scene, string songName) : this()
    {
        var prompt = UnsavedPrompt.Of(scene);

        // 标题也从那一份表来：三处同一句，写在两个地方迟早会分叉
        Title = UnsavedPrompt.Title;
        FirstLineText.Text = UnsavedPrompt.FirstLine(songName);
        SecondLineText.Text = prompt.SecondLine;
        SecondButton.Content = prompt.SecondButton;

        // 不丢东西的那一处（点演奏）不穿警示色：那颗按下去草稿还在编辑器里
        SecondButton.Classes.Set("warn", prompt.Warn);

        // Esc 和右上角那颗 ✕ 是同一件事：关掉窗口、什么都不做（Choice 留着默认的 Cancel）
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;

            e.Handled = true;
            Close();
        };
    }

    /// <summary>开窗就把焦点放到「是」身上 —— 回车落在这颗，而它永远不会丢东西。</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        YesButton.Focus();
    }

    /// <summary>「是」：先存再继续。存和继续都是调用方的事，这儿只把选择记下来。</summary>
    private void OnYesClick(object? sender, RoutedEventArgs e)
    {
        Choice = UnsavedChoice.Save;
        Close();
    }

    /// <summary>第二颗：不存也继续。它叫什么名字由场景定，做的事三处一样。</summary>
    private void OnSecondClick(object? sender, RoutedEventArgs e)
    {
        Choice = UnsavedChoice.Continue;
        Close();
    }

    /// <summary>
    /// 开一颗弹窗问一句，等用户选完。
    /// </summary>
    /// <param name="owner">模态挂在谁身上。曲库那一处挂在曲库窗口上 —— 它压在头上，
    /// 弹到主窗口背后等于没弹。</param>
    /// <param name="scene">三处入口之一。</param>
    /// <param name="songName">正文第一行里那个曲名。</param>
    public static async Task<UnsavedChoice> AskAsync(Window owner, UnsavedScene scene, string songName)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dialog = new UnsavedChangesDialog(scene, songName);
        await dialog.ShowDialog(owner);
        return dialog.Choice;
    }
}
