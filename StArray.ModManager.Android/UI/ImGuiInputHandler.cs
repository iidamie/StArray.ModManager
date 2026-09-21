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
                    "InputConsumer.consume hook installation failed; touch input is unavailable.");
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
    /// 在 Android 输入消费者完成解析后获取完整的 AInputEvent。
    ///
    /// Android 17/厂商版 libinput 已移除 initializeMotionEvent；继续按旧
    /// 特征码计算会得到基址减一，并把 Dobby Hook 到无效地址。consume 是
    /// InputConsumer 的稳定公开动态符号，且其输出事件已经可以交给 ImGui。
    /// </summary>
    [NativeHook("GetConsumeInputEventAddress", Convention = CallingConvention.Cdecl)]
    public unsafe static int OnConsumeInputEvent(
        void* consumer,
        void* factory,
        byte consumeBatches,
        long frameTime,
        uint* outSeq,
        void** outEvent)
    {
        int result = OnConsumeInputEventOriginal(
            consumer,
            factory,
            consumeBatches,
            frameTime,
            outSeq,
            outEvent);

        if (outEvent != null && *outEvent != null)
        {
            IntPtr inputEvent = new(*outEvent);
            if (InputEvents.HasSubscribers)
                InputEvents.RaiseFrom(inputEvent);
            if (IsInitialized)
                ImGuiImplAndroid.HandleInputEvent(inputEvent);
        }

        return result;
    }

    private const string InputConsumerConsumeSymbol =
        "_ZN7android13InputConsumer7consumeEPNS_26InputEventFactoryInterfaceEblPjPPNS_10InputEventE";

    private static nint s_inputLibraryHandle;

    /// <summary>
    /// Resolve InputConsumer.consume without assuming a particular Android
    /// linker namespace. Prefer the normal Dobby resolver, then fall back to
    /// the ELF dynamic symbol table and dlsym.
    /// </summary>
    private static nint GetConsumeInputEventAddress()
    {
        nint address = Dobby.SymbolResolver("libinput.so", InputConsumerConsumeSymbol);
        if (address != nint.Zero)
        {
            Logger.Info(nameof(ImGuiInputHandler),
                $"Resolved InputConsumer.consume through Dobby at 0x{address:X}");
            return address;
        }

        try
        {
            var resolver = new NativeFuncResolver("/system/lib64/libinput.so");
            long rva = resolver.FindSymbolRva(InputConsumerConsumeSymbol);
            nint baseAddress = DL.GetBaseAddress("libinput.so");
            if (rva >= 0 && baseAddress != nint.Zero && rva <= int.MaxValue)
            {
                address = IntPtr.Add(baseAddress, (int)rva);
                Logger.Info(nameof(ImGuiInputHandler),
                    $"Resolved InputConsumer.consume through ELF at 0x{address:X}");
                return address;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(ImGuiInputHandler),
                $"ELF resolution for InputConsumer.consume failed: {ex.Message}");
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

            address = DL.Symbol(handle, InputConsumerConsumeSymbol);
            if (address == nint.Zero)
                continue;

            // Do not close this handle: it may be the only owned reference to
            // the library and Dobby must be able to execute the hook later.
            s_inputLibraryHandle = handle;
            Logger.Info(nameof(ImGuiInputHandler),
                $"Resolved InputConsumer.consume through dlsym at 0x{address:X}");
            return address;
        }

        Logger.Error(nameof(ImGuiInputHandler),
            "InputConsumer.consume was not found in libinput.so.");
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
