using System.Numerics;
using ImGuiNET;

namespace StArray.ModManager.UI;

/// <summary>Screen regions whose current ImGui windows have a real scroll range.</summary>
public static class ImGuiTouchScrollWhitelist
{
    private readonly record struct Region(Vector2 Min, Vector2 Max, int Axes);

    private static List<Region> s_buildingRegions = new();
    private static List<Region> s_activeRegions = new();
    private static bool s_isCollectingFrame;

    /// <summary>Register the current ImGui window when it can scroll on at least one axis.</summary>
    public static void RegisterCurrentWindowIfScrollable()
    {
        if (!s_isCollectingFrame)
            return;

        int axes = 0;
        if (ImGui.GetScrollMaxX() > 0.0f)
            axes |= 1;
        if (ImGui.GetScrollMaxY() > 0.0f)
            axes |= 2;
        if (axes == 0)
            return;

        Vector2 min = ImGui.GetWindowPos();
        Vector2 max = min + ImGui.GetWindowSize();
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y)
            || !float.IsFinite(max.X) || !float.IsFinite(max.Y)
            || max.X <= min.X || max.Y <= min.Y)
            return;

        s_buildingRegions.Add(new Region(min, max, axes));
    }

    /// <summary>Discard registrations left by an incomplete previous UI frame.</summary>
    public static void BeginFrame()
    {
        s_buildingRegions.Clear();
        s_isCollectingFrame = true;
    }

    /// <summary>Publish this frame's regions for input dispatch before the next frame.</summary>
    public static void PublishFrame()
    {
        if (!s_isCollectingFrame)
            return;

        (s_activeRegions, s_buildingRegions) = (s_buildingRegions, s_activeRegions);
        s_buildingRegions.Clear();
        s_isCollectingFrame = false;
    }

    /// <summary>Return scrollable axes registered at a screen-space point.</summary>
    public static int GetAxesAt(float x, float y)
    {
        int axes = 0;
        foreach (Region region in s_activeRegions)
        {
            if (x >= region.Min.X && x < region.Max.X
                && y >= region.Min.Y && y < region.Max.Y)
                axes |= region.Axes;
        }

        return axes;
    }
}
