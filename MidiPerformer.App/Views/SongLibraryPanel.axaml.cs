using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.App.Theme;
using MidiPerformer.Core.UseCases.Project;

namespace MidiPerformer.App.Views;

/// <summary>
/// 歌曲库那一条：曲名列表 + 搜索框 + 删除。曲名不可编辑 —— 点它只是选中这一行，双击才打开；
/// 改名只剩顶栏那格「歌曲名」框（见 <c>MainWindow.OnSongNameKeyDown</c>，改的是当前开着的那首）。
///
/// 搜索是**即时**的（边打边筛，<see cref="OnSearchTextChanged"/> 直接重摆列表），筛的规矩收在纯函数
/// <see cref="Filter"/> 里；「搜不中」和「一首都没有」是**两句不同的空态文案**，见 <see cref="EmptyHintFor"/>。
///
/// 它自己不改盘（写）：点一首、要删除，都只是喊一声（<see cref="OpenRequested"/> /
/// <see cref="DeleteRequested"/>），真正读文件、删文件的是窗口。读是有的 ——
/// <see cref="AddRow"/> 为每一行读一次文件头（<see cref="SongProjectFile.TryReadProjectHeader"/>），
/// 「改过 / 没动过」那格小字就写在文件头里。删除前那句「真要删？」也留在这儿 ——
/// 居中要有 <see cref="Window"/> 才好问，而窗口是这一层拿得到的东西（<c>TopLevel.GetTopLevel</c>）。
///
/// 列表的每一行是代码摆的（曲名 + 改过没改过 + 删除），房子在 .axaml，家具在这儿。
/// 那格小字不写时长：时长要把整份工程读出来算，而「改过没改过」只要读文件头。
/// </summary>
public sealed partial class SongLibraryPanel : UserControl
{
    private readonly SongLibrary? _library;

    /// <summary>
    /// 列表里每一行，按曲名索引 —— 曲名是唯一稳定的东西（<see cref="Refresh"/> 每次都把行整批
    /// 换掉，行号当场过期），而「把某一首标成正开着」手上只有名字。
    /// </summary>
    private readonly Dictionary<string, ListBoxItem> _rows = new();

    /// <summary>
    /// 上一次从曲库读出来的曲名，顺序就是 <c>SongLibrary.Names()</c> 给的序（拼音序）。
    /// 敲键盘筛的是这一份：每按一个字就去问一遍盘、还把每一行的文件头重读一遍，是白花的。
    /// 盘上真变了（导入 / 删除）时由 <see cref="Refresh"/> 重新读一遍。
    /// </summary>
    private IReadOnlyList<string> _names = Array.Empty<string>();

    /// <summary>当前正开着的那首（高亮它）。<c>null</c> = 没有。</summary>
    private string? _current;

    /// <summary>给可视化设计器用的空构造。真跑起来走下面那个。</summary>
    public SongLibraryPanel()
    {
        InitializeComponent();

        // 设计器里没有曲库：摆一个空架子，免得预览是一片空白
        CountText.Text = Count(0);
        ApplyEmptyState(0, 0);
    }

    /// <param name="library">曲库（目录已由组装点注入）。</param>
    /// <param name="tokens">自绘取色桥。见 <see cref="Tokens"/>。</param>
    public SongLibraryPanel(SongLibrary library, TokenSource tokens)
    {
        ArgumentNullException.ThrowIfNull(library);

        InitializeComponent();
        _library = library;
        Tokens = tokens;
        Glass.Tokens = tokens;
        Refresh();
    }

    /// <summary>
    /// 面板底下那一层是什么（毛玻璃要糊的就是它）。<c>null</c> = 底下没东西。
    ///
    /// 今天把它设成主窗口内容的是 <c>SongLibraryWindow</c>：这个面板住在独立窗口里，
    /// 自己底下只有一层窗口底色，而规格要的是「透出底下的卷帘」。
    /// </summary>
    public void ShowBackdrop(Visual? backdrop) => Glass.ShowBackdrop(backdrop);

    /// <summary>用户点开了一首歌（双击，或选中之后回车）。参数是曲名。</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>用户已经确认要删掉这首。参数是曲名，盘上的活由窗口干。</summary>
    public event EventHandler<string>? DeleteRequested;

    /// <summary>
    /// 组装点给的取色桥。这个面板一处都不读它 —— 标题、行、按钮全由 .axaml 里那些令牌
    /// （<c>DynamicResource</c>）着色。留成属性，是为了将来按运行时状态现画的东西有地方取色。
    /// </summary>
    public TokenSource? Tokens { get; }

    /// <summary>
    /// 照曲库现在的样子重摆一遍列表。窗口导入完、存完、改完名、删完之后调 —— 曲库不会喊
    /// 「我变了」（它就是个目录），谁动了盘谁负责喊这一声。
    /// </summary>
    public void Refresh()
    {
        if (_library is null) return;

        _names = _library.Names();
        Rebuild();
    }

    /// <summary>
    /// 按搜索框里此刻的词，把列表重摆一遍。
    ///
    /// 刷新走的就是这一条（<see cref="Refresh"/> 读完盘就调它），所以「导入 / 删掉一首之后
    /// 筛选跟着更新」不用另起一套：盘一变，重摆这一遍自然会把现存的查询串重新筛一次。
    /// </summary>
    private void Rebuild()
    {
        var shown = Filter(_names, Query);

        SongList.Items.Clear();
        _rows.Clear();
        foreach (var name in shown) AddRow(name);

        // 「N 首」报的是曲库一共几首，不是筛完剩几首：它说的是「曲库里有多少东西」。
        // 筛掉了多少，看列表本身和空态那句话。
        CountText.Text = Count(_names.Count);
        ApplyEmptyState(_names.Count, shown.Count);

        // 高亮要重新打上：行是新摆的，上一次的高亮跟着旧行一起没了
        ApplyCurrent();
    }

    /// <summary>搜索框里现在的词。没敲过就是空的（= 不筛）。</summary>
    private string Query => SearchBox.Text ?? "";

    /// <summary>
    /// 按查询串筛一遍曲名 —— **纯函数**：给一张表、拿一张表，不碰控件也不碰盘，
    /// 所以脱开 Avalonia 也测得了（跟 PianoRollGeometry 是同一类做法）。
    ///
    /// 规则：空查询（连全是空白都算空）原样返回**全部**（不是全不匹配）；否则**不区分大小写、按子串**匹配。
    /// 匹配用 <see cref="StringComparison.CurrentCultureIgnoreCase"/>，跟 <c>SongLibrary.Names()</c>
    /// 排序用的 <c>StringComparer.CurrentCulture</c> 是**同一个 culture** ——
    /// 不然中文会出现「排序看着是拼音序、搜出来却不是」，用户会以为搜索框坏了。
    ///
    /// **顺序原样保留**：搜索是过滤，不是重排。
    /// </summary>
    /// <param name="names">曲名，已经按拼音序排好（<c>SongLibrary.Names()</c> 给的序）。</param>
    /// <param name="query">搜索框里那一串；<c>null</c> / 空 / 全是空白 = 不筛。</param>
    public static IReadOnlyList<string> Filter(IReadOnlyList<string> names, string? query)
    {
        ArgumentNullException.ThrowIfNull(names);

        string trimmed = (query ?? "").Trim();
        if (trimmed.Length == 0) return names;

        return names.Where(n => n.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    /// <summary>曲库里一首都没有时的空态文案。</summary>
    public const string EmptyLibraryHint = "还没有曲子";

    /// <summary>搜了、但一首都没匹配上时的空态文案。</summary>
    public const string NoMatchHint = "没有匹配的曲子";

    /// <summary>
    /// 空态该说哪一句。返回 <c>null</c> = 不显示空态（列表里有东西）。
    ///
    /// ⚠️ **两种空态必须是两句不同的话**：曲库真的空了说「还没有曲子」，搜不中说「没有匹配的曲子」。
    /// 都写「还没有曲子」的话，用户搜一个不存在的词会以为**曲子丢了**。
    ///
    /// 曲库本来就空的时候，哪怕手里还挂着一个查询串，也算「还没有曲子」—— 不是他搜没的。
    /// </summary>
    /// <param name="libraryCount">曲库里一共几首。</param>
    /// <param name="matchCount">筛完剩几首。</param>
    public static string? EmptyHintFor(int libraryCount, int matchCount) =>
        libraryCount == 0 ? EmptyLibraryHint
        : matchCount == 0 ? NoMatchHint
        : null;

    /// <summary>把空态那句话摆上（或者收掉）。三条路都走这儿：没配曲库 / 一首都没有 / 搜不到。</summary>
    private void ApplyEmptyState(int libraryCount, int matchCount)
    {
        string? hint = EmptyHintFor(libraryCount, matchCount);
        if (hint is not null) EmptyHint.Text = hint;
        EmptyHint.IsVisible = hint is not null;
    }

    /// <summary>搜索框里改一个字就重筛一遍 —— 「即时」就是这个意思：不用回车、不用点确认。</summary>
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => Rebuild();

    /// <summary>
    /// 把某一首标成「正开着」。<c>null</c> = 一首都不标。名字不在曲库里（比如刚被删掉）就当作没有。
    /// </summary>
    public void MarkCurrent(string? name)
    {
        _current = name;
        ApplyCurrent();
    }

    /// <summary>摆一行。<paramref name="name"/> 是曲名（= 文件名，改名就是改它）。</summary>
    private void AddRow(string name)
    {
        // 曲名用文件名那个，不是文件头里存的 Name：这个曲库的规矩是「文件名即曲名」，
        // 文件头里那个只是导入那一刻留下的复印件，两者不一致时以用户看得见的为准。
        var nameText = new TextBlock { Text = name, Classes = { "song-name" } };

        // 悬浮显示完整曲名：这一格是省略号截断的（.song-name 那条样式）。这一格不接任何手势 ——
        // 点它和点这一行别处完全一样，ToolTip 说的是「这一格装的是什么」。
        ToolTip.SetTip(nameText, name);

        // 只读文件头，不读谱面。读不出来的（坏工程、版本比本程序新）返回 null 而不是抛 ——
        // 一首读不出来不能让整个列表消失。
        ProjectHeader? header = SongProjectFile.TryReadProjectHeader(_library!.PathOf(name));
        var metaText = new TextBlock { Text = Meta(header), Classes = { "song-meta" } };
        if (header is null) metaText.Classes.Add("bad");

        // danger 是 Controls.axaml 里现成的「危险：删轨 / 删曲子」：悬停变 warn 色
        var delete = new Button { Content = "×", Classes = { "row-action", "del", "danger" }, Tag = name };
        delete.Click += OnDeleteClickAsync;
        ToolTip.SetTip(delete, "把这首从曲库里删掉");

        // 三格：名字（占满剩下的宽度）、改过没改过、删除。行动作只剩删除这一颗。
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            ColumnSpacing = 8
        };
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(metaText, 1);
        Grid.SetColumn(delete, 2);
        grid.Children.Add(nameText);
        grid.Children.Add(metaText);
        grid.Children.Add(delete);

        var item = new ListBoxItem { Content = grid, Tag = name };
        item.DoubleTapped += OnRowDoubleTapped;

        _rows[name] = item;
        SongList.Items.Add(item);
    }

    /// <summary>行右边那格小字：这份工程改过没有。</summary>
    private static string Meta(ProjectHeader? header) => header switch
    {
        null => "读不出来",
        { Edited: true } => "改过",
        _ => "没动过",
    };

    /// <summary>「N 首」这句汇总读数。</summary>
    private static string Count(int count) => $"{count} 首";

    private void ApplyCurrent() =>
        SongList.SelectedItem = _current is not null && _rows.TryGetValue(_current, out var item) ? item : null;

    /// <summary>当前选中的那一首（没选中 = null）。</summary>
    private string? SelectedName => (SongList.SelectedItem as ListBoxItem)?.Tag as string;

    /// <summary>
    /// 双击 = 打开这一首。单击只选中，不打开 —— 上下键浏览时每挪一格就重新载入整首曲子的话，
    /// 当前这份没存过的编辑会被顶掉。回车走 <see cref="OnListKeyDown"/>，和双击是同一件事。
    /// </summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBoxItem { Tag: string name } item) return;

        // 点在那颗「删除」上不算打开：双击「删除」不该把这首曲子载进来。
        if (e.Source is Visual source &&
            source.GetVisualAncestors().TakeWhile(v => v != item).Any(v => v is Button))
        {
            return;
        }

        OpenRequested?.Invoke(this, name);
    }

    /// <summary>列表上的键盘：回车打开。上下键（选哪一行）是 ListBox 自带的。</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (SelectedName is not { } name) return;

        if (e.Key != Key.Enter) return;

        e.Handled = true;
        OpenRequested?.Invoke(this, name);
    }

    /// <summary>
    /// 删除：先问一句，问住了才喊。没挂在窗口上（<c>GetTopLevel</c> 不是 Window）就什么都不做 ——
    /// 拿不到窗口就没法居中问一句，而默默删掉是不可接受的。
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
