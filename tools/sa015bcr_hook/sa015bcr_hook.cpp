// Logging proxy for the vendor cipher DLL.
// Drop-in replacement that forwards every call to the renamed original
// (<name>_real.dll) while logging args + buffer contents to a sibling log.

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdint.h>

typedef int (__cdecl *PFN_sa015bcr)(void* seed, const void* password, int algoId, void* out);
typedef int (__cdecl *PFN_getVersion)();

static HMODULE g_realDll = nullptr;
static PFN_sa015bcr g_real_sa015bcr = nullptr;
static PFN_getVersion g_real_getVersion = nullptr;
static CRITICAL_SECTION g_logCs;
static volatile LONG g_csInited = 0;
static volatile LONG g_tableDumped = 0;  // dump password table once per process

static void ensure_cs() {
    if (InterlockedCompareExchange(&g_csInited, 1, 0) == 0) {
        InitializeCriticalSection(&g_logCs);
    }
}

static void load_real() {
    if (g_realDll) return;
    g_realDll = LoadLibraryA("sa015bcr_real.dll");
    if (!g_realDll) {
        g_realDll = LoadLibraryA("C:\\DPS\\sa015bcr_real.dll");
    }
    if (g_realDll) {
        g_real_sa015bcr   = (PFN_sa015bcr)  GetProcAddress(g_realDll, "sa015bcr");
        g_real_getVersion = (PFN_getVersion)GetProcAddress(g_realDll, "getVersion");
    }
}

static void log_line(const char* fmt, ...) {
    ensure_cs();
    EnterCriticalSection(&g_logCs);
    FILE* f = NULL;
    if (fopen_s(&f, "C:\\DPS\\Logs\\sa015bcr_hook.txt", "a") == 0 && f) {
        SYSTEMTIME st; GetLocalTime(&st);
        fprintf(f, "[%04u-%02u-%02u %02u:%02u:%02u.%03u t%lu] ",
                st.wYear, st.wMonth, st.wDay,
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                GetCurrentThreadId());
        va_list args; va_start(args, fmt);
        vfprintf(f, fmt, args);
        va_end(args);
        fputc('\n', f);
        fclose(f);
    }
    LeaveCriticalSection(&g_logCs);
}

static void log_buf(const char* label, const void* p, int max_bytes) {
    if (!p) { log_line("  %-12s: (null)", label); return; }
    // Be careful: pointer may be invalid. Wrap in SEH.
    __try {
        const uint8_t* b = (const uint8_t*)p;
        char hex[3 * 128 + 8] = {0};
        char asc[1 * 128 + 8] = {0};
        int n = max_bytes; if (n > 96) n = 96;
        for (int i = 0; i < n; i++) {
            sprintf_s(hex + i*3, sizeof(hex) - i*3, "%02X ", b[i]);
            asc[i] = (b[i] >= 0x20 && b[i] < 0x7f) ? (char)b[i] : '.';
        }
        asc[n] = 0;
        log_line("  %-12s ptr=%p [%d bytes shown of %d]:", label, p, n, max_bytes);
        log_line("    hex: %s", hex);
        log_line("    asc: %s", asc);
    }
    __except(EXCEPTION_EXECUTE_HANDLER) {
        log_line("  %-12s ptr=%p ACCESS VIOLATION reading buffer", label, p);
    }
}

// Once the host's license-check op has decrypted the password table in-place,
// every per-algo entry is reachable as a contiguous 62-char ASCII record at
// table_base + 0x3398 + algoId * 62. We can back-compute table_base from
// the password pointer we just received in a real call:
//   password_va = table_base + 0x3398 + algoId * 62
//   table_base  = password_va - 0x3398 - algoId * 62
//
// Then dump every algoId 0x00..0xFF. Run once per process.
static void dump_password_table_once(const void* known_password_va, int known_algoId) {
    if (InterlockedCompareExchange(&g_tableDumped, 1, 0) != 0) return;

    uintptr_t base = (uintptr_t)known_password_va
                   - 0x3398
                   - (uintptr_t)known_algoId * 62;

    log_line("******** PASSWORD TABLE DUMP ********");
    log_line("  IVCS5B base inferred as %p (from algoId=0x%X password at %p)",
             (void*)base, known_algoId, known_password_va);
    log_line("  Table layout: 62-char ASCII at base+0x3398+algoId*62");
    log_line("");

    for (int id = 0; id <= 0xFF; id++) {
        const char* entry = (const char*)(base + 0x3398 + (uintptr_t)id * 62);
        __try {
            // Check first byte readable + plausible ASCII
            char prefix0 = entry[0];
            char prefix1 = entry[1];
            // A real entry begins with "01" or "03"; everything else is either
            // unused slot (will look like wide-char or null), or trash. We log
            // them all so the user can sift later.
            bool looks_real = ((prefix0 == '0') && (prefix1 == '1' || prefix1 == '3'));

            // Copy out 62 bytes to a safe local before logging.
            char copy[63] = {0};
            memcpy(copy, entry, 62);
            copy[62] = 0;
            // Replace any non-printable byte with '.' for readability.
            char safe[63] = {0};
            for (int i = 0; i < 62; i++) {
                safe[i] = (copy[i] >= 0x20 && copy[i] < 0x7f) ? copy[i] : '.';
            }

            log_line("  algoId=0x%02X%s  %s", id,
                     looks_real ? " [REAL]" : "       ",
                     safe);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            log_line("  algoId=0x%02X         <ACCESS VIOLATION reading entry>", id);
        }
    }
    log_line("******** END PASSWORD TABLE DUMP ********");
}

extern "C" __declspec(dllexport) int __cdecl sa015bcr(void* seed, const void* password, int algoId, void* out) {
    load_real();
    log_line("==================== sa015bcr ENTRY ====================");
    log_line("  algoId      : 0x%X (%d)", algoId, algoId);
    log_buf("seed",     seed,     8);
    log_buf("password",  password, 96);
    log_buf("out(in)",  out,      8);

    // First real call: dump every per-algo password entry now that the host
    // has decrypted the table. The password pointer we just received tells
    // us where the table base address is.
    dump_password_table_once(password, algoId);

    int rc;
    if (!g_real_sa015bcr) {
        log_line("  REAL sa015bcr NOT RESOLVED, returning 999");
        rc = 999;
    } else {
        rc = g_real_sa015bcr(seed, password, algoId, out);
    }

    log_line("  rc          : %d (0x%X)", rc, rc);
    log_buf("out(after)", out, 8);
    log_line("====================  sa015bcr EXIT ====================");
    return rc;
}

extern "C" __declspec(dllexport) int __cdecl getVersion() {
    load_real();
    if (!g_real_getVersion) { log_line("getVersion: REAL NOT RESOLVED"); return 0; }
    int v = g_real_getVersion();
    log_line("getVersion -> 0x%X", v);
    return v;
}

BOOL APIENTRY DllMain(HMODULE /*hMod*/, DWORD reason, LPVOID /*reserved*/) {
    if (reason == DLL_PROCESS_ATTACH) {
        ensure_cs();
        log_line("DLL_PROCESS_ATTACH (PID=%lu)", GetCurrentProcessId());
    } else if (reason == DLL_PROCESS_DETACH) {
        log_line("DLL_PROCESS_DETACH");
    }
    return TRUE;
}
