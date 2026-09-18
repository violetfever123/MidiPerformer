using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.App.Theme;
using MidiPerformer.Core.UseCases.Project;

namespace MidiPerformer.App.Views;

/// <summary>
/// 歌曲库那一条：曲名列表 + 改名 + 删除。
///
/// **它自己不改盘。** 点一首、要改名、要删除，都只是喊一声
/// （<see cref="OpenRequested"/> / <see cref="RenameRequested"/> / <see cref="DeleteRequested"/>），
/// 真正读文件、改文件名、删文件的是窗口 —— 因为那些事都会**反过来影响窗口手上的状态**
/// （删掉的正好是当前这首怎么办？改名之后标题栏上那个名字要不要跟着改？），
/// 而面板不知道窗口手上有什么。面板只负责「问用户」，问完把名字交出去。
///
/// 「不改盘」说的是**写**：这个类里没有一次 <c>Write</c> / <c>Delete</c> / <c>Rename</c>。
/// **读是有的** —— <see cref="AddRow"/> 为每一行读一次文件头（<see cref="SongProject.TryReadProjectHeader"/>），
/// 因为「改过 / 没动过」那格小字就写在文件头里。那是这条路线上唯一碰盘的地方。
///
/// 唯一的例外是删除前那句「真要删？」：它得有个 <see cref="Window"/> 当爹才好居中，
/// 而**窗口是面板这一层拿得到、控制器拿不到的**东西（<c>TopLevel.GetTopLevel</c>），
/// 所以这句问话留在这儿，删的动作仍然是窗口的。
///
/// 列表的每一行是**代码摆的**（曲名 + 改过没改过 + 改名 + 删除）：行里有什么由曲库的内容决定，
/// 写在 XAML 里反而是死的一堆。房子在 .axaml，家具在这儿。
///
/// 看着那格小字不写时长（wireframe 的 .song .mt 是 4:32）是有意的：
/// 时长要**把整份工程读出来算**，而列表要显示的「改过没改过」只要读文件头就够了
/// （<see cref="SongProject.TryReadProjectHeader"/>）—— 为一行装饰把每首曲子的谱面
/// 都反序列化一遍，是这个列表最不该干的事。
/// </summary>
public sealed partial class SongLibraryPanel : UserControl
{
    private readonly SongLibrary? _library;
    private readonly List<ListBoxItem> _rows = new();

    /// <summary>当前正开着的那首（高亮它）。<c>null</c> = 没有。</summary>
    private string? _current;

    /// <summary>给可视化设计器用的空构造。真跑起来走下面那个。</summary>
    public SongLibraryPanel()
    {
        InitializeComponent();

        // 设计器里没有曲库：摆一个空架子，免得预览是一片空白
        CountText.Text = Count(0);
        EmptyHint.IsVisible = true;
    }

    /// <param name="library">曲库（目录已由组装点注入）。</param>
    /// <param name="tokens">自绘取色桥。见 <see cref="Tokens"/>。</param>
    public SongLibraryPanel(SongLibrary library, TokenSource tokens)
    {
        ArgumentNullException.ThrowIfNull(library);

        InitializeComponent();
        _library = library;
        Tokens = tokens;
        Refresh();
    }

    /// <summary>用户点开了一首歌（双击，或选中之后回车）。参数是曲名。</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>用户想给这首改名（点了「改名」或按了 F2）。参数是**旧名字**，盘上的活由窗口干。</summary>
    public event EventHandler<string>? RenameRequested;

    /// <summary>用户**已经确认**要删掉这首。参数是曲名，盘上的活由窗口干。</summary>
    public event EventHandler<string>? DeleteRequested;

    /// <summary>
    /// 组装点给的取色桥。
    ///
    /// 这个面板今天**一处都不读它**：标题、行、按钮全由 .axaml 里那些令牌
    /// （<c>DynamicResource</c>）着色，主题一换样式系统自己跟上，一行代码都不用写。
    /// 留成属性而不是把参数丢掉，是因为面板里将来任何一个**按运行时状态现画**的东西
    /// （不是控件、样式管不着的那种）都得从这儿取色 —— 一条颜色字面值都不许写。
    /// </summary>
    public TokenSource? Tokens { get; }

    /// <summary>
    /// 照曲库现在的样子重摆一遍列表。
    ///
    /// 什么时候调：窗口导入完一首之后、删完、改完名之后。**不是**每次写入都自动调 ——
    /// 曲库不会喊「我变了」（它就是个目录），谁动了盘谁负责喊这一声。
    /// </summary>
    public void Refresh()
    {
        if (_library is null) return;

        SongList.Items.Clear();
        _rows.Clear();

        var names = _library.Names();
        foreach (var name in names) AddRow(name);

        CountText.Text = Count(names.Count);
        EmptyHint.IsVisible = names.Count == 0;

        // 高亮要重新打上：行是新摆的，上一次的高亮跟着旧行一起没了
        ApplyCurrent();
    }

    /// <summary>
    /// 把某一首标成「正开着」。<c>null</c> = 一首都不标（比如把当前这首关掉了）。
    ///
    /// 名字不在曲库里（比如刚被删掉）就当作没有 —— 界面不该因为一个过期的名字报错。
    /// </summary>
    public void MarkCurrent(string? name)
    {
        _current = name;
        ApplyCurrent();
    }

    /// <summary>摆一行。<paramref name="name"/> 是曲名（= 文件名，改名就是改它）。</summary>
    private void AddRow(string name)
    {
        // 曲名用的是**文件名的那个**，不是文件头里存的那个 Name：这个曲库的规矩就是
        // 「文件名即曲名」（用户改的是文件名，文件头里那个只是导入那一刻留下的复印件），
        // 两者不一致时以用户看得见的那个为准。
        var nameText = new TextBlock { Text = name, Classes = { "song-name" } };

        // 只读文件头，不读谱面。读不出来的（坏工程、版本比本程序新）返回 null 而不是抛 ——
        // 一首读不出来不能让整个列表消失：用户得有机会把它删掉。
        ProjectHeader? header = SongProject.TryReadProjectHeader(_library!.PathOf(name));
        var metaText = new TextBlock { Text = Meta(header), Classes = { "song-meta" } };
        if (header is null) metaText.Classes.Add("bad");

        var rename = new Button { Content = "改名", Classes = { "row-action" }, Tag = name };
        // danger 那条类是 Controls.axaml 里现成的「危险：删轨 / 删曲子」：悬停变 warn 色
        var delete = new Button { Content = "×", Classes = { "row-action", "del", "danger" }, Tag = name };
        rename.Click += OnRenameClick;
        delete.Click += OnDeleteClickAsync;
        ToolTip.SetTip(rename, "改曲名（就是把文件改个名）");
        ToolTip.SetTip(delete, "把这首从曲库里删掉");

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto"),
            ColumnSpacing = 8
        };
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(metaText, 1);
        Grid.SetColumn(rename, 2);
        Grid.SetColumn(delete, 3);
        grid.Children.Add(nameText);
        grid.Children.Add(metaText);
        grid.Children.Add(rename);
        grid.Children.Add(delete);

        var item = new ListBoxItem { Content = grid, Tag = name };
        item.DoubleTapped += OnRowDoubleTapped;

        _rows.Add(item);
        SongList.Items.Add(item);
    }

    /// <summary>行右边那格小字：这份工程改过没有。</summary>
    private static string Meta(ProjectHeader? header) => header switch
    {
        null => "读不出来",
        { Edited: true } => "改过",
        _ => "没动过",
    };

    /// <summary>「N 首」。这句话本该住在 <c>Format</c> 里，但那是别的切片的文件，这条切片只许动自己的。</summary>
    private static string Count(int count) => $"{count} 首";

    private void ApplyCurrent() =>
        SongList.SelectedItem = _current is null
            ? null
            : _rows.FirstOrDefault(row => row.Tag as string == _current);

    /// <summary>当前选中的那一首（没选中 = null）。</summary>
    private string? SelectedName => (SongList.SelectedItem as ListBoxItem)?.Tag as string;

    /// <summary>
    /// 双击 = 打开这一首。
    ///
    /// **单击只选中，不打开**：上下键浏览时每挪一格就重新载入整首曲子的话，
    /// 想看一眼下一首叫什么都不行（载入会把当前这份没存过的编辑顶掉）。
    /// 回车走 <see cref="OnListKeyDown"/>，和双击是同一件事。
    /// </summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBoxItem { Tag: string name } item) return;

        // 点在行里那两颗按钮上不算打开：双击「删除」不该把这首曲子载进来
        if (e.Source is Visual source &&
            source.GetVisualAncestors().TakeWhile(v => v != item).Any(v => v is Button))
        {
            return;
        }

        OpenRequested?.Invoke(this, name);
    }

    /// <summary>列表上的键盘：回车打开、F2 改名。上下键（选哪一行）是 ListBox 自带的。</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (SelectedName is not { } name) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                OpenRequested?.Invoke(this, name);
                break;

            case Key.F2:
                e.Handled = true;
                RenameRequested?.Invoke(this, name);
                break;
        }
    }

    private void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) RenameRequested?.Invoke(this, name);
    }

    /// <summary>
    /// 删除：**先问一句，问住了才喊**。
    ///
    /// 没挂在窗口上（<c>GetTopLevel</c> 不是 Window）就什么都不做 —— 拿不到窗口就没法
    /// 居中问一句，而这种时候默默删掉是不可接受的，问一声又没地方问。
    /// </summary>
    private async void OnDeleteClickAsync(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        bool confirmed = await Dialogs.ConfirmAsync(
            owner,
            "删除曲子",
            $"把「{name}」从曲库里删掉？文件会一起删掉，撤不回来。",
            "删除");

        if (confirmed) DeleteRequested?.Invoke(this, name);
    }
}
