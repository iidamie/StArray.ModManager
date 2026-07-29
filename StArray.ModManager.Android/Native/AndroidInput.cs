using System.Runtime.InteropServices;

namespace StArray.ModManager.Android.Native;

/// <summary>
/// Android NDK 输入系统 API 的 P/Invoke 封装（<c>libandroid.so</c>，对应 NDK 的 <c>android/input.h</c>）。
/// 只导入读取输入事件所需的最小集合；这些都是 NDK 公开导出，无需额外权限或原生库。
/// </summary>
public static class AndroidInput
{
    private const string Lib = "libandroid.so";

    /// <summary>输入事件类型（<c>AINPUT_EVENT_TYPE_*</c>）</summary>
    public enum EventType
    {
        Key = 1,
        Motion = 2,
    }

    /// <summary>触摸动作（<c>AMOTION_EVENT_ACTION_*</c>）。原始值需先与 <see cref="ActionMask"/> 相与。</summary>
    public enum MotionAction
    {
        Down = 0,
        Up = 1,
        Move = 2,
        Cancel = 3,
        Outside = 4,
        PointerDown = 5,
        PointerUp = 6,
        HoverMove = 7,
        Scroll = 8,
        HoverEnter = 9,
        HoverExit = 10,
        ButtonPress = 11,
        ButtonRelease = 12,
    }

    /// <summary>从 <c>AMotionEvent_getAction</c> 的原始值中取出动作位</summary>
    public const int ActionMask = 0xFF;

    /// <summary>从 <c>AMotionEvent_getAction</c> 的原始值中取出指针索引位</summary>
    public const int ActionPointerIndexMask = 0xFF00;

    /// <summary>指针索引位的右移量</summary>
    public const int ActionPointerIndexShift = 8;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern EventType AInputEvent_getType(nint inputEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AMotionEvent_getAction(nint motionEvent);

    /// <summary>
    /// 事件发生时刻，单位纳秒，时钟源为 <c>CLOCK_MONOTONIC</c>。
    /// 这是输入事件由内核打上的硬件时间戳，早于并独立于渲染帧，
    /// 因此可用来还原「按下的真实时刻」而不受帧率采样影响。
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long AMotionEvent_getEventTime(nint motionEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long AMotionEvent_getDownTime(nint motionEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AMotionEvent_getPointerCount(nint motionEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float AMotionEvent_getX(nint motionEvent, int pointerIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float AMotionEvent_getY(nint motionEvent, int pointerIndex);

    /// <summary>取出主动作（已去除指针索引）</summary>
    public static MotionAction GetMainAction(int rawAction)
    {
        return (MotionAction)(rawAction & ActionMask);
    }

    /// <summary>取出指针索引（多点触控时标识是第几根手指）</summary>
    public static int GetPointerIndex(int rawAction)
    {
        return (rawAction & ActionPointerIndexMask) >> ActionPointerIndexShift;
    }
}
