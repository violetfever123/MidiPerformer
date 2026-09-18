# 12: 内置自检与发布产物

**What to build:** exe 开 `PublishTrimmed`，裁剪会改变行为，NUnit 跑的是没裁剪的构建，**证明不了发布产物**。所以照搬 midikey-player 的既有做法：exe 里内置一套自检。

- 环境变量触发，`Program.cs` 第一件事就检查，走这条路时**不建窗口、不注册热键、不碰按键与 MIDI 设备**
- 自检写 PASS / FAIL 报告到文件
- 外面用 `tools/run-selftest.ps1` 驱动，退出码 0 / 1 / 2 / 3
- 自检只覆盖几条事件表冒烟用例（全量在 NUnit 那边）
- 发布单文件自包含 exe，`win-x64`，反射相关的程序集用 `TrimmerRootAssembly` 钉住

**Blocked by:** 04

**Status:** ready-for-agent

- [ ] exe 内置自检，环境变量触发
- [ ] 自检不建窗口、不注册热键、不碰按键与 MIDI 设备
- [ ] `Program.cs` 第一件事就检查自检标志
- [ ] 自检写 PASS / FAIL 报告到文件
- [ ] `tools/run-selftest.ps1` 退出码 0 / 1 / 2 / 3 语义正确
- [ ] 自检覆盖几条事件表冒烟用例
- [ ] 发布单文件自包含 exe，`PublishTrimmed` 开
- [ ] `TrimmerRootAssembly` 钉住 Avalonia 与 DryWetMidi
- [ ] 在**裁剪后的发布产物**上跑通自检
- [ ] 自检报告路径不接受仓库内的路径
