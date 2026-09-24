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
/// 歌曲库那一条：曲名列表 + 删除。曲名不可编辑 —— 点它只是选中这一行，双击才打开；
/// 改名只剩顶栏那格「歌曲名」框（见 <c>MainWindow.OnSongNameKeyDown</c>，改的是当前开着的那首）。
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
