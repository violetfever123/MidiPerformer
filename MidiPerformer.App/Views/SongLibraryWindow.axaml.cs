using Avalonia.Controls;
using Avalonia.Interactivity;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.App.Theme;

namespace MidiPerformer.App.Views;

/// <summary>
/// 歌曲库那个窗口：把 <see cref="SongLibraryPanel"/> 装进一个模态框，底下配一条页脚。
///
/// 职责三件：把面板摆好、把页脚那句话写好、把事件转出去。它自己不改盘 —— 删除、打开都只是把
/// 名字往上报（<see cref="DeleteRequested"/> / <see cref="OpenRequested"/>），落地在
/// <c>MainWindow</c>，因为那些事会反过来影响主窗口手上的状态。
///
/// 事件转发换掉了 sender 的所指：主窗口收到的是这个窗口，才拿得到
/// <see cref="RefreshLibrary"/> 和 <see cref="ShowMessage"/> 去回话。
///
/// 模态是这一层的核心：模态期间主窗口动不了 ⇒ 这份列表不会过期。
/// </summary>
public sealed partial class SongLibraryWindow : Window
{
    private readonly SongLibraryPanel? _panel;

    /// <summary>
    /// 给可视化设计器用的空构造。它同时还是「这份 XAML 编译得过」的条件 —— 窗口没有公开的
    /// 无参构造时，XAML 编译器会给一条 AVLN3001。它不建面板（面板要曲库和取色桥两样），
    /// 所以这扇空窗里 <c>PanelHost</c> 没有内容。
    /// </summary>
    public SongLibraryWindow() => InitializeComponent();

    /// <param name="library">要列的那个目录。</param>
    /// <param name="tokens">取色桥，给面板建行用（和主窗口是同一个）。</param>
    /// <param name="current">打开时正开着的那一首（曲库里没有就传 null）—— 列表里给它加高亮。</param>
    public SongLibraryWindow(SongLibrary library, TokenSource tokens, string? current)
    {
        InitializeComponent();

        _panel = new SongLibraryPanel(library, tokens);
        _panel.OpenRequested += (_, name) => OpenRequested?.Invoke(this, name);
        _panel.DeleteRequested += (_, name) => DeleteRequested?.Invoke(this, name);

        PanelHost.Content = _panel;
        RefreshLibrary(current);
    }

    /// <summary>列表里双击了某一首（或者选中它按了回车）：请主窗口把它装上。</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>某一行的 × 已经问过「真要删？」并且用户说了「删」：请主窗口删掉它。</summary>
    public event EventHandler<string>? DeleteRequested;

    /// <summary>
    /// 重新列一遍曲库，并把某首标成「正开着」。只有两处会喊：开窗那一下，和删掉一首之后。
    /// </summary>
    public void RefreshLibrary(string? current)
    {
        // `?.`：设计器那扇空窗（上面那个无参构造）里没有面板
        _panel?.Refresh();
        _panel?.MarkCurrent(current);
    }

    /// <summary>
    /// 页脚上那句话：删除成了、删除失败了都走这一条 —— 那条横线只有一行，两句话不会同时出现，
    /// 而且在这个窗口里长得也一样。
    /// </summary>
    public void ShowMessage(string message) => StatusText.Text = message;

    /// <summary>「关闭」那一颗，和 <c>Esc</c>（<c>IsCancel</c>）是同一件事。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
