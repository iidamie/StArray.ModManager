#include <jni.h>
#include <array>
#include <cstdint>
#include <android/log.h>

#include <dobby.h>

#define LOG_TAG "StArray.ModManager.Dobby"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

// Minimal ABI-only declarations matching the Android 17 libinput.so symbol.
// These are deliberately incomplete: the wrapper only forwards values and
// never dereferences the framework-private types.
namespace motion_event_abi {
struct LogicalDisplayId { int32_t value; };
enum class MotionFlag : uint32_t {};
struct MotionFlags { uint32_t value; };
enum class MotionClassification : uint32_t {};
struct Transform;
struct PointerProperties;
struct PointerCoords;
}

using motion_event_initialize_fn = void (*) (
    void *event,
    int32_t arg1,
    int32_t arg2,
    uint32_t arg3,
    motion_event_abi::LogicalDisplayId display_id,
    std::array<uint8_t, 32> hmac,
    int32_t arg6,
    int32_t arg7,
    motion_event_abi::MotionFlags motion_flags,
    int32_t arg9,
    int32_t arg10,
    int32_t arg11,
    motion_event_abi::MotionClassification classification,
    const motion_event_abi::Transform *transform,
    float arg15,
    float arg16,
    float arg17,
    float arg18,
    const motion_event_abi::Transform *raw_transform,
    int64_t down_time,
    int64_t event_time,
    uint64_t event_id,
    const motion_event_abi::PointerProperties *properties,
    const motion_event_abi::PointerCoords *coords);

using input_event_callback_fn = void (*)(void *input_event);
static motion_event_initialize_fn g_motion_event_initialize_original = nullptr;
static input_event_callback_fn g_motion_event_callback = nullptr;

static void modmanager_motion_event_initialize_detour(
    void *event,
    int32_t arg1,
    int32_t arg2,
    uint32_t arg3,
    motion_event_abi::LogicalDisplayId display_id,
    std::array<uint8_t, 32> hmac,
    int32_t arg6,
    int32_t arg7,
    motion_event_abi::MotionFlags motion_flags,
    int32_t arg9,
    int32_t arg10,
    int32_t arg11,
    motion_event_abi::MotionClassification classification,
    const motion_event_abi::Transform *transform,
    float arg15,
    float arg16,
    float arg17,
    float arg18,
    const motion_event_abi::Transform *raw_transform,
    int64_t down_time,
    int64_t event_time,
    uint64_t event_id,
    const motion_event_abi::PointerProperties *properties,
    const motion_event_abi::PointerCoords *coords) {
    motion_event_initialize_fn original = g_motion_event_initialize_original;
    if (original == nullptr) return;

    original(
        event,
        arg1,
        arg2,
        arg3,
        display_id,
        hmac,
        arg6,
        arg7,
        motion_flags,
        arg9,
        arg10,
        arg11,
        classification,
        transform,
        arg15,
        arg16,
        arg17,
        arg18,
        raw_transform,
        down_time,
        event_time,
        event_id,
        properties,
        coords);

    input_event_callback_fn callback = g_motion_event_callback;
    if (callback != nullptr && event != nullptr)
        callback(event);
}

// ============================================================================
// C ABI exports for P/Invoke from C# (Mono)
// 命名规范: modmanager_dobby_xxx
// C# 通过 [DllImport("modmanager")] 直接调用
// ============================================================================
extern "C" {

/**
 * DobbyHook — 安装 inline hook。
 * @param address        目标函数地址
 * @param replace_func   替换函数地址
 * @param origin_func    [out] 保存原函数地址的指针
 * @return 0 成功，非 0 失败
 */
int modmanager_dobby_hook(void *address, void *replace_func, void **origin_func) {
    LOGI("DobbyHook at %p, replace=%p", address, replace_func);
    int ret = DobbyHook(address, (dobby_dummy_func_t)replace_func, (dobby_dummy_func_t *)origin_func);
    if (ret != 0) LOGE("DobbyHook failed at %p, ret=%d", address, ret);
    return ret;
}

/**
 * Installs the ABI-preserving native MotionEvent::initialize wrapper.
 * The callback is invoked after the MotionEvent object has been initialized.
 */
int modmanager_install_motion_event_initialize_hook(void *address, void *callback) {
    if (address == nullptr || callback == nullptr) return -1;
    if (g_motion_event_initialize_original != nullptr) {
        g_motion_event_callback = (input_event_callback_fn)callback;
        return 0;
    }

    g_motion_event_callback = (input_event_callback_fn)callback;
    int ret = DobbyHook(
        address,
        (dobby_dummy_func_t)modmanager_motion_event_initialize_detour,
        (dobby_dummy_func_t *)&g_motion_event_initialize_original);
    if (ret != 0) {
        g_motion_event_initialize_original = nullptr;
        g_motion_event_callback = nullptr;
        LOGE("MotionEvent.initialize hook failed at %p, ret=%d", address, ret);
    } else {
        LOGI("MotionEvent.initialize native wrapper installed at %p", address);
    }
    return ret;
}

/**
 * DobbyInstrument — 安装动态指令插桩。
 * @param address      目标函数地址
 * @param pre_handler  前置回调 (dobby_instrument_callback_t)
 * @return 0 成功
 */
int modmanager_dobby_instrument(void *address, void *pre_handler) {
    LOGI("DobbyInstrument at %p, handler=%p", address, pre_handler);
    return DobbyInstrument(address, (dobby_instrument_callback_t)pre_handler);
}

/**
 * DobbyDestroy — 移除 hook 并恢复原函数。
 * @param address  被 hook 的函数地址
 * @return 0 成功
 */
int modmanager_dobby_destroy(void *address) {
    LOGI("DobbyDestroy at %p", address);
    return DobbyDestroy(address);
}

/**
 * DobbySymbolResolver — 按 image 名称和 symbol 名称解析函数地址。
 * @param image_name   动态库名 (e.g., "libil2cpp.so")
 * @param symbol_name   符号名
 * @return 符号地址，失败返回 nullptr
 */
void *modmanager_dobby_symbol_resolver(const char *image_name, const char *symbol_name) {
    void *addr = DobbySymbolResolver(image_name, symbol_name);
    LOGI("DobbySymbolResolver(%s, %s) = %p", image_name, symbol_name, addr);
    return addr;
}

/**
 * DobbyCodePatch — 内存代码补丁。
 * @param address      目标地址
 * @param buffer       补丁数据
 * @param buffer_size  补丁数据大小
 * @return 0 成功
 */
int modmanager_dobby_code_patch(void *address, const uint8_t *buffer, uint32_t buffer_size) {
    LOGI("DobbyCodePatch at %p, size=%u", address, buffer_size);
    return DobbyCodePatch(address, (uint8_t *)buffer, buffer_size);
}

/**
 * DobbyGetVersion — 获取 Dobby 版本字符串。
 */
const char *modmanager_dobby_get_version(void) {
    return DobbyGetVersion();
}

/**
 * modmanager_log_write — write a line to Android logcat.
 * Called from C# via [DllImport("modmanager")].
 */
void modmanager_log_write(int prio, const char *tag, const char *msg) {
    __android_log_write(prio, tag ? tag : "ModManager", msg ? msg : "");
}

} // extern "C"
