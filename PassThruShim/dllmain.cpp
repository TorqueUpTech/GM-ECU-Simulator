#include <windows.h>

// Forward decl - defined in exports.cpp. Closes the file-log handle that
// DebugLog lazily opens into %LOCALAPPDATA%\GmEcuSimulator\shim logs\.
// Safe to call when the file was never opened (no-op).
extern "C" void Shim_CloseDebugLog();

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        OutputDebugStringA("[PassThruShim] DLL_PROCESS_ATTACH\n");
        break;
    case DLL_PROCESS_DETACH:
        OutputDebugStringA("[PassThruShim] DLL_PROCESS_DETACH\n");
        Shim_CloseDebugLog();
        break;
    }
    return TRUE;
}
