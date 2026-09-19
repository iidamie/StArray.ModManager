using ImGuiNET;

namespace StArray.ModManager.Windows.UI;

/// <summary>判断 Win32 输入是否应只交给 ImGui 覆盖层。</summary>
internal static class ImGuiInputCapture
{
    public static bool ShouldCapture(uint message)
    {
        var io = ImGui.GetIO();
        return (IsMouseMessage(message) && io.WantCaptureMouse)
            || (IsKeyboardMessage(message) && io.WantCaptureKeyboard);
    }

    private static bool IsMouseMessage(uint message) => message switch
    {
        0x00A0 or 0x00A1 or 0x00A2 or
        0x0200 or 0x0201 or 0x0202 or 0x0203 or
        0x0204 or 0x0205 or 0x0206 or
        0x0207 or 0x0208 or 0x0209 or 0x020A or
        0x020B or 0x020C or 0x020D or 0x020E or
        0x00FF => true,
        _ => false,
    };

    private static bool IsKeyboardMessage(uint message) => message switch
    {
        0x0100 or 0x0101 or 0x0102 or 0x0103 or
        0x0104 or 0x0105 or 0x0106 or 0x0107 or
        0x010D or 0x010E or 0x010F or 0x0286 or
        0x00FF => true,
        _ => false,
    };
}
