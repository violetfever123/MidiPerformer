namespace MidiPerformer.Core.Model;

/// <summary>
/// 指向某一份 <see cref="Song"/> 里某个音符的坐标：第几条轨 + 那个音的身份（<see cref="Note.Id"/>）。
///
/// 按身份寻址而不是按下标：音符数组按起始 tick 排过序，一次编辑就可能重排，下标随之作废，身份不受影响。
/// 轨那一半仍是下标，所以删掉一整条轨之后这份坐标会指到别的轨上。
/// </summary>
/// <param name="Track"><see cref="Song.Tracks"/> 里的下标。</param>
/// <param name="Id">该音的身份（<see cref="Note.Id"/>）。</param>
public readonly record struct NoteRef(int Track, NoteId Id);
