// Azimuth-Span: a dinput8.dll shim that lets Azimuth run across a row of
// side-by-side monitors (for example a 2560 + 5120 + 2560 triple-screen rig,
// 10240x1440 or 64:9). Azimuth Telemetry installs it next to azimuth.exe.
//
// Two changes, both in memory only (delete the DLL to undo):
//
// 1. The launcher's aspect list. Azimuth offers 16:9 plus the display's own
//    width:height, but drops anything wider than 4:1, so 64:9 (7.1:1) never
//    appears. The function that picks the display's aspect (azimuth.exe
//    1.0.3, RVA 0xf680) is replaced with one that returns the spanned size
//    and allows up to 8:1. It's only replaced when its code matches 1.0.3
//    byte for byte; any other Azimuth version is left alone.
//
// 2. The fullscreen window. Azimuth sizes it to GetMonitorInfoW of its
//    monitor, so azimuth.exe's GetMonitorInfoW import is replaced to report
//    the primary monitor as the whole row.
//
// DirectInput8Create is forwarded to the real system DLL, so input and the
// wheel aren't touched.
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>

#define SPAN_VERSION "0.3"

typedef BOOL(WINAPI *GetMonitorInfoW_t)(HMONITOR, LPMONITORINFO);
typedef HRESULT(WINAPI *DirectInput8Create_t)(HINSTANCE, DWORD, REFIID, LPVOID *, void *);

static GetMonitorInfoW_t real_get_monitor_info;
static DirectInput8Create_t real_direct_input8_create;
static HMODULE real_dinput8;
static wchar_t log_path[MAX_PATH], ini_path[MAX_PATH];
static BOOL enabled = TRUE;
static BOOL have_fixed_rect;
static RECT fixed_rect;
static long size_width, size_height;  // width= / height= in azimuth-span.ini, 0 = find the row
static RECT last_logged;
static unsigned long long last_aspect_logged;

static BOOL active;  // in azimuth.exe (or the test); nothing is logged otherwise

static void log_line(const char *format, ...)
{
    if (!active)
        return;
    FILE *file = _wfopen(log_path, L"a");
    if (!file)
        return;
    SYSTEMTIME now;
    GetLocalTime(&now);
    fprintf(file, "%02d:%02d:%02d ", now.wHour, now.wMinute, now.wSecond);
    va_list args;
    va_start(args, format);
    vfprintf(file, format, args);
    va_end(args);
    fputc('\n', file);
    fclose(file);
}

// Monitors are collected once per call; there are only a handful.
typedef struct
{
    RECT rects[16];
    int count;
} monitor_list;

static BOOL CALLBACK collect_monitor(HMONITOR monitor, HDC dc, LPRECT rect, LPARAM data)
{
    monitor_list *list = (monitor_list *)data;
    MONITORINFO info = {sizeof info};
    (void)dc;
    (void)rect;
    if (list->count < 16 && real_get_monitor_info(monitor, &info))
        list->rects[list->count++] = info.rcMonitor;
    return TRUE;
}

// Grows the primary monitor's rectangle sideways through every monitor that
// has the same top and height and touches its left or right edge, so a row
// of screens becomes one; screens above or below (or of another height) are
// left out.
static RECT span_from(RECT primary)
{
    monitor_list list = {0};
    RECT span = primary;
    BOOL grew = TRUE;
    EnumDisplayMonitors(NULL, NULL, collect_monitor, (LPARAM)&list);
    while (grew)
    {
        grew = FALSE;
        for (int i = 0; i < list.count; i++)
        {
            RECT r = list.rects[i];
            if (r.top != span.top || r.bottom != span.bottom)
                continue;
            if (r.right == span.left)
            {
                span.left = r.left;
                grew = TRUE;
            }
            else if (r.left == span.right)
            {
                span.right = r.right;
                grew = TRUE;
            }
        }
    }
    return span;
}

// The render area: rect= as given, else width= x height= centred on the
// primary monitor (a 2560 + 5120 + 2560 row gives -2560..7680), else the row
// of monitors found automatically.
static RECT area_for(RECT primary)
{
    if (have_fixed_rect)
        return fixed_rect;
    if (size_width > 0 && size_height > 0)
    {
        RECT r;
        r.left = primary.left + ((primary.right - primary.left) - size_width) / 2;
        r.top = primary.top;
        r.right = r.left + size_width;
        r.bottom = r.top + size_height;
        return r;
    }
    return span_from(primary);
}

static RECT primary_span(void)
{
    MONITORINFO info = {sizeof info};
    POINT origin = {0, 0};
    if (have_fixed_rect)
        return fixed_rect;
    if (!real_get_monitor_info(MonitorFromPoint(origin, MONITOR_DEFAULTTOPRIMARY), &info))
    {
        RECT none = {0};
        return none;
    }
    return area_for(info.rcMonitor);
}

static BOOL WINAPI hooked_get_monitor_info(HMONITOR monitor, LPMONITORINFO info)
{
    BOOL ok = real_get_monitor_info(monitor, info);
    if (!ok || !enabled || !(info->dwFlags & MONITORINFOF_PRIMARY))
        return ok;
    RECT span = area_for(info->rcMonitor);
    if (!EqualRect(&span, &last_logged))
    {
        log_line("window: primary monitor %ld,%ld-%ld,%ld reported as %ld,%ld-%ld,%ld (%ld x %ld)",
                 info->rcMonitor.left, info->rcMonitor.top, info->rcMonitor.right, info->rcMonitor.bottom,
                 span.left, span.top, span.right, span.bottom, span.right - span.left, span.bottom - span.top);
        last_logged = span;
    }
    info->rcMonitor = span;
    info->rcWork = span;
    return ok;
}

// --- 1. The launcher's aspect list ------------------------------------------

// azimuth.exe 1.0.3's "aspect of the display" function, RVA 0xf680, 0x78
// bytes. It takes Azimuth's display record (width at +8, height at +0xc)
// and returns width | height << 32, or 16:9 when the display is within 1%
// of 16:9, narrower than 5:4 or wider than 4:1.
#define ASPECT_RVA 0xf680
static const unsigned char aspect_code_1_0_3[] = {
    0x44, 0x8b, 0x49, 0x08, 0x44, 0x8b, 0x41, 0x0c, 0x44, 0x89, 0x4c, 0x24, 0x08, 0x44, 0x89, 0x44, 0x24, 0x0c, 0x45, 0x85,
    0xc9, 0x74, 0x59, 0x45, 0x85, 0xc0, 0x74, 0x54, 0x41, 0x8b, 0xd0, 0x4f, 0x8d, 0x14, 0xc9, 0x48, 0xc1, 0xe2, 0x04, 0x4d,
    0x85, 0xd2, 0x74, 0x21, 0x48, 0x85, 0xd2, 0x74, 0x1c, 0x48, 0x8b, 0xca, 0x49, 0x8b, 0xc2, 0x48, 0x2b, 0xc2, 0x49, 0x2b,
    0xca, 0x4c, 0x3b, 0xd2, 0x48, 0x0f, 0x46, 0xc1, 0x48, 0x6b, 0xc0, 0x64, 0x48, 0x3b, 0xc2, 0x76, 0x23, 0x4a, 0x8d, 0x0c,
    0x8d, 0x00, 0x00, 0x00, 0x00, 0x4b, 0x8d, 0x04, 0x80, 0x48, 0x3b, 0xc8, 0x72, 0x12, 0x4a, 0x8d, 0x04, 0x85, 0x00, 0x00,
    0x00, 0x00, 0x4c, 0x3b, 0xc8, 0x48, 0x8b, 0x44, 0x24, 0x08, 0x76, 0x07, 0x48, 0x8b, 0x05, 0xf1, 0xe1, 0x7d, 0x00, 0xc3,
};

#define ASPECT_16_9 (16ull | 9ull << 32)

// Same rules as Azimuth's, with the spanned size in place of the display's
// when there is one, and up to 8:1 instead of 4:1.
static unsigned long long span_aspect(const unsigned char *display)
{
    unsigned long long w = *(const unsigned int *)(display + 8);
    unsigned long long h = *(const unsigned int *)(display + 12);
    if (enabled)
    {
        RECT span = primary_span();
        if (span.right - span.left > 0 && span.bottom - span.top > 0)
        {
            w = (unsigned long long)(span.right - span.left);
            h = (unsigned long long)(span.bottom - span.top);
        }
    }
    unsigned long long result = w | h << 32;
    if (!w || !h)
        result = ASPECT_16_9;
    else
    {
        unsigned long long a = w * 9, b = h * 16, diff = a > b ? a - b : b - a;
        if (diff * 100 <= b || w * 4 < h * 5 || w > h * 8)
            result = ASPECT_16_9;
    }
    if (result != last_aspect_logged)
    {
        log_line("launcher: display aspect offered as %llu:%llu", result & 0xffffffff, result >> 32);
        last_aspect_logged = result;
    }
    return result;
}

static const char *patch_aspect(void)
{
    unsigned char *code = (unsigned char *)GetModuleHandleW(NULL) + ASPECT_RVA;
    MEMORY_BASIC_INFORMATION region;
    if (!VirtualQuery(code, &region, sizeof region) || region.State != MEM_COMMIT ||
        (unsigned char *)region.BaseAddress + region.RegionSize < code + sizeof aspect_code_1_0_3 ||
        !(region.Protect & (PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)))
        return "not patched: this isn't Azimuth 1.0.3's code";
    if (memcmp(code, aspect_code_1_0_3, sizeof aspect_code_1_0_3) != 0)
        return "not patched: this isn't Azimuth 1.0.3's code";

    // mov rax, span_aspect; jmp rax
    unsigned char jump[12] = {0x48, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xe0};
    void *target = (void *)span_aspect;
    memcpy(jump + 2, &target, sizeof target);
    DWORD old;
    if (!VirtualProtect(code, sizeof jump, PAGE_EXECUTE_READWRITE, &old))
        return "not patched: VirtualProtect failed";
    memcpy(code, jump, sizeof jump);
    VirtualProtect(code, sizeof jump, old, &old);
    FlushInstructionCache(GetCurrentProcess(), code, sizeof jump);
    return "patched (up to 8:1, spanned size)";
}

// --- 2. The fullscreen window -------------------------------------------------

// Replaces one import of the main module (azimuth.exe) by name.
static BOOL patch_import(const char *dll, const char *name, void *hook, void **original)
{
    BYTE *base = (BYTE *)GetModuleHandleW(NULL);
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + ((IMAGE_DOS_HEADER *)base)->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    for (IMAGE_IMPORT_DESCRIPTOR *desc = (IMAGE_IMPORT_DESCRIPTOR *)(base + dir.VirtualAddress); desc->Name; desc++)
    {
        if (_stricmp((char *)(base + desc->Name), dll) != 0 || !desc->OriginalFirstThunk)
            continue;
        IMAGE_THUNK_DATA *names = (IMAGE_THUNK_DATA *)(base + desc->OriginalFirstThunk);
        IMAGE_THUNK_DATA *slots = (IMAGE_THUNK_DATA *)(base + desc->FirstThunk);
        for (; names->u1.AddressOfData; names++, slots++)
        {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal))
                continue;
            IMAGE_IMPORT_BY_NAME *by_name = (IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData);
            if (strcmp((char *)by_name->Name, name) != 0)
                continue;
            DWORD old;
            if (!VirtualProtect(&slots->u1.Function, sizeof(void *), PAGE_READWRITE, &old))
                return FALSE;
            *original = (void *)slots->u1.Function;
            slots->u1.Function = (ULONG_PTR)hook;
            VirtualProtect(&slots->u1.Function, sizeof(void *), old, &old);
            return TRUE;
        }
    }
    return FALSE;
}

static void read_settings(void)
{
    wchar_t text[64];
    enabled = GetPrivateProfileIntW(L"span", L"enabled", 1, ini_path) != 0;
    GetPrivateProfileStringW(L"span", L"rect", L"", text, 64, ini_path);
    have_fixed_rect = swscanf(text, L"%ld,%ld,%ld,%ld", &fixed_rect.left, &fixed_rect.top, &fixed_rect.right,
                              &fixed_rect.bottom) == 4 &&
                      fixed_rect.right > fixed_rect.left && fixed_rect.bottom > fixed_rect.top;
    size_width = (long)GetPrivateProfileIntW(L"span", L"width", 0, ini_path);
    size_height = (long)GetPrivateProfileIntW(L"span", L"height", 0, ini_path);
}

// Only azimuth.exe is changed. Other programs that load this DLL from the
// same folder (Azimuth Telemetry reads DirectInput there) get plain
// DirectInput; AZIMUTH_SPAN_TEST=1 lets the test program through.
static BOOL in_azimuth(void)
{
    wchar_t exe[MAX_PATH], test[8];
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    const wchar_t *name = wcsrchr(exe, L'\\');
    if (name && _wcsicmp(name + 1, L"azimuth.exe") == 0)
        return TRUE;
    return GetEnvironmentVariableW(L"AZIMUTH_SPAN_TEST", test, 8) > 0 && test[0] == L'1';
}

static void attach(HMODULE self)
{
    wchar_t dir[MAX_PATH], system[MAX_PATH];
    GetModuleFileNameW(self, dir, MAX_PATH);
    wchar_t *slash = wcsrchr(dir, L'\\');
    if (slash)
        slash[1] = 0;
    swprintf(log_path, MAX_PATH, L"%sazimuth-span.log", dir);
    swprintf(ini_path, MAX_PATH, L"%sazimuth-span.ini", dir);

    GetSystemDirectoryW(system, MAX_PATH);
    wcscat_s(system, MAX_PATH, L"\\dinput8.dll");
    real_dinput8 = LoadLibraryW(system);
    if (real_dinput8)
        real_direct_input8_create = (DirectInput8Create_t)GetProcAddress(real_dinput8, "DirectInput8Create");

    real_get_monitor_info = GetMonitorInfoW; // replaced by the original import if it is hooked
    if (!in_azimuth())
        return;
    active = TRUE;
    read_settings();
    const char *aspect = enabled ? patch_aspect() : "not patched: disabled";
    BOOL hooked = enabled && patch_import("USER32.dll", "GetMonitorInfoW", (void *)hooked_get_monitor_info,
                                          (void **)&real_get_monitor_info);
    log_line("Azimuth-Span " SPAN_VERSION " loaded: system dinput8 %s; %s%s; aspect list %s; GetMonitorInfoW %s",
             real_direct_input8_create ? "ok" : "MISSING", enabled ? "enabled" : "disabled in azimuth-span.ini",
             have_fixed_rect                       ? ", rect from azimuth-span.ini"
             : size_width > 0 && size_height > 0 ? ", width and height from azimuth-span.ini"
                                                   : ", span found automatically",
             aspect,
             hooked ? "hooked" : "not hooked");
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
        attach(instance);
    }
    return TRUE;
}

HRESULT WINAPI DirectInput8Create(HINSTANCE instance, DWORD version, REFIID iid, LPVOID *out, void *outer)
{
    if (!real_direct_input8_create)
        return E_FAIL;
    return real_direct_input8_create(instance, version, iid, out, outer);
}

// For tests/span_test.c: the replacement aspect function on a display record.
unsigned long long WINAPI AzimuthSpanAspect(const void *display)
{
    return span_aspect((const unsigned char *)display);
}
