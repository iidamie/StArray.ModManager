# Agent Note: Discover touch scrolling from the hit ImGui window

Status: implemented

## Problem

[Managed window registration](2026-10-12-managed-imgui-touch-scroll-regions.md) misses windows drawn by mods and other renderer callbacks unless their drawing code registers them. A cursor check also misses ImGui numeric sliders, which normally keep the arrow cursor while dragging. The Android DLL must continue to work with the existing AAR.

## Decision

The Android input handler resolves the window at the press after ImGui has drawn and activated controls. The hit window supplies its actual scroll range and the scroll limits for the gesture's dominant axis. Titles, menu bars, both scrollbar tracks and active controls that claim directions, including numeric slider and drag widgets, retain ImGui pointer handling. A content drag past the threshold cancels its click and applies finger displacement to the chosen window through existing CImGui scroll setters before layout. EGL and Vulkan use the same handler. Mods can keep calling the old registration entry point for binary compatibility.

The managed reader uses the prefix of the bundled Dear ImGui 1.91.6 docking layout on ARM64. It checks the reported version, context pointer and window bounds. Missing existing exports or a mismatched layout disable touch panning. CImGui and the Android AAR build inputs are identical to the previous revision.

## Alternatives considered

- Explicit registration uses supported managed calls and works across ImGui layouts. It requires every mod window and child to register itself and leaves existing mods uncovered.
- A new CImGui export would let the compiler maintain the native structure layout. That requires an updated AAR and previously caused a crash with the installed library.
- Wheel simulation uses a built-in input event. Its routing follows hover and its queue competes with pointer movement. Direct scroll setters keep the gesture attached to the starting window and use the actual finger displacement.

## Consequences

- Scrollable windows and children rendered in the manager's ImGui context, including those drawn directly by mods, gain touch panning without per-window registration.
- Custom widgets that consume pointer motion should claim ImGui navigation directions if they need to take precedence over panning. A non-scrollable child keeps regular pointer handling when it is the hit window.
- Recheck the managed field offsets and existing CImGui exports whenever the bundled Dear ImGui version changes. Device checks cover a mod-created window, both scrollbar tracks, horizontal slider and drag controls, and a normal title-bar drag.
