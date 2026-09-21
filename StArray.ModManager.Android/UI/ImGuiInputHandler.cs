using System.Runtime.InteropServices;
using ImGuiNET;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;
using StArray.ModManager.Native;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Android.UI;

/// <summary>ImGui input handler / 输入处理器 — touch/key hooks + IME control</summary>
public static partial class ImGuiInputHandler
{
    /// <summary>ImGui 上下文就绪后由渲染器设置</summary>
    public static bool IsInitialized { get; set; }
    

    private static bool s_wantTextInputLast;

    /// <summary>
    /// 安装触摸事件和按键事件 Hook
    /// </summary>
    public static void InstallInputHooks()
    {
        if (!IsInitialized) return;
        try
        {
            if (!InstallHooks())
            {
                Logger.Error(nameof(ImGuiInputHandler),
                    "InputConsumer.consumeSamples hook installation failed; " +
                    "touch input is unavailable.");
            }
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

    /// <summary>
    /// 在 InputConsumer.consumeSamples 完成后分发生成的 AInputEvent。
    /// Android 17 的 consume 函数体较大，直接 Hook 它会破坏系统函数状态；
    /// consumeSamples 是同一输入流程中的短函数，且输出参数 ABI 简单稳定。
    /// </summary>
    [NativeHook("GetConsumeSamplesAddress", Convention = CallingConvention.Cdecl)]
    public unsafe static int OnConsumeSamples(
        void* consumer,
        void* factory,
        void* batch,
        ulong count,
        uint* outSeq,
        void** outEvent)
    {
        int result = OnConsumeSamplesOriginal(
            consumer,
            factory,
            batch,
            count,
            outSeq,
            outEvent);

        try
        {
            if (outEvent != null && *outEvent != null)
            {
                nint inputEvent = new(*outEvent);
                DispatchInputEvent(inputEvent);
            }
        }
        catch (Exception ex)
        {
            // Never let a managed exception unwind through the native input
            // stack and terminate the game process.
            Logger.Error(nameof(ImGuiInputHandler),
                $"Input event dispatch failed: {ex}");
        }

        return result;
    }

    /// <summary>
    /// 把原生事件分发给 Mod 和 ImGui。部分 Android 17 厂商 ROM 会把触摸
    /// 的工具类型报告成 Mouse/Stylus；官方 Android backend 随后会跳过
    /// ACTION_DOWN/ACTION_UP 的左键更新，只留下悬停坐标。对触摸动作在
    /// C# 层按动作码补齐鼠标状态，避免依赖工具类型。
    /// </summary>
    private static void DispatchInputEvent(nint inputEvent)
    {
        if (InputEvents.HasSubscribers)
            InputEvents.RaiseFrom(inputEvent);

        if (!IsInitialized)
            return;

        if (AndroidInput.AInputEvent_getType(inputEvent) != AndroidInput.EventType.Motion)
        {
            ImGuiImplAndroid.HandleInputEvent(inputEvent);
            return;
        }

        int rawAction = AndroidInput.AMotionEvent_getAction(inputEvent);
        AndroidInput.MotionAction action = AndroidInput.GetMainAction(rawAction);
        int pointerCount = AndroidInput.AMotionEvent_getPointerCount(inputEvent);
        if (pointerCount <= 0)
            return;

        int pointerIndex = Math.Clamp(
            AndroidInput.GetPointerIndex(rawAction),
            0,
            pointerCount - 1);
        float x = AndroidInput.AMotionEvent_getX(inputEvent, pointerIndex);
        float y = AndroidInput.AMotionEvent_getY(inputEvent, pointerIndex);
        var io = ImGui.GetIO();

        if (action is AndroidInput.MotionAction.Down
            or AndroidInput.MotionAction.Up
            or AndroidInput.MotionAction.Cancel)
        {
            int toolType = AndroidInput.AMotionEvent_getToolType(inputEvent, pointerIndex);
            Logger.Info(nameof(ImGuiInputHandler),
                $"Motion action={action} raw=0x{rawAction:X8} " +
                $"pointer={pointerIndex}/{pointerCount} tool={toolType} " +
                $"x={x:F1} y={y:F1}");
        }

        switch (action)
        {
            case AndroidInput.MotionAction.Down:
                io.AddMousePosEvent(x, y);
                io.AddMouseButtonEvent(0, true);
                break;

            case AndroidInput.MotionAction.Up:
            case AndroidInput.MotionAction.Cancel:
                io.AddMousePosEvent(x, y);
                io.AddMouseButtonEvent(0, false);
                break;

            case AndroidInput.MotionAction.Move:
            case AndroidInput.MotionAction.HoverMove:
            case AndroidInput.MotionAction.PointerDown:
            case AndroidInput.MotionAction.PointerUp:
                io.AddMousePosEvent(x, y);
                break;

            default:
                ImGuiImplAndroid.HandleInputEvent(inputEvent);
                break;
        }
    }

    private const string InputConsumerConsumeSamplesSymbol =
        "_ZN7android13InputConsumer14consumeSamplesEPNS_26InputEventFactoryInterfaceERNS0_5BatchEmPjPPNS_10InputEventE";

    private static nint s_inputLibraryHandle;

    /// <summary>
    /// Resolve InputConsumer.consume without assuming a particular Android
    /// linker namespace. Prefer the normal Dobby resolver, then fall back to
    /// the ELF dynamic symbol table and dlsym.
    /// </summary>
    private static nint GetConsumeSamplesAddress()
    {
        nint address = Dobby.SymbolResolver("libinput.so", InputConsumerConsumeSamplesSymbol);
        if (address != nint.Zero)
        {
            Logger.Info(nameof(ImGuiInputHandler),
                $"Resolved InputConsumer.consumeSamples through Dobby at 0x{address:X}");
            return address;
        }

        try
        {
            var resolver = new NativeFuncResolver("/system/lib64/libinput.so");
            long rva = resolver.FindSymbolRva(InputConsumerConsumeSamplesSymbol);
            nint baseAddress = DL.GetBaseAddress("libinput.so");
            if (rva >= 0 && baseAddress != nint.Zero && rva <= int.MaxValue)
            {
                address = IntPtr.Add(baseAddress, (int)rva);
                Logger.Info(nameof(ImGuiInputHandler),
                    $"Resolved InputConsumer.consumeSamples through ELF at 0x{address:X}");
                return address;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(ImGuiInputHandler),
                $"ELF resolution for InputConsumer.consumeSamples failed: {ex.Message}");
        }

        foreach (string library in new[] { "/system/lib64/libinput.so", "libinput.so" })
        {
            nint handle = DL.OpenHandle(
                library,
                DL.RTLDFlags.RTLD_NOW | DL.RTLDFlags.RTLD_NOLOAD);
            if (handle == nint.Zero)
            {
                handle = DL.OpenHandle(
                    library,
                    DL.RTLDFlags.RTLD_NOW | DL.RTLDFlags.RTLD_LOCAL);
            }

            if (handle == nint.Zero)
                continue;

            address = DL.Symbol(handle, InputConsumerConsumeSamplesSymbol);
            if (address == nint.Zero)
                continue;

            // Do not close this handle: it may be the only owned reference to
            // the library and Dobby must be able to execute the hook later.
            s_inputLibraryHandle = handle;
            Logger.Info(nameof(ImGuiInputHandler),
                $"Resolved InputConsumer.consumeSamples through dlsym at 0x{address:X}");
            return address;
        }

        Logger.Error(nameof(ImGuiInputHandler),
            "InputConsumer.consumeSamples was not found in libinput.so.");
        return nint.Zero;
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
