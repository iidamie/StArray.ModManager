using System.Numerics;

namespace StArray.ModManager.UI;

/// <summary>Screen regions where Android may translate a touch drag into ImGui scrolling.</summary>
public static class ImGuiTouchScrollWhitelist
{
    private readonly record struct Region(Vector2 Min, Vector2 Max);

    private static List<Region> s_buildingRegions = new();
    private static List<Region> s_activeRegions = new();
    private static bool s_isCollectingFrame;

    /// <summary>Register a screen-space region during the current ImGui frame.</summary>
    public static void Register(Vector2 min, Vector2 max)
    {
        if (!s_isCollectingFrame
            || !float.IsFinite(min.X) || !float.IsFinite(min.Y)
            || !float.IsFinite(max.X) || !float.IsFinite(max.Y)
            || max.X <= min.X || max.Y <= min.Y)
            return;

        s_buildingRegions.Add(new Region(min, max));
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

    /// <summary>Returns whether a screen-space point is inside a published region.</summary>
    public static bool Contains(float x, float y)
    {
        foreach (Region region in s_activeRegions)
        {
            if (x >= region.Min.X && x < region.Max.X
                && y >= region.Min.Y && y < region.Max.Y)
                return true;
        }

        return false;
    }
}
