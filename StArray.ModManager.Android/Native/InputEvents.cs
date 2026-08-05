using StArray.ModManager.Manager;

namespace StArray.ModManager.Android.Native;

/// <summary>一次触摸事件的快照。所有字段在广播时即已从原生事件读出，订阅方无需再碰原生指针。</summary>
/// <param name="Action">主动作，已去除指针索引</param>
/// <param name="PointerIndex">指针索引（多点触控时标识第几根手指）</param>
/// <param name="EventTimeNanos">事件发生时刻，纳秒，<c>CLOCK_MONOTONIC</c></param>
/// <param name="X">触点 X 坐标（像素）</param>
/// <param name="Y">触点 Y 坐标（像素）</param>
public readonly record struct TouchEventInfo(
    AndroidInput.MotionAction Action,
    int PointerIndex,
    long EventTimeNanos,
    float X,
    float Y);

/// <summary>
/// 输入事件广播点 —— 把 ModManager 已经装好的 <c>libinput.so</c> Hook 拿到的原始触摸事件
/// 转发给需要硬件时间戳的 Mod。
/// </summary>
/// <remarks>
/// <para>
/// 存在的意义：Dobby 拒绝在同一地址安装第二个 Hook（<c>Interceptor::find</c> 命中即返回 -1），
/// 而 <c>InputConsumer::consume</c> / <c>consumeSamples</c> 已被 ImGui 输入处理占用。
/// Mod 若想拿到触摸事件的内核时间戳，只能复用这里的广播，不能自行 Hook。
/// </para>
/// <para>
/// <b>回调运行在 Android 输入分发线程，不是 Unity 主线程。</b>
/// 订阅方必须足够廉价，且不得访问任何 Unity 对象 —— 只应把数据放进队列，留到主线程处理。
/// </para>
/// </remarks>
public static class InputEvents
{
    private const string LogTag = nameof(InputEvents);

    /// <summary>是否已有订阅者。用于在热路径上先做一次廉价短路，无人订阅时不读原生事件。</summary>
    public static bool HasSubscribers => s_onTouch != null;

    private static Action<TouchEventInfo>? s_onTouch;
    private static bool s_faultLogged;

    /// <summary>
    /// 触摸事件抵达时广播（输入分发线程）。订阅方抛出的异常会被捕获并记录，不会影响其他订阅方或游戏本身。
    /// </summary>
    public static event Action<TouchEventInfo>? OnTouch
    {
        add => s_onTouch += value;
        remove => s_onTouch -= value;
    }


    /// <summary>
    /// 从原生 <c>AInputEvent*</c> 解析并广播。
    /// 由 ImGui 输入 Hook 在调用原函数之后立即调用。
    /// 任何异常都在此吞掉 —— 此处位于原生调用栈上，异常逃逸会直接杀死输入系统。
    /// </summary>
    internal static void RaiseFrom(nint inputEvent)
    {
        Action<TouchEventInfo>? handlers = s_onTouch;
        if (handlers == null || inputEvent == 0)
            return;

        TouchEventInfo info;
        try
        {
            if (AndroidInput.AInputEvent_getType(inputEvent) != AndroidInput.EventType.Motion)
                return;

            int rawAction = AndroidInput.AMotionEvent_getAction(inputEvent);
            int pointerIndex =
                (rawAction & AndroidInput.MotionMask.PointerIndex)
                >> AndroidInput.MotionMask.PointerIndexShift;
            var action = (AndroidInput.MotionAction)
                (rawAction & AndroidInput.MotionMask.Action);
            info = new TouchEventInfo(
                action,
                pointerIndex,
                AndroidInput.AMotionEvent_getEventTime(inputEvent),
                AndroidInput.AMotionEvent_getX(inputEvent, pointerIndex),
                AndroidInput.AMotionEvent_getY(inputEvent, pointerIndex));
        }
        catch (Exception exception)
        {
            LogOnce($"Failed to read native input event: {exception}");
            return;
        }

        // 逐个隔离：某个订阅方抛异常不应影响其余订阅方。
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<TouchEventInfo>)handler)(info);
            }
            catch (Exception exception)
            {
                LogOnce($"Touch event subscriber threw: {exception}");
            }
        }
    }

    /// <summary>
    /// 只记录第一次故障。此处每帧可能被调用多次，持续写日志本身就会造成卡顿。
    /// </summary>
    private static void LogOnce(string message)
    {
        if (s_faultLogged)
            return;
        s_faultLogged = true;
        try
        {
            Logger.Error(LogTag, $"{message} (further occurrences suppressed)");
        }
        catch
        {
            // 日志系统本身不可用时只能放弃，绝不能让异常逃逸回原生调用栈。
        }
    }
}
