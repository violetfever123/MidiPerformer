namespace MidiPerformer.Core.UseCases.Perform.Repertoire;

/// <summary>MIDI 文件解析后的一个音符（已换算成秒）。</summary>
public sealed class RawNote
{
    public int Pitch { get; init; }        // MIDI 音高 0..127, 60 = C4
    public double Start { get; init; }     // 起始秒
    public double End { get; init; }       // 结束秒
    public int Velocity { get; init; }

    public override string ToString() => $"{Music.NoteName(Pitch)} {Start:F2}s~{End:F2}s";
}

/// <summary>MIDI 音高相关的命名工具。</summary>
public static class Music
{
    private static readonly string[] Names =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static readonly string[] JianPu =
        { "1", "#1", "2", "#2", "3", "4", "#4", "5", "#5", "6", "#6", "7" };

    /// <summary>标准音名，如 C4 / F#5。</summary>
    public static string NoteName(int pitch) => $"{Names[Mod(pitch, 12)]}{pitch / 12 - 1}";

    /// <summary>简谱记号（含升降号），音高模 12。</summary>
    public static string DegreeName(int pitch) => JianPu[Mod(pitch, 12)];

    public static int Mod(int a, int b) => ((a % b) + b) % b;
}
