using System.Runtime.CompilerServices;
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
    private static nint s_keyInputLibraryHandle;

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

    /*
    /// <summary>触摸事件 Hook 回调</summary>
    [NativeHook("libinput.so","_ZN7android13InputConsumer14consumeSamplesEPNS_26InputEventFactoryInterfaceERNS0_5BatchEmPjPPNS_10InputEventE")]
    public unsafe static long OnConsumeSamples(void* thiz,void* factory, IntPtr batch,
        ulong count, uint* outSeq, void** outEvent)
    {
        var result = OnConsumeSamplesOriginal(thiz,factory, batch, count, outSeq, outEvent);
        if (IsInitialized && *outEvent != null) ImGuiImplAndroid.HandleInputEvent(new IntPtr(*outEvent));
        return result;
    }
    
    [NativeHook("libinput.so","_ZN7android13InputConsumer7consumeEPNS_26InputEventFactoryInterfaceEblPjPPNS_10InputEventE")]
    public unsafe static long OnConsume(void* thiz, void* factory, bool consumeBatches, ulong frameTime, uint* outSeq, void** outEvent)
    {
        var result = OnConsumeOriginal(thiz, factory, consumeBatches, frameTime, outSeq, outEvent);
        if (IsInitialized && *outEvent != null) ImGuiImplAndroid.HandleInputEvent(new IntPtr(*outEvent));
        return result;
    }*/
    
    [NativeHook("GetInitializeMotionEventAddress")]
    public unsafe static bool OnInitializeMotionEvent(void* @event, void* message)
    {
        var result = OnInitializeMotionEventOriginal(@event, message);
        var x = AndroidInput.AMotionEvent_getX(new(@event), 0);
        var y = AndroidInput.AMotionEvent_getY(new(@event), 0);
        if (InputEvents.HasSubscribers)
            InputEvents.RaiseFrom(new IntPtr(@event));
        ImGuiImplAndroid.HandleInputEvent(new IntPtr(@event));
        return result;
    }

    /// <summary>
    /// 独立的 Android 按键事件广播 Hook。现有触摸 Hook 保持不变；该 Hook 只把原始按键
    /// 交给 InputEvents，避免各 Mod 自己重复接入 libinput。
    /// </summary>
    [NativeHook("GetInitializeKeyEventAddress")]
    public unsafe static void OnInitializeKeyEvent(
        void* consumer,
        void* @event,
        void* message)
    {
        OnInitializeKeyEventOriginal(consumer, @event, message);
        if (InputEvents.HasSubscribers)
            InputEvents.RaiseFrom(new IntPtr(@event));
    }

    private static nint GetInitializeKeyEventAddress()
    {
        var resolver = new NativeFuncResolver("/system/lib64/libinput.so");
        string[] symbols =
        {
            "_ZN7android13InputConsumer18initializeKeyEventEPNS_8KeyEventEPKNS_12InputMessageE",
            "_ZN7android13InputConsumer17initializeKeyEventEPNS_8KeyEventEPKNS_12InputMessageE",
        };

        foreach (string symbol in symbols)
        {
            long rva = resolver.FindSymbolRva(symbol);
            if (rva < 0)
                continue;

            resolver.Load();
            return resolver.GetFuncPtr(rva);
        }

        // Some Android system images strip InputConsumer symbols from the ELF
        // symbol table while dlsym can still resolve the loaded symbol. Use a
        // real dlopen handle here; NativeFuncResolver intentionally returns a
        // mapped base address and that value is not valid for dlsym/dlclose.
        string[] libraries =
        {
            "/system/lib64/libinput.so",
            "libinput.so",
        };
        foreach (string library in libraries)
        {
            nint handle = DL.OpenHandle(
                library,
                DL.RTLDFlags.RTLD_NOW | DL.RTLDFlags.RTLD_NOLOAD);
            if (handle == nint.Zero)
                continue;

            foreach (string symbol in symbols)
            {
                nint address = DL.Symbol(handle, symbol);
                if (address == nint.Zero)
                    continue;

                // Keep the valid handle alive for the lifetime of the process;
                // closing it can crash the Android linker on this device.
                s_keyInputLibraryHandle = handle;
                Logger.Info(
                    nameof(ImGuiInputHandler),
                    $"Resolved initializeKeyEvent through dlsym: {symbol}");
                return address;
            }
        }

        throw new KeyNotFoundException("InputConsumer.initializeKeyEvent was not found.");
    }

    private static nint GetInitializeMotionEventAddress()
    {
        byte?[] sig = NativeFuncResolver.ParseHexPattern(
            "e8 0f 19 fc fd 7b 01 a9 fc 6f 02 a9 fa 67 03 a9 " +
            "f8 5f 04 a9 f6 57 05 a9 f4 4f 06 a9 fd 43 00 91");
        var r = new NativeFuncResolver("/system/lib64/libinput.so");
        long rva = r.FindSymbolRva("_ZN7android13InputConsumer21initializeMotionEventEPNS_11MotionEventEPKNS_12InputMessageE");
        try
        {
            if (rva < 0)
            {
                var text = r.TextBytes;
                long textAddr = r.TextBaseAddress;
                int pos = 0;
                while ((pos = NativeFuncResolver.Search(text, sig, pos)) >= 0)
                {
                    int ctxEnd = Math.Min(pos + 60, text.Length);
                    bool hasMrs = false, hasLdr = false;
                    for (int j = pos + 32; j <= ctxEnd - 4; j += 4)
                    {
                        int inst = BitConverter.ToInt32(text.AsSpan(j));
                        if (!hasMrs && (inst & 0xffffffe0) == 0xd53bd040)
                            hasMrs = true;
                        if (!hasLdr && (inst & 0xffe003e0) == 0xb9400020)
                        {
                            int imm12 = (inst >> 10) & 0xfff;
                            if (imm12 * 4 == 0xc) hasLdr = true;
                        }
                        if (hasMrs && hasLdr) break;
                    }
                    if (hasMrs && hasLdr)
                    {
                        rva = textAddr + pos;
                        if (pos >= 4)
                        {
                            int prev = BitConverter.ToInt32(text.AsSpan(pos - 4));
                            if (prev == unchecked((int)0xd503233f)) rva -= 4;
                        }
                        break;
                    }
                    pos++;
                }
                if (rva < 0) throw new KeyNotFoundException("initializeMotionEvent not found by signature.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(ImGuiInputHandler), ex.ToString());
        }
        r.Load();
        return r.GetFuncPtr(rva);
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
