using MidiPerformer.Adapters.Gateways;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Analysis;
using MidiPerformer.Core.UseCases.Project;

namespace MidiPerformer.App.Views;

/// <summary>从曲库里读出来的一首：曲子本身，外加它那份缓存的文件头。</summary>
/// <param name="Song">曲子。走缓存那一趟是无损的，走 <c>.mid</c> 那一趟是有损的。</param>
/// <param name="Header">
/// 缓存的文件头；<c>null</c> = 这一趟读的是 <c>.mid</c>（没有缓存 / 缓存读不出来），
/// 于是「改过没有」「从哪来」这两样没有就是没有。
/// </param>
public readonly record struct CachedSong(Song Song, ProjectHeader? Header);

/// <summary>
/// 曲库那一支的**缓存**规矩 —— 一句话定死：
/// **<c>.mid</c> 是你的文件；<c>.mproj</c> 是本程序自己的缓存**（住在 <c>songs\.work\</c>）。
///
/// 两件事，都在这一个类型里：
/// <list type="bullet">
/// <item><b>保存时总是写两份</b>（<see cref="Save"/>）—— 跟 <c>.mid</c> 一起走。
/// 没有条件、没有判据、没有表：缓存不判断「需不需要」，它跟着主文件一起更新。</item>
/// <item><b>打开时有缓存就用缓存</b>（<see cref="Load"/>）—— 没有 / 读不出来就静默读 <c>.mid</c>，
/// 不弹窗、不拦路。这就是「缓存」两个字的定义。</item>
/// </list>
///
/// 为什么提成一个类型：这两段坏掉**都不报错**（只是移调悄悄没了、列表那一格说错话），
/// 而起一个真窗口在 NUnit 里要一台有桌面会话的机器 —— 于是它们的证据得来自能拿真文件喂的代码。
/// 主窗口那两处（<c>MainWindow.SaveTo</c> / <c>MainWindow.TryOpenLibrarySong</c>）
/// 只管把手上这份递进来、把读回来的装上去。
/// </summary>
public static class SongCache
{
    /// <summary>
    /// 缓存的文件头 —— 曲名、改过没有、来路，外加**这一刻**这份曲子有几条能弹的轨。
    ///
    /// <paramref name="edited"/> 传的是内存里那个粘性标记（<c>MainWindow._edited</c>），
    /// 也就是「保存这一刻，这份东西动过没有」：这一格是曲库列表上「编辑过」三个字的**唯一来源**。
    ///
    /// 可弹轨数算一趟全曲扫描，存下来是为了让列表每一行只读一个数字（二十首就是二十趟扫描，
    /// 那是「打开曲库窗口卡一下」的现成配方）。它是**上次保存时**的数，不是实时读数。
    /// </summary>
    public static ProjectHeader HeaderFor(string name, Song song, bool edited, string? importedFrom) =>
        new(SongProjectFile.ProjectVersion, name, edited, importedFrom, PlayableTracks.Of(song).Count);

    /// <summary>
    /// 保存：① <c>songs\&lt;名字&gt;.mid</c>（标准 MIDI，你的文件：能拷给别人、能用别的软件打开）
    /// ② <c>songs\.work\&lt;名字&gt;.mproj</c>（本程序自己的缓存）。
    /// **两条都无条件走** —— 少一条就是 bug（少了缓存，移调、删光的轨、轨的身份就再也回不来；
    /// 少了主文件，曲库里那首歌整个没了）。
    /// </summary>
    /// <exception cref="InvalidDataException">写不进去（曲子写不成 MIDI、目录建不出来、盘满、文件被占用）。</exception>
    public static void Save(SongLibrary library, string name, Song song, ProjectHeader header)
    {
        // 整份先在内存里拼好再落盘（MidiWriter / WriteProject 各自的规矩），IO 故障由曲库换成中文报出来
        library.WriteBytes(name, MidiWriter.WriteBytes(song));
        library.WriteWork(name, SongProjectFile.WriteProject(song, header));
    }

    /// <summary>
    /// 打开：**有缓存而且读得出来就用缓存**（无损），否则静默读 <c>.mid</c>（有损）。
    ///
    /// 「读不出来」包括：文件不在、被截断、JSON 坏了、版本不对（<see cref="SongProjectFile.TryReadProjectHeader"/>
    /// 只认当前版本）—— 一律**当它不在**，读 <c>.mid</c> 继续，不弹窗、不拦路。
    ///
    /// ⚠️ **代价写在明处**：降级那一趟，**移调和删光的轨会一起没掉**，程序不会拦你。
    /// 接受它的理由：① <c>.work\</c> 在 <c>songs\</c> 里面、名字带点，没有任何正常操作会碰到它；
    /// ② 程序退出时强制保存，正常使用下它和 <c>.mid</c> 永远同步。
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// 连 <c>.mid</c> 都读不出来（曲库里没有这一首、文件坏了）—— 那一趟没得降级，交给调用方去报。
    /// </exception>
    public static CachedSong Load(SongLibrary library, string name)
    {
        string work = library.WorkPathOf(name);

        if (SongProjectFile.TryReadProjectHeader(work) is { } header)
        {
            try
            {
                return new CachedSong(SongProjectFile.LoadProject(work).Song, header);
            }
            catch (InvalidDataException)
            {
                // 头读得出来、身子读不出来（写了一半、里面那棵树坏了）：当这份缓存不在，往下读 .mid
            }
        }

        // 没有缓存 / 缓存坏了：读 .mid 继续
        return new CachedSong(MidiReader.ReadBytes(library.ReadBytes(name)), Header: null);
    }
}
