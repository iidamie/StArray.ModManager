# Agent Note: Keep Android touch-scroll detection in managed UI

Status: implemented

## Problem

The dynamic `ScrollMax` query added a custom CImGui export. The Android managed DLL then required a host AAR built with that export; the uploaded log shows `EntryPointNotFoundException` when the app loaded an older `cimgui.so`, followed by an ImGui frame assertion. The user requires the AAR and Native layer to remain unchanged.

## Decision

The C# UI registers each framework-owned window while it is current and has a real scroll range, using `ImGui.GetScrollMaxX/Y()` and its screen bounds. Android publishes these regions once per frame and enables touch panning only when a gesture starts inside a registered region. Keep the existing guards for sliders, text inputs, scrollbar thumbs, window movement, and resizing.

Register the Mod table, log child, main window, and scrollable mod-settings window. A mod that draws its own scrollable child can call the public `ImGuiTouchScrollWhitelist.RegisterCurrentWindowIfScrollable()` while that child is current.

## Alternatives considered

- Keep the CImGui export and rebuild the AAR. It automatically detects arbitrary ImGui windows and parent scroll routing, but changes `libcimgui.so` and breaks when the managed DLL is paired with an older host AAR.
- Read `ImGuiWindow.ScrollMax` by field offset from C#. It avoids an AAR change and supports arbitrary windows, but depends on a private layout that can change with Dear ImGui versions.
- Turn every captured touch drag into a wheel event. It requires no window registration, but also changes input on non-scrollable windows and controls.

## Consequences

- The CImGui sources and Android AAR inputs return to their pre-export state; this feature changes only managed UI and input code.
- Current framework-owned scrollable windows are detected from their actual per-axis scroll ranges, so non-scrollable pages keep their original touch behavior.
- Each new framework-owned scrollable window must register itself. Custom mod child windows need to call the public registration method while active; omitting it leaves that child on the original input path.

## Verification

The Android AAR from Actions run `38269738802` contains the custom export, confirmed with `nm -D`; it is the modified artifact that this change removes. Actions run `38271881889` passes Android AAR, Android DLL, and Windows jobs. Its AAR is byte-identical to baseline run `38151564912` (SHA-256 `fbc3c9e5ece3de4d31e5d7abb1d81ab3e959924bdf958757ebd612e10e76f95c`). CI does not simulate touch, so device behavior still requires a device check.
