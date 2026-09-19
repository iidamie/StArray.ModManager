// DX11 Present hook — cimgui-based ImGui overlay
#include "hook_common.h"
#include "cimgui.h"
#include "kiero.hpp"
#include "kiero_d3d11.hpp"
#include <d3dcompiler.h>

typedef HRESULT(APIENTRY *PresentFn)(IDXGISwapChain *, UINT, UINT);

namespace DX11Hook
{
    IDXGISwapChain *SwapChain = nullptr;
    ID3D11DeviceContext *DeviceContext = nullptr;

    static PresentFn OriginalPresent = nullptr;
    static bool Initialised = false;
    static bool isIl2Cpp;

    static bool IsMouseMessage(UINT message)
    {
        switch (message)
        {
        case WM_NCMOUSEMOVE:
        case WM_NCLBUTTONDOWN: case WM_NCLBUTTONUP: case WM_NCLBUTTONDBLCLK:
        case WM_NCRBUTTONDOWN: case WM_NCRBUTTONUP: case WM_NCRBUTTONDBLCLK:
        case WM_NCMBUTTONDOWN: case WM_NCMBUTTONUP: case WM_NCMBUTTONDBLCLK:
        case WM_MOUSEMOVE: case WM_MOUSELEAVE:
        case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_LBUTTONDBLCLK:
        case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_RBUTTONDBLCLK:
        case WM_MBUTTONDOWN: case WM_MBUTTONUP: case WM_MBUTTONDBLCLK:
        case WM_XBUTTONDOWN: case WM_XBUTTONUP: case WM_XBUTTONDBLCLK:
        case WM_MOUSEWHEEL: case WM_MOUSEHWHEEL:
        case WM_INPUT:
            return true;
        default:
            return false;
        }
    }

    static bool IsKeyboardMessage(UINT message)
    {
        switch (message)
        {
        case WM_KEYDOWN: case WM_KEYUP: case WM_SYSKEYDOWN: case WM_SYSKEYUP:
        case WM_CHAR: case WM_DEADCHAR: case WM_SYSCHAR: case WM_SYSDEADCHAR:
        case WM_IME_STARTCOMPOSITION: case WM_IME_ENDCOMPOSITION:
        case WM_IME_COMPOSITION: case WM_IME_CHAR:
        case WM_INPUT:
            return true;
        default:
            return false;
        }
    }

    static bool ImGuiOwnsMessage(UINT message)
    {
        const ImGuiIO *io = igGetIO();
        return (IsMouseMessage(message) && io->WantCaptureMouse)
            || (IsKeyboardMessage(message) && io->WantCaptureKeyboard);
    }

    LRESULT APIENTRY HookWndProc(HWND h, UINT m, WPARAM w, LPARAM l)
    {
        // Always feed the event to ImGui first. Its capture flags describe
        // whether the same event must be hidden from the game underneath.
        if (ImGui_ImplWin32_WndProcHandler(h, m, w, l))
            return true;
        if (ImGuiOwnsMessage(m))
            return 0;

        if (m == WM_SIZE && w != SIZE_MINIMIZED && SwapChain)
        {
            SwapChain->ResizeBuffers(0, LOWORD(l), HIWORD(l), DXGI_FORMAT_UNKNOWN, 0);

            ID3D11Device *device = nullptr;
            if (SUCCEEDED(SwapChain->GetDevice(__uuidof(ID3D11Device), (void **)&device)))
            {
                ID3D11DeviceContext *context = nullptr;
                device->GetImmediateContext(&context);
                if (context)
                {
                    D3D11_VIEWPORT vp = {};
                    vp.Width = (FLOAT)LOWORD(l);
                    vp.Height = (FLOAT)HIWORD(l);
                    vp.MinDepth = 0.0f;
                    vp.MaxDepth = 1.0f;
                    context->RSSetViewports(1, &vp);
                    context->Release();
                }
                device->Release();
            }
            // Fall through to let the game handle WM_SIZE too
        }

        return CallWindowProcW(g_OriginalWndProc, h, m, w, l);
    }

    ID3D11RenderTargetView *MainRTV = nullptr;
    ID3D11Device *CachedDevice = nullptr;

    ID3D11RenderTargetView *gameRTV;
    HRESULT HookPresent(IDXGISwapChain *swapChain, UINT syncInterval, UINT flags)
    {
        if (!Initialised)
        {
            DXGI_SWAP_CHAIN_DESC desc;
            swapChain->GetDesc(&desc);
            g_GameWindow = desc.OutputWindow;

            ID3D11Device *device = nullptr;
            swapChain->GetDevice(__uuidof(ID3D11Device), (void **)&device);
            device->GetImmediateContext(&DeviceContext);
            CachedDevice = device;
            if (imgui_callbacks.init_callback)
                imgui_callbacks.init_callback();

            if (ImGui_ImplWin32_Init(g_GameWindow) && ImGui_ImplDX11_Init(device, DeviceContext))
            {
                SwapChain = swapChain;
                g_OriginalWndProc = (WNDPROC)SetWindowLongPtrW(g_GameWindow,
                                                               GWLP_WNDPROC, (LONG_PTR)HookWndProc);
                isIl2Cpp = IsIl2Cpp();
                Initialised = true;
                ImGui_Initialised = true;
                DEBUG_LOG("DX11Hook: ImGui initialised, hwnd=%p", g_GameWindow);
            }
        }

        if (Initialised)
        {
            // Rebuild RTV each frame
            if (isIl2Cpp)
            {
                // TODO: impl resize
                if (MainRTV)
                {
                    MainRTV->Release();
                    MainRTV = nullptr;
                }
                ID3D11Texture2D *bb = nullptr;
                swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void **)&bb);
                if (bb)
                {
                    CachedDevice->CreateRenderTargetView(bb, nullptr, &MainRTV);
                    bb->Release();
                }
                DeviceContext->OMSetRenderTargets(1, &MainRTV, nullptr);
            }
            ImGui_ImplDX11_NewFrame();
            ImGui_ImplWin32_NewFrame();

            if (imgui_callbacks.render_callback)
                imgui_callbacks.render_callback();
            ImGui_ImplDX11_RenderDrawData(igGetDrawData());
        }

        return OriginalPresent(swapChain, syncInterval, flags);
    }

    bool InstallHook()
    {
        DEBUG_LOG("DX11Hook::InstallHook: locating D3D11 methods via kiero...");

        if (GetModuleHandle("d3d11.dll") == nullptr)
        {
            DEBUG_LOG("DX11Hook::InstallHook: GetModuleHandle failed");
            LoadLibrary("d3d11.dll");
        }
        kiero::D3D11Output output;
        auto err = kiero::locate<kiero::Implementation_D3D11>(nullptr, &output);
        if (err != kiero::Error_Nil)
        {
            DEBUG_LOG("DX11Hook::InstallHook: kiero locate failed (err=%d)", err);
            return false;
        }

        if (output.swapchain_methods.size() <= 8 || !output.swapchain_methods[8])
        {
            DEBUG_LOG("DX11Hook::InstallHook: Present method not found in vtable");
            return false;
        }

        auto presentAddr = output.swapchain_methods[8];
        DEBUG_LOG("DX11Hook::InstallHook: Present found at %p", presentAddr);

        MH_Initialize();
        if (MH_CreateHook(presentAddr, (void *)HookPresent, (void **)&OriginalPresent) != MH_OK || MH_EnableHook(presentAddr) != MH_OK)
        {
            DEBUG_LOG("DX11Hook::InstallHook: MinHook failed");
            return false;
        }

        DEBUG_LOG("DX11Hook::InstallHook: hook installed successfully");
        return true;
    }
}
