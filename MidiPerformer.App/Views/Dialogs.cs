using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MidiPerformer.Adapters.Gateways;

namespace MidiPerformer.App.Views;

/// <summary>
/// 两个小模态框：**问一个名字**和**确认一件坏事**。曲库要的就这两样，
/// 所以只有这两个方法 —— 以后真需要第三种再往这儿加，不开一个通用的「对话框工厂」。
///
/// 长相全靠 <c>Styles/Controls.axaml</c> 里那几条**应用级**样式：窗口底色、按钮、
/// 输入框（连控件模板里的 hover / focus 都在那儿管着）。这个文件里没有一个颜色字面值，
/// 也没有一条自己的样式 —— 于是暗色主题一换它跟着换，不用在这儿再写一遍。
/// （<c>TextBlock.faint</c> 那几条住在 MainWindow 的 Window.Styles 里，只有那个窗口吃得到，
/// 这儿用不上，所以框里的文字就用窗口默认的 ink，不硬凑一个颜色。）
///
/// **回车 = 确定，Esc = 取消**（走 <c>IsDefault</c> / <c>IsCancel</c>，Avalonia 自己接键）。
/// 取消一律返回「什么都没发生」：<c>null</c> / <c>false</c> —— 调用方只判这一个值，
/// 不必再分「用户点了取消」和「用户把窗口叉掉了」。
/// </summary>
public static class Dialogs
{
    /// <summary>确定 / 取消。两个框都用这两个词，改一处就一起改。</summary>
    private static readonly string OkText = "确定";
    private static readonly string CancelText = "取消";

    /// <summary>
    /// 问一个新的曲名。<paramref name="initial"/> 是原来的名字（改名时预填，导入时是文件名的建议）。
    ///
    /// 返回**消毒过的**名字（去掉文件名里不能用的字符、首尾空白、结尾的点），
    /// 也就是曲库真会用的那一个 —— 交回去 <c>"a/b"</c> 而曲库存成 <c>"ab"</c> 的话，
    /// 用户接下来看到的每个地方都对不上。
    ///
    /// 不能用的名字（空的、全是非法字符、Windows 保留设备名）**确定按钮直接灰着**，
    /// 而不是等点了再弹一句错：判据和曲库是同一个 <see cref="SongLibrary.IsUsableName"/>，
    /// 两处各写一套的话，早晚出现「界面让过、曲库不让存」。
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

        // 打开时全选：改名十有八九是整条换掉，不是改中间一个字
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        string? result = null;
        ok.Click += (_, _) =>
        {
            // 回车也能按到这个按钮，所以这儿得再判一次 —— 灰按钮挡得住鼠标，挡不住键盘
            if (!SongLibrary.IsUsableName(box.Text)) return;

            result = SongLibrary.Sanitize(box.Text);
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();

        await window.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// 问一句「真要这么干？」。<paramref name="confirmText"/> 是那个会真动手的按钮上写的字
    /// （「删除」这样的动词，别写「确定」—— 用户扫一眼就知道按下去会发生什么）。
    ///
    /// 叉掉窗口、按 Esc、点取消，三种都返回 <c>false</c>：**只有明确点了那个动词才算数**。
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
    ///
    /// <c>SizeToContent</c> + <c>CanResize = false</c>：一句话的框自己量身高，
    /// 免得出现一片空白或者一条挤扁的字。**不进任务栏**（它是附属窗口，不是一个应用），
    /// 位置钉在父窗口正中间（<c>CenterOwner</c>）—— 跟着屏幕居中会在多显示器上跑到别的屏去。
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
