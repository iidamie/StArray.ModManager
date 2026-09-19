using System.Runtime.InteropServices;
using System.Threading;
using ImGuiNET;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Android.UI;

/// <summary>ImGui input handler / 输入处理器 — touch/key hooks + IME control</summary>
public static partial class ImGuiInputHandler
{
    /// <summary>ImGui 上下文就绪后由渲染器设置</summary>
    public static bool IsInitialized { get; set; }
    

    private static bool s_wantTextInputLast;
    private const int StatusOk = 0;
    private const int StatusWouldBlock = -11; // -EAGAIN / WOULD_BLOCK
    private const string SendFinishedSignalSymbol =
        "_ZN7android13InputConsumer18sendFinishedSignalEjb";

    private static readonly object s_finishedSignalLock = new();
    private static SendFinishedSignalDelegate? s_sendFinishedSignal;
    private static bool s_finishedSignalResolutionAttempted;
    private static int s_captureTouchSequence;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SendFinishedSignalDelegate(nint consumer, uint sequence, byte handled);

    /// <summary>
    /// 安装触摸事件和按键事件 Hook
    /// </summary>
    public static void InstallInputHooks()
    {
        if (!IsInitialized) return;
        try
        {
            InstallHooks();
            // IME 字符回调：Java nativeSendChar → C → 此回调 → ImGui
            NativeFunctions.SetOnAcceptCharCallback(codepoint =>
            {
                ImGui.GetIO().AddInputCharacter(codepoint);
            });

            // IME 特殊键回调：Java nativeSendKey → C → 此回调 → ImGui
            NativeFunctions.SetOnAcceptKeyCallback(keyCode =>
            {
                var io = ImGui.GetIO();
                switch (keyCode)
                {
                    case 67:
                        io.AddKeyEvent(ImGuiKey.Backspace, true);
                        io.AddKeyEvent(ImGuiKey.Backspace, false);
                        break; // KEYCODE_DEL
                    case 112:
                        io.AddKeyEvent(ImGuiKey.Delete, true);
                        io.AddKeyEvent(ImGuiKey.Delete, false);
                        break; // KEYCODE_FORWARD_DEL
                    case 66:
                        io.AddKeyEvent(ImGuiKey.Enter, true);
                        io.AddKeyEvent(ImGuiKey.Enter, false);
                        break; // KEYCODE_ENTER
                    case 21:
                        io.AddKeyEvent(ImGuiKey.LeftArrow, true);
                        io.AddKeyEvent(ImGuiKey.LeftArrow, false);
                        break; // KEYCODE_DPAD_LEFT
                    case 22:
                        io.AddKeyEvent(ImGuiKey.RightArrow, true);
                        io.AddKeyEvent(ImGuiKey.RightArrow, false);
                        break; // KEYCODE_DPAD_RIGHT
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(ImGuiInputHandler), ex.ToString());
        }
        IsInitialized = true;
    }

    /// <summary>在 InputConsumer 产出完整事件后送入 ImGui。</summary>
    [NativeHook(
        "libinput.so",
        "_ZN7android13InputConsumer7consumeEPNS_26InputEventFactoryInterfaceEblPjPPNS_10InputEventE",
        Convention = CallingConvention.Cdecl)]
    public unsafe static int OnConsume(
        void* thiz,
        void* factory,
        bool consumeBatches,
        long frameTime,
        uint* outSeq,
        void** outEvent)
    {
        var result = OnConsumeOriginal(
            thiz, factory, consumeBatches, frameTime, outSeq, outEvent);

        if (result != StatusOk || outEvent == null || *outEvent == null)
            return result;

        var inputEvent = new IntPtr(*outEvent);
        if (InputEvents.HasSubscribers)
            InputEvents.RaiseFrom(inputEvent);

        if (IsInitialized)
        {
            ImGuiImplAndroid.HandleInputEvent(inputEvent);

            if (ShouldConsumeForImGui(inputEvent)
                && outSeq != null
                && TryFinishInputEvent((nint)thiz, *outSeq))
            {
                // NativeInputEventReceiver treats WOULD_BLOCK as "no event
                // available".  The event remains valid and has already been
                // acknowledged as handled, so Unity never receives it.
                return StatusWouldBlock;
            }
        }

        return result;
    }

    private static bool ShouldConsumeForImGui(IntPtr inputEvent)
    {
        var io = ImGui.GetIO();
        return AndroidInput.AInputEvent_getType(inputEvent) switch
        {
            AndroidInput.EventType.Key => io.WantCaptureKeyboard,
            AndroidInput.EventType.Motion => ConsumeCapturedTouch(inputEvent, io.WantCaptureMouse),
            _ => false,
        };
    }

    private static bool ConsumeCapturedTouch(IntPtr inputEvent, bool wantCaptureMouse)
    {
        var action = AndroidInput.GetMainAction(AndroidInput.AMotionEvent_getAction(inputEvent));
        switch (action)
        {
            case AndroidInput.MotionAction.Down:
            case AndroidInput.MotionAction.PointerDown:
            {
                Volatile.Write(ref s_captureTouchSequence, wantCaptureMouse ? 1 : 0);
                return wantCaptureMouse;
            }

            case AndroidInput.MotionAction.Up:
            case AndroidInput.MotionAction.PointerUp:
            case AndroidInput.MotionAction.Cancel:
            {
                bool captured = Volatile.Read(ref s_captureTouchSequence) != 0;
                Volatile.Write(ref s_captureTouchSequence, 0);
                return captured;
            }

            default:
                return Volatile.Read(ref s_captureTouchSequence) != 0;
        }
    }

    private static bool TryFinishInputEvent(nint consumer, uint sequence)
    {
        var sendFinishedSignal = GetSendFinishedSignal();
        if (sendFinishedSignal == null)
            return false;

        try
        {
            return sendFinishedSignal(consumer, sequence, handled: 1) == StatusOk;
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(ImGuiInputHandler),
                $"Failed to acknowledge captured input event: {ex.Message}");
            return false;
        }
    }

    private static SendFinishedSignalDelegate? GetSendFinishedSignal()
    {
        lock (s_finishedSignalLock)
        {
            if (s_sendFinishedSignal != null)
                return s_sendFinishedSignal;
            if (s_finishedSignalResolutionAttempted)
                return null;

            s_finishedSignalResolutionAttempted = true;
            var address = HookHelper.GetFunction("libinput.so", SendFinishedSignalSymbol);
            if (address == nint.Zero)
            {
                Logger.Warn(nameof(ImGuiInputHandler),
                    "InputConsumer.sendFinishedSignal is unavailable; forwarding input to the game.");
                return null;
            }

            s_sendFinishedSignal =
                Marshal.GetDelegateForFunctionPointer<SendFinishedSignalDelegate>(address);
            return s_sendFinishedSignal;
        }
    }

    private static JavaClass? s_utilsClass;
    private static nint s_showKeyboardMethod;

    /// <summary>根据 ImGui 文本输入状态切换软键盘</summary>
    public static void UpdateIme()
    {
        if (!IsInitialized) return;
        bool want = ImGui.GetIO().WantTextInput;
        if (want == s_wantTextInputLast) return;
        s_wantTextInputLast = want;

        // 懒加载缓存 Java 类引用
        if (s_utilsClass == null)
        {
            s_utilsClass = new JavaClass("starray.android.modmanager.ModManagerUtils");
            s_showKeyboardMethod = s_utilsClass.GetStaticMethodID("showKeyboard", "(Z)V");
        }

        s_utilsClass.CallStaticVoidMethod1(s_showKeyboardMethod, want ? 1 : 0);
        Logger.Info(nameof(ImGuiInputHandler), $"IME {(want ? "Show" : "Hide")}");
    }
}
