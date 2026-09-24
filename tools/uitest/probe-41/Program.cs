// 41 号工单的探针：语料里每个 .mid 各有几条可弹的轨，以及**新旧两种顺序**分别是什么。
//
// 挑 verify-41.ps1 的输入曲子要用：要挑一条「可弹轨 ≥ 2、下标不连续、且旧排序给出的顺序 ≠ 原曲下标升序」的，
// 否则「按原曲下标升序」那条断言是平凡真的，改之前也是绿的，证明不了任何事。
//
// 下面的 `旧打分` 是把 41 号删掉的那份 `TrackRanking.Score` **逐字抄回来**（含两张关键词表），
// 只为在这里当对照组用。它不参与产品代码，也不该被抄回任何地方。
//
// 用法: dotnet run --project .scratch/probe-41

using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Analysis;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Project;

string dir = @"C:\Users\cao17\Desktop\midiplayer\drywetmidi\Resources\MIDI files\Valid\MultiTrack\Middle";

string[] 旋律词 = { "旋律", "主旋律", "主唱", "人声", "女声", "男声", "独奏", "主音",
                    "lead", "melod", "vocal", "vox", "solo", "sing" };
string[] 伴奏词 = { "伴奏", "和声", "和弦", "低音", "吉他", "钢琴伴", "节奏",
                    "bass", "chord", "back", "guitar", "pad", "rhythm", "fx" };

double 旧打分(Track track, Song song)
{
    double s = 0;
    foreach (var kw in 旋律词)
        if (track.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)) { s += 45; break; }
    foreach (var kw in 伴奏词)
        if (track.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)) { s -= 35; break; }

    var notes = track.Notes;
    var mapped = NoteMapper.Map(RepertoireToSeconds.Convert(notes, song.TempoMap), track.Transpose, null);
    s += 30.0 * mapped.InRangeCount / notes.Count;

    if (notes.Count < 8) s -= 20;
    s += Math.Min(notes.Count / 50.0, 8.0);

    double total = song.TotalSeconds;
    if (total > 1)
    {
        double cover = Math.Clamp(song.TempoMap.SecondsAt(track.EndTick) / total, 0, 1);
        s += 60.0 * cover * cover;
        if (cover < 0.25) s -= 25;
    }
    return s;
}

foreach (var path in Directory.GetFiles(dir, "*.mid").OrderBy(p => p))
{
    Song song;
    try { song = MidiReader.Read(path); }
    catch (Exception ex) { Console.WriteLine($"=== {Path.GetFileName(path)} === 读取失败: {ex.Message}"); continue; }

    var 新 = PlayableTracks.Of(song);

    // 旧实现：同一条筛选，但按分数降序、同分按下标升序
    var 旧 = new List<(int Idx, double Score)>();
    for (int i = 0; i < song.Tracks.Count; i++)
        if (PlayableTracks.IsPlayable(song.Tracks[i], song.TempoMap))
            旧.Add((i, 旧打分(song.Tracks[i], song)));
    旧 = 旧.OrderByDescending(x => x.Score).ThenBy(x => x.Idx).ToList();

    var 新序 = 新.Select(x => x.SongTrackIndex).ToList();
    var 旧序 = 旧.Select(x => x.Idx).ToList();
    bool 不同 = !新序.SequenceEqual(旧序);

    Console.WriteLine($"=== {Path.GetFileName(path)} ===");
    Console.WriteLine($"  可弹 {新.Count} 条  新序=[{string.Join(", ", 新序)}]  旧序=[{string.Join(", ", 旧序)}]  {(不同 ? "★ 两种顺序不同" : "（两种顺序相同）")}");
    foreach (var (idx, score) in 旧)
        Console.WriteLine($"     [{idx,2}] 旧分 {score,8:0.00}  名=\"{(idx < song.Tracks.Count ? song.Tracks[idx].Name : "?")}\"");
    Console.WriteLine();
}
