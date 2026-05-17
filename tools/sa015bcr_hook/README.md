# sa015bcr.dll logging proxy

Logs every call to `sa015bcr.dll`'s exported `sa015bcr` and `getVersion`
functions made by DPS, including the contents of the `password` buffer
that DPS passes in (the missing link in the static reverse).

## Build

```powershell
.\build.ps1
```

Produces `sa015bcr_hook.dll` (32-bit, /MT static CRT, no MSVCR dependency).

## Install (requires admin; auto-elevates)

```powershell
.\install.ps1
```

This renames `C:\DPS\sa015bcr.dll` to `C:\DPS\sa015bcr_real.dll` and drops
the proxy over the top. The proxy forwards every call to the real DLL and
appends to `C:\DPS\Logs\sa015bcr_hook.txt`.

## Use

1. Start the simulator with an Algo 92 archive (e.g. the 2018 Silverado one
   the most recent log used).
2. Run DPS, drive it through the same flow that produced
   `Chal 1122334406  Resp ECBFF787A4  Status Success`.
3. Read `C:\DPS\Logs\sa015bcr_hook.txt` and send it to me.

The log is human-readable. For each call you'll see:
- `algoId` (e.g. `0x92`)
- `seed`: the 5 seed bytes
- `password`: the actual pointer DPS passes AND the first 96 bytes there
- `out(in)` / `out(after)`: output buffer before and after the call
- `rc`: return value from the real `sa015bcr`

## Uninstall

```powershell
.\uninstall.ps1
```
