using ImGuiNET;

namespace StArray.ModManager.Windows.UI;

/// <summary>
/// Decides whether a Win32 message belongs to the ImGui overlay instead of the
/// game window underneath it.
/// </summary>
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
        0x00A0 or 0x00A1 or 0x00A2 or // WM_NCMOUSEMOVE / non-client buttons
        0x0200 or 0x0201 or 0x0202 or 0x0203 or // WM_MOUSEMOVE / left
        0x0204 or 0x0205 or 0x0206 or // right / middle
        0x0207 or 0x0208 or 0x0209 or 0x020A or // X buttons / wheel
        0x020B or 0x020C or 0x020D or 0x020E or // X buttons / horizontal wheel
        0x00FF => true, // WM_INPUT (raw mouse input)
        _ => false,
    };

    private static bool IsKeyboardMessage(uint message) => message switch
    {
        0x0100 or 0x0101 or 0x0102 or 0x0103 or // key / char / dead-char
        0x0104 or 0x0105 or 0x0106 or 0x0107 or // system key / char
        0x010D or 0x010E or 0x010F or // IME composition
        0x0286 or // WM_IME_CHAR
        0x00FF => true, // WM_INPUT (raw keyboard input)
        _ => false,
    };
}
