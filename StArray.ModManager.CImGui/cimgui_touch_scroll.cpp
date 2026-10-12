#include "imgui/imgui.h"
#include "imgui/imgui_internal.h"
#include "cimgui.h"

CIMGUI_API int ImGuiWindow_GetTouchScrollAxes(ImGuiWindow* window)
{
    // Mirror ImGui's child-to-parent wheel routing for each axis.
    int axes = 0;
    for (int axis = 0; axis < 2; axis++)
    {
        ImGuiWindow* candidate = window;
        while (candidate && (candidate->Flags & ImGuiWindowFlags_ChildWindow))
        {
            const bool has_scrolling = candidate->ScrollMax[axis] != 0.0f;
            const bool inputs_disabled = (candidate->Flags & ImGuiWindowFlags_NoScrollWithMouse)
                && !(candidate->Flags & ImGuiWindowFlags_NoMouseInputs);
            if (has_scrolling && !inputs_disabled)
                break;
            candidate = candidate->ParentWindow;
        }

        if (candidate
            && candidate->ScrollMax[axis] != 0.0f
            && !(candidate->Flags & (ImGuiWindowFlags_NoScrollWithMouse | ImGuiWindowFlags_NoMouseInputs)))
            axes |= 1 << axis;
    }

    return axes;
}
