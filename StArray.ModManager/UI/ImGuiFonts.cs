using System.Diagnostics;
using System.Runtime.InteropServices;
using ImGuiNET;
using StArray.ModManager.Manager;

namespace StArray.ModManager.UI;

/// <summary>Builds the manager font atlas independently of the current ImGui context.</summary>
public static unsafe class ImGuiFonts
{
    // The atlas keeps these pointers in its font configs for future rebuilds.
    private static readonly nint AllUnicode = AllocateRanges(0x0001, 0xFFFF);
    private static readonly nint Icons = AllocateRanges(0xE000, 0xF8FF);

    public static void Build(ImFontAtlasPtr atlas, bool prepareRgbaTexture = false)
    {
        long started = Stopwatch.GetTimestamp();
        const string latinFont = "NotoSans-Regular.ttf";

        if (AddFont(atlas, "NotoSansCJK-Regular.otf", merge: false, AllUnicode))
            AddFont(atlas, latinFont, merge: true, AllUnicode);
        else if (!AddFont(atlas, latinFont, merge: false, AllUnicode))
            atlas.AddFontDefault();

        AddFont(atlas, "NotoSansSymbols2-Regular.ttf", merge: true, AllUnicode);
        AddFont(atlas, "OpenMoji-black-glyf.ttf", merge: true, AllUnicode);
        AddFont(atlas, "fa-solid-900.ttf", merge: true, Icons);

        if (!atlas.Build())
            throw new InvalidOperationException("Could not build the ImGui font atlas.");

        if (prepareRgbaTexture)
        {
            byte* pixels;
            ImGuiNative.ImFontAtlas_GetTexDataAsRGBA32(atlas.NativePtr, &pixels, null, null, null);
        }

        Logger.Info("FontAtlas",
            $"Built {atlas.TexWidth}x{atlas.TexHeight}, {atlas.Fonts.Size} fonts in " +
            $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");
    }

    private static nint AllocateRanges(ushort first, ushort last)
    {
        var ranges = (ushort*)Marshal.AllocHGlobal(sizeof(ushort) * 3);
        ranges[0] = first;
        ranges[1] = last;
        ranges[2] = 0;
        return (nint)ranges;
    }

    private static bool AddFont(ImFontAtlasPtr atlas, string fileName, bool merge, nint glyphRanges)
    {
        using var stream = typeof(ImGuiFonts).Assembly.GetManifestResourceStream(
            "StArray.ModManager.Resources." + fileName);
        if (stream == null)
            return false;

        int length = checked((int)stream.Length);
        void* data = ImGuiNative.igMemAlloc((uint)length);
        if (data == null)
            throw new OutOfMemoryException();

        try
        {
            stream.ReadExactly(new Span<byte>(data, length));
            var config = ImGuiNative.ImFontConfig_ImFontConfig();
            try
            {
                config->MergeMode = merge ? (byte)1 : (byte)0;
                config->FontDataOwnedByAtlas = 1;
                atlas.AddFontFromMemoryTTF((nint)data, length, 16f, config, glyphRanges);
                data = null; // The atlas frees this buffer using the ImGui allocator.
            }
            finally
            {
                ImGuiNative.ImFontConfig_destroy(config);
            }
            return true;
        }
        finally
        {
            if (data != null)
                ImGuiNative.igMemFree(data);
        }
    }
}
