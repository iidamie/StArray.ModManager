using StArray.ModManager.Manager;

namespace StArray.ModManager.Android.Native;

/// <summary>一次完整触摸事件的快照。所有字段在广播时即已从原生事件读出，订阅方无需再碰原生指针。</summary>
/// <param name="Action">主动作，已去除指针索引</param>
/// <param name="PointerIndex">指针索引（多点触控时标识第几根手指）</param>
/// <param name="PointerId">稳定的指针 ID；比 PointerIndex 更适合跨事件跟踪手指</param>
/// <param name="EventTimeNanos">事件发生时刻，纳秒，<c>CLOCK_MONOTONIC</c></param>
/// <param name="X">触点 X 坐标（像素）</param>
/// <param name="Y">触点 Y 坐标（像素）</param>
public readonly record struct TouchEventInfo(
    AndroidInput.MotionAction Action,
    int PointerIndex,
    int PointerId,
    long EventTimeNanos,
    float X,
    float Y);

/// <summary>只包含异步输入所需字段的原始触摸快照。</summary>
/// <remarks>
/// 该事件不读取坐标，且只广播 Down/Up/Cancel。高 KPS 时订阅方可避免为每个 Move
/// 事件创建完整快照或触发额外的原生坐标读取。
/// </remarks>
public readonly record struct TouchTimestampInfo(
    AndroidInput.MotionAction Action,
    int PointerId,
    long EventTimeNanos);

/// <summary>
/// 输入事件广播点 —— 把 ModManager 已经装好的 <c>libinput.so</c> Hook 拿到的原始触摸事件
/// 转发给需要硬件时间戳的 Mod。
/// </summary>
/// <remarks>
/// <para>
/// 存在的意义：Dobby 拒绝在同一地址安装第二个 Hook（<c>Interceptor::find</c> 命中即返回 -1），
/// 而 <c>InputConsumer::consume</c> 已被 ImGui 输入处理占用。
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

    private static Action<TouchEventInfo>? s_onTouch;
    private static Action<TouchTimestampInfo>? s_onTouchTimestamp;
    private static int s_touchSubscriberCount;
    private static int s_touchTimestampSubscriberCount;
    private static bool s_faultLogged;

    /// <summary>是否已有订阅者。用于在热路径上先做一次廉价短路，无人订阅时不读原生事件。</summary>
    public static bool HasSubscribers =>
        Volatile.Read(ref s_touchSubscriberCount) > 0
        || Volatile.Read(ref s_touchTimestampSubscriberCount) > 0;

    /// <summary>
    /// 完整触摸事件广播（输入分发线程）。保留坐标和 Move 事件，供需要完整手势信息的 Mod 使用。
    /// </summary>
    public static event Action<TouchEventInfo>? OnTouch
    {
        add
        {
            s_onTouch += value;
            Interlocked.Increment(ref s_touchSubscriberCount);
        }
        remove
        {
            s_onTouch -= value;
            Interlocked.Decrement(ref s_touchSubscriberCount);
        }
    }

    /// <summary>
    /// 原始触摸时间戳快速通道。只广播 Down、PointerDown、Up、PointerUp 和 Cancel，且不读取坐标。
    /// </summary>
    public static event Action<TouchTimestampInfo>? OnTouchTimestamp
    {
        add
        {
            s_onTouchTimestamp += value;
            Interlocked.Increment(ref s_touchTimestampSubscriberCount);
        }
        remove
        {
            s_onTouchTimestamp -= value;
            Interlocked.Decrement(ref s_touchTimestampSubscriberCount);
        }
    }

    /// <summary>
    /// 从原生 <c>AInputEvent*</c> 解析并广播。
    /// 由 ImGui 输入 Hook 在调用原函数之后立即调用。
    /// 任何异常都在此吞掉 —— 此处位于原生调用栈上，异常逃逸会直接杀死输入系统。
    /// </summary>
    internal static void RaiseFrom(nint inputEvent)
    {
        Action<TouchEventInfo>? handlers = s_onTouch;
        Action<TouchTimestampInfo>? timestampHandlers = s_onTouchTimestamp;
        if (handlers == null && timestampHandlers == null || inputEvent == 0)
            return;

        try
        {
            if (AndroidInput.AInputEvent_getType(inputEvent) != AndroidInput.EventType.Motion)
                return;

            int rawAction = AndroidInput.AMotionEvent_getAction(inputEvent);
            AndroidInput.MotionAction action = AndroidInput.GetMainAction(rawAction);
            int pointerIndex = AndroidInput.GetPointerIndex(rawAction);
            bool timestampAction = action is AndroidInput.MotionAction.Down
                or AndroidInput.MotionAction.PointerDown
                or AndroidInput.MotionAction.Up
                or AndroidInput.MotionAction.PointerUp
                or AndroidInput.MotionAction.Cancel;

            if (timestampHandlers != null && timestampAction)
            {
                long eventTimeNanos = AndroidInput.AMotionEvent_getEventTime(inputEvent);
                int pointerId = action == AndroidInput.MotionAction.Cancel
                    ? -1
                    : AndroidInput.AMotionEvent_getPointerId(inputEvent, pointerIndex);
                DispatchTimestampHandlers(
                    timestampHandlers,
                    new TouchTimestampInfo(action, pointerId, eventTimeNanos));
            }

            if (handlers == null)
                return;

            TouchEventInfo info = new(
                action,
                pointerIndex,
                action == AndroidInput.MotionAction.Cancel
                    ? -1
                    : AndroidInput.AMotionEvent_getPointerId(inputEvent, pointerIndex),
                AndroidInput.AMotionEvent_getEventTime(inputEvent),
                AndroidInput.AMotionEvent_getX(inputEvent, pointerIndex),
                AndroidInput.AMotionEvent_getY(inputEvent, pointerIndex));
            DispatchTouchHandlers(handlers, info);
        }
        catch (Exception exception)
        {
            LogOnce($"Failed to read native input event: {exception}");
        }
    }

    private static void DispatchTimestampHandlers(
        Action<TouchTimestampInfo> handlers,
        TouchTimestampInfo info)
    {
        if (Volatile.Read(ref s_touchTimestampSubscriberCount) == 1)
        {
            try
            {
                handlers(info);
            }
            catch (Exception exception)
            {
                LogOnce($"Touch timestamp subscriber threw: {exception}");
            }
            return;
        }

        // 逐个隔离：某个订阅方抛异常不应影响其余订阅方。
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<TouchTimestampInfo>)handler)(info);
            }
            catch (Exception exception)
            {
                LogOnce($"Touch timestamp subscriber threw: {exception}");
            }
        }
    }

    private static void DispatchTouchHandlers(
        Action<TouchEventInfo> handlers,
        TouchEventInfo info)
    {
        if (Volatile.Read(ref s_touchSubscriberCount) == 1)
        {
            try
            {
                handlers(info);
            }
            catch (Exception exception)
            {
                LogOnce($"Touch event subscriber threw: {exception}");
            }
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

    /// <summary>只记录第一次故障。此处每帧可能被调用多次，持续写日志本身就会造成卡顿。</summary>
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
