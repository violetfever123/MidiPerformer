# 04: 真的发出去（最小安全演奏器）

**What to build:** 第一个能把按键真正发到系统里的切片。含：

- `Core/UseCases/Perform/Dispatch/Dispatcher` —— 按时间戳 sleep，最后 1.5ms 改成自旋等待。时间取自 02 建好的 `Core/UseCases/Timeline/SongWalker` 和注入的 `IClock`
- `Core/UseCases/Perform/Safety/Watchdog` —— 曲长 + 5 秒硬超时，到点无条件停止并松开所有按键
- `Adapters/Gateways/InputSender` —— `SendInput` + `KEYEVENTF_SCANCODE` + 扫描码 + 鼠标左右中三键修饰键 + `ReleaseEverything`，**实现 `Ports/Outbound/IEventSink`**
- `Adapters/Gateways/GlobalHotkeys` —— F6 急停，`WH_KEYBOARD_LL` 低层钩子 + `LLKHF_INJECTED` 过滤掉自己发的按键
- `Adapters/Gateways/SystemClock` —— **实现 `Ports/Outbound/IClock`**。不建它就没法往 `Dispatcher` 里注真时钟

以及一个最小的演奏器窗口（选文件 → 选时序档位 → 开始 / 急停）。

窗口二里**常驻一句风险提示**——虚拟输入违反游戏规则、可能导致封号、风险自负。按「开始」之前就该看见它：不是弹窗、不是首次提示、不是折叠在帮助里。

急停和看门狗必须在这一块——没有它们的演奏器不能拿来试。

**验收不依赖游戏。** 游戏还没装好，所以用假时钟 + 记录型 sink 做逻辑验证，用空白文本编辑器做真机冒烟：口琴的键位是 `Z X C V B N M ,`，在记事本里点开始应该能看到 `zxcvbnm,` 依次打出来，间隔与谱面相符。

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] 假时钟 + 记录型 sink 下，事件按时间戳顺序发出，时序误差在容差内
- [ ] 时序参数全部取自 `Core/UseCases/Perform/Repertoire/InputTiming`，没有另写一套数
- [ ] 注入假时钟，看门狗在曲长 + 5 秒处触发停止并释放全部按键
- [ ] F6 触发后，`Z X C V B N M` 逗号与鼠标左右中键**全部**被释放
- [ ] 程序自己发的按键不会再次触发 F6（`LLKHF_INJECTED` 过滤生效）
- [ ] 演奏自然放完后自动停止并释放所有按键
- [ ] 真机冒烟（不依赖游戏）：空白文本编辑器里能看到 `zxcvbnm,` 依次打出，间隔与谱面相符
- [ ] 真机冒烟：按 F6 立刻停止
- [ ] 真机冒烟：急停后没有按键卡在按下状态
- [ ] 游戏内实际效果**未验证**（游戏未装好），已记录到 13
- [ ] 窗口二常驻风险提示（虚拟输入违反游戏规则、可能封号、风险自负），按「开始」之前可见
- [ ] 外观一律取自 **03** 的令牌，本切片不新增颜色值
