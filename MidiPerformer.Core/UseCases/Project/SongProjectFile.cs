using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.UseCases.Project;

/// <summary>
/// 工程文件进出：<see cref="Song"/> + <see cref="ProjectHeader"/> ⇄ .mproj，逐字段精确 ——
/// 轨数与每轨的轨块序号/声道/名字/音色/移调、每个音的五个字段（含 <see cref="Note.Id"/>）、
/// 速度表的分辨率与两张事件表，一个都不能变。MIDI 那一半在 <see cref="MidiReader"/> / <see cref="MidiWriter"/>。
///
/// 格式是 JSON，文件头与谱面平铺在同一层，于是「缺 Song 字段」是读取端要单独认的一种坏文件。
/// 实体直接序列化，没有 DTO 层；模型上算出来的属性由 <see cref="DropDerivedProperties"/> 挡住不写。
/// </summary>
public static class SongProjectFile
{
    /// <summary>
    /// 当前 .mproj 的版本。**只认这一个版本**，读到别的（老的也好、新的也好）都不猜着读。
    ///
    /// 版本 2 加了 <see cref="ProjectHeader.PlayableTrackCount"/>。老的版本 1 里没有这个字段，
    /// 而读它的 <see cref="ReadPlayableTrackCount"/> 走的是「缺了或类型不对都当没有」这条房子规矩，
    /// 于是 v1 会读出一个 <c>0</c> —— 那不是一个错误，是一个**安静地答错**的答案
    /// （曲库那一行会显示「不可播放」，而那首歌可能弹得了，见 <see cref="TryReadProjectHeader"/>）。
    /// </summary>
    public const int ProjectVersion = 2;

    /// <summary>
    /// <see cref="Song"/> + 文件头 → .mproj 的 JSON 文本。
    /// 不校验谱面：JSON 里没有「装不下」的值，什么 <see cref="Song"/> 都写得出来（空曲也一样）。
    /// </summary>
    public static string WriteProject(Song song, ProjectHeader header)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(header);

        // 先整体序列化进内存再交给调用方：中途出错不会在盘上留下半截文件。
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            // 字段名一律从 record 上取（nameof）。版本号不取 header 里的值：写出去的只有当前这一种格式。
            writer.WriteNumber(nameof(ProjectHeader.Version), ProjectVersion);
            writer.WriteString(nameof(ProjectHeader.Name), header.Name);
            writer.WriteBoolean(nameof(ProjectHeader.Edited), header.Edited);
            if (header.ImportedFrom is null)
                writer.WriteNull(nameof(ProjectHeader.ImportedFrom));
            else
                writer.WriteString(nameof(ProjectHeader.ImportedFrom), header.ImportedFrom);

            writer.WriteNumber(nameof(ProjectHeader.PlayableTrackCount), header.PlayableTrackCount);

            writer.WritePropertyName(SongFieldName);
            JsonSerializer.Serialize(writer, song, ProjectJson);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// .mproj 的 JSON 文本 → <see cref="Song"/> + 文件头。
    /// 读不回来时抛 <see cref="InvalidDataException"/>，消息是给人看的中文（与 <see cref="MidiReader.Read"/> 同一条规矩）。
    ///
    /// 版本闸门只管上界（比当前新就报错，不猜着读；老版本照读）。所以读回来的
    /// <see cref="ProjectHeader.Version"/> 是**文件里那个数**：用它之前先看一眼 ——
    /// 版本 1 的文件里没有 <see cref="ProjectHeader.PlayableTrackCount"/>，读出来是 <c>0</c>。
    /// 曲库那条路不走这儿（<see cref="TryReadProjectHeader"/> 把 v1 整个判成读不出来）。
    /// </summary>
    public static (ProjectHeader Header, Song Song) ReadProject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("工程文件是空的：多半是保存没写完，或者复制/下载时丢了内容。");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"工程文件不是合法的 JSON：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("工程文件的内容不是一个 JSON 对象，多半不是 .mproj 文件。");

            int version = ReadVersion(root);
            // 比当前新：不猜着读
            if (version > ProjectVersion)
            {
                throw new InvalidDataException(
                    $"这份工程是更新版本的 MIDI 演奏器存的（版本 {version}，本程序只认到 {ProjectVersion}）。" +
                    "请换用新版本的程序打开，或者用导出的 MIDI 文件。");
            }
            if (version < 1)
                throw new InvalidDataException($"工程文件的版本号不合法（{version}）。");

            Song song = ReadSong(root);
            var header = new ProjectHeader(
                version, ReadName(root), ReadEdited(root), ReadImportedFrom(root), ReadPlayableTrackCount(root));
            return (header, song);
        }
    }

    /// <summary>
    /// <see cref="Song"/> + 文件头 → 盘上的 .mproj。
    /// 先整体序列化进内存再一次落盘，不落半截文件；文件是 UTF-8 无 BOM。
    /// </summary>
    public static void SaveProject(Song song, ProjectHeader header, string path) =>
        File.WriteAllText(path, WriteProject(song, header));

    /// <summary>
    /// 盘上的 .mproj → <see cref="Song"/> + 文件头。
    /// 文件不在或读不动（被占着、路径不允许）也抛 <see cref="InvalidDataException"/>：「读不回来」只有一种异常。
    /// </summary>
    public static (ProjectHeader Header, Song Song) LoadProject(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"工程文件读不出来（{path}）：{ex.Message}", ex);
        }

        return ReadProject(json);
    }

    /// <summary>
    /// 只问文件头，不碰谱面 —— 曲库列表为每一首读一次的就是它。
    /// 不抛：坏了、不是 JSON、读不动，一律返回 null，免得一首坏曲子让整个曲库列表消失。
    ///
    /// ⚠️ **只认当前版本**（<see cref="ProjectVersion"/>），老的 v1 也判成读不出来。
    /// 别把它放松成「<c>1..ProjectVersion</c> 这个区间」：两处版本闸门都只卡上界，
    /// <c>1 &gt; 2</c> 是假、v1 照过，而 v1 里没有 <see cref="ProjectHeader.PlayableTrackCount"/>
    /// —— 读出来是 <c>0</c>，那一行就显示「不可播放」，**而那首歌可能弹得了**。
    /// 那是一个安静地答错；返回 null（= 读不出来 → 降级读 <c>.mid</c>）才是响亮的。
    /// </summary>
    public static ProjectHeader? TryReadProjectHeader(string path)
    {
        try
        {
            // 只解析、不建对象：谱面那棵树一个都不造。
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            int version = ReadVersion(root);
            if (version != ProjectVersion) return null;

            return new ProjectHeader(
                version, ReadName(root), ReadEdited(root), ReadImportedFrom(root), ReadPlayableTrackCount(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    // ==================== .mproj 的内部件 ====================

    /// <summary>谱面挂在 JSON 的这个字段下。缺了它就是一份坏工程。</summary>
    private const string SongFieldName = "Song";

    /// <summary>
    /// 工程文件的写法：缩进 + 中文不转义（默认转义会把曲名和路径写成一片转义序列）。
    /// 用 <see cref="UnicodeRanges.All"/> 而不是 Relaxed：中文原样写出去，<c>&lt;</c>、<c>&amp;</c> 照样转义。
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>读写共用的序列化设置，写法见 <see cref="WriterOptions"/>。</summary>
    private static readonly JsonSerializerOptions ProjectJson = CreateProjectJson();

    private static JsonSerializerOptions CreateProjectJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropDerivedProperties } },
        };
        options.Converters.Add(new Converters.TimeDivisionConverter());
        options.Converters.Add(new Converters.TempoMapConverter());
        options.Converters.Add(new Converters.NoteConverter());
        return options;
    }

    /// <summary>
    /// 只留构造器收得到的那些属性，公开属性里其余的（算出来的派生视图）从类型信息里摘掉：
    /// 写出去会给同一个事实开第二个真相源，读回来时它们本该由构造器重算。
    /// 摘掉而不是把 <c>ShouldSerialize</c> 设成 false —— STJ 写成员时先取值再问要不要写，
    /// 而派生属性的 getter 会抛（<c>Song.TotalSeconds</c> 对一个大到不现实的 tick 就抛）。
    /// 用「构造器参数以外的一律不留」这条笼统规矩，以后加派生属性不用回来补名单。
    /// </summary>
    private static void DropDerivedProperties(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;

        var fromConstructor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var constructor in info.Type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            foreach (var parameter in constructor.GetParameters())
                if (parameter.Name is { } name) fromConstructor.Add(name);

        // 先抄一份再删：不能一边遍历一边改这个集合
        foreach (var property in info.Properties.Where(p => !fromConstructor.Contains(p.Name)).ToArray())
            info.Properties.Remove(property);
    }

    private static Song ReadSong(JsonElement root)
    {
        if (!root.TryGetProperty(SongFieldName, out var element) || element.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException("工程文件里没有 Song 字段：这份文件不是 .mproj，或者保存时没写完。");

        try
        {
            Song song = element.Deserialize<Song>(ProjectJson)
                ?? throw new InvalidDataException("工程文件里的 Song 字段是空的。");

            // 身份是盘上的数据，进来之前得盘一遍：老工程没有这个字段，手改过的文件可能两个音一个号。
            return NormalizeIdentities(song);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"工程文件里的谱面读不出来：{ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException($"工程文件里的谱面读不出来：{ex.Message}");
        }
    }

    /// <summary>
    /// 把读进来的身份盘一遍：合规的（互不相同、都不是 0 号）原样留着，不合规的整轨重发
    /// （判据见 <see cref="NoteIdentity.Normalized"/>）。版本 1 的老工程和手改过的文件都会落到这里。
    /// 身份进了文件但格式版本号不动，<see cref="Converters.NoteConverter"/> 把这个字段当可选的读。
    /// 走 <see cref="Track"/> 的复制构造而不是 <see cref="Track.WithNotes"/>：这里是读文件，
    /// 文件里什么顺序就还是什么顺序，不替它重排。
    /// </summary>
    private static Song NormalizeIdentities(Song song)
    {
        var tracks = new Track[song.Tracks.Count];
        for (int i = 0; i < tracks.Length; i++)
            tracks[i] = song.Tracks[i] with { Notes = NoteIdentity.Normalized(song.Tracks[i].Notes) };

        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 读版本号，只认数字：<c>TryGetInt32</c> 对字符串元素是抛异常而不是返回 false，
    /// 不先判一下，「版本号写成字符串」这种坏文件冒出去的就是英文异常。
    /// </summary>
    private static int ReadVersion(JsonElement root)
    {
        if (!root.TryGetProperty(nameof(ProjectHeader.Version), out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out int version))
        {
            throw new InvalidDataException("工程文件里没有版本号（Version 字段）：这份文件不是 .mproj。");
        }
        return version;
    }

    /// <summary>
    /// 曲名 / 是否改过 / 从哪导入的，缺了或类型不对都当没有，不算坏文件 ——
    /// 它们只是给人看的信息，不值得为它们把一份读得出来的谱面拦在门外。
    /// </summary>
    private static string ReadName(JsonElement root) =>
        root.TryGetProperty(nameof(ProjectHeader.Name), out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    private static bool ReadEdited(JsonElement root) =>
        root.TryGetProperty(nameof(ProjectHeader.Edited), out var element) &&
        element.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        element.GetBoolean();

    private static string? ReadImportedFrom(JsonElement root) =>
        root.TryGetProperty(nameof(ProjectHeader.ImportedFrom), out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// 可弹轨数，和上面三个同一套规矩：缺了或类型不对都当没有（<c>0</c>），不算坏文件 ——
    /// 它只是曲库列表那一格的一个标记，不值得为它把一份读得出来的谱面拦在门外。
    ///
    /// ⚠️ 这条规矩正是「**v1 文件要整个判成读不出来**」的原因：v1 里没有这个字段，
    /// 这儿会老老实实返回 <c>0</c>（= 「不可播放」），而不是报错。
    /// 所以认不认 v1 是**调用方**的事，见 <see cref="TryReadProjectHeader"/>。
    /// </summary>
    private static int ReadPlayableTrackCount(JsonElement root) =>
        root.TryGetProperty(nameof(ProjectHeader.PlayableTrackCount), out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out int count)
            ? count
            : 0;
}

/// <summary>
/// 工程文件的文件头 —— 一个 .mproj 的身份，也是曲库列表要显示的三个东西。
/// </summary>
/// <param name="Version">格式版本，见 <see cref="SongProjectFile.ProjectVersion"/>。读回来的这个数是**文件里那个**，不一定是当前版本。</param>
/// <param name="Name">曲名。也就是它存进曲库后的文件名（去扩展名）。</param>
/// <param name="Edited">粘性标记：这首曲子被编辑过并且存过盘。撤销回初始状态也不会变回 <c>false</c>。</param>
/// <param name="ImportedFrom">当初从哪个文件导入的（原始 MIDI 的全路径）。没导入过的工程是 null。</param>
/// <param name="PlayableTrackCount">
/// **上次保存时**这份曲子里能弹的轨有几条（<c>PlayableTracks.Of</c> 那个数）。
/// 缓存它是为了让曲库列表每一行只读一个数字：算它要把每条轨的音符全走一遍，
/// 二十首就是二十趟全曲扫描。存的是**条数**而不是「能不能弹」—— 算「≥1」和算「一共几条」
/// 是同一趟扫描，多存一个数字不多花一分钱。
/// ⚠️ 它是缓存，不是实时读数：在别的软件里改了那份 <c>.mid</c>，它就不准了。
/// </param>
public sealed record ProjectHeader(
    int Version, string Name, bool Edited, string? ImportedFrom, int PlayableTrackCount);
