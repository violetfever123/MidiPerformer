
namespace MidiPerformer.Core.UseCases.Perform.Repertoire;

/// <summary>八度档位：相对基准八度的偏移。</summary>
public enum Slot
{
    Low = -1,   // 比基准低一个八度 → 按住鼠标左键
    Mid = 0,    // 基准八度     → 不按鼠标
    High = 1    // 比基准高一个八度 → 按住鼠标右键
}

/// <summary>映射后的单个可演奏音符。</summary>
public sealed class MappedNote
{
    public int Pitch { get; init; }
    public double Start { get; init; }   // 秒（未乘速度）
    public double End { get; init; }
    public char Key { get; init; }       // 'Z'..'M'
    public bool Sharp { get; init; }     // true → 需按住鼠标中键（升半音）
    public Slot OctaveSlot { get; init; }
    public bool InRange { get; init; }   // false → 超出三个八度，空拍跳过
    public string SkipReason { get; init; } = "";
}

/// <summary>一次映射的结果。</summary>
public sealed class MappingResult
{
    public int BaseOctave { get; set; }        // 基准八度（MIDI 编号，C4=第4八度）
    public List<MappedNote> Notes { get; init; } = new();
    public int InRangeCount => Notes.Count(n => n.InRange);
    public int SkipCount => Notes.Count(n => !n.InRange);
}

/// <summary>
/// 把主旋律音高映射到游戏口琴按键：do..ti → z x c v b n m，升半音加按鼠标中键；
/// 不按鼠标=基准八度、左键=低八度、右键=高八度；超出三个八度则空拍。
/// </summary>
public static class NoteMapper
{
    /// <summary>do..ti 对应的键位。</summary>
    public static readonly char[] Keys = PlayKeys.Keys;

    /// <summary>高高音do 用的键（逗号）。</summary>
    public const char TopKey = PlayKeys.TopKey;

    /// <summary>该音高在基准八度下是否可演奏（基准±1 八度，外加最高两个音）。</summary>
    private static bool Reachable(int pitch, int baseOct)
    {
        int d = pitch / 12 - 1 - baseOct;
        if (d is >= -1 and <= 1) return true;
        if (d == 2)
        {
            int pc = Music.Mod(pitch, 12);
            return pc is 0 or 1;   // 高高音do / 高高音#do（右键+逗号，可再加中键）
        }
        return false;
    }

    /// <summary>自然音（无升降）对应的半音号。</summary>
    private static readonly int[] NaturalPc = { 0, 2, 4, 5, 7, 9, 11 };

    private static readonly HashSet<int> SharpPc = new() { 1, 3, 6, 8, 10 };

    private static int DiatonicIndexOf(int pitchClass) => pitchClass switch
    {
        0 => 0, 2 => 1, 4 => 2, 5 => 3, 7 => 4, 9 => 5, 11 => 6,
        _ => -1
    };

    /// <summary>取任意音高的键位（升号音先降到最近的自然音）。</summary>
    public static char KeyOfPitch(int pitch)
    {
        int pc = Music.Mod(pitch, 12);
        int idx = DiatonicIndexOf(pc);
        if (idx < 0)
        {
            // 升号音：降半音后取自然音
            pc = Music.Mod(pc - 1, 12);
            idx = DiatonicIndexOf(pc);
        }
        return Keys[idx];
    }

    /// <summary>该音高是否需要按中键。</summary>
    public static bool IsSharpPitch(int pitch) => SharpPc.Contains(Music.Mod(pitch, 12));

    /// <summary>自动选出基准八度，使可演奏区容纳最多音符。</summary>
    public static int AutoBaseOctave(IReadOnlyList<int> pitches)
    {
        if (pitches.Count == 0) return 4;

        int minO = int.MaxValue, maxO = int.MinValue;
        double sumO = 0;
        foreach (var p in pitches)
        {
            int o = p / 12 - 1;
            if (o < minO) minO = o;
            if (o > maxO) maxO = o;
            sumO += o;
        }
        double meanO = sumO / pitches.Count;

        int bestB = minO;
        int bestPlay = -1;
        for (int b = minO; b <= maxO; b++)
        {
            int play = pitches.Count(p => Reachable(p, b));
            if (play > bestPlay ||
                (play == bestPlay && Math.Abs(b - meanO) < Math.Abs(bestB - meanO)))
            {
                bestPlay = play;
                bestB = b;
            }
        }
        return bestB;
    }

    /// <summary>
    /// 执行映射，返回每个音符的键位与档位。manualBaseOctave 为 null 时自动选基准八度。
    /// </summary>
    public static MappingResult Map(IReadOnlyList<RawNote> notes, int transpose, int? manualBaseOctave)
    {
        var result = new MappingResult();

        var valid = new List<int>();
        foreach (var n in notes)
        {
            int p = n.Pitch + transpose;
            if (p >= 0 && p <= 127) valid.Add(p);
        }

        int baseOctave = manualBaseOctave ?? AutoBaseOctave(valid);
        result.BaseOctave = baseOctave;

        foreach (var n in notes)
        {
            int p = n.Pitch + transpose;
            if (p < 0 || p > 127)
            {
                result.Notes.Add(new MappedNote
                {
                    Pitch = p, Start = n.Start, End = n.End,
                    Key = ' ', Sharp = false, OctaveSlot = Slot.Mid,
                    InRange = false, SkipReason = "移调后超出 MIDI 音域"
                });
                continue;
            }

            int oct = p / 12 - 1;
            int diff = oct - baseOctave;

            if (diff < -1 || diff > 2)
            {
                result.Notes.Add(new MappedNote
                {
                    Pitch = p, Start = n.Start, End = n.End,
                    Key = KeyOfPitch(p), Sharp = IsSharpPitch(p),
                    OctaveSlot = Slot.Mid,
                    InRange = false,
                    SkipReason = $"音区超出口琴可演奏范围(第{oct}八度)"
                });
                continue;
            }

            if (diff == 2)
            {
                // 最上方只有两个音：高高音do、高高音#do（右键 + 逗号，带#再加中键）
                int pc = Music.Mod(p, 12);
                if (pc is not (0 or 1))
                {
                    result.Notes.Add(new MappedNote
                    {
                        Pitch = p, Start = n.Start, End = n.End,
                        Key = TopKey, Sharp = pc == 1,
                        OctaveSlot = Slot.High,
                        InRange = false,
                        SkipReason = "最高只能到 高高音#do"
                    });
                    continue;
                }
                result.Notes.Add(new MappedNote
                {
                    Pitch = p,
                    Start = n.Start,
                    End = n.End,
                    Key = TopKey,
                    Sharp = pc == 1,
                    OctaveSlot = Slot.High,
                    InRange = true
                });
                continue;
            }

            result.Notes.Add(new MappedNote
            {
                Pitch = p,
                Start = n.Start,
                End = n.End,
                Key = KeyOfPitch(p),
                Sharp = IsSharpPitch(p),
                OctaveSlot = (Slot)diff,
                InRange = true
            });
        }

        return result;
    }

    /// <summary>
    /// 多声部合奏合成单音线：同刻只保留 Rank 最小的声部，低优先级音落在高优先级音
    /// 持续期间时让位（超出部分补回）。
    /// </summary>
    public static List<RawNote> MergeVoicesByPriority(IEnumerable<(int Rank, RawNote Note)> voices)
    {
        var ordered = voices
            .OrderBy(v => v.Note.Start)
            .ThenBy(v => v.Rank)
            .ThenBy(v => v.Note.Pitch)
            .ToList();
        if (ordered.Count == 0) return new List<RawNote>();

        const double eps = 0.025;
        // 1) 同刻组内选 Rank 最小者；被压掉的低优先级音若更长，"超出主声部结束"的尾巴稍后补回。
        var items = new List<(int Rank, RawNote Note)>();
        int i = 0;
        while (i < ordered.Count)
        {
            int j = i;
            double s0 = ordered[i].Note.Start;
            while (j + 1 < ordered.Count && ordered[j + 1].Note.Start - s0 <= eps) j++;

            var best = ordered[i];
            for (int k = i + 1; k <= j; k++)
            {
                if (ordered[k].Rank < best.Rank) best = ordered[k];
            }
            items.Add(best);
            for (int k = i; k <= j; k++)
            {
                var o = ordered[k];
                if (o.Note.End > best.Note.End && o.Rank > best.Rank)
                {
                    items.Add((o.Rank, new RawNote
                    {
                        Pitch = o.Note.Pitch,
                        Start = best.Note.End,          // 主声部结束后才轮到它
                        End = o.Note.End,
                        Velocity = o.Note.Velocity
                    }));
                }
            }
            i = j + 1;
        }

        // 2) 排序后统一压制：低优先级音落在高优先级音持续期间 → 让位（超出部分补尾巴）
        items = items.OrderBy(v => v.Note.Start).ThenBy(v => v.Rank).ThenBy(v => v.Note.Pitch).ToList();
        var result = new List<RawNote>();
        RawNote? lastNote = null;
        int lastRank = int.MaxValue;
        foreach (var c in items)
        {
            if (lastNote != null && c.Note.Start < lastNote.End && c.Rank > lastRank)
            {
                if (c.Note.End > lastNote.End)
                {
                    result.Add(new RawNote
                    {
                        Pitch = c.Note.Pitch,
                        Start = lastNote.End,
                        End = c.Note.End,
                        Velocity = c.Note.Velocity
                    });
                }
                continue;
            }
            result.Add(c.Note);
            lastNote = c.Note;
            lastRank = c.Rank;
        }

        return result.OrderBy(n => n.Start).ToList();
    }

    /// <summary>整体平移旋律，使首音从 0 秒开始（相对时值不变）。</summary>
    public static List<RawNote> TrimLeadingSilence(IReadOnlyList<RawNote> notes)
    {
        if (notes.Count == 0) return new List<RawNote>();
        double first = notes.Min(n => n.Start);
        if (first <= 0.001) return notes.ToList();

        return notes.Select(n => new RawNote
        {
            Pitch = n.Pitch,
            Start = Math.Max(0, n.Start - first),
            End = Math.Max(0, n.End - first),
            Velocity = n.Velocity
        }).ToList();
    }

    /// <summary>单音描述。</summary>
    public static string Describe(MappedNote n, bool withTime)
    {
        string slot = n.OctaveSlot switch
        {
            Slot.Low => "低八度(按左键)",
            Slot.High => "高八度(按右键)",
            _ => "基准八度"
        };
        string keyShow = n.Key == TopKey ? "，" : n.Key.ToString();
        string sharp = n.Sharp ? "+升半音(按中键) " : "";
        string time = withTime ? $"{n.Start:F2}s " : "";
        return $"{time}{Music.NoteName(n.Pitch)}({Music.DegreeName(n.Pitch)}) → 按[{keyShow}] {sharp}{slot}";
    }
}
