using Avalonia.Controls;
using Avalonia.Interactivity;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.App.Theme;

namespace MidiPerformer.App.Views;

/// <summary>
/// 歌曲库那个窗口：把 <see cref="SongLibraryPanel"/> 装进一个模态框，底下配一条页脚。
///
/// **它和面板的分工**：面板是「曲库长什么样」（列表、每一行、悬停、「真要删？」那句问话），
/// 窗口是「它摆在哪、多大、开着的时候外面能不能动」。所以从侧栏搬进窗口，
/// 面板那一层几乎没改 —— 这一层是新的。
///
/// **它自己也不改盘**，和面板一样：删除、打开都只是把名字往上报
/// （<see cref="DeleteRequested"/> / <see cref="OpenRequested"/>），落地在 <c>MainWindow</c>。
/// 理由也相同：那些事会反过来影响窗口手上的状态（正开着的是哪一首、文件名框里写什么、
/// 提示行上那句话），而**主窗口手上才有的那些东西，这个窗口看不见**。
///
/// 这一层的职责只有三件：把面板摆好、把页脚那句话写好、把事件转出去。
/// 事件转发看着像多余的二传（面板喊一声，这儿原样喊一声），其实换掉了 sender 的所指 ——
/// 主窗口收到的是**这个窗口**（<c>sender</c>），才拿得到 <see cref="RefreshLibrary"/> 和
/// <see cref="ShowMessage"/> 去回话。面板是窗口的私事，主窗口不该知道它存在。
///
/// **模态是这一层的核心决定**，不是顺手：见 <see cref="ShowDialog"/> 那一处，
/// 以及 .axaml 里那段说明（一句话：模态期间主窗口动不了 ⇒ 这份列表不会过期 ⇒
/// 主窗口那边所有「推一把刷新」的代码都不需要了）。
/// </summary>
public sealed partial class SongLibraryWindow : Window
{
    private readonly SongLibraryPanel? _panel;

    /// <summary>
    /// 给可视化设计器用的空构造 —— 和 <c>MainWindow</c> / <c>PerformerWindow</c> /
    /// <see cref="SongLibraryPanel"/> 一样。
    ///
    /// **它同时还是「这份 XAML 编译得过」的条件**：窗口没有公开的无参构造时，
    /// XAML 编译器会给一条 AVLN3001 —— 「avares://…/SongLibraryWindow.axaml 运行时就加载不到」。
    /// 真跑起来的窗口是下面那个（组装点给曲库），这一条只把 .axaml 摆出来。
    ///
    /// 它**不建面板**：面板要曲库和取色桥两样，而 <see cref="SongLibraryPanel"/> 那条
    /// 真构造对曲库是 <c>ThrowIfNull</c> 的（设计器里没有曲库）。所以这扇空窗里
    /// <c>PanelHost</c> 没有内容，页脚也照旧 —— 预览看到的就是个空架子。
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
    /// 重新列一遍曲库，并把某首标成「正开着」。
    ///
    /// 和主窗口从前那个同名方法干的是同一件事，只是现在只有两处会喊：
    /// **开窗那一下**，和**删掉一首之后**（删完列表里那一行得消失）。
    /// 别的时候曲库不会变 —— 模态框摆着的时候主窗口动不了，这个窗口自己只删不存。
    /// </summary>
    public void RefreshLibrary(string? current)
    {
        // `?.`：设计器那扇空窗（上面那个无参构造）里没有面板
        _panel?.Refresh();
        _panel?.MarkCurrent(current);
    }

    /// <summary>
    /// 页脚上那句话：删除成了、删除失败了，都走这一条。
    ///
    /// **一个方法，不是两个**（主窗口那边是 ShowError / ShowNotice 一对）：
    /// 那条横线只有一行，两句话不会同时出现，而它们在这个窗口里**长得也一样**——
    /// 分成两个名字就会让人以为颜色不同，去找一个根本不存在的差别。
    /// </summary>
    public void ShowMessage(string message) => StatusText.Text = message;

    /// <summary>「关闭」那一颗，和 <c>Esc</c>（<c>IsCancel</c>）是同一件事。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
