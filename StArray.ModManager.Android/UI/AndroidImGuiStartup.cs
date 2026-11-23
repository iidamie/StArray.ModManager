using ImGuiNET;
using StArray.ModManager.Manager;
using StArray.ModManager.UI;

namespace StArray.ModManager.Android.UI;

/// <summary>Prepares CPU font data without blocking an EGL or Vulkan present callback.</summary>
internal sealed unsafe class AndroidImGuiStartup
{
    private Task<nint>? _fontAtlas;
    private nint _context;

    private static nint BuildFontAtlas()
    {
        ImFontAtlas* atlas = ImGuiNative.ImFontAtlas_ImFontAtlas();
        try
        {
            ImGuiFonts.Build(new ImFontAtlasPtr(atlas), prepareRgbaTexture: true);
            return (nint)atlas;
        }
        catch
        {
            ImGuiNative.ImFontAtlas_destroy(atlas);
            throw;
        }
    }

    public bool TryInitializeContext()
    {
        var fontAtlas = _fontAtlas ??= Task.Run(BuildFontAtlas);
        if (!fontAtlas.IsCompleted)
            return false;

        if (_context == 0)
        {
            // The Android renderer and its shared atlas live for the lifetime of the game.
            // Only this render thread creates and selects the ImGui context.
            if (fontAtlas.IsCompletedSuccessfully)
                _context = ImGui.CreateContext(new ImFontAtlasPtr((ImFontAtlas*)fontAtlas.Result));
            else
            {
                Logger.Warn(nameof(AndroidImGuiStartup),
                    $"Using the default font: {fontAtlas.Exception?.GetBaseException().Message}");
                _context = ImGui.CreateContext();
            }

            ImGui.SetCurrentContext(_context);
            if (!fontAtlas.IsCompletedSuccessfully)
                ImGui.GetIO().Fonts.AddFontDefault();
            ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;
        }
        return true;
    }
}
