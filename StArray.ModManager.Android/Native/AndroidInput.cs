using System;
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

    /// <summary>ALooper_prepare 选项</summary>
    [Flags]
    public enum PrepareFlags
    {
        None = 0,
        AllowNonCallbacks = 1 << 0,
    }

    /// <summary>按键动作</summary>
    public enum KeyAction
    {
        Down = 0,
        Up = 1,
        Multiple = 2,
    }

    /// <summary>Meta 键状态</summary>
    [Flags]
    public enum MetaState
    {
        None = 0,
        ShiftOn = 0x01,
        AltOn = 0x02,
        ShiftLeftOn = 0x40,
        ShiftRightOn = 0x80,
        AltLeftOn = 0x10,
        AltRightOn = 0x20,
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

    /// <summary>不透明输入事件句柄</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AInputEvent { }

    /// <summary>不透明输入队列句柄</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AInputQueue { }

    /// <summary>不透明 Looper 句柄</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ALooper { }

    /// <summary>
    /// AInputQueue_attachLooper 回调签名。
    /// 调用方必须保持委托实例的引用，防止被 GC 回收。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int InputQueueCallback(int fd, int events, nint data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint ALooper_prepare(PrepareFlags opts);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void AInputQueue_attachLooper(
        nint queue,
        nint looper,
        int ident,
        InputQueueCallback callback,
        nint data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void AInputQueue_detachLooper(nint queue);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AInputQueue_getEvent(nint queue, out nint outEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void AInputQueue_finishEvent(nint queue, nint inputEvent, int handled);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern EventType AInputEvent_getType(nint inputEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AMotionEvent_getAction(nint motionEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AKeyEvent_getKeyCode(nint inputEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern KeyAction AKeyEvent_getAction(nint inputEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern MetaState AKeyEvent_getMetaState(nint inputEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AKeyEvent_getRepeatCount(nint inputEvent);

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

    /// <summary>取出指针的稳定 ID；它跨越后续 MotionEvent，比索引更适合跟踪多指输入。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AMotionEvent_getPointerId(nint motionEvent, int pointerIndex);

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

    /// <summary>从原生事件句柄取出主动作，并验证事件类型。</summary>
    public static MotionAction GetMainAction(nint motionEvent)
    {
        if (AInputEvent_getType(motionEvent) != EventType.Motion)
            throw new InvalidOperationException("事件不是 Motion 类型");
        return GetMainAction(AMotionEvent_getAction(motionEvent));
    }

    /// <summary>从原生事件句柄取出指针索引。</summary>
    public static int GetPointerIndex(nint motionEvent)
    {
        return GetPointerIndex(AMotionEvent_getAction(motionEvent));
    }
}
