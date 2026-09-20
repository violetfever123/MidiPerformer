using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MidiPerformer.Adapters.Gateways;

namespace MidiPerformer.App.Views;

/// <summary>
/// 两个小模态框：问一个名字、确认一件坏事。曲库要的就这两样，所以只有这两个方法。
/// 外观全靠在 <c>Styles/Controls.axaml</c> 的应用级样式，这里没有颜色字面值。
/// 回车 = 确定，Esc = 取消（走 <c>IsDefault</c> / <c>IsCancel</c>，Avalonia 自己接键）。
/// 取消一律返回「什么都没发生」（<c>null</c> / <c>false</c>），调用方只判这一个值，
/// 不必再分「点了取消」和「叉掉了窗口」。
/// </summary>
public static class Dialogs
{
    /// <summary>确定 / 取消两个按钮的文字，两个框共用。</summary>
    private static readonly string OkText = "确定";
    private static readonly string CancelText = "取消";

    /// <summary>
    /// 问一个新的曲名。<paramref name="initial"/> 是原来的名字（改名时预填，导入时是文件名的建议）。
    /// 返回消毒过的名字（去掉文件名的非法字符、首尾空白、结尾的点），也就是曲库真会用的那一个。
    /// 名字不能用时（空的、全是非法字符、Windows 保留设备名）确定按钮直接灰着，判据与曲库共用
    /// <see cref="SongLibrary.IsUsableName"/>。
    /// </summary>
    public static async Task<string?> AskNameAsync(Window owner, string title, string initial)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var box = new TextBox { Text = initial ?? "", Width = 280 };

        var ok = new Button { Content = OkText, Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = CancelText, IsCancel = true };

        Window window = BuildDialog(title, box, ok, cancel);

        void SyncOk() => ok.IsEnabled = SongLibrary.IsUsableName(box.Text);
        box.TextChanged += (_, _) => SyncOk();
        SyncOk();

        // 打开时全选
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        string? result = null;
        ok.Click += (_, _) =>
        {
            // 回车也能触发这个按钮，灰按钮挡不住键盘，所以再判一次
            if (!SongLibrary.IsUsableName(box.Text)) return;

            result = SongLibrary.Sanitize(box.Text);
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();

        await window.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// 问一句「真要这么干？」。<paramref name="confirmText"/> 是会真动手的那个按钮上写的字
    /// （「删除」这样的动词，别写「确定」）。
    /// 叉掉窗口、按 Esc、点取消都返回 <c>false</c>，只有明确点了那个按钮才算数。
    /// </summary>
    public static async Task<bool> ConfirmAsync(
        Window owner, string title, string message, string confirmText)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360
        };

        var confirm = new Button { Content = confirmText, Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = CancelText, IsCancel = true };

        Window window = BuildDialog(title, text, confirm, cancel);

        bool confirmed = false;
        confirm.Click += (_, _) =>
        {
            confirmed = true;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();

        await window.ShowDialog(owner);
        return confirmed;
    }

    /// <summary>
    /// 一个居中的小窗：<paramref name="body"/> 竖着摞起来，按钮在右下角。
    /// 框自己量身高（<c>SizeToContent</c> + <c>CanResize = false</c>），不进任务栏，
    /// 位置钉在父窗口正中间（<c>CenterOwner</c>，跟屏幕居中会在多显示器上跑到别的屏去）。
    /// </summary>
    private static Window BuildDialog(string title, Control body, params Button[] buttons)
    {
        var stack = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        stack.Children.Add(body);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        foreach (var button in buttons) row.Children.Add(button);
        stack.Children.Add(row);

        return new Window
        {
            Title = title,
            Content = stack,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 320
        };
    }
}
