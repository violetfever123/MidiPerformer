# 03: 视觉令牌层

**What to build:** 把 wireframe 里那套视觉落成代码，作为**全程序唯一的外观来源**。窗口一 / 窗口二 / 悬浮窗口之后都照着它长。

准绳是 `docs/wireframe.html`——它不只是布局草图，里面有一套完整的视觉：颜色令牌（明暗两套）、字体、圆角、阴影。这一块就是把那套东西搬进 Avalonia。

- **令牌**：`App/Styles/Tokens.axaml`，与 wireframe 的 `:root` 逐条对应。共 **25 个**（wireframe 里 28 个，其中 `--annot` / `--annot-soft` / `--annot-line` 三个是紫色圆形标注点专用的，属于线框说明层，不进软件）。明暗各一套，走 `ThemeVariant`。
- **控件外观**：`App/Styles/Controls.axaml`——按钮（主 / 普通 / 启动）、输入框、下拉框、滑块、列表、滚动条、标签片。`Avalonia.Themes.Fluent` 保留，它提供控件模板，我们只覆盖长相。
- **自绘取色桥**：卷帘与悬浮层是**代码画的**，不在 XAML 里，拿不到 `DynamicResource`。需要一个能从当前主题取令牌的入口，并在主题切换时跟着变。不做这一步，结果就是控件变浅色、卷帘还是硬编码深色。**这是最容易漏的一条。**
- **字体**：正文 `Segoe UI` / `Microsoft YaHei` 14px；数字用 `Cascadia Mono` + `tabular-nums`（卷帘刻度、BPM、小节号要能对齐）。
- **样板窗口**（dev-only）：把令牌、圆角和控件平铺出来。它是这一块的可见产物，也是日后改样式时的对照页。

**不做：** 自绘窗口边框。窗口用**系统标题栏**，wireframe 里的 `.wintitle`（含右侧三个圆点）不实现——这一处是「几乎一模一样」的例外。

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] 全项目 `.axaml` 与自绘代码里**没有字面颜色值**（令牌是唯一来源）
- [ ] `Tokens.axaml` 的 25 个令牌与 `docs/wireframe.html` 的 `:root` 逐条对应，明暗各一套
- [ ] 令牌只在 `Tokens.axaml` 里定义一次，别处一律引用
- [ ] 切换主题时，XAML 控件与自绘层**同时**变
- [ ] 控件外观齐全：按钮（主 / 普通 / 启动）、输入框、下拉框、滑块、列表、滚动条、标签片
- [ ] 数字位置用等宽 + `tabular-nums`
- [ ] 样板窗口可见，与 wireframe 的对应部分逐块对照
- [ ] 窗口用系统标题栏，没有自绘边框
