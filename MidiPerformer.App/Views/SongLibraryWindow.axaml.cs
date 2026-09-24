using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.App.Theme;

namespace MidiPerformer.App.Views;

/// <summary>
/// 歌曲库那个窗口：把 <see cref="SongLibraryPanel"/> 装进一个模态框，底下配一条页脚。
///
/// 职责三件：把面板摆好、把页脚那句话写好、把事件转出去。它自己不改盘 —— 删除、打开、
/// 导入都只是把这件事往上报（<see cref="DeleteRequested"/> / <see cref="OpenRequested"/> /
/// <see cref="ImportRequested"/>），落地在 <c>MainWindow</c>，因为那些事会反过来影响主窗口手上的状态。
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
        // 换掉 sender：主窗口收到的是这扇窗口，才拿得到它去当文件框的父窗口、去页脚回话
        _panel.ImportRequested += (_, _) => _importRequested?.Invoke(this, EventArgs.Empty);

        PanelHost.Content = _panel;
        RefreshLibrary(current);
    }

    /// <summary>
    /// 开窗那一下把底子交给面板：**主窗口**的内容。规格要的是「曲库面板透出底下的卷帘」，
    /// 而这个面板今天住在一个独立窗口里 —— 它自己底下只有一层窗口底色，
    /// 真正有东西可透的是身后那份卷帘。见 <see cref="SongLibraryPanel.ShowBackdrop"/>。
    ///
    /// 挂在 <c>OnOpened</c> 而不是构造里：<c>Owner</c> 要 <c>ShowDialog</c> 才设得上，
    /// 而窗口的位置也要到这时候才定下来（位图是按屏幕坐标从主窗口里抠的）。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (Owner?.Content is Visual backdrop) _panel?.ShowBackdrop(backdrop);
    }

    /// <summary>列表里双击了某一首（或者选中它按了回车）：请主窗口把它装上。</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>某一行的 × 已经问过「真要删？」并且用户说了「删」：请主窗口删掉它。</summary>
    public event EventHandler<string>? DeleteRequested;

    private EventHandler? _importRequested;

    /// <summary>
    /// 标题行右上角那颗「导入 MIDI…」被按了（55 号从主窗口工具栏搬进来的那一颗）。
    ///
    /// **跨窗口**：面板喊一声，这一层换掉 sender 往上报（和打开 / 删除同一个做法），
    /// 真正干活的是主窗口 —— 导入要读文件、装曲子、写曲库，三件事都在它手上。
    /// 这一声也带上自己，主窗口那边**拿它当文件框的父窗口**：发起导入的是这一扇，
    /// 而它是模态、压在主窗口头上，文件框要是挂在主窗口上就会开在它**后面**。
    ///
    /// **接上命令才让那颗按钮露面**：这扇窗口自己不会导入，没人接的时候摆一颗按下去
    /// 什么都不发生、也不报错的按钮比不摆更坏（工单 55 的验收里专门有一条）。
    /// 写成自定义访问器而不是另设一个「启用」方法，是为了让「有人接」和「按钮在」
    /// 在同一个动作里发生 —— 不这么写就多出一处要记得调的地方，而漏调不报错。
    /// </summary>
    public event EventHandler? ImportRequested
    {
        add
        {
            _importRequested += value;

            // 设计器那扇空窗（下面那个无参构造）里没有面板
            if (_panel is { } panel) panel.ImportAvailable = true;
        }

        remove
        {
            _importRequested -= value;
            if (_panel is { } panel) panel.ImportAvailable = false;
        }
    }

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
