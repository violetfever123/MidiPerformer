using System.Text.Json;
using System.Text.Json.Serialization;
using MidiPerformer.Core.Model;
// 这三行别名是跟着代码一起从 SongProject.cs 搬过来的。原先它们是为了绕开 DryWetMidi 的同名类型
// （见 MidiReader 的文件头），这个文件里没有 DryWetMidi 可撞，其实用不着；
// 留着是为了让三个转换器的代码、注释和类型名与搬家前一个字都不差 ——
// 那几个「为什么要自己写转换器」的论证，比代码本身值钱。
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Core.UseCases.Project;

/// <summary>
/// 工程文件用的三个 <see cref="JsonConverter{T}"/>：时间分辨率、音符、速度表。
///
/// 三个都是**必须自己写**的，理由分别写在各自的类型上（构造器私有 / 缺字段会被悄悄补默认值 /
/// STJ 拒绝配对）。它们从前是挂在 <c>SongProject</c> 底下的私有嵌套类，拆文件时原样搬到这里 ——
/// 从「私有」变成 <see cref="SongProjectFile"/> 看得见的 <c>internal</c>，
/// 那是搬家的代价，不是设计动作。
/// </summary>
internal static class Converters
{
    /// <summary>
    /// <see cref="ModelTimeDivision"/> 的读写。**必须自己写**：它的构造器是私有的
    /// （只有 <c>PulsesPerQuarter</c> / <c>Smpte</c> 两个工厂），STJ 自己建不出来，
    /// 不管就是一句英文的 <c>NotSupportedException</c>。
    ///
    /// 写成三个整数（两种模式互斥，另一种的字段是 0），读回来按「SMPTE 帧率是不是 &gt; 0」
    /// 分流 —— 和模型的 <c>IsSmpte</c> 是同一个判据，不另立一套。
    /// </summary>
    internal sealed class TimeDivisionConverter : JsonConverter<ModelTimeDivision>
    {
        public override ModelTimeDivision Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("时间分辨率应该是一个对象。");

            int ticksPerQuarter = Field(root, "TicksPerQuarterNote");
            int framesPerSecond = Field(root, "SmpteFramesPerSecond");
            int ticksPerFrame = Field(root, "SmpteTicksPerFrame");

            if (framesPerSecond > 0)
            {
                if (ticksPerFrame < 1)
                    throw new JsonException($"SMPTE 分辨率每帧至少要 1 tick（现在是 {ticksPerFrame}）。");
                if (ticksPerQuarter != 0)
                    throw new JsonException("时间分辨率同时写着 PPQ 和 SMPTE 两种模式，只能有一种。");
                return ModelTimeDivision.Smpte(framesPerSecond, ticksPerFrame);
            }

            if (ticksPerFrame != 0)
                throw new JsonException("时间分辨率里没有 SMPTE 帧率，却有「每帧 tick 数」。");
            if (ticksPerQuarter < 1)
                throw new JsonException($"时间分辨率里的每四分音符 tick 数至少要 1（现在是 {ticksPerQuarter}）。");
            return ModelTimeDivision.PulsesPerQuarter(ticksPerQuarter);
        }

        public override void Write(
            Utf8JsonWriter writer, ModelTimeDivision value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("TicksPerQuarterNote", value.TicksPerQuarterNote);
            writer.WriteNumber("SmpteFramesPerSecond", value.SmpteFramesPerSecond);
            writer.WriteNumber("SmpteTicksPerFrame", value.SmpteTicksPerFrame);
            writer.WriteEndObject();
        }

        /// <summary>
        /// 取一个整数字段。缺了、或者不是数字（写成字符串、小数、null），都当场说清楚。
        ///
        /// 为什么要自己判 <see cref="JsonValueKind.Number"/>：<c>JsonElement.TryGetInt32</c> 对一个
        /// **字符串**元素不是返回 false，是**抛** <c>InvalidOperationException</c>（实测），
        /// 而那是个英文异常；而且它会从转换器里冒出去，被 STJ 换成一句
        /// 「The JSON value could not be converted to …」，中文原因就全没了。
        /// </summary>
        private static int Field(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
                throw new JsonException($"时间分辨率里缺 {name} 字段（或者它不是个数字）。");
            if (!element.TryGetInt32(out int value))
                throw new JsonException($"时间分辨率里的 {name} 不是一个整数。");
            return value;
        }
    }

    /// <summary>
    /// <see cref="ModelNote"/> 的读写。模型的四个字段就是文件的四个字段，一个不多一个不少。
    ///
    /// 为什么要自己写，而不是靠 STJ 按构造器参数配：**STJ 对缺字段是悄悄补默认值的**
    /// （.NET 8 的默认行为：构造器参数没配到属性时，值类型就填 0，不报错）。
    /// 一份被改坏 / 手改漏了一行的工程会静默地读成一堆 velocity = 0 或者 length = 0 的音 ——
    /// 那不是「读出来了」，是「读出了一个错的谱面还告诉用户没问题」。宁可在这儿报中文错。
    ///
    /// 顺带把值的范围也拦下：音高与力度是七位整数（0..127，模型和 MIDI 都是这个约定），
    /// tick 不能是负数。这些都是「文件里写着但物理上不可能」的值。
    ///
    /// <b>身份（<see cref="ModelNote.Id"/>）写进文件，但读的时候可以缺。</b>
    /// 这条工单里那个「要不要持久化身份」的决定就是「要」，理由三条：
    /// <list type="number">
    /// <item>模型的公开字段就是文件里的字段 —— 这是这个仓库的规矩，而且有测试盯着
    /// （<c>SongProjectFileTests.文件里的字段就是构造器的参数</c>）。不写的话就得在那条测试上开一个例外，
    /// 而例外是给人看漏的。</item>
    /// <item>兼容不用付代价：这个字段**可选**。版本 1 的老工程没有它，读出来是 0 号「没有身份」，
    /// 读完由 <c>SongProjectFile</c> 按位置整轨重发一遍（和重新导入同一个 MIDI 是同一个函数发的号）。
    /// 老程序读新文件也不受影响：它不认识这个字段，当没看见。</item>
    /// <item>这样「存盘再打开」拿到的还是同一套身份。今天没人靠它活着（选中集本来就不跨重载存活），
    /// 但界面一旦按身份记选中，这一格就是现成的 —— 而写它只花一个字段。</item>
    /// </list>
    /// 反过来，MIDI 导出那边**不带身份**：标准 MIDI 里没有地方放它，也没必要放 ——
    /// 重新导入时按位置重发一遍，而选中集本来就不跨文件重载存活。
    /// </summary>
    internal sealed class NoteConverter : JsonConverter<ModelNote>
    {
        public override ModelNote Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("音符应该是一个对象。");

            int pitch = Int(root, "Pitch");
            long startTick = Long(root, "StartTick");
            long lengthTicks = Long(root, "LengthTicks");
            int velocity = Int(root, "Velocity");
            NoteId id = ReadId(root);

            if (pitch is < 0 or > 127)
                throw new JsonException($"音符的音高是 {pitch}，不在 0..127 里。");
            if (velocity is < 0 or > 127)
                throw new JsonException($"音符的力度是 {velocity}，不在 0..127 里。");
            if (startTick < 0)
                throw new JsonException($"音符的起始 tick 是负数（{startTick}）。");
            if (lengthTicks < 0)
                throw new JsonException($"音符的时值是负数（{lengthTicks}）。");

            return new ModelNote(pitch, startTick, lengthTicks, velocity, id);
        }

        public override void Write(Utf8JsonWriter writer, ModelNote value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(nameof(ModelNote.Pitch), value.Pitch);
            writer.WriteNumber(nameof(ModelNote.StartTick), value.StartTick);
            writer.WriteNumber(nameof(ModelNote.LengthTicks), value.LengthTicks);
            writer.WriteNumber(nameof(ModelNote.Velocity), value.Velocity);
            writer.WriteNumber(nameof(ModelNote.Id), value.Id.Value);
            writer.WriteEndObject();
        }

        /// <summary>
        /// 读身份。**可以缺**（版本 1 的老工程里没有这个字段，手改过的文件也可能漏掉它）：
        /// 缺了就是 0 号「没有身份」，整轨的身份由读取端重发一遍。
        ///
        /// 但**有就得是个像样的号**：类型不对或者是个负数，当场报中文错 —— 和上面那几个字段一条规矩。
        /// 负数身份不是「没有身份」，是个坏值，放过去它会一路混进按身份认音的地方，
        /// 而那种坏法不会报错，只会认错音。
        /// </summary>
        private static NoteId ReadId(JsonElement root)
        {
            if (!root.TryGetProperty(nameof(ModelNote.Id), out var element)) return NoteId.None;
            if (element.ValueKind == JsonValueKind.Null) return NoteId.None;
            if (element.ValueKind != JsonValueKind.Number)
                throw new JsonException($"音符的身份（{nameof(ModelNote.Id)}）不是一个数字。");
            if (!element.TryGetInt32(out int value))
                throw new JsonException($"音符的身份（{nameof(ModelNote.Id)}）不是一个整数。");
            if (value < 0)
                throw new JsonException($"音符的身份是负数（{value}）：要么 0（没有身份），要么正数。");

            return new NoteId(value);
        }

        private static int Int(JsonElement root, string name)
        {
            long value = Long(root, name);
            if (value is < int.MinValue or > int.MaxValue)
                throw new JsonException($"音符的 {name} 是 {value}，超出了整数的范围。");
            return (int)value;
        }

        private static long Long(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
                throw new JsonException($"音符里缺 {name} 字段（或者它不是个数字）。这份工程多半被改坏了。");
            if (!element.TryGetInt64(out long value))
                throw new JsonException($"音符的 {name} 不是一个整数。");
            return value;
        }
    }

    /// <summary>
    /// <see cref="ModelTempoMap"/> 的读写。**必须自己写**，理由不是「不方便」，是 STJ **拒绝**：
    /// 它的构造器收的是 <c>IEnumerable&lt;TempoChange&gt;</c>，而属性是 <c>IReadOnlyList&lt;TempoChange&gt;</c>，
    /// STJ 要求构造器参数与属性**同名且同类型**才能配对，类型对不上就一句
    /// <c>InvalidOperationException: Each parameter ... must bind to an object property or field</c>，
    /// 而且是在**读第一份文件时**才炸（实测：写出去一切正常，读回来才报错）。
    ///
    /// 为什么不改模型的构造器签名：模型是为了「调用方能传数组、能传 LINQ」才收
    /// <c>IEnumerable</c> 的，**文件格式不该反过来规定模型的签名**。写个转换器就都保住了。
    ///
    /// 表缺了当空表（构造器本来就有默认值），但**类型不对要报错** ——
    /// 一个不是列表的东西假装成速度表，读出来的曲子会静默地变回 120 BPM。
    /// </summary>
    internal sealed class TempoMapConverter : JsonConverter<ModelTempoMap>
    {
        public override ModelTempoMap Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("速度表应该是一个对象。");

            if (!root.TryGetProperty(nameof(ModelTempoMap.Division), out var divisionElement))
                throw new JsonException("速度表里没有时间分辨率（Division 字段），这份工程读不出曲子的时间轴。" +
                    "多半是保存时没写完。");

            ModelTimeDivision division = divisionElement.Deserialize<ModelTimeDivision>(options)
                ?? throw new JsonException("速度表里的时间分辨率是空的。");

            return new ModelTempoMap(
                division,
                Table<TempoChange>(root, nameof(ModelTempoMap.TempoChanges), options),
                Table<TimeSignatureChange>(root, nameof(ModelTempoMap.TimeSignatureChanges), options));
        }

        public override void Write(Utf8JsonWriter writer, ModelTempoMap value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WritePropertyName(nameof(ModelTempoMap.Division));
            JsonSerializer.Serialize(writer, value.Division, options);

            // 写的是构造器收下的那两张表（= TempoMap 自己归一化过的视图），
            // 不是从哪儿读来的原始事件流 —— 工程文件里只留一份算得出来的东西。
            writer.WritePropertyName(nameof(ModelTempoMap.TempoChanges));
            JsonSerializer.Serialize(writer, value.TempoChanges, options);

            writer.WritePropertyName(nameof(ModelTempoMap.TimeSignatureChanges));
            JsonSerializer.Serialize(writer, value.TimeSignatureChanges, options);

            writer.WriteEndObject();
        }

        /// <summary>读一张事件表。缺 = 空表，但不是列表就是坏文件（见类型上的注释）。</summary>
        private static IReadOnlyList<T> Table<T>(JsonElement root, string name, JsonSerializerOptions options)
        {
            if (!root.TryGetProperty(name, out var element)) return Array.Empty<T>();
            if (element.ValueKind == JsonValueKind.Null) return Array.Empty<T>();
            if (element.ValueKind != JsonValueKind.Array)
                throw new JsonException($"速度表里的 {name} 应该是一个列表。");

            return element.Deserialize<T[]>(options) ?? Array.Empty<T>();
        }
    }
}
