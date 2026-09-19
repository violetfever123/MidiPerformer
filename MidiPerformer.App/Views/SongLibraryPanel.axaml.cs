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
/// 歌曲库那一条：曲名列表 + 删除（改名不用按钮了，点名字当场改，见下）。
///
/// **它自己不改盘。** 点一首、要改名、要删除，都只是喊一声
/// （<see cref="OpenRequested"/> / <see cref="RenameRequested"/> / <see cref="DeleteRequested"/>），
/// 真正读文件、改文件名、删文件的是窗口 —— 因为那些事都会**反过来影响窗口手上的状态**
/// （删掉的正好是当前这首怎么办？改名之后标题栏上那个名字要不要跟着改？），
/// 而面板不知道窗口手上有什么。面板只负责「问用户」，问完把名字交出去。
///
/// 「不改盘」说的是**写**：这个类里没有一次 <c>Write</c> / <c>Delete</c> / <c>Rename</c>。
/// **读是有的** —— <see cref="AddRow"/> 为每一行读一次文件头（<see cref="SongProjectFile.TryReadProjectHeader"/>），
/// 因为「改过 / 没动过」那格小字就写在文件头里。那是这条路线上唯一碰盘的地方。
///
/// 唯一的例外是删除前那句「真要删？」：它得有个 <see cref="Window"/> 当爹才好居中，
/// 而**窗口是面板这一层拿得到、控制器拿不到的**东西（<c>TopLevel.GetTopLevel</c>），
/// 所以这句问话留在这儿，删的动作仍然是窗口的。
///
/// 列表的每一行是**代码摆的**（曲名 + 改过没改过 + 删除）：行里有什么由曲库的内容决定，
/// 写在 XAML 里反而是死的一堆。房子在 .axaml，家具在这儿。
///
/// 曲名那一格是**两半**：一个 <see cref="TextBlock"/> 和一个 <see cref="TextBox"/>，
/// 谁亮见 <see cref="BeginRename"/> —— 25 号工单把「改名」那颗按钮删了，名字本身就在那儿，
/// 点一下就进编辑。为什么是**单击**不是双击：双击在这一行上已经是「打开这首」了
/// （<see cref="OnRowDoubleTapped"/>），两个手势撞在一起，用户想双击打开一首曲子时
/// 会顺手把名字框顶上来。F2 那条路留着，键盘用户没有「点」这个动作。
///
/// 面板手上**没有报错的地方**（<c>ShowError</c> 是窗口的），所以「这个名字不能用」
/// 不在这儿判死，而是照原样交给曲库去抛它那句现成的中文（见 <see cref="CommitRename"/>）。
///
/// 看着那格小字不写时长（wireframe 的 .song .mt 是 4:32）是有意的：
/// 时长要**把整份工程读出来算**，而列表要显示的「改过没改过」只要读文件头就够了
/// （<see cref="SongProjectFile.TryReadProjectHeader"/>）—— 为一行装饰把每首曲子的谱面
/// 都反序列化一遍，是这个列表最不该干的事。
/// </summary>
public sealed partial class SongLibraryPanel : UserControl
{
    private readonly SongLibrary? _library;

    /// <summary>
    /// 列表里每一行的两半，按**曲名**索引。
    ///
    /// 按名字而不是按行号：曲名是这个列表里唯一稳定的东西（<see cref="Refresh"/> 每次都把行
    /// 整批换掉，行号当场就过期了），而按 F2 那条路手上只有「当前选中的是哪首歌」这一个名字。
    /// </summary>
    private readonly Dictionary<string, Row> _rows = new();

    /// <summary>
    /// 正被编辑的那一行的曲名（没人改 = <c>null</c>）。
    ///
    /// 它管两件事，两件都必需：
    ///   · <see cref="BeginRename"/> 不重复起头（F2 落在已经开着的框上时）；
    ///   · 提交 / 取消之后那次 <c>LostFocus</c> 不再回头提交一遍 ——
    ///     <see cref="EndRename"/> 里清焦点**会**引出一次 <c>LostFocus</c>，
    ///     没有这面旗的话，一次改名会被自己回调进来提交两遍。
    /// </summary>
    private string? _editing;

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

    /// <summary>
    /// 用户想给这首改名（在名字那格上点了一下，或按了 F2 之后回车确定）。
    ///
    /// 带的是**新名和旧名两个**：25 号之前只有旧名，新名是窗口弹框问出来的，
    /// 面板不知道；现在名字是行内那个框给的，窗口那边没有别的来源，只能一起递过去。
    /// 盘上的活仍然是窗口干。
    /// </summary>
    public event EventHandler<RenameRequest>? RenameRequested;

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

        // 旗先落再拆行：拆行会把正握着焦点的那个名字框一起拆掉，那一下会引来一次
        // LostFocus（那一路也是「提交」）—— 旗不倒的话，它会对着一个已经在拆的行提交一次
        _editing = null;

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

        // 悬浮显示**完整曲名**：这一格是省略号截断的（.song-name 那条样式），
        // 长名字在列表里看不出全貌，而曲名恰恰是这一行里最要紧的信息。
        // 只写名字、不写「点一下改名」之类的说明 —— 那件事已经由光标形状说了
        // （IBeam）+ 名字框上自己的 ToolTip 说了，两处再说一遍是噪音。
        ToolTip.SetTip(nameText, name);

        // 点名字那一小块进编辑。用 Tapped（松手）而不是 PointerPressed（按下）：
        // 按下那一下还要先落到 ListBoxItem 上完成「选中这一行」，抢在它前面换控件
        // 会把这一次点击后半截（松手）交给一个刚冒出来的框。
        // 这一下也**不吞**（不设 e.Handled）：往上没有谁在等 Tapped，而「选中这一行」那件事
        // 走的是按下那一路，两不相干。
        nameText.Tapped += (_, _) => BeginRename(name);

        // 顶上来的那个框：平时藏着（IsVisible=False），点名字才换上。两份控件摆在同一格，
        // 尺寸由这一格算，行高不会因为换了控件而跳。KeyDown / LostFocus 和 TrackLaneView
        // 那边名字框是同一套规矩：回车确定、Esc 取消、焦点走了也算数。
        var nameBox = new TextBox { Text = name, Classes = { "song-rename" }, IsVisible = false };
        nameBox.KeyDown += OnNameBoxKeyDown;
        nameBox.LostFocus += OnNameBoxLostFocus;

        // 只读文件头，不读谱面。读不出来的（坏工程、版本比本程序新）返回 null 而不是抛 ——
        // 一首读不出来不能让整个列表消失：用户得有机会把它删掉。
        ProjectHeader? header = SongProjectFile.TryReadProjectHeader(_library!.PathOf(name));
        var metaText = new TextBlock { Text = Meta(header), Classes = { "song-meta" } };
        if (header is null) metaText.Classes.Add("bad");

        // danger 那条类是 Controls.axaml 里现成的「危险：删轨 / 删曲子」：悬停变 warn 色
        var delete = new Button { Content = "×", Classes = { "row-action", "del", "danger" }, Tag = name };
        delete.Click += OnDeleteClickAsync;
        ToolTip.SetTip(delete, "把这首从曲库里删掉");

        // 三格：名字（占满剩下的宽度）、改过没改过、删除。「改名」那一颗按钮没了，
        // 名字那一格自己就是入口 —— 顶上来的是上面那个框，它和名字共用第 0 格。
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            ColumnSpacing = 8
        };
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(nameBox, 0);
        Grid.SetColumn(metaText, 1);
        Grid.SetColumn(delete, 2);
        grid.Children.Add(nameText);
        grid.Children.Add(nameBox);
        grid.Children.Add(metaText);
        grid.Children.Add(delete);

        var item = new ListBoxItem { Content = grid, Tag = name };
        item.DoubleTapped += OnRowDoubleTapped;

        _rows[name] = new Row(item, nameText, nameBox);
        SongList.Items.Add(item);
    }

    /// <summary>
    /// 在这一行上开一个改名框。
    ///
    /// 起点永远是**已经落地的那个名字**（<c>name</c>，也就是曲名），不是框里剩的那行字：
    /// 上一次没收摊的半截名字不该留到这一次。
    ///
    /// 全选是给鼠标用户的：改名十有八九是整条换掉、不是改中间一个字
    /// （想改中间就再点一下，那之后就是普通的编辑了）。
    /// </summary>
    private void BeginRename(string name)
    {
        if (!_rows.TryGetValue(name, out var row)) return;
        if (_editing == name) return;                 // 已经开着（F2 落在同一个框上）：不重来一遍

        // 上一次还没收摊的（点第二个名字时焦点其实已经移开、那一路就提交了）：
        // 收掉它，别让两个框同时亮着 —— 那时「回车」算改哪一条说不清
        CancelRename();

        _editing = name;
        row.NameText.IsVisible = false;
        row.NameBox.IsVisible = true;
        row.NameBox.Text = name;
        row.NameBox.Focus();
        row.NameBox.SelectAll();
    }

    /// <summary>
    /// 名字框上的键盘：回车确定、Esc 取消。
    ///
    /// 两条都要 <c>e.Handled = true</c>：这个框住在 ListBoxItem 里，往上冒会撞到
    /// <see cref="OnListKeyDown"/> —— 不拦的话「回车」会在改名之余**顺手把这首曲子打开**
    /// （载入会把手上没存过的编辑顶掉）。
    /// </summary>
    private void OnNameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                CommitRename();
                break;

            case Key.Escape:
                e.Handled = true;
                CancelRename();
                break;
        }
    }

    /// <summary>焦点走了也算数：这一格收摊之后就没人在看它了。</summary>
    private void OnNameBoxLostFocus(object? sender, RoutedEventArgs e) => CommitRename();

    /// <summary>
    /// 把框里那个名字交出去。
    ///
    /// **能用的名字先消毒再喊**：曲库落盘用的是消毒过的那个（改名前那条路弹框问名字时
    /// <c>Dialogs.AskNameAsync</c> 返回的就是 <c>Sanitize</c> 的结果）。不消毒的话，
    /// 用户打「a/b」会看到「改成了「a/b」」而盘上躺着的叫「ab」—— 接下来按保存，
    /// 存进的是另一个名字。
    ///
    /// **不能用的名字照原样喊**（空的、只剩文件名里不能用的字符、Windows 保留设备名）：
    /// 面板手上没有报错的地方，而曲库的 Rename 会为这种名字抛一句现成的中文
    /// （见 <c>SongLibrary.Sanitize</c>）—— 交给窗口去喊，用户才看得见「为什么没改成」。
    /// 自己在这儿把框退回原名的话，在用户眼里就是「我打了回车，什么都没发生」。
    /// </summary>
    private void CommitRename()
    {
        if (_editing is not { } oldName) return;
        if (!_rows.TryGetValue(oldName, out var row)) { _editing = null; return; }

        string typed = row.NameBox.Text ?? string.Empty;
        // 先收摊再喊：喊出去之后窗口会重建整个列表（Refresh），那时这一行已经不存在了；
        // 而且收摊会把旗落下，下面那次 LostFocus 才不会再提交一遍
        EndRename();

        if (string.Equals(typed, oldName, StringComparison.Ordinal)) return;   // 一个字都没动

        string wanted = SongLibrary.IsUsableName(typed) ? SongLibrary.Sanitize(typed) : typed.Trim();

        // 消毒完还是老名字（比如只多打了几个空格、或者打了个非法字符）：盘上什么都不会变，
        // 就别让窗口白跑一趟、还留一句「改成了」的提示
        if (string.Equals(wanted, oldName, StringComparison.Ordinal)) return;

        RenameRequested?.Invoke(this, new RenameRequest(oldName, wanted));
    }

    private void CancelRename()
    {
        if (_editing is null) return;
        EndRename();
    }

    /// <summary>
    /// 收摊：框收起来、名字还回来、旗落下。
    ///
    /// 三件事的次序是有讲究的：
    ///   · 先落旗再动别的 —— 下面那句 <c>ClearFocus</c> 会引来一次 <c>LostFocus</c>
    ///     （「焦点走了算数」那一路），旗不倒的话这一次改名会被自己回调进来提交第二遍；
    ///   · 名字退回框里 —— 框里**始终**是已经落地的那个名字，半截字不会留到下一次；
    ///   · 主动清焦点：框收起来之后它还攥着焦点的话，窗口那一套快捷键会一直让着它
    ///     （见 <c>OnWindowKeyDown</c> 开头那条「焦点在输入框里就让开」的规矩），
    ///     Ctrl+S 会没反应，而屏幕上根本没有一个输入框在接字。
    /// </summary>
    private void EndRename()
    {
        if (_editing is not { } name || !_rows.TryGetValue(name, out var row))
        {
            _editing = null;
            return;
        }

        _editing = null;
        row.NameBox.IsVisible = false;
        row.NameText.IsVisible = true;
        row.NameBox.Text = row.NameText.Text;

        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
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
        SongList.SelectedItem =
            _current is not null && _rows.TryGetValue(_current, out var row) ? row.Item : null;

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

        // 点在那颗「删除」上不算打开：双击「删除」不该把这首曲子载进来。
        //
        // 名字那一格也在拦的范围内（TextBox）：双击名字时**第一下**就把名字框顶上来了
        // （单击 = 进改名，见 BeginRename），第二下落在框里 —— 那是在改名字，
        // 不该顺手把这首曲子载进来（载入会把手上没存过的编辑顶掉）。
        if (e.Source is Visual source &&
            source.GetVisualAncestors().TakeWhile(v => v != item).Any(v => v is TextBox or Button))
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
                // 从前喊一声、由窗口弹框问名字；现在就在这一行上开框 ——
                // 名字框自己带 KeyDown，接下来的回车 / Esc 走 OnNameBoxKeyDown
                BeginRename(name);
                break;
        }
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

    /// <summary>
    /// 一行的两半：平时显示名字的那个 <see cref="TextBlock"/>，和点进去之后顶上来的那个框。
    ///
    /// 两个控件都**常驻**（而不是点的时候临时 new 一个框塞进去）：收摊时要有个东西把名字
    /// 还回去，而且行高一直是这一格算的 —— 临时换控件会让这一行的高度跟着输入框的
    /// 内边距跳一下。
    /// </summary>
    private sealed record Row(ListBoxItem Item, TextBlock NameText, TextBox NameBox);
}

/// <summary>
/// 用户要把 <paramref name="OldName"/> 这首改成 <paramref name="NewName"/>。
///
/// 为什么不像别的请求那样只带一个新值：改名的**旧名字**只有面板知道（它是那一行上的字），
/// 窗口手上只有「当前开着的那首叫什么」，而用户改的完全可以是别的某一首。
/// </summary>
public sealed record RenameRequest(string OldName, string NewName);
