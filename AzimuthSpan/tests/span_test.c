// Loads out\dinput8.dll into this process (which, like azimuth.exe, imports
// GetMonitorInfoW from USER32) and checks the primary monitor now reports
// the spanned rectangle and that DirectInput8Create still works.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>

static const GUID iid_direct_input8w = {0xBF798031, 0x483A, 0x4DA2, {0xAA, 0x99, 0x5D, 0x64, 0xED, 0x36, 0x97, 0x00}};
typedef HRESULT(WINAPI *create_t)(HINSTANCE, DWORD, REFIID, LPVOID *, void *);

static void show(const char *when)
{
    MONITORINFO info = {sizeof info};
    GetMonitorInfoW(MonitorFromPoint((POINT){0, 0}, MONITOR_DEFAULTTOPRIMARY), &info);
    printf("%s: %ld,%ld-%ld,%ld (%ld x %ld)\n", when, info.rcMonitor.left, info.rcMonitor.top, info.rcMonitor.right,
           info.rcMonitor.bottom, info.rcMonitor.right - info.rcMonitor.left, info.rcMonitor.bottom - info.rcMonitor.top);
}

int main(int argc, char **argv)
{
    show("before");
    HMODULE shim = LoadLibraryA(argc > 1 ? argv[1] : "out/dinput8.dll");
    if (!shim)
        return printf("load failed %lu\n", GetLastError()), 1;
    show("after");
    void *di = NULL;
    HRESULT hr = ((create_t)GetProcAddress(shim, "DirectInput8Create"))(GetModuleHandleW(NULL), 0x0800, &iid_direct_input8w, &di, NULL);
    printf("DirectInput8Create 0x%08lx, object %s\n", hr, di ? "created" : "missing");
    if (di)
        (*(ULONG(WINAPI **)(void *))(*(void ***)di + 2))(di);

    // The launcher's display aspect for a 5120x1440 display record: the
    // spanned size when the screens form a row, else Azimuth's own rules.
    typedef unsigned long long(WINAPI * aspect_t)(const void *);
    aspect_t aspect = (aspect_t)GetProcAddress(shim, "AzimuthSpanAspect");
    unsigned int record[8] = {0, 1000, 5120, 1440};
    unsigned long long a = aspect ? aspect(record) : 0;
    printf("aspect offered: %llu:%llu\n", a & 0xffffffff, a >> 32);
    return hr == S_OK && a ? 0 : 1;
}
