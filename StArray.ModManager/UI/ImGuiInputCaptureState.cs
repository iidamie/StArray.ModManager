using System.Threading;

namespace StArray.ModManager.UI;

/// <summary>
/// Publishes the screen rectangles of interactive manager windows from the
/// render thread to the Android input thread.
/// </summary>
public static class ImGuiInputCaptureState
{
    private readonly record struct Region(float Left, float Top, float Right, float Bottom)
    {
        public bool Contains(float x, float y) =>
            x >= Left && x < Right && y >= Top && y < Bottom;
    }

    private sealed class Snapshot(Region[] regions)
    {
        public static readonly Snapshot Empty = new([]);
        public Region[] Regions { get; } = regions;
    }

    private static readonly List<Region> s_frameRegions = [];
    private static Snapshot s_snapshot = Snapshot.Empty;

    /// <summary>Starts collecting input-enabled window rectangles for a frame.</summary>
    public static void BeginFrame() => s_frameRegions.Clear();

    /// <summary>Registers an ImGui window rectangle in display coordinates.</summary>
    public static void RegisterWindow(float x, float y, float width, float height)
    {
        if (width <= 0 || height <= 0)
            return;

        s_frameRegions.Add(new Region(x, y, x + width, y + height));
    }

    /// <summary>Atomically publishes this frame's rectangles to the input thread.</summary>
    public static void PublishFrame() =>
        Volatile.Write(ref s_snapshot, new Snapshot([.. s_frameRegions]));

    /// <summary>Removes all interactive overlay regions, for example when hidden.</summary>
    public static void Clear() => Volatile.Write(ref s_snapshot, Snapshot.Empty);

    /// <summary>Returns whether a raw Android touch lies inside an interactive ImGui window.</summary>
    public static bool Contains(float x, float y)
    {
        var snapshot = Volatile.Read(ref s_snapshot);
        foreach (var region in snapshot.Regions)
            if (region.Contains(x, y))
                return true;
        return false;
    }
}
