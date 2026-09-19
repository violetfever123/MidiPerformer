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
/// 工程文件进出：<see cref="Song"/> + <see cref="ProjectHeader"/> ⇄ .mproj。
///
/// **S1 缝的另一半**（见 spec「Testing Decisions」）：<see cref="Song"/> ⇄ .mproj。要的也是逐字段精确：
/// SaveProject → LoadProject 之后，轨数、每轨的轨块序号/声道/名字/音色/移调、
/// 每个音的四个字段、速度表的分辨率与两张事件表，一个都不能变。
/// （MIDI 那半在 <see cref="MidiReader"/> / <see cref="MidiWriter"/>。）
///
/// **文件格式**：JSON，平铺成一个对象 —— 文件头那几个字段就是文件最上面那几行：
///     {
///       "Version": 1,
///       "Name": "起风了",
///       "Edited": true,
///       "ImportedFrom": "C:\\下载\\起风了.mid",
///       "Song": { "Tracks": [ … ], "TempoMap": { … } }
///     }
/// 头和信息平铺在一层，是因为它们确实是「这份文件的头」；于是「缺 Song」也就成了
/// 读取端要单独认的一种坏文件。
///
/// **实体直接序列化，没有 DTO 层**（spec「文件与存储」）：只有一个消费者的文件格式，
/// 多一层映射是纯仪式。代价是模型上那几个「算出来的属性」得挡住不写 —— 见 <see cref="DropDerivedProperties"/>。
/// </summary>
public static class SongProjectFile
{
    /// <summary>当前 .mproj 的版本。读到比它大的版本就报错，不猜着读 —— 猜出来的谱面比读不出来更坏。</summary>
    public const int ProjectVersion = 1;

    /// <summary>
    /// <see cref="Song"/> + 文件头 → .mproj 的 JSON 文本。
    ///
    /// 这里**不校验谱面**：JSON 里没有「装不下」的值，MIDI 导出那边的越界检查（分辨率上限之类）
    /// 在这儿一条都不适用。什么 <see cref="Song"/> 都写得出来，空曲（0 轨）也一样。
    /// </summary>
    public static string WriteProject(Song song, ProjectHeader header)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(header);

        // 先整体序列化进内存再交给调用方，和 MIDI 的 Write 是对称的：序列化中途出错
        // 不会在盘上留下半截文件（那半截会被当成本地文件损坏，更难查）。
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            // 字段名一律从 record 上取（nameof），改了属性名这里跟着改，不会两边对不上。
            // 版本号**不取 header 里的那个值**：写出去的只有当前这一种格式，
            // 照着调用方手里那个数写，等于让文件声称自己是另一种格式。
            writer.WriteNumber(nameof(ProjectHeader.Version), ProjectVersion);
            writer.WriteString(nameof(ProjectHeader.Name), header.Name);
            writer.WriteBoolean(nameof(ProjectHeader.Edited), header.Edited);
            if (header.ImportedFrom is null)
                writer.WriteNull(nameof(ProjectHeader.ImportedFrom));
            else
                writer.WriteString(nameof(ProjectHeader.ImportedFrom), header.ImportedFrom);

            writer.WritePropertyName(SongFieldName);
            JsonSerializer.Serialize(writer, song, ProjectJson);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// .mproj 的 JSON 文本 → <see cref="Song"/> + 文件头。
    ///
    /// 读不回来时抛 <see cref="InvalidDataException"/>，消息是给人看的中文 ——
    /// 和 MIDI 的读取端（<see cref="MidiReader.Read"/>）同一条规矩：宁可说清楚哪儿坏了，不给英文异常。
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
            // 比当前新：不猜着读。将来真加了版本 2，迁移就写在下面这一行之后
            //（「版本 1 → 2 要补什么」是那个版本的事，现在没有）
            if (version > ProjectVersion)
            {
                throw new InvalidDataException(
                    $"这份工程是更新版本的 MIDI 演奏器存的（版本 {version}，本程序只认到 {ProjectVersion}）。" +
                    "请换用新版本的程序打开，或者用导出的 MIDI 文件。");
            }
            if (version < 1)
                throw new InvalidDataException($"工程文件的版本号不合法（{version}）。");

            Song song = ReadSong(root);
            var header = new ProjectHeader(version, ReadName(root), ReadEdited(root), ReadImportedFrom(root));
            return (header, song);
        }
    }

    /// <summary>
    /// <see cref="Song"/> + 文件头 → 盘上的 .mproj。
    ///
    /// 先整体序列化进内存再一次落盘，和 <see cref="MidiWriter.Write"/> 同一个理由：不落半截文件。
    /// 文件是 **UTF-8 无 BOM**（<see cref="File.WriteAllText(string, string)"/> 的默认），
    /// 中文因此原样在里面，diff 工具和编辑器都读得懂。
    /// </summary>
    public static void SaveProject(Song song, ProjectHeader header, string path) =>
        File.WriteAllText(path, WriteProject(song, header));

    /// <summary>
    /// 盘上的 .mproj → <see cref="Song"/> + 文件头。
    ///
    /// 文件不在 / 读不动（被别的程序占着、路径不允许）也抛 <see cref="InvalidDataException"/>：
    /// 这个特性里「读不回来」只有一种异常，调用方 catch 一处就够，
    /// 提示语里带着路径和系统给的原因，照样查得出是什么事。
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
    ///
    /// 所以它**不抛**：坏了、不是 JSON、读不动，一律返回 null。
    /// 一首读不出来的曲子不能让整个曲库列表消失 —— 用户得有机会把那首从列表里删掉。
    /// </summary>
    public static ProjectHeader? TryReadProjectHeader(string path)
    {
        try
        {
            // 只解析、不建对象：谱面那棵树（每个音符一个对象）一个都不造。
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            int version = ReadVersion(root);
            if (version < 1 || version > ProjectVersion) return null;

            return new ProjectHeader(version, ReadName(root), ReadEdited(root), ReadImportedFrom(root));
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
    /// 工程文件的写法：缩进 + 中文不转义。
    ///
    /// 缩进是给 diff 工具的（工程文件会跟着 git 走，一行到底的 JSON 一比就是整文件重写）；
    /// 中文不转义是给人看的 —— 默认转义会把曲名和导入路径写成一片 <c>\u8D77\u98CE</c>。
    /// 用 <see cref="UnicodeRanges.All"/> 而不是那个名字很吓人的 Relaxed：
    /// 中文照样原样写出去，而 <c>&lt;</c>、<c>&amp;</c> 该转义还是转义。
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>读写共用的序列化设置。写法见 <see cref="WriterOptions"/>，读这边只用到转换器与类型信息。</summary>
    private static readonly JsonSerializerOptions ProjectJson = CreateProjectJson();

    private static JsonSerializerOptions CreateProjectJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropDerivedProperties } },
            // 工程文件里不写 null 之外的东西 —— 默认行为就够，这里不额外开任何开关
        };
        options.Converters.Add(new Converters.TimeDivisionConverter());
        options.Converters.Add(new Converters.TempoMapConverter());
        options.Converters.Add(new Converters.NoteConverter());
        return options;
    }

    /// <summary>
    /// **只留构造器收得到的那些属性**，公开属性里其余的（= 算出来的派生视图）直接从类型信息里摘掉。
    ///
    /// 为什么要摘掉：模型上那些算出来的东西 —— <c>Song.EndTick</c> / <c>TotalSeconds</c>、
    /// <c>Track.NoteCount</c> / <c>EndTick</c> —— 写进文件就是给同一个事实开了第二个真相源：
    /// 改一个字段忘改另一个，文件里就自相矛盾；而**读**回来时它们本该由构造器重新算出来，
    /// 一旦被当成「必填」，字段缺一个就整份工程读不回来。
    ///
    /// 为什么是「摘掉」而不是把 <c>ShouldSerialize</c> 设成 false —— 那是这个坑踩出来的：
    /// STJ 写一个成员时是**先取值、再问要不要写**（<c>GetMemberAndWriteJson</c> 里
    /// <c>Get(obj)</c> 在 <c>ShouldSerialize</c> 之前），所以设 false 只挡住了写出去，
    /// 挡不住取值这个动作本身。而派生属性的 getter 是**会抛的**：<c>Song.TotalSeconds</c> 对
    /// 一个大到不现实的 tick 会抛「时间跨度太大」—— 于是「存一份 tick 很大的工程」会当场炸，
    /// 而它本该只是一个数字。摘掉之后取值这一步根本不存在。
    ///
    /// 用「构造器参数以外的一律不留」这条笼统的规矩，而不是逐个点名：
    /// 以后模型上再加派生属性（或者加一个真字段）不用回来补名单，规矩自己就成立。
    /// 这些类型上**没有加任何序列化特性** —— 模型不该知道文件格式这回事。
    /// （<c>TimeDivision</c> / <c>TempoMap</c> / <c>Note</c> 走各自的转换器，不经过这里，
    /// 见 <c>Converters</c>。）
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
            return element.Deserialize<Song>(ProjectJson)
                ?? throw new InvalidDataException("工程文件里的 Song 字段是空的。");
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
    /// 读版本号。**只认数字**：<c>TryGetInt32</c> 对字符串元素是抛异常而不是返回 false
    /// （见 <c>Converters.TimeDivisionConverter.Field</c> 的注释），不先判一下，
    /// 「版本号写成字符串」这种坏文件冒出去的就是一句英文的 InvalidOperationException。
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
    /// 曲名 / 是否改过 / 从哪导入的，缺了或类型不对**都当没有**，不算坏文件。
    ///
    /// 它们只是给人看的信息：曲名本来就从文件名来（见 <c>SongLibrary</c>），
    /// 「改过没改过」缺省就是没动过，导入来源丢了顶多看不到出处。
    /// 为这仨字段把一份读得出来的谱面拦在门外，是拿用户的时间换格式的洁癖。
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
}

/// <summary>
/// 工程文件的文件头 —— 一个 .mproj 的身份，也是曲库列表要显示的三个东西。
/// </summary>
/// <param name="Version">格式版本，见 <see cref="SongProjectFile.ProjectVersion"/>。</param>
/// <param name="Name">曲名。也就是它存进曲库后的文件名（去扩展名）。</param>
/// <param name="Edited">
/// **粘性**标记：这首曲子被编辑过并且存过盘。
///
/// 它是**进度指示**（「这首我动过」），**不是**和原始导入的逐字节比较 ——
/// 撤销回初始状态也不会把它变回 <c>false</c>，再存一版照样是 <c>true</c>。
/// 这是有意的，别当 bug 查：要「和刚导入时一模一样」就得留一份原始 MIDI 逐字节比对，
/// 既费盘又答非所问 —— 用户要看的是「我记得这首还没弄完」，不是文件的哈希。
/// </param>
/// <param name="ImportedFrom">当初从哪个文件导入的（原始 MIDI 的全路径）。没导入过的工程是 null。</param>
public sealed record ProjectHeader(int Version, string Name, bool Edited, string? ImportedFrom);
