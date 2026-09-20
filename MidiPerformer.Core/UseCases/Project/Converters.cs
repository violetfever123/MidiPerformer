using System.Text.Json;
using System.Text.Json.Serialization;
using MidiPerformer.Core.Model;
// 与 MidiReader / MidiWriter 用同一套别名（这个文件里没有 DryWetMidi 可撞，只为名字一致）。
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Core.UseCases.Project;

/// <summary>
/// 工程文件用的三个 <see cref="JsonConverter{T}"/>：时间分辨率、音符、速度表。
/// 三个都必须自己写，各自的理由写在那三个类型上。它们是 <see cref="SongProjectFile"/> 看得见的 <c>internal</c>。
/// </summary>
internal static class Converters
{
    /// <summary>
    /// <see cref="ModelTimeDivision"/> 的读写。必须自己写：它的构造器是私有的，STJ 自己建不出来。
    /// 写成三个整数（两种模式互斥，另一种的字段是 0），读回来按「SMPTE 帧率是不是 &gt; 0」分流，
    /// 和模型的 <c>IsSmpte</c> 是同一个判据。
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
        /// 自己判 <see cref="JsonValueKind.Number"/> 是因为 <c>TryGetInt32</c> 对字符串元素是抛
        /// <c>InvalidOperationException</c>，冒出去还会被 STJ 换成一句英文，中文原因就全没了。
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
    /// 必须自己写：STJ 对缺字段是悄悄补默认值的，一份被改坏或手改漏行的工程会静默地读成
    /// 一堆 velocity = 0 的音，还告诉用户没问题。顺带把值的范围也拦下：音高与力度 0..127，tick 不能是负数。
    ///
    /// 身份（<see cref="ModelNote.Id"/>）写进文件，但读的时候可以缺 —— 缺了就是 0 号「没有身份」，
    /// 读完由 <c>SongProjectFile</c> 整轨重发（版本 1 的老工程就是这样）。
    /// MIDI 导出那边不带身份：标准 MIDI 里没有地方放它。
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
        /// 读身份，可以缺（版本 1 的老工程里没有这个字段）：缺了就是 0 号「没有身份」，
        /// 整轨的身份由读取端重发一遍。但有就得是个像样的号 —— 类型不对或负数当场报错，
        /// 负数身份是坏值，放过去会一路混进按身份认音的地方且不报错。
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
    /// <see cref="ModelTempoMap"/> 的读写。必须自己写：它的构造器收 <c>IEnumerable&lt;TempoChange&gt;</c>
    /// 而属性是 <c>IReadOnlyList&lt;TempoChange&gt;</c>，STJ 要求两者同名且同类型才配得上，对不上的话
    /// 读第一份文件时才炸。模型收 <c>IEnumerable</c> 是为了调用方能传数组、传 LINQ，不该为文件格式改签名。
    ///
    /// 表缺了当空表，但类型不对要报错 —— 一个不是列表的东西假装成速度表，读出来的曲子会静默地变回 120 BPM。
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

            // 写的是构造器收下的那两张表（TempoMap 自己归一化过的视图），不是原始事件流。
            writer.WritePropertyName(nameof(ModelTempoMap.TempoChanges));
            JsonSerializer.Serialize(writer, value.TempoChanges, options);

            writer.WritePropertyName(nameof(ModelTempoMap.TimeSignatureChanges));
            JsonSerializer.Serialize(writer, value.TimeSignatureChanges, options);

            writer.WriteEndObject();
        }

        /// <summary>读一张事件表。缺 = 空表，但不是列表就是坏文件。</summary>
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
