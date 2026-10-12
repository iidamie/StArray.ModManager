# Agent Note: 按 ImGui 滚动能力启用 Android 触控平移

Status: implemented

## Problem

按两个已知列表矩形登记触控滚动，无法覆盖其他通过 `BeginChild`、表格或窗口创建的可滚动区域。全局按 `WantCaptureMouse` 启用又会让标题栏、非滚动窗口和普通组件被误判为平移。

## Decision

Android 在触摸起点命中 ImGui 窗口后，查询该窗口及 ImGui 滚轮路由会检查的父窗口链。只有存在非零 `ScrollMax` 且允许鼠标滚轮的轴，手势才可转换成该轴的滚动。手势按移动主轴锁定方向，方向不支持时保持原始鼠标拖动。输入框、滑块、滚动条、窗口移动和窗口缩放继续走直接拖动；鼠标、手写笔和多指输入不会进入触摸平移判定。

ImGui.NET 没有暴露任意 `ImGuiWindow` 的 `ScrollMax`。CImGui 增加一个只读轴查询导出，Android C# 输入处理器调用它；Android JNI、Java 桥接和 Native 输入 Hook 不改。

## Alternatives considered

- 在 UI 中登记两个显式矩形。它对 Mod 列表和日志有效，但不能覆盖其他可滚动子窗口；用户要求所有可滚动区域都支持。
- 对所有 `WantCaptureMouse` 拖动发滚轮。它无法区分滚动窗口和窗口标题栏/普通组件，已产生窗口拖动冲突。
- 在 C# 里按 `ImGuiWindow*` 的字段偏移读取 `ScrollMax`。这样避免改 C++，但把易变的 ImGui 内部结构布局写进托管代码；只读 CImGui 查询更稳定，也只暴露必要状态。

## Consequences

- 任意当前内容可滚动且允许滚轮的 ImGui 窗口都会支持触控平移，不再需要逐页登记。
- 不可滚动窗口、禁用滚轮的窗口以及没有对应滚动轴的滑动方向保持原有输入行为。
- CImGui 增加一个 C++ 查询文件和一个 CMake 源条目；Android JNI/输入 Hook 仍未改动。
- 查询依赖 ImGui 内部的 `ScrollMax` 和当前滚轮路由规则；升级 Dear ImGui 时要重新对照 `FindBestWheelingWindow`，并同步调整导出。

## Verification

Actions 的 Build 工作流检查 Android AAR、Android DLL 和 Windows 构建。CI 不模拟触控；设备上仍需确认任意滚动子窗口可平移、非滚动窗口可拖动。
