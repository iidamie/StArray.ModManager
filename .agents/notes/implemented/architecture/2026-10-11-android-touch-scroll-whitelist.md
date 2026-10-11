# Agent Note: 限定 Android 触控滚动白名单区域

Status: implemented

## Problem

Android 输入处理器先前会把任意被 ImGui 捕获的拖动转换成滚动手势，导致非列表页面的窗口和组件也被改写。管理器 UI 位于核心程序集，触控输入位于 Android 程序集。

## Decision

核心程序集维护 `ImGuiTouchScrollWhitelist`，以双缓冲保存当前帧登记的屏幕区域。管理器在构建 `ModTable` 的纵向滚动子窗口和 `LogScroll` 子窗口时登记其矩形。Android 渲染器在 UI 构建完成后发布这些区域，输入处理器只在触摸起点落入已发布区域时启动平移。

每帧开始时 Android 清空待登记区域；UI 构建期间登记区域；UI 构建后发布。手势起点只与完整发布的列表区域匹配，并在该触摸周期内固定分类，因此不会读到半帧布局或因手指移出列表而切换行为。只有单指触屏事件可以进入平移判定。鼠标、手写笔、多指触控，以及白名单以外的组件继续使用 ImGui 现有输入行为。滚动条、窗口移动、窗口缩放、滑块和文本输入仍走直接交互。

## Alternatives considered

- 按 `WantCaptureMouse` 或窗口可滚动状态全局启用平移。这样实现简单，但表格子窗口和同一主窗口中的非列表页面难以区分，正是窗口拖动冲突的来源。
- 按窗口名或标签页名白名单。它避免修改 Native 层，但表格的实际滚动目标是内层子窗口，而且主窗口包含多个页面，名称无法精确限定触摸区域。
- 在 Android Native 输入层识别列表区域。它能拿到底层事件，但 UI 本身已经知道 `BeginTable(ScrollY)` 与日志子窗口的矩形；复制布局判断会扩大 Native 改动并重复 UI 状态。

## Consequences

- 当前只有 Mod 列表表格和控制台日志子窗口登记区域；设置页、Mod 设置窗口、弹窗和自定义 Mod 面板不会触发触控平移。
- 新增可滚动列表时，UI 必须在对应的 ImGui 子窗口打开期间调用 `Register`。
- 核心 UI 增加托管注册 API，Android 渲染器负责帧边界，输入处理器消费已发布的矩形；没有 Native 源码改动。

## Verification

Actions 的 Build 工作流覆盖 Android AAR、Android DLL 和 Windows 构建。CI 不模拟触控；最终手感仍需在设备上分别检查白名单列表和白名单外窗口。
