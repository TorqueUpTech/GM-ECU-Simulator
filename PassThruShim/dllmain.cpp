#include <windows.h>

// Forward decls - defined in exports.cpp. Shim_LogAttach eagerly opens the
// file log so the on-disk file proves LoadLibrary even when the host filters
// us out before calling PassThruOpen. Shim_CloseDebugLog closes that handle;
// safe to call when the file was never opened (no-op).
extern "C" void Shim_LogAttach();
extern "C" void Shim_CloseDebugLog();

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        OutputDebugStringA("[PassThruShim] DLL_PROCESS_ATTACH\n");
        Shim_LogAttach();
        break;
    case DLL_PROCESS_DETACH:
        OutputDebugStringA("[PassThruShim] DLL_PROCESS_DETACH\n");
        Shim_CloseDebugLog();
        break;
    }
    return TRUE;
}
