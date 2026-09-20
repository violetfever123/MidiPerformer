using MidiPerformer.Adapters.Gateways;
using NUnit.Framework;

namespace MidiPerformer.Tests.Perform;

/// <summary>
/// <c>SendInput</c> 那个联合体的布局：64 位下 <c>INPUT</c> 必须是 40 字节，
/// 少一个字段或少一层对齐填充尺寸就变了。
/// 尺寸对不上时 <c>SendInput</c> 按 <c>cbSize</c> 直接拒收，返回值 0，一个事件都不发。
/// </summary>
public class InputSenderLayoutTests
{
    [Test]
    public void INPUT联合体在64位下是40字节()
    {
        Assert.That(Environment.Is64BitProcess, Is.True,
            "本程序只发 win-x64（见 spec 的构建一节），这条断言在别的位宽下没有意义");

        // 读这个属性会触发 InputSender 的静态构造，那里也有一道同样的检查，给发布产物用。
        Assert.That(InputSender.InputStructSize, Is.EqualTo(40));
    }
}
