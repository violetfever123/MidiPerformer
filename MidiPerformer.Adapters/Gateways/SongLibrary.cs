using System.Text;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 曲库：硬盘上一个平铺的目录，一首曲子一个文件，**文件名就是曲名**。
///
/// **纯目录操作，不认识 JSON。** 它收发的是字符串 —— 里面装的是 .mproj 的 JSON，
/// 但那件事归 <c>Core/UseCases/Project/SongProject</c>：曲库只管「有哪些名字、
/// 名字对应哪个文件、把这个名字换掉、把这个名字删掉」。所以它不是 Controller 的料，
/// 也不需要端口（见 spec「端口与网关是两样东西」：网关是默认的，端口是挣来的）。
///
/// 目录由构造参数注入（spec：路径一定从组装点来），默认的那个是 exe 旁边的 <c>.\songs\</c>，
/// 由组装点自己拼好传进来 —— 这个类不猜自己在哪，测试也就塞得进临时目录。
///
/// 除非另有说明，**名字一律先消毒**（见 <see cref="Sanitize"/>）：名字从用户手里来
/// （导入时命名、改名），从列表里来的一定已经干净，消毒对它是个空操作。
/// 名字在曲库里不存在时抛中文 <see cref="InvalidDataException"/> —— 静默成功会掩盖传错的名字，
/// 而「删不掉的那首」比一句错话难查得多。
/// </summary>
public sealed class SongLibrary
{
    /// <summary>曲库文件的后缀。**文件名去掉它就是曲名** —— 改名就是改文件名。</summary>
    public const string Extension = ".mproj";

    /// <summary>
    /// Windows 文件名里不能出现的字符。冒号也在里头：它同时是「C:」那种盘符分隔符，
    /// 放进去会让一个曲名变成一条路径。
    /// </summary>
    private static readonly char[] InvalidNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>
    /// Windows 保留的设备名。这些名字（不带扩展名时）会被系统当成设备，"CON.mproj"
    /// 根本建不出来 —— 与其让用户看到一句系统报错，不如在消毒这一步就说明白。
    /// </summary>
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        // 这两个漏了不会「建不出来」，是**建得出来但用不了**：它们也是控制台设备名，
        // 少了名字里那个 $ 就认不出来，于是存的时候才炸一句系统错误。
        "CONIN$", "CONOUT$"
    };

    private readonly string _directory;

    /// <param name="directory">曲库目录。**一定由组装点注入**（默认是 exe 旁边的 .\songs\）。</param>
    public SongLibrary(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("曲库目录是空的：组装点得给一个目录。");

        // 存全路径：界面上要告诉用户「导入的曲子存进这个文件夹」，相对路径说不清是哪儿
        _directory = Path.GetFullPath(directory);
    }

    /// <summary>
    /// 曲库目录（全路径）。
    ///
    /// 注意：这个属性叫 <c>Directory</c>，所以类里面要用 <see cref="System.IO.Directory"/> 时
    /// 必须写全名 —— 简单名会被这个属性抢先绑掉。
    /// </summary>
    public string Directory => _directory;

    /// <summary>
    /// 曲库里所有曲名，按名字排序。
    ///
    /// 目录不存在就是空的 —— 装完还没导入过任何曲子的机器上，<c>.\songs\</c> 本来就还不存在，
    /// 那不是错误。只认 .mproj，别的文件（用户自己丢进来的、别的程序留下的）不进列表。
    /// </summary>
    public IReadOnlyList<string> Names()
    {
        var info = new DirectoryInfo(_directory);
        if (!info.Exists) return Array.Empty<string>();

        return info.EnumerateFiles()
            .Where(f => f.Extension.Equals(Extension, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            // 按当前区域排序：中文曲名按拼音走，和用户在资源管理器里看到的一样。
            // 序号（Ordinal）是按码点排的，中文看着就是随机顺序。
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToArray();
    }

    /// <summary>曲库里有叫这个名字的吗。</summary>
    public bool Contains(string name) => File.Exists(PathOf(name));

    /// <summary>曲名 → 文件全路径（先消毒）。</summary>
    public string PathOf(string name) => Path.Combine(_directory, Sanitize(name) + Extension);

    /// <summary>读一首曲子的正文（.mproj 的 JSON 文本）。原样读出，一个字都不动。</summary>
    /// <exception cref="InvalidDataException">曲库里没有这个名字，或者文件读不动。</exception>
    public string Read(string name)
    {
        string safe = Sanitize(name);
        string path = PathOf(safe);

        if (!File.Exists(path)) throw new InvalidDataException($"曲库里没有叫「{safe}」的曲子。");

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"曲库里的「{safe}」读不出来：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 把一首曲子的正文写进曲库。**目录不存在就建出来**（第一次导入时它还不存在）。
    ///
    /// 撞名**直接覆盖** —— 这不是疏忽，正是「保存」：同一首曲子改完再存一次，
    /// 走的就是「写回同一个名字」。要保住旧的就得先改个名（存成另一首）。
    /// </summary>
    public void Write(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string safe = Sanitize(name);
        string path = PathOf(safe);

        // 先建目录再落盘。和 SaveProject 一样是「一次写完」，
        // 半截文件会被当成一份坏工程，比没有文件更坏。
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            File.WriteAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"「{safe}」存不进曲库（{path}）：{ex.Message}", ex);
        }
    }

    /// <summary>从曲库里删掉一首曲子（连文件一起删）。</summary>
    /// <exception cref="InvalidDataException">曲库里没有这个名字。</exception>
    public void Delete(string name)
    {
        string safe = Sanitize(name);
        string path = PathOf(safe);

        if (!File.Exists(path)) throw new InvalidDataException($"曲库里没有叫「{safe}」的曲子，删不掉。");

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"「{safe}」删不掉：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 改名 —— 就是把文件换个名字，内容一个字节都不碰。
    ///
    /// 撞名**抛错，不覆盖**：和 <see cref="Write"/> 相反。写是「保存这首」（同名就是它自己），
    /// 改名是「把 A 叫成 B」—— B 已经有人叫了，动手就等于把 B 那首悄悄删了。
    /// 大小写不同的那个名字（Windows 上不分大小写）不算撞名，是同一首改了个写法。
    /// </summary>
    /// <exception cref="InvalidDataException">旧名字不在曲库里，或者新名字已经有人用了。</exception>
    public void Rename(string oldName, string newName)
    {
        string from = Sanitize(oldName);
        string to = Sanitize(newName);
        if (string.Equals(from, to, StringComparison.Ordinal)) return;   // 名字没变，什么都不用做

        string fromPath = PathOf(from);
        if (!File.Exists(fromPath)) throw new InvalidDataException($"曲库里没有叫「{from}」的曲子，改不了名。");

        string toPath = PathOf(to);
        bool sameNameDifferentCase = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        if (!sameNameDifferentCase && File.Exists(toPath))
            throw new InvalidDataException($"曲库里已经有一首叫「{to}」的，换个名字。");

        try
        {
            File.Move(fromPath, toPath, overwrite: sameNameDifferentCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"「{from}」改名叫「{to}」没改成：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 曲名消毒：把用户输的名字变成能当文件名的那个部分。
    ///
    /// 去掉 Windows 文件名里的非法字符、首尾空白、**结尾的点和空格**（后两者 Windows 会无声吞掉，
    /// 不自己去掉的话，写下去的名字和读回来的对不上）。
    ///
    /// 消毒完什么都不剩（空名字、全是非法字符、系统保留的设备名）就抛中文
    /// <see cref="InvalidDataException"/> —— 这种名字没有一个能落盘的合理写法，
    /// 与其猜一个不如让调用方重新起。
    /// </summary>
    /// <exception cref="InvalidDataException">名字空、全是非法字符、或者是保留设备名。</exception>
    public static string Sanitize(string? name)
    {
        string trimmed = (name ?? "").Trim();

        var builder = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
        {
            if (!char.IsControl(c) && Array.IndexOf(InvalidNameChars, c) < 0) builder.Append(c);
        }

        // 去非法字符可能又露出来一个新的结尾点/空格（"a. /b" → "a. b" 不会，但 "a. " → "a."），所以再收一次
        string cleaned = builder.ToString().Trim().TrimEnd('.', ' ').Trim();

        if (cleaned.Length == 0)
            throw new InvalidDataException("曲名是空的（或者只剩下文件名里不能用的字符），换一个。");

        if (IsReservedDeviceName(cleaned))
            throw new InvalidDataException($"「{cleaned}」是 Windows 保留的设备名，不能当曲名，换一个。");

        return cleaned;
    }

    /// <summary>这个名字能当曲名吗。等价于「<see cref="Sanitize"/> 会不会抛」，给界面灰按钮用。</summary>
    public static bool IsUsableName(string? name)
    {
        try
        {
            Sanitize(name);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>保留设备名按**第一个点之前**那截判：Windows 就是这么认的，"CON.mproj" 一样建不出来。</summary>
    private static bool IsReservedDeviceName(string name)
    {
        int dot = name.IndexOf('.');
        string stem = dot >= 0 ? name[..dot] : name;
        return ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}
